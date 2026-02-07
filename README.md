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

## Usage

```bash
dotnet run -- <inputPath> [outputPath] [--pretty] [--no-expected-list] [--no-map-name] [--case-sensitive|--case-insensitive] [--recursive] [--dump-zip]
```

```bash
dotnet .\bin\Release\net8.0\EmbeddedBlocksAndItemsChecker.dll <inputPath> [outputPath] [--pretty] [--no-expected-list] [--no-map-name] [--case-sensitive|--case-insensitive] [--recursive] [--dump-zip]
```

## Flags

- `--pretty`: Pretty-print JSON output
- `--no-expected-list`: Keep `expectedEmbeddedItemModels` as `null`
- `--no-map-name`: Keep `mapName` as `null`
- `--case-sensitive`: Compare model paths using exact casing
- `--case-insensitive`: Compare model paths ignoring case (default)
- `--recursive`: When `inputPath` is a folder, scan subfolders too
- `--dump-zip`: Print embedded ZIP entry names to stderr (debug)
- `--help`: Print usage help and exit with code `2`

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
- `note`: extra context such as missing expected embedded items in map data, embedded ZIP read errors, or club item warnings

If embedded ZIP data cannot be opened, it is treated as empty and this is reported in `note`.

JSON shape:
- File input: one JSON object
- Folder input: JSON array of objects

## Exit codes

- `0`: Completed and all produced reports have `error = null`
- `1`: At least one produced report has `error`, or fatal unhandled exception
- `2`: Argument error (`--help`, missing args, unknown flag)

## Build

```bash
dotnet build -c Release
```

## Publish Windows build

```bash
dotnet publish -c Release -r win-x64 --self-contained false
```
