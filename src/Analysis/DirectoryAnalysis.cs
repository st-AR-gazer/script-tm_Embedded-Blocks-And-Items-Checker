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
    private static List<EmbeddedReport> AnalyzeDirectory(
        CliOptions opts,
        JsonSerializerOptions jsonOptions,
        bool outputIsDirectory,
        IReadOnlyDictionary<string, ManualEmbeddingOverride> manualEmbeddingOverrides)
    {
        var searchOption = opts.RecursiveDirectorySearch
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;

        var comparer = opts.CaseSensitive
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;

        var mapPaths = Directory.EnumerateFiles(opts.InputPath, "*", searchOption)
            .Where(IsMapGbxPath)
            .OrderBy(path => path, comparer)
            .ToList();

        var reports = new List<EmbeddedReport>(mapPaths.Count);
        var usedFallbackNames = outputIsDirectory
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : null;
        using var incrementalArrayWriter = !outputIsDirectory && !string.IsNullOrWhiteSpace(opts.OutputPath)
            ? new IncrementalJsonArrayWriter(opts.OutputPath!, jsonOptions.WriteIndented)
            : null;

        foreach (var mapPath in mapPaths)
        {
            var report = AnalyzeMap(mapPath, opts, manualEmbeddingOverrides);
            reports.Add(report);

            if (outputIsDirectory)
            {
                WriteReportToOutputDirectory(opts.OutputPath, report, jsonOptions, usedFallbackNames);
            }
            else
            {
                incrementalArrayWriter?.Append(report, jsonOptions);
            }
        }

        if (reports.Count == 0)
        {
            var noMapsReport = new EmbeddedReport
            {
                MapPath = opts.InputPath,
                MatchMode = opts.CaseSensitive ? "case-sensitive" : "case-insensitive",
                HasProperlyEmbeddedBlocks = false,
                Error = $"No .Map.Gbx files found in directory: {opts.InputPath}"
            };
            reports.Add(noMapsReport);

            if (outputIsDirectory)
            {
                WriteReportToOutputDirectory(opts.OutputPath, noMapsReport, jsonOptions, usedFallbackNames);
            }
            else
            {
                incrementalArrayWriter?.Append(noMapsReport, jsonOptions);
            }
        }

        return reports;
    }


    private sealed class IncrementalJsonArrayWriter : IDisposable
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private readonly FileStream stream;
        private readonly bool pretty;
        private bool hasAnyObject;

        public IncrementalJsonArrayWriter(string outputPath, bool pretty)
        {
            this.pretty = pretty;

            var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            stream = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
            WriteString("[\n]\n");
            stream.Flush(flushToDisk: true);
        }

        public void Append(EmbeddedReport report, JsonSerializerOptions jsonOptions)
        {
            var objectJson = JsonSerializer.Serialize(report, jsonOptions);
            if (pretty)
                objectJson = IndentByTwo(objectJson);

            stream.Position = Math.Max(0, stream.Length - 2);
            if (hasAnyObject)
                WriteString(",\n");

            WriteString(objectJson);
            WriteString("\n]\n");
            stream.SetLength(stream.Position);
            stream.Flush(flushToDisk: true);
            hasAnyObject = true;
        }

        private static string IndentByTwo(string json)
        {
            var normalized = json.Replace("\r\n", "\n", StringComparison.Ordinal);
            var lines = normalized.Split('\n');
            return string.Join("\n", lines.Select(line => "  " + line));
        }

        private void WriteString(string value)
        {
            var bytes = Utf8NoBom.GetBytes(value);
            stream.Write(bytes, 0, bytes.Length);
        }

        public void Dispose()
            => stream.Dispose();
    }


    private static bool IsMapGbxPath(string path)
        => path.EndsWith(".Map.Gbx", StringComparison.OrdinalIgnoreCase);


    private static bool ShouldTreatOutputAsDirectory(string? outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            return false;

        if (Directory.Exists(outputPath))
            return true;

        if (File.Exists(outputPath))
            return false;

        if (EndsWithDirectorySeparator(outputPath))
            return true;

        return !Path.HasExtension(outputPath);
    }


    private static bool EndsWithDirectorySeparator(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        var last = path[path.Length - 1];
        return last == Path.DirectorySeparatorChar || last == Path.AltDirectorySeparatorChar;
    }


    private static void WriteJsonOutputFile(string? outputPath, string json)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            return;

        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(outputPath, json);
    }


    private static void WriteReportToOutputDirectory(
        string? outputDirectory,
        EmbeddedReport report,
        JsonSerializerOptions jsonOptions,
        HashSet<string>? usedFallbackNames)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
            return;

        Directory.CreateDirectory(outputDirectory);

        var fileStem = BuildPerMapOutputFileStem(report, usedFallbackNames);
        var outputPath = Path.Combine(outputDirectory, fileStem + ".json");
        var json = JsonSerializer.Serialize(report, jsonOptions);

        File.WriteAllText(outputPath, json);
    }


    private static string BuildPerMapOutputFileStem(EmbeddedReport report, HashSet<string>? usedFallbackNames)
    {
        var uidStem = SanitizeFileNamePart(report.MapUid);
        if (!string.IsNullOrWhiteSpace(uidStem))
            return uidStem;

        var mapPath = report.MapPath ?? string.Empty;
        var fallback = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(mapPath));
        var fallbackStem = SanitizeFileNamePart(fallback);
        if (string.IsNullOrWhiteSpace(fallbackStem))
            fallbackStem = "unknown-map";

        if (usedFallbackNames is null)
            return fallbackStem;

        if (usedFallbackNames.Add(fallbackStem))
            return fallbackStem;

        int suffix = 2;
        while (true)
        {
            var candidate = $"{fallbackStem}-{suffix}";
            if (usedFallbackNames.Add(candidate))
                return candidate;

            suffix++;
        }
    }


}
