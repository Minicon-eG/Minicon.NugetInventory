# Minicon.NugetInventory

Ein dotnet-Tool, das ein Verzeichnis rekursiv nach C#-Projekten durchsucht, alle verwendeten
NuGet-Pakete **in allen verwendeten Versionen** einsammelt, die Paketbeschreibungen von
nuget.org lädt und das Ergebnis als CSV speichert.

## Installation

```bash
dotnet tool install --global Minicon.NugetInventory
```

## Verwendung

```bash
nuget-inventory <verzeichnis> [Optionen]
```

| Option | Beschreibung |
|---|---|
| `-o, --output <datei>` | Pfad der CSV-Ausgabedatei (Standard: `nuget-packages.csv`) |
| `-d, --delimiter <zeichen>` | CSV-Trennzeichen (Standard: `;`, passend für Excel/DE) |
| `--no-descriptions` | Keine Beschreibungen von nuget.org laden (offline) |
| `-h, --help` | Hilfe anzeigen |

### Beispiel

```bash
nuget-inventory ~/RiderProjects/MeineSolution -o pakete.csv
```

Ausgabe (`pakete.csv`):

| PackageId | Version | Projects | Description |
|---|---|---|---|
| Newtonsoft.Json | 13.0.3 | Api/Api.csproj \| Core/Core.csproj | Json.NET is a popular... |
| Newtonsoft.Json | 12.0.1 | Legacy/Legacy.csproj | Json.NET is a popular... |

## Was wird erkannt?

- `PackageReference` in `.csproj` (Attribut `Version`, `VersionOverride` oder `<Version>`-Element)
- Central Package Management: `PackageVersion` und `GlobalPackageReference` aus `Directory.Packages.props` (auch in übergeordneten Verzeichnissen)
- Legacy `packages.config` neben dem Projekt
- `bin`, `obj`, `node_modules`, `.git`, `.vs`, `.idea`, `packages` werden übersprungen

Jede Paket/Version-Kombination ergibt eine CSV-Zeile inklusive der Projekte, die sie verwenden.
Die CSV wird als UTF-8 mit BOM geschrieben, damit Excel Umlaute korrekt darstellt.
