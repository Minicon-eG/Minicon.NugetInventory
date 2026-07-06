using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

var (directory, output, delimiter, fetchMetadata, showHelp) = ParseArgs(args);

if (showHelp || directory is null)
{
    Console.WriteLine("""
        nuget-inventory – sammelt alle NuGet-Pakete aus C#-Projekten eines Verzeichnisses

        Verwendung:
          nuget-inventory <verzeichnis> [Optionen]

        Optionen:
          -o, --output <datei>      Pfad der CSV-Ausgabedatei (Standard: nuget-packages.csv)
          -d, --delimiter <zeichen> CSV-Trennzeichen (Standard: ';')
          --no-metadata             Keine Metadaten (Beschreibung, Lizenz, Abhängigkeiten)
                                    von nuget.org laden (Alias: --no-descriptions)
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

// Metadaten (Beschreibung, Lizenz, Abhängigkeiten) je Paketversion von nuget.org laden
var metadata = new ConcurrentDictionary<(string Id, string Version), PackageMetadata>();
var fallbackDescriptions = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
if (fetchMetadata && usages.Count > 0)
{
    var pairs = usages.Keys.ToList();
    Console.WriteLine($"Lade Metadaten für {pairs.Count} Paketversionen von nuget.org ...");

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("nuget-inventory/1.1");

    var done = 0;
    await Parallel.ForEachAsync(pairs, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (pair, ct) =>
    {
        metadata[pair] = await FetchNuspecMetadataAsync(http, pair.Id, pair.Version, ct);
        var count = Interlocked.Increment(ref done);
        if (count % 25 == 0 || count == pairs.Count)
            Console.WriteLine($"  {count}/{pairs.Count}");
    });

    // Fallback über die Suche, wenn das nuspec keine Beschreibung geliefert hat
    // (z. B. unbekannte Version, Versionsbereich oder Paket nicht auf nuget.org)
    var missingIds = pairs
        .Where(p => metadata[p].Description.Length == 0)
        .Select(p => p.Id)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
    if (missingIds.Count > 0)
    {
        Console.WriteLine($"Lade Beschreibungen für {missingIds.Count} Pakete über die nuget.org-Suche ...");
        await Parallel.ForEachAsync(missingIds, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (id, ct) =>
        {
            fallbackDescriptions[id] = await FetchDescriptionAsync(http, id, ct);
        });
    }
}

// CSV schreiben
var outputPath = Path.GetFullPath(output);
var rows = usages
    .OrderBy(u => u.Key.Id, StringComparer.OrdinalIgnoreCase)
    .ThenBy(u => u.Key.Version, VersionStringComparer.Instance);

var sb = new StringBuilder();
sb.AppendLine(string.Join(delimiter, new[] { "PackageId", "Version", "Projects", "Description", "License", "Dependencies" }.Select(f => CsvEscape(f, delimiter))));
foreach (var ((id, version), projects) in rows)
{
    metadata.TryGetValue((id, version), out var meta);
    meta ??= PackageMetadata.Empty;
    var description = meta.Description.Length > 0 ? meta.Description : fallbackDescriptions.GetValueOrDefault(id, "");
    sb.AppendLine(string.Join(delimiter,
    [
        CsvEscape(id, delimiter),
        CsvEscape(version, delimiter),
        CsvEscape(string.Join(" | ", projects), delimiter),
        CsvEscape(description, delimiter),
        CsvEscape(meta.License, delimiter),
        CsvEscape(meta.Dependencies, delimiter),
    ]));
}

// UTF-8 mit BOM, damit Excel Umlaute korrekt anzeigt
await File.WriteAllTextAsync(outputPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
Console.WriteLine($"CSV geschrieben: {outputPath}");
return 0;

static (string? Directory, string Output, string Delimiter, bool FetchMetadata, bool ShowHelp) ParseArgs(string[] args)
{
    string? directory = null;
    var output = "nuget-packages.csv";
    var delimiter = ";";
    var fetchMetadata = true;
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
            case "--no-metadata" or "--no-descriptions":
                fetchMetadata = false;
                break;
            default:
                directory ??= args[i];
                break;
        }
    }

    return (directory, output, delimiter, fetchMetadata, showHelp);
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

// Lädt das .nuspec einer konkreten Paketversion vom flatcontainer und extrahiert
// Beschreibung, Lizenz und Abhängigkeiten. Liefert Empty, wenn die Version nicht
// auflösbar ist (Range/Wildcard/unbekannt) oder das Paket nicht auf nuget.org liegt.
static async Task<PackageMetadata> FetchNuspecMetadataAsync(HttpClient http, string packageId, string version, CancellationToken ct)
{
    var normalized = NormalizeVersion(version);
    if (normalized.Length == 0)
        return PackageMetadata.Empty;

    try
    {
        var idLower = packageId.ToLowerInvariant();
        var url = $"https://api.nuget.org/v3-flatcontainer/{idLower}/{normalized.ToLowerInvariant()}/{idLower}.nuspec";
        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            return PackageMetadata.Empty;

        var doc = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var meta = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "metadata");
        if (meta is null)
            return PackageMetadata.Empty;

        var description = FlattenWhitespace(meta.Elements().FirstOrDefault(e => e.Name.LocalName == "description")?.Value ?? "");

        var licenseElement = meta.Elements().FirstOrDefault(e => e.Name.LocalName == "license");
        var licenseUrl = meta.Elements().FirstOrDefault(e => e.Name.LocalName == "licenseUrl")?.Value.Trim() ?? "";
        var license = licenseElement?.Attribute("type")?.Value switch
        {
            "expression" => licenseElement.Value.Trim(),
            "file" => licenseUrl.Length > 0 ? licenseUrl : "(Lizenzdatei im Paket)",
            _ => licenseUrl,
        };

        // Abhängigkeiten über alle Zielframework-Gruppen hinweg zusammenfassen
        var dependencies = meta.Descendants()
            .Where(e => e.Name.LocalName == "dependency")
            .Select(d =>
            {
                var depId = d.Attribute("id")?.Value?.Trim();
                if (string.IsNullOrEmpty(depId))
                    return null;
                var range = d.Attribute("version")?.Value?.Trim();
                return string.IsNullOrEmpty(range) ? depId : $"{depId} {range}";
            })
            .Where(d => d is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase);

        return new PackageMetadata(description, license, string.Join(" | ", dependencies));
    }
    catch
    {
        return PackageMetadata.Empty;
    }
}

// Normalisiert auf die von nuget.org verwendete Form (führende Nullen weg, mind. 3 Teile,
// vierter Teil entfällt bei 0). Liefert "", wenn keine konkrete Version vorliegt.
static string NormalizeVersion(string version)
{
    var v = version.Trim().Trim('[', ']', '(', ')');
    if (v.Length == 0 || v.Contains(','))
        return "";

    var plus = v.IndexOf('+');
    if (plus >= 0)
        v = v[..plus];

    var dash = v.IndexOf('-');
    var release = dash >= 0 ? v[..dash] : v;
    var suffix = dash >= 0 ? v[dash..] : "";

    var parts = release.Split('.');
    var numbers = new List<int>(4);
    foreach (var part in parts)
    {
        if (!int.TryParse(part, out var number))
            return "";
        numbers.Add(number);
    }

    while (numbers.Count < 3)
        numbers.Add(0);
    if (numbers.Count == 4 && numbers[3] == 0)
        numbers.RemoveAt(3);
    if (numbers.Count > 4)
        return "";

    return string.Join('.', numbers) + suffix;
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
        return FlattenWhitespace(description);
    }
    catch
    {
        return "";
    }
}

// Zeilenumbrüche/Mehrfach-Whitespace einebnen, damit die CSV lesbar bleibt
static string FlattenWhitespace(string value) =>
    string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

static string CsvEscape(string value, string delimiter)
{
    if (value.Contains('"') || value.Contains(delimiter) || value.Contains('\n') || value.Contains('\r'))
        return $"\"{value.Replace("\"", "\"\"")}\"";
    return value;
}

sealed record CpmInfo(Dictionary<string, string> Versions, List<(string Id, string Version)> GlobalPackages);

sealed record PackageMetadata(string Description, string License, string Dependencies)
{
    public static readonly PackageMetadata Empty = new("", "", "");
}

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
