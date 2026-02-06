using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

using GBX.NET;
using GBX.NET.Engines.Game;
using GBX.NET.LZO;
using GBX.NET.ZLib;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var opts = ParseArgs(args);

            Gbx.LZO = new Lzo();
            Gbx.ZLib = new ZLib();

            var report = AnalyzeMap(opts);

            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = opts.Pretty,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            var json = JsonSerializer.Serialize(report, jsonOptions);

            var outputPath = opts.OutputPath;
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllText(outputPath, json);
            }

            Console.WriteLine(json);
            return string.IsNullOrWhiteSpace(report.Error) ? 0 : 1;
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

    private static EmbeddedReport AnalyzeMap(CliOptions opts)
    {
        var report = new EmbeddedReport
        {
            MapPath = opts.MapPath,
            MatchMode = opts.CaseSensitive ? "case-sensitive" : "case-insensitive"
        };

        if (!File.Exists(opts.MapPath))
        {
            report.Error = $"Map file does not exist: {opts.MapPath}";
            report.HasProperlyEmbeddedBlocks = false;
            return report;
        }

        if (!LooksLikeGbx(opts.MapPath))
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

            map = Gbx.ParseNode<CGameCtnChallenge>(opts.MapPath, settings);
        }
        catch (Exception ex)
        {
            report.Error = "Failed to parse map GBX.";
            report.Note = $"{ex.GetType().Name}: {ex.Message}";
            report.HasProperlyEmbeddedBlocks = false;
            return report;
        }

        report.MapUid = map.MapUid;
        if (opts.IncludeMapName)
            report.MapName = map.MapName;

        var caseSensitive = opts.CaseSensitive;
        var comparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

        var expected = map.ExpectedEmbeddedItemModels;
        report.ExpectedEmbeddedItemCount = expected?.Count ?? 0;

        var expectedDisplay = expected is null
            ? new List<string>()
            : expected.Select(BuildDisplayPath).ToList();

        if (opts.IncludeExpectedList)
            report.ExpectedEmbeddedItemModels = expectedDisplay;

        var embeddedSet = new HashSet<string>(comparer);
        bool embeddedZipReadable = false;
        string? embeddedZipError = null;
        try
        {
            using var zip = map.OpenReadEmbeddedZipData();
            embeddedZipReadable = true;
            report.EmbeddedZipEntryCount = zip.Entries.Count;

            foreach (var entry in zip.Entries)
            {
                var normalized = NormalizePath(entry.FullName);
                var trimmed = TrimItemsBlocksPrefix(normalized);
                if (!string.IsNullOrWhiteSpace(trimmed))
                    embeddedSet.Add(trimmed);
            }
        }
        catch (Exception ex)
        {
            embeddedZipReadable = false;
            embeddedZipError = $"{ex.GetType().Name}: {ex.Message}";
            report.EmbeddedZipEntryCount = 0;
        }

        var missingExpected = new List<string>();
        if (expected is null || expected.Count == 0)
        {
            AppendNote(report, "No expected embedded items found in the map data.");
        }
        else
        {
            foreach (var ident in expected)
            {
                var expectedPath = NormalizeExpectedPath(ident);
                if (string.IsNullOrWhiteSpace(expectedPath))
                    continue;

                if (!embeddedSet.Contains(expectedPath))
                    missingExpected.Add(BuildDisplayPath(ident));
            }
        }

        report.MissingExpectedEmbeddedItemModels = missingExpected;
        report.MissingExpectedEmbeddedItemCount = missingExpected.Count;

        var usedCustom = BuildUsedCustomModels(map, comparer);
        report.UsedCustomItemCount = usedCustom.Count;

        var notProperlyEmbedded = usedCustom.Where(item => !embeddedSet.Contains(item)).ToList();
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
        path = NormalizePath(ident.Id);
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (!path.Contains('/'))
            return false;

        if (IsNadeoAuthor(ident.Author))
            return false;

        path = TrimItemsBlocksPrefix(path);
        return !string.IsNullOrWhiteSpace(path);
    }

    private static bool TryGetCustomBlockPath(Ident ident, out string path)
    {
        path = NormalizePath(ident.Id);
        if (string.IsNullOrWhiteSpace(path))
            return false;

        bool hasCustomSuffix = path.EndsWith("_CustomBlock", StringComparison.OrdinalIgnoreCase);
        if (hasCustomSuffix)
            path = path.Substring(0, path.Length - "_CustomBlock".Length);

        bool hasPath = path.Contains('/');
        if (!hasPath && !hasCustomSuffix)
            return false;

        if (IsNadeoAuthor(ident.Author))
            return false;

        path = TrimItemsBlocksPrefix(path);
        return !string.IsNullOrWhiteSpace(path);
    }

    private static bool IsNadeoAuthor(string? author)
        => string.Equals(author?.Trim(), "Nadeo", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeExpectedPath(Ident ident)
        => TrimItemsBlocksPrefix(NormalizePath(ident.Id));

    private static string BuildDisplayPath(Ident ident)
    {
        var id = NormalizePath(ident.Id);
        if (string.IsNullOrWhiteSpace(id))
            return string.Empty;

        if (id.Contains('/'))
            return id;

        var author = NormalizePath(ident.Author);
        if (!string.IsNullOrWhiteSpace(author))
            return $"{author}/{id}";

        var collection = NormalizePath(ident.Collection.String);
        if (!string.IsNullOrWhiteSpace(collection))
            return $"{collection}/{id}";

        return id;
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var p = path.Replace('\\', '/').Trim();

        while (p.StartsWith("./", StringComparison.Ordinal))
            p = p.Substring(2);

        while (p.Contains("//", StringComparison.Ordinal))
            p = p.Replace("//", "/", StringComparison.Ordinal);

        return p.TrimStart('/');
    }

    private static string TrimItemsBlocksPrefix(string path)
    {
        var p = NormalizePath(path);

        const string itemsPrefix = "Items/";
        if (p.StartsWith(itemsPrefix, StringComparison.OrdinalIgnoreCase))
            return p.Substring(itemsPrefix.Length);

        const string blocksPrefix = "Blocks/";
        if (p.StartsWith(blocksPrefix, StringComparison.OrdinalIgnoreCase))
            return p.Substring(blocksPrefix.Length);

        return p;
    }

    private static bool LooksLikeGbx(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> b = stackalloc byte[3];
            var read = fs.Read(b);
            return read == 3 && b[0] == 0x47 && b[1] == 0x42 && b[2] == 0x58;
        }
        catch
        {
            return false;
        }
    }

    private static void AppendNote(EmbeddedReport report, string note)
    {
        if (string.IsNullOrWhiteSpace(note))
            return;

        if (string.IsNullOrWhiteSpace(report.Note))
            report.Note = note;
        else
            report.Note = report.Note + " " + note;
    }

    private static CliOptions ParseArgs(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase))
            throw new ArgException("No arguments provided.");

        if (args.Length < 2)
            throw new ArgException("You must specify both <mapFile> and <outputJson>.");

        var mapPath = args[0];
        var outputPath = args[1];

        bool pretty = false;
        bool includeExpectedList = true;
        bool includeMapName = true;
        bool caseSensitive = false;

        for (int i = 2; i < args.Length; i++)
        {
            var a = args[i];
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
                case "--help":
                    break;
                default:
                    throw new ArgException($"Unknown flag: {a}");
            }
        }

        return new CliOptions(mapPath, outputPath, pretty, includeExpectedList, includeMapName, caseSensitive);
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
@"Usage:
  tm_Embedded_Blocks_Items_Checker <mapFile> <outputJson> [--pretty] [--no-expected-list] [--no-map-name] [--case-sensitive|--case-insensitive]

