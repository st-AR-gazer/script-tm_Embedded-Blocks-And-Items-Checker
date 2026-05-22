# Embedded Blocks And Items Checker

CLI tool for checking Trackmania map embedding consistency.

It compares:
- expected embedded item models stored in map metadata
- custom item and block models used by the map
- entries present in the map embedded ZIP data

The tool prints JSON to stdout and optionally writes to the output path you provide.

## Requirements

- .NET 8 SDK or runtime
- A Trackmania `.Map.Gbx` file, or a folder containing `.Map.Gbx` files

## Project layout

- `src/Program.cs`: CLI entrypoint and shared startup
- `src/Cli/`: legacy analyzer CLI parsing and help text
- `src/Analysis/`: map analysis, matching, output writing, and manual overrides
- `src/Inspection/`: TMX download, inspection artifact writing, and regression suite runner
- `src/Models/`: private report and configuration models shared by the partial program files

## Usage

```bash
dotnet run -- <inputPath> [outputPath] [--pretty] [--no-expected-list] [--no-map-name] [--case-sensitive|--case-insensitive] [--recursive] [--dump-zip] [--relaxed-stem-match] [--manual-overrides <path>]
```

```bash
dotnet .\bin\Release\net8.0\EmbeddedBlocksAndItemsChecker.dll <inputPath> [outputPath] [--pretty] [--no-expected-list] [--no-map-name] [--case-sensitive|--case-insensitive] [--recursive] [--dump-zip] [--relaxed-stem-match] [--manual-overrides <path>]
```

```bash
dotnet run -- inspect-tmx <tmxMapId> [outputDirectory] [--pretty] [--no-expected-list] [--no-map-name] [--case-sensitive|--case-insensitive] [--dump-zip] [--relaxed-stem-match] [--manual-overrides <path>] [--extract-zip|--no-extract-zip]
```

```bash
dotnet run -- inspect-map <mapPath> [outputDirectory] [--pretty] [--no-expected-list] [--no-map-name] [--case-sensitive|--case-insensitive] [--dump-zip] [--relaxed-stem-match] [--manual-overrides <path>] [--extract-zip|--no-extract-zip]
```

```bash
dotnet run -- run-suite <suitePath> [outputDirectory] [--pretty] [--no-expected-list] [--no-map-name] [--case-sensitive|--case-insensitive] [--dump-zip] [--relaxed-stem-match] [--manual-overrides <path>] [--extract-zip|--no-extract-zip]
```

## Flags

- `--pretty`: Pretty-print JSON output
- `--no-expected-list`: Keep `expectedEmbeddedItemModels` as `null`
- `--no-map-name`: Keep `mapName` as `null`
- `--case-sensitive`: Compare model paths using exact casing
- `--case-insensitive`: Compare model paths ignoring case (default)
- `--recursive`: When `inputPath` is a folder, scan subfolders too
- `--dump-zip`: Print embedded ZIP entry names to stderr (debug)
- `--relaxed-stem-match`: Enable relaxed model stem fallback matching (off by default)
- `--no-relaxed-stem-match`: Disable relaxed model stem fallback matching
- `--manual-overrides <path>`: Load map-specific manual embedding overrides from a JSON file
- `--extract-zip`: Extract embedded ZIP entries in inspector workflows (default)
- `--no-extract-zip`: Skip embedded ZIP extraction in inspector workflows
- `--help`: Print usage help and exit with code `2`

## Inspector workflows

These commands are meant for the "why did this map get flagged?" debugging loop.

### `inspect-tmx`

Downloads a Trackmania Exchange map by id using `https://trackmania.exchange/mapgbx/<id>`, then builds a self-contained inspection folder.

Example:

```bash
dotnet run -- inspect-tmx 44741 --pretty
```

Default output goes under `inspection_runs/`. You can also pass a folder explicitly:

```bash
dotnet run -- inspect-tmx 44741 .\inspection_runs\kiafosu-check --pretty
```

### `inspect-map`

Copies a local `.Map.Gbx` into an inspection folder and writes the same artifacts:

```bash
dotnet run -- inspect-map .\test_maps\Black Narcissus.Map.Gbx .\inspection_runs\black-narcissus --pretty
```

### Inspection folder contents

Each inspection folder contains:

- `input/`: the inspected map file
- `report.json`: the analyzer JSON report
- `summary.txt`: quick human-readable summary
- `notes.txt`: report notes line by line
- `source.json`: source metadata (TMX id, URLs, original local path, stored map path)
- `lists/expectedEmbeddedItemModels.txt`
- `lists/usedCustomItemModels.txt`
- `lists/missingExpectedEmbeddedItemModels.txt`
- `lists/excludedClubExpectedItemModels.txt`
- `lists/usedClubItemModels.txt`
- `lists/notProperlyEmbeddedItemModels.txt`
- `embedded-zip/entries.txt`: raw entry name to canonical path mapping
- `embedded-zip/manifest.json`: structured ZIP entry metadata
- `embedded-zip/extracted/`: extracted embedded ZIP payload when `--no-extract-zip` is not used

## Regression suite workflow

`run-suite` lets you keep a list of known cases and rerun them after matching logic changes.

Example:

```bash
dotnet run -- run-suite .\map_inspection_suite.example.json --pretty
```

The suite file is JSON with a `cases` array. Each case must define exactly one of:

- `tmxMapId`
- `mapPath`

Optional expectation fields:

