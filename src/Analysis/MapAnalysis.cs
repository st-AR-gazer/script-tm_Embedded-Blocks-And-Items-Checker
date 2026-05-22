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
    private static EmbeddedReport AnalyzeMap(
        string mapPath,
        CliOptions opts,
        IReadOnlyDictionary<string, ManualEmbeddingOverride> manualEmbeddingOverrides)
    {
        var report = new EmbeddedReport
        {
            MapPath = mapPath,
            MatchMode = opts.CaseSensitive ? "case-sensitive" : "case-insensitive"
        };

        if (!File.Exists(mapPath))
        {
            report.Error = $"Map file does not exist: {mapPath}";
            report.HasProperlyEmbeddedBlocks = false;
            return report;
        }

        if (!LooksLikeGbx(mapPath))
        {
            report.Error = "Not a GBX file (missing GBX magic header).";
            report.HasProperlyEmbeddedBlocks = false;
            return report;
        }

        CGameCtnChallenge map;
        try
        {
            var settings = new GbxReadSettings
            {
                IgnoreExceptionsInBody = true,
                SafeSkippableChunks = true
            };

            map = Gbx.ParseNode<CGameCtnChallenge>(mapPath, settings);
        }
        catch (Exception ex)
        {
            report.Error = "Failed to parse map GBX.";
            AppendNote(report, $"{ex.GetType().Name}: {ex.Message}");
            report.HasProperlyEmbeddedBlocks = false;
            return report;
        }

        report.MapUid = map.MapUid;
        if (opts.IncludeMapName)
            report.MapName = map.MapName;

        var caseSensitive = opts.CaseSensitive;
        var comparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var manualOverride = GetManualEmbeddingOverrideForMap(map.MapUid, comparer, manualEmbeddingOverrides);

        var expected = map.ExpectedEmbeddedItemModels;
        report.ExpectedEmbeddedItemCount = expected?.Count ?? 0;

        var expectedDisplay = expected is null
            ? new List<string>()
            : expected.Select(BuildDisplayPath).ToList();

        if (opts.IncludeExpectedList)
            report.ExpectedEmbeddedItemModels = expectedDisplay;

        var embeddedSet = new HashSet<string>(comparer);
        var embeddedFileNameCounts = new Dictionary<string, int>(comparer);
        var embeddedLossyPathCounts = new Dictionary<string, int>(comparer);
        var embeddedRelaxedStemCounts = new Dictionary<string, int>(comparer);
        bool embeddedZipReadable = false;
        string? embeddedZipError = null;
        try
        {
            using var zip = map.OpenReadEmbeddedZipData();
            embeddedZipReadable = true;
            report.EmbeddedZipEntryCount = zip.Entries.Count;

            if (opts.DumpZipEntries)
            {
                Console.Error.WriteLine($"Embedded ZIP entries for: {mapPath}");
                foreach (var entry in zip.Entries)
                {
                    var canonical = CanonicalizeModelPath(entry.FullName);
                    Console.Error.WriteLine($"- {entry.FullName} => {canonical}");
                }
                Console.Error.WriteLine();
            }

            foreach (var entry in zip.Entries)
            {
                var embeddedPath = CanonicalizeModelPath(entry.FullName);
                if (string.IsNullOrWhiteSpace(embeddedPath))
                    continue;

                if (embeddedPath.EndsWith("/", StringComparison.Ordinal))
                    continue;

                embeddedSet.Add(embeddedPath);

                if (LooksLikeGbxModelPath(embeddedPath))
                {
                    var fileName = GetModelFileName(embeddedPath);
                    if (!string.IsNullOrWhiteSpace(fileName))
                        embeddedFileNameCounts[fileName] = embeddedFileNameCounts.TryGetValue(fileName, out var c) ? c + 1 : 1;

                    var lossyPath = BuildLossyComparablePath(embeddedPath);
                    if (!string.IsNullOrWhiteSpace(lossyPath))
                        embeddedLossyPathCounts[lossyPath] = embeddedLossyPathCounts.TryGetValue(lossyPath, out var lossyCount) ? lossyCount + 1 : 1;

                    if (opts.RelaxedStemMatching)
                    {
                        var relaxedStem = BuildRelaxedModelStem(embeddedPath);
                        if (!string.IsNullOrWhiteSpace(relaxedStem))
                            embeddedRelaxedStemCounts[relaxedStem] = embeddedRelaxedStemCounts.TryGetValue(relaxedStem, out var relaxedCount) ? relaxedCount + 1 : 1;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            embeddedZipReadable = false;
            embeddedZipError = $"{ex.GetType().Name}: {ex.Message}";
            report.EmbeddedZipEntryCount = 0;
        }

        var missingExpected = new List<string>();
        var excludedExpectedClub = new List<string>();
        int expectedMatchedByFileNameCount = 0;
        int expectedMatchedByLossyPathCount = 0;
        int expectedMatchedByRelaxedStemCount = 0;
        int expectedMatchedByManualOverrideCount = 0;
        if (expected is null || expected.Count == 0)
        {
            AppendNote(report, "No expected embedded items found in the map data.");
        }
        else
        {
            foreach (var ident in expected)
            {
                var expectedPath = CanonicalizeExpectedPath(ident);
                if (string.IsNullOrWhiteSpace(expectedPath))
                    continue;

                if (IsAnyClubPath(expectedPath))
                {
                    excludedExpectedClub.Add(BuildDisplayPath(ident));
                    continue;
                }

                if (!IsPresentInEmbeddedZip(expectedPath, embeddedSet, embeddedFileNameCounts, embeddedLossyPathCounts, embeddedRelaxedStemCounts, manualOverride.Paths, opts.RelaxedStemMatching, out var matchedByFileName, out var matchedByLossyPath, out var matchedByRelaxedStem, out var matchedByManualOverride))
                    missingExpected.Add(BuildDisplayPath(ident));
                else if (matchedByFileName)
                    expectedMatchedByFileNameCount++;
                else if (matchedByLossyPath)
                    expectedMatchedByLossyPathCount++;
                else if (matchedByRelaxedStem)
                    expectedMatchedByRelaxedStemCount++;
                else if (matchedByManualOverride)
                    expectedMatchedByManualOverrideCount++;
            }
        }

        report.MissingExpectedEmbeddedItemModels = missingExpected;
        report.MissingExpectedEmbeddedItemCount = missingExpected.Count;
        report.ExcludedClubExpectedItemModels = excludedExpectedClub;
        report.ExcludedClubExpectedItemCount = excludedExpectedClub.Count;
        report.ExpectedEmbeddedItemCount = Math.Max(0, report.ExpectedEmbeddedItemCount - excludedExpectedClub.Count);

        var usedCustom = BuildUsedCustomModels(map, comparer);
        report.UsedCustomItemCount = usedCustom.Count;

        var usedClubItems = usedCustom.Where(IsAnyClubPath).ToList();
        usedClubItems.Sort(comparer);
        report.UsedClubItemModels = usedClubItems;
        report.UsedClubItemCount = usedClubItems.Count;

        var notProperlyEmbedded = new List<string>();
        int usedMatchedByFileNameCount = 0;
        int usedMatchedByLossyPathCount = 0;
        int usedMatchedByRelaxedStemCount = 0;
        int usedMatchedByManualOverrideCount = 0;
        foreach (var item in usedCustom)
        {
            if (IsAnyClubPath(item))
                continue;

            if (!IsPresentInEmbeddedZip(item, embeddedSet, embeddedFileNameCounts, embeddedLossyPathCounts, embeddedRelaxedStemCounts, manualOverride.Paths, opts.RelaxedStemMatching, out var matchedByFileName, out var matchedByLossyPath, out var matchedByRelaxedStem, out var matchedByManualOverride))
            {
                notProperlyEmbedded.Add(item);
                continue;
            }

            if (matchedByFileName)
                usedMatchedByFileNameCount++;
            else if (matchedByLossyPath)
                usedMatchedByLossyPathCount++;
            else if (matchedByRelaxedStem)
                usedMatchedByRelaxedStemCount++;
            else if (matchedByManualOverride)
                usedMatchedByManualOverrideCount++;
        }
        notProperlyEmbedded.Sort(comparer);
        report.NotProperlyEmbeddedItemModels = notProperlyEmbedded;
        report.NotProperlyEmbeddedItemCount = notProperlyEmbedded.Count;
        report.HasProperlyEmbeddedBlocks = notProperlyEmbedded.Count == 0;

        if (!embeddedZipReadable)
        {
            AppendNote(report, "Embedded data ZIP not available; treating as empty.");
            if (!string.IsNullOrWhiteSpace(embeddedZipError))
                AppendNote(report, $"Embedded ZIP error ({embeddedZipError}).");
        }

        if (excludedExpectedClub.Count > 0)
        {
            AppendNote(report,
                $"Ignored {excludedExpectedClub.Count} expected club item(s) for missing-expected checks.");
        }

        if (usedClubItems.Count > 0)
        {
            AppendNote(report,
                $"Map uses {usedClubItems.Count} club item(s).");
            AppendNote(report,
                "Club items were excluded from missing and not-properly-embedded checks because availability is resolved by the game client.");
        }

        if (expectedMatchedByFileNameCount > 0 || usedMatchedByFileNameCount > 0)
        {
            AppendNote(report,
                $"Matched {expectedMatchedByFileNameCount} expected model(s) and {usedMatchedByFileNameCount} used model(s) by file name only (embedded ZIP path differs).");
        }

        if (expectedMatchedByLossyPathCount > 0 || usedMatchedByLossyPathCount > 0)
        {
            AppendNote(report,
                $"Matched {expectedMatchedByLossyPathCount} expected model(s) and {usedMatchedByLossyPathCount} used model(s) by lossy Unicode path normalization.");
        }

        if (opts.RelaxedStemMatching && (expectedMatchedByRelaxedStemCount > 0 || usedMatchedByRelaxedStemCount > 0))
        {
            AppendNote(report,
                $"Matched {expectedMatchedByRelaxedStemCount} expected model(s) and {usedMatchedByRelaxedStemCount} used model(s) by relaxed model stem matching.");
        }

        if (expectedMatchedByManualOverrideCount > 0 || usedMatchedByManualOverrideCount > 0)
        {
            AppendNote(report,
                $"Applied manual embedding override. Matched {expectedMatchedByManualOverrideCount} expected model(s) and {usedMatchedByManualOverrideCount} used model(s).");
        }

        if (manualOverride.Notes.Length > 0)
        {
            foreach (var manualNote in manualOverride.Notes)
                AppendNote(report, manualNote);
        }

        return report;
    }


    private static HashSet<string> BuildUsedCustomModels(CGameCtnChallenge map, StringComparer comparer)
    {
        var used = new HashSet<string>(comparer);

        if (map.AnchoredObjects is not null)
        {
            foreach (var ao in map.AnchoredObjects)
            {
                if (TryGetCustomItemPath(ao.ItemModel, out var path))
                    used.Add(path);
            }
        }

        if (map.Blocks is not null)
        {
            foreach (var block in map.Blocks)
            {
                if (TryGetCustomBlockPath(block.BlockModel, out var path))
                    used.Add(path);
            }
        }

        return used;
    }


    private static bool TryGetCustomItemPath(Ident ident, out string path)
    {
        path = CanonicalizeModelPath(ident.Id);
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (IsNadeoAuthor(ident.Author))
            return false;

        return path.Contains('/') || LooksLikeGbxModelPath(path);
    }


    private static bool TryGetCustomBlockPath(Ident ident, out string path)
    {
        var id = NormalizePath(ident.Id);
        if (string.IsNullOrWhiteSpace(id))
        {
            path = string.Empty;
            return false;
        }

        bool hasCustomSuffix = id.EndsWith("_CustomBlock", StringComparison.OrdinalIgnoreCase);
        if (hasCustomSuffix)
            id = id.Substring(0, id.Length - "_CustomBlock".Length);

        bool hasPath = id.Contains('/');
        if (!hasPath && !hasCustomSuffix)
        {
            path = string.Empty;
            return false;
        }

        if (IsNadeoAuthor(ident.Author))
        {
            path = string.Empty;
            return false;
        }

        path = CanonicalizeModelPath(BuildDisplayPathFromParts(id, ident));
        return !string.IsNullOrWhiteSpace(path);
    }


    private static bool IsNadeoAuthor(string? author)
        => string.Equals(author?.Trim(), "Nadeo", StringComparison.OrdinalIgnoreCase);


}
