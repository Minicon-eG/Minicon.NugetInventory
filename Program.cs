using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

var (directory, output, delimiter, fetchDescriptions, showHelp) = ParseArgs(args);

if (showHelp || directory is null)
{
    Console.WriteLine("""
        nuget-inventory – sammelt alle NuGet-Pakete aus C#-Projekten eines Verzeichnisses

        Verwendung:
          nuget-inventory <verzeichnis> [Optionen]

        Optionen:
          -o, --output <datei>      Pfad der CSV-Ausgabedatei (Standard: nuget-packages.csv)
          -d, --delimiter <zeichen> CSV-Trennzeichen (Standard: ';')
          --no-descriptions         Keine Beschreibungen von nuget.org laden
          -h, --help                Diese Hilfe anzeigen
        """);
    return directory is null && !showHelp ? 1 : 0;
}

var rootDir = Path.GetFullPath(directory);
if (!Directory.Exists(rootDir))
{
    Console.Error.WriteLine($"Verzeichnis nicht gefunden: {rootDir}");
    return 1;
}

var excludedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules", ".git", ".vs", ".idea", "packages" };

var projectFiles = Directory
    .EnumerateFiles(rootDir, "*.csproj", SearchOption.AllDirectories)
    .Where(p => !GetRelativeSegments(rootDir, p).Any(excludedDirs.Contains))
    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
    .ToList();

Console.WriteLine($"{projectFiles.Count} Projekt(e) gefunden unter {rootDir}");
if (projectFiles.Count == 0)
    return 0;

// (PackageId, Version) -> Menge der Projekte, die diese Kombination verwenden
var usages = new Dictionary<(string Id, string Version), SortedSet<string>>();
var cpmCache = new Dictionary<string, CpmInfo?>(StringComparer.OrdinalIgnoreCase);

foreach (var projectFile in projectFiles)
{
    var projectName = Path.GetRelativePath(rootDir, projectFile);
    try
    {
        foreach (var (id, version) in ReadPackages(projectFile, cpmCache))
            AddUsage(usages, id, version, projectName);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Warnung: {projectName} konnte nicht gelesen werden ({ex.Message})");
    }
}

Console.WriteLine($"{usages.Count} Paket/Version-Kombinationen, {usages.Keys.Select(k => k.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()} eindeutige Pakete");

// Beschreibungen von nuget.org laden
var descriptions = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
if (fetchDescriptions && usages.Count > 0)
{
    var uniqueIds = usages.Keys.Select(k => k.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    Console.WriteLine($"Lade Beschreibungen für {uniqueIds.Count} Pakete von nuget.org ...");

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("nuget-inventory/1.0");

    var done = 0;
    await Parallel.ForEachAsync(uniqueIds, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (id, ct) =>
    {
        descriptions[id] = await FetchDescriptionAsync(http, id, ct);
        var count = Interlocked.Increment(ref done);
        if (count % 25 == 0 || count == uniqueIds.Count)
            Console.WriteLine($"  {count}/{uniqueIds.Count}");
    });
}

// CSV schreiben
var outputPath = Path.GetFullPath(output);
var rows = usages
    .OrderBy(u => u.Key.Id, StringComparer.OrdinalIgnoreCase)
    .ThenBy(u => u.Key.Version, VersionStringComparer.Instance);

var sb = new StringBuilder();
sb.AppendLine(string.Join(delimiter, new[] { "PackageId", "Version", "Projects", "Description" }.Select(f => CsvEscape(f, delimiter))));
foreach (var ((id, version), projects) in rows)
{
    descriptions.TryGetValue(id, out var description);
    sb.AppendLine(string.Join(delimiter,
    [
        CsvEscape(id, delimiter),
        CsvEscape(version, delimiter),
        CsvEscape(string.Join(" | ", projects), delimiter),
        CsvEscape(description ?? "", delimiter),
    ]));
}

// UTF-8 mit BOM, damit Excel Umlaute korrekt anzeigt
await File.WriteAllTextAsync(outputPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
Console.WriteLine($"CSV geschrieben: {outputPath}");
return 0;

static (string? Directory, string Output, string Delimiter, bool FetchDescriptions, bool ShowHelp) ParseArgs(string[] args)
{
    string? directory = null;
    var output = "nuget-packages.csv";
    var delimiter = ";";
    var fetchDescriptions = true;
    var showHelp = false;

    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "-h" or "--help":
                showHelp = true;
                break;
            case "-o" or "--output" when i + 1 < args.Length:
                output = args[++i];
                break;
            case "-d" or "--delimiter" when i + 1 < args.Length:
                delimiter = args[++i];
                break;
            case "--no-descriptions":
                fetchDescriptions = false;
                break;
            default:
                directory ??= args[i];
                break;
        }
    }

    return (directory, output, delimiter, fetchDescriptions, showHelp);
}

static IEnumerable<string> GetRelativeSegments(string root, string path) =>
    Path.GetRelativePath(root, Path.GetDirectoryName(path)!)
        .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

