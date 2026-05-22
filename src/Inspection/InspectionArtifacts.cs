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
    private static ModelArtifactSummary WriteModelArtifacts(string mapPath, string outputDirectory, bool caseSensitive)
    {
        var listsDirectory = Path.Combine(outputDirectory, "lists");
        Directory.CreateDirectory(listsDirectory);

        if (!TryLoadMapForInspection(mapPath, out var map, out var error))
        {
            var errorPath = Path.Combine(listsDirectory, "error.txt");
            WriteLinesFile(errorPath, new[] { error ?? "Unknown map read error." });
            return new ModelArtifactSummary
            {
                ListsDirectory = Path.GetFullPath(listsDirectory),
                Error = error
            };
        }

        var comparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var expectedModels = map!.ExpectedEmbeddedItemModels is null
            ? new List<string>()
            : map.ExpectedEmbeddedItemModels.Select(BuildDisplayPath).OrderBy(x => x, comparer).ToList();
        var usedCustomModels = BuildUsedCustomModels(map, comparer).OrderBy(x => x, comparer).ToList();

        var expectedPath = Path.Combine(listsDirectory, "expectedEmbeddedItemModels.txt");
        var usedCustomPath = Path.Combine(listsDirectory, "usedCustomItemModels.txt");

        WriteLinesFile(expectedPath, expectedModels);
        WriteLinesFile(usedCustomPath, usedCustomModels);

        return new ModelArtifactSummary
        {
            ListsDirectory = Path.GetFullPath(listsDirectory),
            ExpectedEmbeddedItemModelsPath = Path.GetFullPath(expectedPath),
            UsedCustomItemModelsPath = Path.GetFullPath(usedCustomPath),
            ExpectedEmbeddedItemCount = expectedModels.Count,
            UsedCustomItemCount = usedCustomModels.Count
        };
    }


    private static void WriteReportListArtifacts(EmbeddedReport report, string listsDirectory, ModelArtifactSummary summary)
    {
        Directory.CreateDirectory(listsDirectory);

        var missingExpectedPath = Path.Combine(listsDirectory, "missingExpectedEmbeddedItemModels.txt");
        var excludedClubExpectedPath = Path.Combine(listsDirectory, "excludedClubExpectedItemModels.txt");
        var usedClubItemsPath = Path.Combine(listsDirectory, "usedClubItemModels.txt");
        var notProperlyEmbeddedPath = Path.Combine(listsDirectory, "notProperlyEmbeddedItemModels.txt");

        WriteLinesFile(missingExpectedPath, report.MissingExpectedEmbeddedItemModels);
        WriteLinesFile(excludedClubExpectedPath, report.ExcludedClubExpectedItemModels);
        WriteLinesFile(usedClubItemsPath, report.UsedClubItemModels);
        WriteLinesFile(notProperlyEmbeddedPath, report.NotProperlyEmbeddedItemModels);

        summary.MissingExpectedEmbeddedItemModelsPath = Path.GetFullPath(missingExpectedPath);
        summary.ExcludedClubExpectedItemModelsPath = Path.GetFullPath(excludedClubExpectedPath);
        summary.UsedClubItemModelsPath = Path.GetFullPath(usedClubItemsPath);
        summary.NotProperlyEmbeddedItemModelsPath = Path.GetFullPath(notProperlyEmbeddedPath);
    }


    private static EmbeddedZipArtifactSummary WriteEmbeddedZipArtifacts(string mapPath, string outputDirectory, bool extractZip)
    {
        var zipDirectory = Path.Combine(outputDirectory, "embedded-zip");
        var extractedDirectory = Path.Combine(zipDirectory, "extracted");
        var entriesPath = Path.Combine(zipDirectory, "entries.txt");
        var manifestPath = Path.Combine(zipDirectory, "manifest.json");

        Directory.CreateDirectory(zipDirectory);
        if (extractZip)
            Directory.CreateDirectory(extractedDirectory);

        if (!TryLoadMapForInspection(mapPath, out var map, out var mapLoadError))
        {
            WriteLinesFile(entriesPath, new[] { mapLoadError ?? "Unknown map read error." });
            return new EmbeddedZipArtifactSummary
            {
                Directory = Path.GetFullPath(zipDirectory),
                EntriesPath = Path.GetFullPath(entriesPath),
                ManifestPath = Path.GetFullPath(manifestPath),
                ExtractedDirectory = extractZip ? Path.GetFullPath(extractedDirectory) : null,
                Error = mapLoadError
            };
        }

        try
        {
            using var zip = map!.OpenReadEmbeddedZipData();
            var entryManifest = new List<EmbeddedZipEntryArtifact>(zip.Entries.Count);
            var lines = new List<string>(zip.Entries.Count);
            var usedRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int extractedCount = 0;

            for (int i = 0; i < zip.Entries.Count; i++)
            {
                var entry = zip.Entries[i];
                var isDirectory = IsDirectoryZipEntry(entry);
                var canonical = CanonicalizeModelPath(entry.FullName);
                string? extractedRelativePath = null;

                if (extractZip && !isDirectory)
                {
                    extractedRelativePath = BuildUniqueExtractionRelativePath(entry.FullName, i + 1, usedRelativePaths);
                    var destinationPath = Path.Combine(extractedDirectory, extractedRelativePath);
                    EnsurePathStaysUnderRoot(destinationPath, extractedDirectory);

                    var destinationDir = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrWhiteSpace(destinationDir))
                        Directory.CreateDirectory(destinationDir);

                    using var entryStream = entry.Open();
                    using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                    entryStream.CopyTo(fileStream);
                    extractedCount++;
                }

                lines.Add($"{entry.FullName} => {canonical}");
                entryManifest.Add(new EmbeddedZipEntryArtifact
                {
                    Index = i + 1,
                    OriginalPath = entry.FullName,
                    CanonicalModelPath = canonical,
                    ExtractedRelativePath = extractedRelativePath,
                    CompressedLength = entry.CompressedLength,
                    Length = entry.Length
                });
            }

            WriteLinesFile(entriesPath, lines);
            File.WriteAllText(
                manifestPath,
                JsonSerializer.Serialize(entryManifest, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                }));

            return new EmbeddedZipArtifactSummary
            {
                Directory = Path.GetFullPath(zipDirectory),
                EntriesPath = Path.GetFullPath(entriesPath),
                ManifestPath = Path.GetFullPath(manifestPath),
                ExtractedDirectory = extractZip ? Path.GetFullPath(extractedDirectory) : null,
                EntryCount = zip.Entries.Count,
                ExtractedFileCount = extractedCount
            };
        }
        catch (Exception ex)
        {
            var error = $"{ex.GetType().Name}: {ex.Message}";
            WriteLinesFile(entriesPath, new[] { error });
            return new EmbeddedZipArtifactSummary
            {
                Directory = Path.GetFullPath(zipDirectory),
                EntriesPath = Path.GetFullPath(entriesPath),
                ManifestPath = Path.GetFullPath(manifestPath),
                ExtractedDirectory = extractZip ? Path.GetFullPath(extractedDirectory) : null,
                Error = error
            };
        }
    }


    private static bool TryLoadMapForInspection(string mapPath, out CGameCtnChallenge? map, out string? error)
    {
        map = null;
        error = null;

        if (!File.Exists(mapPath))
        {
            error = $"Map file does not exist: {mapPath}";
            return false;
        }

        if (!LooksLikeGbx(mapPath))
        {
            error = "Not a GBX file (missing GBX magic header).";
            return false;
        }

        try
        {
            var settings = new GbxReadSettings
            {
                IgnoreExceptionsInBody = true,
                SafeSkippableChunks = true
            };

            map = Gbx.ParseNode<CGameCtnChallenge>(mapPath, settings);
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }


}
