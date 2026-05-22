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
    private static Dictionary<string, ManualEmbeddingOverride> LoadManualEmbeddingOverrides(string? overridesPath)
    {
        var result = new Dictionary<string, ManualEmbeddingOverride>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(overridesPath))
            return result;

        if (!File.Exists(overridesPath))
            throw new ArgException($"Manual overrides file does not exist: {overridesPath}");

        ManualEmbeddingOverridesFile? file;
        try
        {
            var json = File.ReadAllText(overridesPath);
            file = JsonSerializer.Deserialize<ManualEmbeddingOverridesFile>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            throw new ArgException($"Failed to parse manual overrides file '{overridesPath}'. {ex.GetType().Name}: {ex.Message}");
        }

        if (file?.Overrides is null || file.Overrides.Count == 0)
            return result;

        foreach (var entry in file.Overrides)
        {
            var mapUid = entry.MapUid?.Trim();
            if (string.IsNullOrWhiteSpace(mapUid))
                throw new ArgException($"Manual overrides file '{overridesPath}' contains an entry with missing 'mapUid'.");

            if (result.ContainsKey(mapUid))
                throw new ArgException($"Manual overrides file '{overridesPath}' contains duplicate mapUid '{mapUid}'.");

            var modelPaths = entry.TreatAsEmbeddedModelPaths?
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray()
                ?? Array.Empty<string>();

            var notes = new List<string>();
            if (!string.IsNullOrWhiteSpace(entry.Note))
                notes.Add(entry.Note.Trim());

            if (entry.Notes is not null)
            {
                foreach (var note in entry.Notes)
                {
                    if (!string.IsNullOrWhiteSpace(note))
                        notes.Add(note.Trim());
                }
            }

            result[mapUid] = new ManualEmbeddingOverride(
                Notes: notes.ToArray(),
                TreatAsEmbeddedModelPaths: modelPaths);
        }

        return result;
    }


    private static ManualEmbeddingOverrideState GetManualEmbeddingOverrideForMap(
        string? mapUid,
        StringComparer comparer,
        IReadOnlyDictionary<string, ManualEmbeddingOverride> manualEmbeddingOverrides)
    {
        if (string.IsNullOrWhiteSpace(mapUid))
            return ManualEmbeddingOverrideState.Empty(comparer);

        if (!manualEmbeddingOverrides.TryGetValue(mapUid, out var configured))
            return ManualEmbeddingOverrideState.Empty(comparer);

        var paths = new HashSet<string>(comparer);
        foreach (var modelPath in configured.TreatAsEmbeddedModelPaths)
        {
            var canonical = CanonicalizeModelPath(modelPath);
            if (!string.IsNullOrWhiteSpace(canonical))
                paths.Add(canonical);
        }

        return new ManualEmbeddingOverrideState(paths, configured.Notes ?? Array.Empty<string>());
    }


}
