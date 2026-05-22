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
    private static List<string> EvaluateSuiteCase(InspectionSuiteCase testCase, EmbeddedReport? report)
    {
        var failures = new List<string>();
        if (report is null)
        {
            failures.Add("No report was produced.");
            return failures;
        }

        if (!string.IsNullOrWhiteSpace(report.Error))
            failures.Add($"Analyzer error: {report.Error}");

        if (testCase.ExpectHasProperlyEmbeddedBlocks.HasValue
            && report.HasProperlyEmbeddedBlocks != testCase.ExpectHasProperlyEmbeddedBlocks.Value)
        {
            failures.Add(
                $"Expected hasProperlyEmbeddedBlocks={testCase.ExpectHasProperlyEmbeddedBlocks.Value}, got {report.HasProperlyEmbeddedBlocks}.");
        }

        if (testCase.ExpectNotProperlyEmbeddedItemCount.HasValue
            && report.NotProperlyEmbeddedItemCount != testCase.ExpectNotProperlyEmbeddedItemCount.Value)
        {
            failures.Add(
                $"Expected notProperlyEmbeddedItemCount={testCase.ExpectNotProperlyEmbeddedItemCount.Value}, got {report.NotProperlyEmbeddedItemCount}.");
        }

        if (testCase.ExpectMissingExpectedEmbeddedItemCount.HasValue
            && report.MissingExpectedEmbeddedItemCount != testCase.ExpectMissingExpectedEmbeddedItemCount.Value)
        {
            failures.Add(
                $"Expected missingExpectedEmbeddedItemCount={testCase.ExpectMissingExpectedEmbeddedItemCount.Value}, got {report.MissingExpectedEmbeddedItemCount}.");
        }

        return failures;
    }


    private static InspectionSuiteFile LoadInspectionSuiteFile(string suitePath)
    {
        if (!File.Exists(suitePath))
            throw new ArgException($"Suite file does not exist: {suitePath}");

        InspectionSuiteFile? suiteFile;
        try
        {
            var json = File.ReadAllText(suitePath);
            suiteFile = JsonSerializer.Deserialize<InspectionSuiteFile>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            throw new ArgException($"Failed to parse suite file '{suitePath}'. {ex.GetType().Name}: {ex.Message}");
        }

        if (suiteFile?.Cases is null || suiteFile.Cases.Count == 0)
            throw new ArgException($"Suite file '{suitePath}' must contain at least one case.");

        for (int i = 0; i < suiteFile.Cases.Count; i++)
        {
            var testCase = suiteFile.Cases[i];
            bool hasTmxId = !string.IsNullOrWhiteSpace(testCase.TmxMapId);
            bool hasMapPath = !string.IsNullOrWhiteSpace(testCase.MapPath);
            if (hasTmxId == hasMapPath)
            {
                throw new ArgException(
                    $"Suite file '{suitePath}' case #{i + 1} must define exactly one of 'tmxMapId' or 'mapPath'.");
            }

            if (hasTmxId && !IsAllDigits(testCase.TmxMapId!))
            {
                throw new ArgException(
                    $"Suite file '{suitePath}' case #{i + 1} has a non-numeric tmxMapId '{testCase.TmxMapId}'.");
            }
        }

        return suiteFile;
    }


}
