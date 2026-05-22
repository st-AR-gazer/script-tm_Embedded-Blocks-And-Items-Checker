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
            p = StripAbsoluteTrackmaniaPrefix(p);
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
            && (path[1] == ':' || path[1] == '_')
            && path[2] == '/';


    private static string StripAbsoluteTrackmaniaPrefix(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        string[] rootMarkers =
        {
            "/Trackmania/Items/",
            "/Trackmania/Blocks/",
            "/Trackmania2020/Items/",
            "/Trackmania2020/Blocks/"
        };

        int bestRootIndex = int.MaxValue;
        string? matchedRoot = null;
        foreach (var marker in rootMarkers)
        {
            int idx = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && idx < bestRootIndex)
            {
                bestRootIndex = idx;
                matchedRoot = marker;
            }
        }

        if (matchedRoot is not null)
            return path.Substring(bestRootIndex + matchedRoot.Length);

        const string itemsMarker = "/Items/";
        const string blocksMarker = "/Blocks/";
        int idxItems = path.IndexOf(itemsMarker, StringComparison.OrdinalIgnoreCase);
        int idxBlocks = path.IndexOf(blocksMarker, StringComparison.OrdinalIgnoreCase);

        if (idxItems < 0 && idxBlocks < 0)
            return path;

        if (idxItems >= 0 && (idxBlocks < 0 || idxItems <= idxBlocks))
            return path.Substring(idxItems + itemsMarker.Length);

        return path.Substring(idxBlocks + blocksMarker.Length);
    }


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
        Dictionary<string, int> embeddedLossyPathCounts,
        Dictionary<string, int> embeddedRelaxedStemCounts,
        HashSet<string> manualOverridePaths,
        bool allowRelaxedStemMatching,
        out bool matchedByFileName,
        out bool matchedByLossyPath,
        out bool matchedByRelaxedStem,
        out bool matchedByManualOverride)
    {
        matchedByFileName = false;
        matchedByLossyPath = false;
        matchedByRelaxedStem = false;
        matchedByManualOverride = false;

        var key = CanonicalizeModelPath(modelPath);
        if (string.IsNullOrWhiteSpace(key))
            return false;

        if (embeddedSet.Contains(key))
            return true;

        if (manualOverridePaths.Contains(key))
        {
            matchedByManualOverride = true;
            return true;
        }

        var lossyKey = BuildLossyComparablePath(key);
        if (!string.IsNullOrWhiteSpace(lossyKey)
            && embeddedLossyPathCounts.TryGetValue(lossyKey, out var lossyPathCount)
            && lossyPathCount == 1)
        {
            matchedByLossyPath = true;
            return true;
        }

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

        if (allowRelaxedStemMatching)
        {
            var relaxedStem = BuildRelaxedModelStem(key);
            if (!string.IsNullOrWhiteSpace(relaxedStem)
                && embeddedRelaxedStemCounts.TryGetValue(relaxedStem, out var relaxedCount)
                && relaxedCount == 1)
            {
                matchedByRelaxedStem = true;
                return true;
            }
        }

        return false;
    }


    private static string BuildLossyComparablePath(string path)
    {
        var canonical = CanonicalizeModelPath(path);
        if (string.IsNullOrWhiteSpace(canonical))
            return string.Empty;

        var sb = new StringBuilder(canonical.Length);
        foreach (var c in canonical)
        {
            if (c <= 127)
                sb.Append(c);
        }

        return NormalizePath(sb.ToString());
    }


    private static string BuildRelaxedModelStem(string path)
    {
        var canonical = CanonicalizeModelPath(path);
        if (string.IsNullOrWhiteSpace(canonical))
            return string.Empty;

        var fileName = GetModelFileName(canonical);
        if (string.IsNullOrWhiteSpace(fileName))
            return string.Empty;

        if (fileName.EndsWith(".gbx", StringComparison.OrdinalIgnoreCase))
            fileName = fileName.Substring(0, fileName.Length - 4);

        const string itemSuffix = ".item";
        const string blockSuffix = ".block";
        if (fileName.EndsWith(itemSuffix, StringComparison.OrdinalIgnoreCase))
            fileName = fileName.Substring(0, fileName.Length - itemSuffix.Length);
        else if (fileName.EndsWith(blockSuffix, StringComparison.OrdinalIgnoreCase))
            fileName = fileName.Substring(0, fileName.Length - blockSuffix.Length);

        var sb = new StringBuilder(fileName.Length);
        for (int i = 0; i < fileName.Length; i++)
        {
            var c = fileName[i];
            if (char.IsLetter(c))
            {
                if ((c == 'x' || c == 'X')
                    && i > 0
                    && i + 1 < fileName.Length
                    && char.IsDigit(fileName[i - 1])
                    && char.IsDigit(fileName[i + 1]))
                {
                    continue;
                }

                sb.Append(char.ToLowerInvariant(c));
            }
        }

        var stem = sb.ToString();
        if (stem.Length < 8)
            return string.Empty;

        return stem;
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

        report.Notes ??= new List<string>();
        report.Notes.Add(note.Trim());
    }


}
