using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
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

            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = opts.Pretty,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            string json;
            bool hasErrors;
            bool inputIsDirectory = Directory.Exists(opts.InputPath);
            bool outputIsDirectory = ShouldTreatOutputAsDirectory(opts.OutputPath);

            if (inputIsDirectory)
            {
                var reports = AnalyzeDirectory(opts, jsonOptions, outputIsDirectory);
                json = JsonSerializer.Serialize(reports, jsonOptions);
                hasErrors = reports.Any(r => !string.IsNullOrWhiteSpace(r.Error));
            }
            else
            {
                var report = AnalyzeMap(opts.InputPath, opts);
                json = JsonSerializer.Serialize(report, jsonOptions);
                hasErrors = !string.IsNullOrWhiteSpace(report.Error);

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
            return hasErrors ? 1 : 0;
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

    private static List<EmbeddedReport> AnalyzeDirectory(
        CliOptions opts,
        JsonSerializerOptions jsonOptions,
        bool outputIsDirectory)
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
            ? new IncrementalJsonArrayWriter(opts.OutputPath, jsonOptions.WriteIndented)
            : null;

        foreach (var mapPath in mapPaths)
        {
            var report = AnalyzeMap(mapPath, opts);
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

    private static bool ShouldTreatOutputAsDirectory(string outputPath)
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

    private static void WriteJsonOutputFile(string outputPath, string json)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            return;

        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(outputPath, json);
    }

    private static void WriteReportToOutputDirectory(
        string outputDirectory,
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

    private static string SanitizeFileNamePart(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return string.Empty;

        var invalid = Path.GetInvalidFileNameChars();
        var safeChars = input
            .Trim()
            .Select(c => invalid.Contains(c) ? '-' : c)
            .ToArray();

        return new string(safeChars).Trim().Trim('.');
    }

    private static EmbeddedReport AnalyzeMap(string mapPath, CliOptions opts)
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
        var embeddedFileNameCounts = new Dictionary<string, int>(comparer);
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

                if (!IsPresentInEmbeddedZip(expectedPath, embeddedSet, embeddedFileNameCounts, out var matchedByFileName))
                    missingExpected.Add(BuildDisplayPath(ident));
                else if (matchedByFileName)
                    expectedMatchedByFileNameCount++;
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
        foreach (var item in usedCustom)
        {
            if (IsAnyClubPath(item))
                continue;

            if (!IsPresentInEmbeddedZip(item, embeddedSet, embeddedFileNameCounts, out var matchedByFileName))
            {
                notProperlyEmbedded.Add(item);
                continue;
            }

            if (matchedByFileName)
                usedMatchedByFileNameCount++;
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

        if (!path.Contains('/'))
            return false;

        if (IsNadeoAuthor(ident.Author))
            return false;

        return !string.IsNullOrWhiteSpace(path);
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

    private static bool IsAnyClubPath(string path)
    {
        var p = CanonicalizeModelPath(path);
        return p.StartsWith("club:", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("ClubItems/", StringComparison.OrdinalIgnoreCase);
    }

    private static string CanonicalizeExpectedPath(Ident ident)
        => CanonicalizeModelPath(BuildDisplayPath(ident));

    private static string BuildDisplayPath(Ident ident)
        => BuildDisplayPathFromParts(ident.Id, ident);

    private static string BuildDisplayPathFromParts(string id, Ident ident)
    {
        var idNorm = NormalizePath(id);
        if (string.IsNullOrWhiteSpace(idNorm))
            return string.Empty;

        if (idNorm.Contains('/'))
            return idNorm;

        var author = NormalizePath(ident.Author);
        if (!string.IsNullOrWhiteSpace(author))
            return $"{author}/{idNorm}";

        var collection = NormalizePath(ident.Collection.String);
        if (!string.IsNullOrWhiteSpace(collection))
            return $"{collection}/{idNorm}";

        return idNorm;
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

    private static string CanonicalizeModelPath(string? path)
    {
        var p = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(p))
            return string.Empty;

        if (LooksLikeWindowsAbsolutePath(p))
        {
            const string slashItems = "/Items/";
            const string slashBlocks = "/Blocks/";
            int idxItems = p.LastIndexOf(slashItems, StringComparison.OrdinalIgnoreCase);
            int idxBlocks = p.LastIndexOf(slashBlocks, StringComparison.OrdinalIgnoreCase);
            if (idxItems >= 0 || idxBlocks >= 0)
            {
                if (idxItems > idxBlocks)
                    p = p.Substring(idxItems + slashItems.Length);
                else
                    p = p.Substring(idxBlocks + slashBlocks.Length);
            }
        }

        while (true)
        {
            bool stripped = false;

            const string gameDataPrefix = "GameData/";
            if (p.StartsWith(gameDataPrefix, StringComparison.OrdinalIgnoreCase))
            {
                p = p.Substring(gameDataPrefix.Length);
                stripped = true;
            }

            const string userDataPrefix = "UserData/";
            if (p.StartsWith(userDataPrefix, StringComparison.OrdinalIgnoreCase))
            {
                p = p.Substring(userDataPrefix.Length);
                stripped = true;
            }

            const string dataPrefix = "Data/";
            if (p.StartsWith(dataPrefix, StringComparison.OrdinalIgnoreCase))
            {
                p = p.Substring(dataPrefix.Length);
                stripped = true;
            }

            const string contentPrefix = "Content/";
            if (p.StartsWith(contentPrefix, StringComparison.OrdinalIgnoreCase))
            {
                p = p.Substring(contentPrefix.Length);
                stripped = true;
            }

            const string itemsPrefix = "Items/";
            if (p.StartsWith(itemsPrefix, StringComparison.OrdinalIgnoreCase))
            {
                p = p.Substring(itemsPrefix.Length);
                stripped = true;
            }

            const string blocksPrefix = "Blocks/";
            if (p.StartsWith(blocksPrefix, StringComparison.OrdinalIgnoreCase))
            {
                p = p.Substring(blocksPrefix.Length);
                stripped = true;
            }

            if (!stripped)
                break;
        }

        const string customBlockSuffix = "_CustomBlock";
        if (p.EndsWith(customBlockSuffix, StringComparison.OrdinalIgnoreCase))
            p = p.Substring(0, p.Length - customBlockSuffix.Length);

        return p;
    }

    private static bool LooksLikeWindowsAbsolutePath(string path)
        => path.Length >= 3
            && char.IsLetter(path[0])
            && path[1] == ':'
            && path[2] == '/';

    private static bool LooksLikeGbxModelPath(string path)
        => path.EndsWith(".gbx", StringComparison.OrdinalIgnoreCase);

    private static string GetModelFileName(string modelPath)
    {
        var p = NormalizePath(modelPath);
        if (string.IsNullOrWhiteSpace(p))
            return string.Empty;

        int lastSlash = p.LastIndexOf('/');
        return lastSlash >= 0 ? p.Substring(lastSlash + 1) : p;
    }

    private static bool IsPresentInEmbeddedZip(
        string modelPath,
        HashSet<string> embeddedSet,
        Dictionary<string, int> embeddedFileNameCounts,
        out bool matchedByFileName)
    {
        matchedByFileName = false;

        var key = CanonicalizeModelPath(modelPath);
        if (string.IsNullOrWhiteSpace(key))
            return false;

        if (embeddedSet.Contains(key))
            return true;

        if (!LooksLikeGbxModelPath(key))
            return false;

        var fileName = GetModelFileName(key);
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        if (embeddedFileNameCounts.TryGetValue(fileName, out var count) && count == 1)
        {
            matchedByFileName = true;
            return true;
        }

        return false;
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
        if (args.Length == 0)
            throw new ArgException("No arguments provided.");

        if (args.Contains("--help", StringComparer.OrdinalIgnoreCase))
            throw new ArgException("Help requested.");

        if (args.Length < 2)
            throw new ArgException("You must specify both <inputPath> and <outputPath>.");

        var inputPath = args[0];
        var outputPath = args[1];

        bool pretty = false;
        bool includeExpectedList = true;
        bool includeMapName = true;
        bool caseSensitive = false;
        bool recursiveDirectorySearch = false;
        bool dumpZipEntries = false;

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
                case "--recursive":
                    recursiveDirectorySearch = true;
                    break;
                case "--dump-zip":
                    dumpZipEntries = true;
                    break;
                case "--help":
                    break;
                default:
                    throw new ArgException($"Unknown flag: {a}");
            }
        }

        return new CliOptions(inputPath, outputPath, pretty, includeExpectedList, includeMapName, caseSensitive, recursiveDirectorySearch, dumpZipEntries);
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
@"Usage:
  tm_Embedded_Blocks_Items_Checker <inputPath> <outputPath> [--pretty] [--no-expected-list] [--no-map-name] [--case-sensitive|--case-insensitive] [--recursive] [--dump-zip]

Flags:
  --pretty              Pretty-print JSON output
  --no-expected-list    Omit the full expected embedded list from JSON
  --no-map-name         Omit map name from JSON
  --case-sensitive      Match ZIP entry paths with exact casing
  --case-insensitive    Match ZIP entry paths ignoring case (default)
  --recursive           When inputPath is a directory, scan subdirectories
  --dump-zip            Print embedded ZIP entry names to stderr (debug)

Notes:
  - Expected embedded items are matched against the embedded ZIP entries.
  - 'NotProperlyEmbeddedItemModels' is computed from used custom items/blocks that are missing in the embedded ZIP.
  - ZIP entry paths are compared without the leading Items/ or Blocks/ prefix.
  - Paths starting with club: are excluded from missing and not-properly-embedded checks and reported as warnings.
  - outputPath can be a JSON file path or a directory path.
  - outputPath is treated as a directory when it exists as a directory, ends with a slash, or has no file extension.
  - If outputPath is a directory, one file per map is written using mapUid as file name.
  - If inputPath is a directory and outputPath is a JSON file, objects are appended to a live JSON array after each map."
        );
    }

    private sealed record CliOptions(
        string InputPath,
        string OutputPath,
        bool Pretty,
        bool IncludeExpectedList,
        bool IncludeMapName,
        bool CaseSensitive,
        bool RecursiveDirectorySearch,
        bool DumpZipEntries
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
        public int ExcludedClubExpectedItemCount { get; set; }
        public int EmbeddedZipEntryCount { get; set; }
        public int UsedCustomItemCount { get; set; }
        public int UsedClubItemCount { get; set; }
        public int NotProperlyEmbeddedItemCount { get; set; }

        public List<string>? ExpectedEmbeddedItemModels { get; set; }
        public List<string>? MissingExpectedEmbeddedItemModels { get; set; }
        public List<string>? ExcludedClubExpectedItemModels { get; set; }
        public List<string>? UsedClubItemModels { get; set; }
        public List<string>? NotProperlyEmbeddedItemModels { get; set; }

        public string? Error { get; set; }
        public string? Note { get; set; }
    }

    private sealed class ArgException : Exception
    {
        public ArgException(string message) : base(message) { }
    }

}