static void AddUsage(Dictionary<(string, string), SortedSet<string>> usages, string id, string version, string project)
{
    var key = (id, version);
    if (!usages.TryGetValue(key, out var projects))
        usages[key] = projects = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
    projects.Add(project);
}

static IEnumerable<(string Id, string Version)> ReadPackages(string projectFile, Dictionary<string, CpmInfo?> cpmCache)
{
    var doc = XDocument.Load(projectFile);
    var cpm = FindCpm(Path.GetDirectoryName(projectFile)!, cpmCache);

    foreach (var element in doc.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
    {
        var id = element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value;
        if (string.IsNullOrWhiteSpace(id))
            continue;

        var version = element.Attribute("Version")?.Value
                      ?? element.Attribute("VersionOverride")?.Value
                      ?? element.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value;

        if (string.IsNullOrWhiteSpace(version) && cpm?.Versions.TryGetValue(id, out var cpmVersion) == true)
            version = cpmVersion;

        yield return (id.Trim(), string.IsNullOrWhiteSpace(version) ? "(unbekannt)" : version.Trim());
    }

    // GlobalPackageReference aus Directory.Packages.props gilt für alle Projekte darunter
    if (cpm is not null)
        foreach (var (id, version) in cpm.GlobalPackages)
            yield return (id, version);

    // Legacy: packages.config neben dem Projekt
    var packagesConfig = Path.Combine(Path.GetDirectoryName(projectFile)!, "packages.config");
    if (File.Exists(packagesConfig))
    {
        foreach (var element in XDocument.Load(packagesConfig).Descendants().Where(e => e.Name.LocalName == "package"))
        {
            var id = element.Attribute("id")?.Value;
            if (!string.IsNullOrWhiteSpace(id))
                yield return (id.Trim(), element.Attribute("version")?.Value?.Trim() ?? "(unbekannt)");
        }
    }
}

static CpmInfo? FindCpm(string startDir, Dictionary<string, CpmInfo?> cache)
{
    if (cache.TryGetValue(startDir, out var cached))
        return cached;

    CpmInfo? result = null;
    var propsFile = Path.Combine(startDir, "Directory.Packages.props");
    if (File.Exists(propsFile))
    {
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var globals = new List<(string, string)>();
        foreach (var element in XDocument.Load(propsFile).Descendants())
        {
            var id = element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value;
            var version = element.Attribute("Version")?.Value;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version))
                continue;
            if (element.Name.LocalName == "PackageVersion")
                versions[id.Trim()] = version.Trim();
            else if (element.Name.LocalName == "GlobalPackageReference")
                globals.Add((id.Trim(), version.Trim()));
        }
        result = new CpmInfo(versions, globals);
    }
    else
    {
        var parent = Path.GetDirectoryName(startDir);
        if (parent is not null)
            result = FindCpm(parent, cache);
    }

    return cache[startDir] = result;
}

static async Task<string> FetchDescriptionAsync(HttpClient http, string packageId, CancellationToken ct)
{
    try
    {
        var url = $"https://azuresearch-usnc.nuget.org/query?q=packageid:{Uri.EscapeDataString(packageId)}&prerelease=true&take=1";
        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            return "";

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var data = json.RootElement.GetProperty("data");
        if (data.GetArrayLength() == 0)
            return "";

        var first = data[0];
        var foundId = first.GetProperty("id").GetString();
        if (!string.Equals(foundId, packageId, StringComparison.OrdinalIgnoreCase))
            return "";

        var description = first.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "";
        // Zeilenumbrüche/Mehrfach-Whitespace einebnen, damit die CSV lesbar bleibt
        return string.Join(' ', description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
    catch
    {
        return "";
    }
}

static string CsvEscape(string value, string delimiter)
{
    if (value.Contains('"') || value.Contains(delimiter) || value.Contains('\n') || value.Contains('\r'))
        return $"\"{value.Replace("\"", "\"\"")}\"";
    return value;
}

sealed record CpmInfo(Dictionary<string, string> Versions, List<(string Id, string Version)> GlobalPackages);

// Sortiert Versionsstrings numerisch (1.2.10 nach 1.2.9), fällt sonst auf Textvergleich zurück
sealed class VersionStringComparer : IComparer<string>
{
    public static readonly VersionStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
            return string.CompareOrdinal(x, y);

        var xParts = x.Split('.', '-', '+');
        var yParts = y.Split('.', '-', '+');
        for (var i = 0; i < Math.Max(xParts.Length, yParts.Length); i++)
        {
            var xPart = i < xParts.Length ? xParts[i] : "";
            var yPart = i < yParts.Length ? yParts[i] : "";
            var result = int.TryParse(xPart, out var xNum) && int.TryParse(yPart, out var yNum)
                ? xNum.CompareTo(yNum)
                : string.Compare(xPart, yPart, StringComparison.OrdinalIgnoreCase);
            if (result != 0)
                return result;
        }
        return 0;
    }
}
