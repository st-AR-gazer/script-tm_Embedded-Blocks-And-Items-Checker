# Embedded Blocks And Items Checker

CLI tool for checking Trackmania map embedding consistency.

It compares:
- expected embedded item models stored in map metadata
- custom item and block models used by the map
- entries present in the map embedded ZIP data

The tool prints a JSON report to stdout and writes the same JSON to the output file path you provide.

## Requirements

- .NET 8 SDK or runtime
- A Trackmania `.Map.Gbx` file

## Usage

```bash
dotnet run -- <mapFile> <outputJson> [--pretty] [--no-expected-list] [--no-map-name] [--case-sensitive|--case-insensitive]
```

```bash
dotnet .\bin\Release\net8.0\EmbeddedBlocksAndItemsChecker.dll <mapFile> <outputJson> [--pretty] [--no-expected-list] [--no-map-name] [--case-sensitive|--case-insensitive]
```

## Flags

- `--pretty`: Pretty-print JSON output
- `--no-expected-list`: Keep `expectedEmbeddedItemModels` as `null`
- `--no-map-name`: Keep `mapName` as `null`
- `--case-sensitive`: Compare model paths using exact casing
- `--case-insensitive`: Compare model paths ignoring case (default)
- `--help`: Print usage help and exit with code `2`

## Matching behavior

- Verifies map file exists and starts with `GBX` magic header.
- Reads map with GBX.NET (`IgnoreExceptionsInBody=true`, `SafeSkippableChunks=true`).
- Loads expected embedded item models from `map.ExpectedEmbeddedItemModels`.
- Loads embedded ZIP entries from `map.OpenReadEmbeddedZipData()`.
- Normalizes paths by replacing `\` with `/`, trimming `./`, collapsing duplicate `/`, and removing leading `/`.
- Removes leading `Items/` or `Blocks/` before comparisons.
- Custom model detection rules:
- Anchored objects: custom if author is not `Nadeo` and id has a `/`.
- Blocks: custom if author is not `Nadeo` and id has a `/`, or id uses the custom block suffix (that suffix is removed before compare).

## JSON output fields

- `mapUid`: map UID when parse succeeds, otherwise `null`
- `mapName`: map name when parse succeeds and `--no-map-name` is not used, otherwise `null`
- `mapPath`: input map path from CLI
- `matchMode`: `case-sensitive` or `case-insensitive`
- `hasProperlyEmbeddedBlocks`: `true` when no used custom model is missing from embedded ZIP
- `expectedEmbeddedItemCount`: number of expected embedded item models in map metadata
- `missingExpectedEmbeddedItemCount`: expected models missing from embedded ZIP
- `embeddedZipEntryCount`: number of entries in embedded ZIP (or `0` when ZIP is unavailable)
- `usedCustomItemCount`: unique custom item and block models used in the map
- `notProperlyEmbeddedItemCount`: used custom models missing from embedded ZIP
- `expectedEmbeddedItemModels`: expected models display list, or `null` when `--no-expected-list` is used, or on early failure
- `missingExpectedEmbeddedItemModels`: expected models missing from embedded ZIP, or `null` on early failure
- `notProperlyEmbeddedItemModels`: used custom models missing from embedded ZIP, or `null` on early failure
- `error`: error string when checks fail, otherwise `null`
- `note`: extra context such as missing expected embedded items in map data or embedded ZIP read errors

If embedded ZIP data cannot be opened, it is treated as empty and this is reported in `note`.

## Exit codes

- `0`: Completed and `error` is `null`
- `1`: Completed with `error`, or fatal unhandled exception
- `2`: Argument error (`--help`, missing args, unknown flag)

## Build

```bash
dotnet build -c Release
```

## Publish Windows build

```bash
dotnet publish -c Release -r win-x64 --self-contained false
```
