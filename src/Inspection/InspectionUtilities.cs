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
    private static JsonSerializerOptions CreateJsonOptions(bool pretty)
        => new JsonSerializerOptions
        {
            WriteIndented = pretty,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };


    private static HttpClient CreateInspectionHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("EmbeddedBlocksAndItemsChecker/0.1.1 (+TMX inspector)");
        return client;
    }


    private static string ResolveInspectionOutputDirectory(string? requestedOutputDirectory, string defaultSlug)
    {
        if (!string.IsNullOrWhiteSpace(requestedOutputDirectory))
            return requestedOutputDirectory;

        return Path.Combine(
            "inspection_runs",
            $"{defaultSlug}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}Z");
    }


    private static string ResolveSuiteOutputDirectory(string? requestedOutputDirectory, string suitePath)
    {
        if (!string.IsNullOrWhiteSpace(requestedOutputDirectory))
            return requestedOutputDirectory;

        var suiteStem = BuildSlugFromFileName(Path.GetFileNameWithoutExtension(suitePath));
        return Path.Combine(
            "inspection_suites",
            $"{suiteStem}-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}Z");
    }


    private static string BuildDownloadedMapFileName(string tmxMapId, string? suggestedName)
    {
        var cleanedName = string.IsNullOrWhiteSpace(suggestedName)
            ? string.Empty
            : Uri.UnescapeDataString(suggestedName.Trim().Trim('"'));

        if (!string.IsNullOrWhiteSpace(cleanedName))
        {
            var fileName = Path.GetFileName(cleanedName);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                if (!fileName.EndsWith(".Map.Gbx", StringComparison.OrdinalIgnoreCase))
                    fileName += ".Map.Gbx";

                return fileName;
            }
        }

        return $"tmx-{tmxMapId}.Map.Gbx";
    }


    private static string BuildTmxDownloadUrl(string tmxMapId)
        => $"https://trackmania.exchange/mapgbx/{tmxMapId}";


    private static string BuildTmxMapPageUrl(string tmxMapId)
        => $"https://trackmania.exchange/mapshow/{tmxMapId}";


    private static string ResolveSuiteCaseMapPath(string suiteDirectory, string mapPath)
        => Path.GetFullPath(Path.IsPathRooted(mapPath) ? mapPath : Path.Combine(suiteDirectory, mapPath));


    private static string BuildDefaultSuiteCaseLabel(InspectionSuiteCase testCase)
    {
        if (!string.IsNullOrWhiteSpace(testCase.TmxMapId))
            return $"tmx-{testCase.TmxMapId!.Trim()}";

        var fileName = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(testCase.MapPath));
        if (!string.IsNullOrWhiteSpace(fileName))
            return fileName;

        return "map-case";
    }


    private static string BuildSlugFromFileName(string? input)
    {
        var sanitized = SanitizeFileNamePart(input)
            .Replace(' ', '-')
            .Replace('_', '-')
            .Trim('-')
            .ToLowerInvariant();

        while (sanitized.Contains("--", StringComparison.Ordinal))
            sanitized = sanitized.Replace("--", "-", StringComparison.Ordinal);

        if (string.IsNullOrWhiteSpace(sanitized))
            sanitized = "inspection";

        return sanitized.Length <= 60
            ? sanitized
            : sanitized.Substring(0, 60).Trim('-');
    }


    private static void WriteLinesFile(string path, IEnumerable<string>? lines)
    {
        var safeLines = lines?
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Trim())
            .ToList()
            ?? new List<string>();

        File.WriteAllLines(path, safeLines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }


    private static string BuildInspectionSummary(
        string outputDirectory,
        InspectionSourceInfo source,
        EmbeddedReport report,
        EmbeddedZipArtifactSummary zipArtifacts,
        ModelArtifactSummary modelArtifacts)
    {
        var lines = new List<string>
        {
            "Map Inspection",
            $"GeneratedAtUtc: {DateTime.UtcNow:O}",
            $"OutputDirectory: {Path.GetFullPath(outputDirectory)}",
            $"SourceKind: {source.Kind}",
            $"SourceReference: {BuildSourceReference(source)}",
            $"StoredMapPath: {source.StoredMapPath}",
            $"MapUid: {report.MapUid ?? "(null)"}",
            $"MapName: {report.MapName ?? "(null)"}",
            $"HasProperlyEmbeddedBlocks: {report.HasProperlyEmbeddedBlocks}",
            $"MissingExpectedEmbeddedItemCount: {report.MissingExpectedEmbeddedItemCount}",
            $"NotProperlyEmbeddedItemCount: {report.NotProperlyEmbeddedItemCount}",
            $"EmbeddedZipEntryCount: {report.EmbeddedZipEntryCount}",
            $"UsedCustomItemCount: {report.UsedCustomItemCount}",
            $"UsedClubItemCount: {report.UsedClubItemCount}",
            $"Error: {report.Error ?? "(none)"}",
            $"EmbeddedZipArtifactsError: {zipArtifacts.Error ?? "(none)"}",
            $"ModelArtifactsError: {modelArtifacts.Error ?? "(none)"}"
        };

        if (report.Notes is not null && report.Notes.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Notes:");
            lines.AddRange(report.Notes.Select(note => "- " + note));
        }

        return string.Join(Environment.NewLine, lines);
    }


    private static string BuildSuiteSummary(SuiteRunResult result)
    {
        var lines = new List<string>
        {
            "Inspection Suite",
            $"SuitePath: {result.SuitePath}",
            $"OutputDirectory: {result.OutputDirectory}",
            $"StartedAtUtc: {result.StartedAtUtc:O}",
            $"CompletedAtUtc: {result.CompletedAtUtc:O}",
            $"CaseCount: {result.CaseCount}",
            $"ExpectationFailureCount: {result.ExpectationFailureCount}",
            $"ErrorCount: {result.ErrorCount}",
            string.Empty,
            "Cases:"
        };

        foreach (var caseResult in result.Cases ?? Enumerable.Empty<SuiteCaseResult>())
        {
            var report = caseResult.Inspection?.Report;
            lines.Add(
                $"- #{caseResult.Index} {caseResult.Label}: matched={caseResult.ExpectationMatched}, hasProperlyEmbeddedBlocks={report?.HasProperlyEmbeddedBlocks}, missingExpected={report?.MissingExpectedEmbeddedItemCount}, notProperlyEmbedded={report?.NotProperlyEmbeddedItemCount}");
            if (caseResult.FailureReasons is not null)
            {
                foreach (var failure in caseResult.FailureReasons)
                    lines.Add($"  failure: {failure}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }


    private static string BuildSourceReference(InspectionSourceInfo source)
    {
        if (!string.IsNullOrWhiteSpace(source.TmxMapId))
            return $"TMX {source.TmxMapId}";

        return source.LocalSourcePath ?? "(unknown)";
    }


    private static bool IsDirectoryZipEntry(ZipArchiveEntry entry)
        => entry.FullName.EndsWith("/", StringComparison.Ordinal)
            || entry.FullName.EndsWith("\\", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(entry.Name);


    private static string BuildUniqueExtractionRelativePath(string originalPath, int index, HashSet<string> usedRelativePaths)
    {
        var normalized = NormalizePath(originalPath);
        var fileName = Path.GetFileName(normalized);
        var safeFileName = SanitizePathSegmentForExtraction(fileName);
        if (string.IsNullOrWhiteSpace(safeFileName) || safeFileName == "_")
            safeFileName = "entry.bin";

        var stem = Path.GetFileNameWithoutExtension(safeFileName);
        var extension = Path.GetExtension(safeFileName);
        if (stem.Length > 80)
            stem = stem.Substring(0, 80).Trim();

        var candidate = $"{index:D4}__{stem}{extension}";
        var uniqueCandidate = candidate;
        int suffix = 2;
        while (!usedRelativePaths.Add(uniqueCandidate))
        {
            var duplicateStem = Path.GetFileNameWithoutExtension(candidate);
            var duplicateExt = Path.GetExtension(candidate);
            uniqueCandidate = $"{duplicateStem}-{suffix}{duplicateExt}";
            suffix++;
        }

        return uniqueCandidate;
    }


    private static string SanitizePathSegmentForExtraction(string segment)
    {
        if (segment == "." || segment == "..")
            return "_";

        var safe = SanitizeFileNamePart(segment);
        return string.IsNullOrWhiteSpace(safe) ? "_" : safe;
    }


    private static void EnsurePathStaysUnderRoot(string targetPath, string rootDirectory)
    {
        var fullTarget = Path.GetFullPath(targetPath);
        var fullRoot = Path.GetFullPath(rootDirectory);
        if (!fullRoot.EndsWith(Path.DirectorySeparatorChar))
            fullRoot += Path.DirectorySeparatorChar;

        if (!fullTarget.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refused to write outside extraction root: {targetPath}");
    }


    private static bool IsAllDigits(string value)
        => !string.IsNullOrWhiteSpace(value) && value.All(char.IsDigit);


}
