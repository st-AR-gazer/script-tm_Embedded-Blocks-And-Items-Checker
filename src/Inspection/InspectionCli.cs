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
    private static InspectTmxOptions ParseInspectTmxArgs(string[] args)
    {
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgException("inspect-tmx requires <tmxMapId>.");

        var tmxMapId = args[1].Trim();
        if (!IsAllDigits(tmxMapId))
            throw new ArgException($"TMX map id must be numeric. Received: {tmxMapId}");

        string? outputDirectory = null;
        int optionsStartIndex = 2;
        if (args.Length > 2 && !args[2].StartsWith("--", StringComparison.Ordinal))
        {
            outputDirectory = args[2];
            optionsStartIndex = 3;
        }

        var common = ParseInspectionCommonOptions(args, optionsStartIndex);
        return new InspectTmxOptions(tmxMapId, outputDirectory, common);
    }


    private static InspectMapOptions ParseInspectMapArgs(string[] args)
    {
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgException("inspect-map requires <mapPath>.");

        string? outputDirectory = null;
        int optionsStartIndex = 2;
        if (args.Length > 2 && !args[2].StartsWith("--", StringComparison.Ordinal))
        {
            outputDirectory = args[2];
            optionsStartIndex = 3;
        }

        var common = ParseInspectionCommonOptions(args, optionsStartIndex);
        return new InspectMapOptions(args[1], outputDirectory, common);
    }


    private static RunSuiteOptions ParseRunSuiteArgs(string[] args)
    {
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]) || args[1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgException("run-suite requires <suitePath>.");

        string? outputDirectory = null;
        int optionsStartIndex = 2;
        if (args.Length > 2 && !args[2].StartsWith("--", StringComparison.Ordinal))
        {
            outputDirectory = args[2];
            optionsStartIndex = 3;
        }

        var common = ParseInspectionCommonOptions(args, optionsStartIndex);
        return new RunSuiteOptions(args[1], outputDirectory, common);
    }


    private static InspectionCommonOptions ParseInspectionCommonOptions(string[] args, int startIndex)
    {
        bool pretty = false;
        bool includeExpectedList = true;
        bool includeMapName = true;
        bool caseSensitive = false;
        bool dumpZipEntries = false;
        bool relaxedStemMatching = false;
        bool extractZip = true;
        string? manualOverridesPath = null;

        for (int i = startIndex; i < args.Length; i++)
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
                case "--dump-zip":
                    dumpZipEntries = true;
                    break;
                case "--relaxed-stem-match":
                    relaxedStemMatching = true;
                    break;
                case "--no-relaxed-stem-match":
                    relaxedStemMatching = false;
                    break;
                case "--extract-zip":
                    extractZip = true;
                    break;
                case "--no-extract-zip":
                    extractZip = false;
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
                    throw new ArgException("Help requested.");
                default:
                    throw new ArgException($"Unknown flag: {a}");
            }
        }

        return new InspectionCommonOptions(
            Pretty: pretty,
            IncludeExpectedList: includeExpectedList,
            IncludeMapName: includeMapName,
            CaseSensitive: caseSensitive,
            DumpZipEntries: dumpZipEntries,
            RelaxedStemMatching: relaxedStemMatching,
            ManualOverridesPath: manualOverridesPath,
            ExtractZip: extractZip);
    }


}
