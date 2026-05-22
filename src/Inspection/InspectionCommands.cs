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
    private static readonly HttpClient InspectionHttpClient = CreateInspectionHttpClient();


    private static bool TryRunExtendedCommand(string[] args, out int exitCode)
    {
        exitCode = 0;

        if (args.Length == 0)
            return false;

        switch (args[0].Trim())
        {
            case "inspect-tmx":
                exitCode = RunInspectTmx(args);
                return true;
            case "inspect-map":
                exitCode = RunInspectMap(args);
                return true;
            case "run-suite":
                exitCode = RunInspectionSuite(args);
                return true;
            default:
                return false;
        }
    }


    private static int RunInspectTmx(string[] args)
    {
        var opts = ParseInspectTmxArgs(args);
        var jsonOptions = CreateJsonOptions(opts.Pretty);
        var manualOverrides = LoadManualEmbeddingOverrides(opts.Common.ManualOverridesPath);
        var outputDirectory = ResolveInspectionOutputDirectory(opts.OutputDirectory, $"tmx-{opts.TmxMapId}");

        var source = new InspectionSourceInfo
        {
            Kind = "tmx",
            TmxMapId = opts.TmxMapId,
            DownloadUrl = BuildTmxDownloadUrl(opts.TmxMapId),
            MapPageUrl = BuildTmxMapPageUrl(opts.TmxMapId),
            RequestedAtUtc = DateTime.UtcNow
        };

        var result = RunInspectionBundle(outputDirectory, source, opts.Common, manualOverrides, localSourcePath: null);
        var json = JsonSerializer.Serialize(result, jsonOptions);
        Console.WriteLine(json);
        return string.IsNullOrWhiteSpace(result.Report?.Error) ? 0 : 1;
    }


    private static int RunInspectMap(string[] args)
    {
        var opts = ParseInspectMapArgs(args);
        var jsonOptions = CreateJsonOptions(opts.Pretty);
        var manualOverrides = LoadManualEmbeddingOverrides(opts.Common.ManualOverridesPath);
        var defaultSlug = BuildSlugFromFileName(Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(opts.MapPath)));
        var outputDirectory = ResolveInspectionOutputDirectory(opts.OutputDirectory, defaultSlug);

        var source = new InspectionSourceInfo
        {
            Kind = "local-map",
            LocalSourcePath = Path.GetFullPath(opts.MapPath),
            RequestedAtUtc = DateTime.UtcNow
        };

        var result = RunInspectionBundle(outputDirectory, source, opts.Common, manualOverrides, opts.MapPath);
        var json = JsonSerializer.Serialize(result, jsonOptions);
        Console.WriteLine(json);
        return string.IsNullOrWhiteSpace(result.Report?.Error) ? 0 : 1;
    }


    private static int RunInspectionSuite(string[] args)
    {
        var opts = ParseRunSuiteArgs(args);
        var jsonOptions = CreateJsonOptions(opts.Pretty);
        var manualOverrides = LoadManualEmbeddingOverrides(opts.Common.ManualOverridesPath);
        var suiteFile = LoadInspectionSuiteFile(opts.SuitePath);
        var suiteOutputDirectory = ResolveSuiteOutputDirectory(opts.OutputDirectory, opts.SuitePath);
        var suiteDirectory = Path.GetDirectoryName(Path.GetFullPath(opts.SuitePath)) ?? Environment.CurrentDirectory;

        Directory.CreateDirectory(suiteOutputDirectory);

        var startedAtUtc = DateTime.UtcNow;
        var caseResults = new List<SuiteCaseResult>();
        int expectationFailureCount = 0;
        int errorCount = 0;

        for (int i = 0; i < suiteFile.Cases!.Count; i++)
        {
            var testCase = suiteFile.Cases[i];
            var caseLabel = string.IsNullOrWhiteSpace(testCase.Label)
                ? BuildDefaultSuiteCaseLabel(testCase)
                : testCase.Label!.Trim();
            var caseSlug = BuildSlugFromFileName(caseLabel);
            var caseDirectory = Path.Combine(
                suiteOutputDirectory,
                "cases",
                $"{(i + 1).ToString("D3", CultureInfo.InvariantCulture)}-{caseSlug}");

            InspectionSourceInfo source;
            string? localSourcePath;
            if (!string.IsNullOrWhiteSpace(testCase.TmxMapId))
            {
                source = new InspectionSourceInfo
                {
                    Kind = "tmx",
                    TmxMapId = testCase.TmxMapId!.Trim(),
                    DownloadUrl = BuildTmxDownloadUrl(testCase.TmxMapId!.Trim()),
                    MapPageUrl = BuildTmxMapPageUrl(testCase.TmxMapId!.Trim()),
                    RequestedAtUtc = DateTime.UtcNow
                };
                localSourcePath = null;
            }
            else
            {
                var resolvedMapPath = ResolveSuiteCaseMapPath(suiteDirectory, testCase.MapPath!);
                source = new InspectionSourceInfo
                {
                    Kind = "local-map",
                    LocalSourcePath = resolvedMapPath,
                    RequestedAtUtc = DateTime.UtcNow
                };
                localSourcePath = resolvedMapPath;
            }

            var inspection = RunInspectionBundle(caseDirectory, source, opts.Common, manualOverrides, localSourcePath);
            var failureReasons = EvaluateSuiteCase(testCase, inspection.Report);
            bool expectationMatched = failureReasons.Count == 0;

            if (!string.IsNullOrWhiteSpace(inspection.Report?.Error))
                errorCount++;

            if (!expectationMatched)
                expectationFailureCount++;

            caseResults.Add(new SuiteCaseResult
            {
                Index = i + 1,
                Label = caseLabel,
                Notes = testCase.Notes?.Where(note => !string.IsNullOrWhiteSpace(note)).Select(note => note.Trim()).ToList(),
                ExpectationMatched = expectationMatched,
                FailureReasons = failureReasons,
                Inspection = inspection
            });
        }

        var completedAtUtc = DateTime.UtcNow;
        var result = new SuiteRunResult
        {
            SuitePath = Path.GetFullPath(opts.SuitePath),
            OutputDirectory = Path.GetFullPath(suiteOutputDirectory),
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = completedAtUtc,
            CaseCount = caseResults.Count,
            ExpectationFailureCount = expectationFailureCount,
            ErrorCount = errorCount,
            Cases = caseResults
        };

        var suiteReportPath = Path.Combine(suiteOutputDirectory, "suite-report.json");
        File.WriteAllText(suiteReportPath, JsonSerializer.Serialize(result, jsonOptions));

        var suiteSummaryPath = Path.Combine(suiteOutputDirectory, "suite-summary.txt");
        File.WriteAllText(suiteSummaryPath, BuildSuiteSummary(result));

        Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
        return expectationFailureCount == 0 && errorCount == 0 ? 0 : 1;
    }


}