Flags:
  --pretty              Pretty-print JSON output
  --no-expected-list    Omit the full expected embedded list from JSON
  --no-map-name         Omit map name from JSON
  --case-sensitive      Match ZIP entry paths with exact casing
  --case-insensitive    Match ZIP entry paths ignoring case (default)

Notes:
  - Expected embedded items are matched against the embedded ZIP entries.
  - 'NotProperlyEmbeddedItemModels' is computed from used custom items/blocks that are missing in the embedded ZIP.
  - ZIP entry paths are compared without the leading Items/ or Blocks/ prefix."
        );
    }

    private sealed record CliOptions(
        string MapPath,
        string OutputPath,
        bool Pretty,
        bool IncludeExpectedList,
        bool IncludeMapName,
        bool CaseSensitive
    );

    private sealed class EmbeddedReport
    {
        public string? MapUid { get; set; }
        public string? MapName { get; set; }
        public string? MapPath { get; set; }
        public string? MatchMode { get; set; }

        public bool HasProperlyEmbeddedBlocks { get; set; }

        public int ExpectedEmbeddedItemCount { get; set; }
        public int MissingExpectedEmbeddedItemCount { get; set; }
        public int EmbeddedZipEntryCount { get; set; }
        public int UsedCustomItemCount { get; set; }
        public int NotProperlyEmbeddedItemCount { get; set; }

        public List<string>? ExpectedEmbeddedItemModels { get; set; }
        public List<string>? MissingExpectedEmbeddedItemModels { get; set; }
        public List<string>? NotProperlyEmbeddedItemModels { get; set; }

        public string? Error { get; set; }
        public string? Note { get; set; }
    }

    private sealed class ArgException : Exception
    {
        public ArgException(string message) : base(message) { }
    }

}
