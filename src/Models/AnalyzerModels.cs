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
    private sealed class ManualEmbeddingOverridesFile
    {
        public List<ManualEmbeddingOverrideEntry>? Overrides { get; set; }
    }


    private sealed class ManualEmbeddingOverrideEntry
    {
        public string? MapUid { get; set; }
        public string? Note { get; set; }
        public string[]? Notes { get; set; }
        public string[]? TreatAsEmbeddedModelPaths { get; set; }
    }


    private sealed record ManualEmbeddingOverride(
        string[] Notes,
        string[] TreatAsEmbeddedModelPaths
    );


    private sealed record ManualEmbeddingOverrideState(
        HashSet<string> Paths,
        string[] Notes
    )
    {
        public static ManualEmbeddingOverrideState Empty(StringComparer comparer)
            => new ManualEmbeddingOverrideState(new HashSet<string>(comparer), Array.Empty<string>());
    }


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
        public List<string>? Notes { get; set; }
    }


}
