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
    private static InspectionRunResult RunInspectionBundle(
        string outputDirectory,
        InspectionSourceInfo source,
        InspectionCommonOptions common,
        IReadOnlyDictionary<string, ManualEmbeddingOverride> manualOverrides,
        string? localSourcePath)
    {
        Directory.CreateDirectory(outputDirectory);

        var inputDirectory = Path.Combine(outputDirectory, "input");
        Directory.CreateDirectory(inputDirectory);

        var storedMapPath = PrepareInspectionInputMap(source, localSourcePath, inputDirectory);
        source.StoredMapPath = storedMapPath;

        var cliOptions = new CliOptions(
            InputPath: storedMapPath,
            OutputPath: null,
            Pretty: common.Pretty,
            IncludeExpectedList: common.IncludeExpectedList,
            IncludeMapName: common.IncludeMapName,
            CaseSensitive: common.CaseSensitive,
            RecursiveDirectorySearch: false,
            DumpZipEntries: common.DumpZipEntries,
            RelaxedStemMatching: common.RelaxedStemMatching,
            ManualOverridesPath: common.ManualOverridesPath);

        var report = AnalyzeMap(storedMapPath, cliOptions, manualOverrides);
        var jsonOptions = CreateJsonOptions(common.Pretty);

        var reportPath = Path.Combine(outputDirectory, "report.json");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, jsonOptions));

        var modelArtifactSummary = WriteModelArtifacts(storedMapPath, outputDirectory, common.CaseSensitive);
        WriteReportListArtifacts(report, Path.Combine(outputDirectory, "lists"), modelArtifactSummary);
        var zipArtifactSummary = WriteEmbeddedZipArtifacts(storedMapPath, outputDirectory, common.ExtractZip);

        var sourcePath = Path.Combine(outputDirectory, "source.json");
        File.WriteAllText(sourcePath, JsonSerializer.Serialize(source, jsonOptions));

        var notesPath = Path.Combine(outputDirectory, "notes.txt");
        WriteLinesFile(notesPath, report.Notes);

        var summaryPath = Path.Combine(outputDirectory, "summary.txt");
        File.WriteAllText(summaryPath, BuildInspectionSummary(outputDirectory, source, report, zipArtifactSummary, modelArtifactSummary));

        return new InspectionRunResult
        {
            OutputDirectory = Path.GetFullPath(outputDirectory),
            Source = source,
            Report = report,
            Artifacts = new InspectionArtifactPaths
            {
                InputMapPath = Path.GetFullPath(storedMapPath),
                ReportPath = Path.GetFullPath(reportPath),
                SummaryPath = Path.GetFullPath(summaryPath),
                SourcePath = Path.GetFullPath(sourcePath),
                NotesPath = Path.GetFullPath(notesPath),
                ListsDirectory = Path.GetFullPath(Path.Combine(outputDirectory, "lists")),
                EmbeddedZipDirectory = Path.GetFullPath(Path.Combine(outputDirectory, "embedded-zip"))
            },
            ModelArtifacts = modelArtifactSummary,
            EmbeddedZipArtifacts = zipArtifactSummary
        };
    }


    private static string PrepareInspectionInputMap(InspectionSourceInfo source, string? localSourcePath, string inputDirectory)
    {
        if (string.Equals(source.Kind, "tmx", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(DownloadTmxMap(source.TmxMapId!, inputDirectory));

        if (string.IsNullOrWhiteSpace(localSourcePath))
            throw new ArgException("Local inspection source path is missing.");

        if (!File.Exists(localSourcePath))
            throw new ArgException($"Map file does not exist: {localSourcePath}");

        var fileName = Path.GetFileName(localSourcePath);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "map.Map.Gbx";

        var destinationPath = Path.Combine(inputDirectory, fileName);
        File.Copy(localSourcePath, destinationPath, overwrite: true);
        return Path.GetFullPath(destinationPath);
    }


    private static string DownloadTmxMap(string tmxMapId, string inputDirectory)
    {
        if (!IsAllDigits(tmxMapId))
            throw new ArgException($"TMX map id must be numeric. Received: {tmxMapId}");

        Directory.CreateDirectory(inputDirectory);

        var downloadUrl = BuildTmxDownloadUrl(tmxMapId);
        using var response = InspectionHttpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
            throw new ArgException($"TMX download failed for map id {tmxMapId}. HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");

        var suggestedName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName;

        var fileName = BuildDownloadedMapFileName(tmxMapId, suggestedName);
        var destinationPath = Path.Combine(inputDirectory, fileName);

        using var responseStream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
        using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        responseStream.CopyTo(fileStream);
        fileStream.Flush(flushToDisk: true);

        return destinationPath;
    }


}
