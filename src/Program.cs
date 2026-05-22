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
    private static int Main(string[] args)
    {
        try
        {
            Gbx.LZO = new Lzo();
            Gbx.ZLib = new ZLib();

            if (TryRunExtendedCommand(args, out var extendedExitCode))
                return extendedExitCode;

            var opts = ParseArgs(args);
            var manualEmbeddingOverrides = LoadManualEmbeddingOverrides(opts.ManualOverridesPath);

            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = opts.Pretty,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            string json;
            bool hasFailures;
            bool inputIsDirectory = Directory.Exists(opts.InputPath);
            bool outputIsDirectory = ShouldTreatOutputAsDirectory(opts.OutputPath);

            if (inputIsDirectory)
            {
                var reports = AnalyzeDirectory(opts, jsonOptions, outputIsDirectory, manualEmbeddingOverrides);
                json = JsonSerializer.Serialize(reports, jsonOptions);
                hasFailures = reports.Any(ReportShouldFail);
            }
            else
            {
                var report = AnalyzeMap(opts.InputPath, opts, manualEmbeddingOverrides);
                json = JsonSerializer.Serialize(report, jsonOptions);
                hasFailures = ReportShouldFail(report);

                if (outputIsDirectory)
                {
                    var usedFallbackNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    WriteReportToOutputDirectory(opts.OutputPath, report, jsonOptions, usedFallbackNames);
                }
                else
                {
                    WriteJsonOutputFile(opts.OutputPath, json);
                }
            }

            Console.WriteLine(json);
            return hasFailures ? 1 : 0;
        }
        catch (ArgException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            PrintHelp();
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Fatal error: " + ex);
            return 1;
        }
    }


}