- `expectHasProperlyEmbeddedBlocks`
- `expectNotProperlyEmbeddedItemCount`
- `expectMissingExpectedEmbeddedItemCount`

Relative `mapPath` values are resolved from the suite file's directory.

A sample manifest is included at `map_inspection_suite.example.json`.

## Matching behavior

- If `inputPath` is a folder, the tool scans `.Map.Gbx` files in that folder and returns one report per file.
- Folder scanning is top level only unless `--recursive` is used.
- Verifies map file exists and starts with `GBX` magic header.
- Reads map with GBX.NET (`IgnoreExceptionsInBody=true`, `SafeSkippableChunks=true`).
- Loads expected embedded item models from `map.ExpectedEmbeddedItemModels`.
- Loads embedded ZIP entries from `map.OpenReadEmbeddedZipData()`.
- Normalizes paths by replacing `\` with `/`, trimming `./`, collapsing duplicate `/`, and removing leading `/`.
- Removes leading `Items/` or `Blocks/` before comparisons.
- If embedded ZIP entries contain a Windows absolute path (e.g., `C:/Users/.../Documents/Trackmania/Items/...`), the tool extracts the portion after `Items/` or `Blocks/` before comparing.
- If a model path does not match any embedded ZIP entry by path, it falls back to matching by unique `.Gbx` file name (and notes that it did so).
- If `--relaxed-stem-match` is enabled, it can also fall back to a unique relaxed model stem match for filename-shape mismatches.
- If `--manual-overrides` is provided, listed model paths are treated as embedded for the specified map UIDs.
- Paths starting with `club:` cannot be validated outside the game client, so they are excluded from missing and not-properly-embedded checks and reported as warnings.
- Custom model detection rules:
- Anchored objects: custom if author is not `Nadeo` and id has a `/`.
- Blocks: custom if author is not `Nadeo` and id has a `/`, or id uses the custom block suffix (that suffix is removed before compare).

## Output path behavior

- If `outputPath` is omitted, no output file(s) are written.
- If `outputPath` is a JSON file path, the tool writes one combined JSON payload to that file.
- In folder input mode with a JSON file output path, objects are appended into a live JSON array so progress is saved continuously while existing entries stay in place.
- If `outputPath` is a folder path, the tool writes one JSON file per map.
- `outputPath` is treated as a folder when it already exists as a folder, ends with a slash, or has no file extension.
- Per-map folder export uses `mapUid` as the file name: `<mapUid>.json`.
- If `mapUid` is missing, a sanitized map file name is used as fallback.
- If multiple maps share the same `mapUid`, they write to the same file name.

## Manual Overrides JSON

Use `--manual-overrides <path>` with a JSON file like:

```json
{
  "overrides": [
    {
      "mapUid": "Ln74Kb41PaKrjxzywCeUFZmu9Dj",
      "notes": [
        "Expected model name differs from embedded ZIP naming.",
        "Known naming mismatch: Particles_white vs Particles32x32White."
      ],
      "treatAsEmbeddedModelPaths": [
        "1-Scenery/particles/Particles_white.Gbx"
      ]
    }
  ]
}
```

`note` is also supported as a single-string legacy alias and is appended into the map output `notes` array.
`notes` values in a manual override entry are appended to that map report's `notes` array.

## JSON output fields

- `mapUid`: map UID when parse succeeds, otherwise `null`
- `mapName`: map name when parse succeeds and `--no-map-name` is not used, otherwise `null`
- `mapPath`: input map path from CLI
- `matchMode`: `case-sensitive` or `case-insensitive`
- `hasProperlyEmbeddedBlocks`: `true` when no used custom model is missing from embedded ZIP
- `expectedEmbeddedItemCount`: number of expected embedded item models checked for embedding (club items are excluded)
- `missingExpectedEmbeddedItemCount`: expected models missing from embedded ZIP
- `excludedClubExpectedItemCount`: number of expected models excluded because they start with `club:`
- `embeddedZipEntryCount`: number of entries in embedded ZIP (or `0` when ZIP is unavailable)
- `usedCustomItemCount`: unique custom item and block models used in the map
- `usedClubItemCount`: number of used custom models that start with `club:`
- `notProperlyEmbeddedItemCount`: used custom models missing from embedded ZIP
- `expectedEmbeddedItemModels`: expected models display list, or `null` when `--no-expected-list` is used, or on early failure
- `missingExpectedEmbeddedItemModels`: expected models missing from embedded ZIP, or `null` on early failure
- `excludedClubExpectedItemModels`: expected models excluded because they start with `club:`
- `usedClubItemModels`: used custom models that start with `club:`
- `notProperlyEmbeddedItemModels`: used custom models missing from embedded ZIP, or `null` on early failure
- `error`: error string when checks fail, otherwise `null`
- `notes`: array of extra context messages (for example missing expected embedded items in map data, embedded ZIP read errors, or club item warnings)

If embedded ZIP data cannot be opened, it is treated as empty and this is reported in `notes`.

JSON shape:
- File input: one JSON object
- Folder input: JSON array of objects

## Exit codes

- `0`: Completed and all produced reports have `error = null`
- `1`: At least one produced report has `error`, or fatal unhandled exception
- `2`: Argument error (`--help`, missing args, unknown flag)

For `run-suite`, exit code `1` is also used when a case expectation does not match the produced report.

## Build

```bash
dotnet build -c Release
```

## Publish Windows build

```bash
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true
```
