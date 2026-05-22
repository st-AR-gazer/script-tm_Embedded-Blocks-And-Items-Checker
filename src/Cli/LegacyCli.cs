using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;

using GBX.NET;
using GBX.NET.Engines.Game;
using GBX.NET.LZO;
using GBX.NET.ZLib;

internal static partial class Program
{
    private static CliOptions ParseArgs(string[] args)
    {
        if (args.Length == 0)
            throw new ArgException("No arguments provided.");

        if (args.Contains("--help", StringComparer.OrdinalIgnoreCase))
            throw new ArgException("Help requested.");

        if (args.Length < 1)
            throw new ArgException("You must specify <inputPath>.");

        var inputPath = args[0];

        if (inputPath.StartsWith("--", StringComparison.Ordinal))
            throw new ArgException("First argument must be <inputPath> (not a flag).");

        string? outputPath = null;
        int optionsStartIndex = 1;
        if (args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal))
        {
            outputPath = args[1];
            optionsStartIndex = 2;
        }

        bool pretty = false;
        bool includeExpectedList = true;
        bool includeMapName = true;
        bool caseSensitive = false;
        bool recursiveDirectorySearch = false;
        bool dumpZipEntries = false;
        bool relaxedStemMatching = false;
        string? manualOverridesPath = null;

        for (int i = optionsStartIndex; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--manual-overrides=", StringComparison.OrdinalIgnoreCase))
            {
                manualOverridesPath = a.Substring("--manual-overrides=".Length).Trim();
                if (string.IsNullOrWhiteSpace(manualOverridesPath))
                    throw new ArgException("Missing path in --manual-overrides=<path>.");
                continue;
            }

            switch (a)
            {
                case "--pretty":
                    pretty = true;
                    break;
                case "--no-expected-list":
                    includeExpectedList = false;
                    break;
                case "--no-map-name":
                    includeMapName = false;
                    break;
                case "--case-insensitive":
                    caseSensitive = false;
                    break;
                case "--case-sensitive":
                    caseSensitive = true;
                    break;
                case "--recursive":
                    recursiveDirectorySearch = true;
                    break;
                case "--dump-zip":
                    dumpZipEntries = true;
                    break;
                case "--relaxed-stem-match":
                    relaxedStemMatching = true;
                    break;
                case "--no-relaxed-stem-match":
                    relaxedStemMatching = false;
                    break;
                case "--manual-overrides":
                    if (i + 1 >= args.Length)
                        throw new ArgException("Missing path after --manual-overrides.");

                    var pathArg = args[i + 1];
                    if (pathArg.StartsWith("--", StringComparison.Ordinal))
                        throw new ArgException("Missing path after --manual-overrides.");

                    manualOverridesPath = pathArg;
                    i++;
                    break;
                case "--help":
                    break;
                default:
                    throw new ArgException($"Unknown flag: {a}");
            }
        }

        return new CliOptions(inputPath, outputPath, pretty, includeExpectedList, includeMapName, caseSensitive, recursiveDirectorySearch, dumpZipEntries, relaxedStemMatching, manualOverridesPath);
    }


    private static void PrintHelp()
    {
        Console.WriteLine(
@"Usage:
  tm_Embedded_Blocks_Items_Checker <inputPath> [outputPath] [--pretty] [--no-expected-list] [--no-map-name] [--case-sensitive|--case-insensitive] [--recursive] [--dump-zip] [--relaxed-stem-match] [--manual-overrides <path>]
  tm_Embedded_Blocks_Items_Checker inspect-tmx <tmxMapId> [outputDirectory] [--pretty] [--no-expected-list] [--no-map-name] [--case-sensitive|--case-insensitive] [--dump-zip] [--relaxed-stem-match] [--manual-overrides <path>] [--extract-zip|--no-extract-zip]
  tm_Embedded_Blocks_Items_Checker inspect-map <mapPath> [outputDirectory] [--pretty] [--no-expected-list] [--no-map-name] [--case-sensitive|--case-insensitive] [--dump-zip] [--relaxed-stem-match] [--manual-overrides <path>] [--extract-zip|--no-extract-zip]
  tm_Embedded_Blocks_Items_Checker run-suite <suitePath> [outputDirectory] [--pretty] [--no-expected-list] [--no-map-name] [--case-sensitive|--case-insensitive] [--dump-zip] [--relaxed-stem-match] [--manual-overrides <path>] [--extract-zip|--no-extract-zip]

Flags:
  --pretty              Pretty-print JSON output
  --no-expected-list    Omit the full expected embedded list from JSON
  --no-map-name         Omit map name from JSON
  --case-sensitive      Match ZIP entry paths with exact casing
  --case-insensitive    Match ZIP entry paths ignoring case (default)
  --recursive           When inputPath is a directory, scan subdirectories
  --dump-zip            Print embedded ZIP entry names to stderr (debug)
  --relaxed-stem-match  Enable relaxed model stem fallback (off by default)
  --no-relaxed-stem-match
                       Disable relaxed model stem fallback
  --manual-overrides    Path to a JSON file with map-specific embedding overrides
  --extract-zip         Extract embedded ZIP entries in inspector workflows (default)
  --no-extract-zip      Skip extracting embedded ZIP entries in inspector workflows

Notes:
  - Expected embedded items are matched against the embedded ZIP entries.
  - 'NotProperlyEmbeddedItemModels' is computed from used custom items/blocks that are missing in the embedded ZIP.
  - ZIP entry paths are compared without the leading Items/ or Blocks/ prefix.
  - Relaxed model stem fallback is disabled by default and can be enabled with --relaxed-stem-match.
  - Manual overrides are loaded from --manual-overrides when provided.
  - Paths starting with club: are excluded from missing and not-properly-embedded checks and reported as warnings.
  - inspect-tmx downloads a TMX map by id and writes a full inspection folder.
  - inspect-map writes the same inspection folder for a local .Map.Gbx file.
  - run-suite replays a JSON manifest of TMX ids and/or local maps as regression cases.
  - outputPath is optional. If omitted, no output file(s) are written (JSON is still printed to stdout).
  - outputPath can be a JSON file path or a directory path.
  - outputPath is treated as a directory when it exists as a directory, ends with a slash, or has no file extension.
  - If outputPath is a directory, one file per map is written using mapUid as file name.
  - If inputPath is a directory and outputPath is a JSON file, objects are appended to a live JSON array after each map."
        );
    }


    private sealed record CliOptions(
        string InputPath,
        string? OutputPath,
        bool Pretty,
        bool IncludeExpectedList,
        bool IncludeMapName,
        bool CaseSensitive,
        bool RecursiveDirectorySearch,
        bool DumpZipEntries,
        bool RelaxedStemMatching,
        string? ManualOverridesPath
    );


    private sealed class ArgException : Exception
    {
        public ArgException(string message) : base(message) { }
    }


}
