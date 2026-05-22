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
    private sealed record InspectTmxOptions(
        string TmxMapId,
        string? OutputDirectory,
        InspectionCommonOptions Common)
    {
        public bool Pretty => Common.Pretty;
    }


    private sealed record InspectMapOptions(
        string MapPath,
        string? OutputDirectory,
        InspectionCommonOptions Common)
    {
        public bool Pretty => Common.Pretty;
    }


    private sealed record RunSuiteOptions(
        string SuitePath,
        string? OutputDirectory,
        InspectionCommonOptions Common)
    {
        public bool Pretty => Common.Pretty;
    }


    private sealed record InspectionCommonOptions(
        bool Pretty,
        bool IncludeExpectedList,
        bool IncludeMapName,
        bool CaseSensitive,
        bool DumpZipEntries,
        bool RelaxedStemMatching,
        string? ManualOverridesPath,
        bool ExtractZip);


    private sealed class InspectionRunResult
    {
        public string? OutputDirectory { get; set; }
        public InspectionSourceInfo? Source { get; set; }
        public InspectionArtifactPaths? Artifacts { get; set; }
        public ModelArtifactSummary? ModelArtifacts { get; set; }
        public EmbeddedZipArtifactSummary? EmbeddedZipArtifacts { get; set; }
        public EmbeddedReport? Report { get; set; }
    }


    private sealed class InspectionSourceInfo
    {
        public string? Kind { get; set; }
        public string? TmxMapId { get; set; }
        public string? LocalSourcePath { get; set; }
        public string? DownloadUrl { get; set; }
        public string? MapPageUrl { get; set; }
        public string? StoredMapPath { get; set; }
        public DateTime RequestedAtUtc { get; set; }
    }


    private sealed class InspectionArtifactPaths
    {
        public string? InputMapPath { get; set; }
        public string? ReportPath { get; set; }
        public string? SummaryPath { get; set; }
        public string? SourcePath { get; set; }
        public string? NotesPath { get; set; }
        public string? ListsDirectory { get; set; }
        public string? EmbeddedZipDirectory { get; set; }
    }


    private sealed class ModelArtifactSummary
    {
        public string? ListsDirectory { get; set; }
        public string? ExpectedEmbeddedItemModelsPath { get; set; }
        public string? MissingExpectedEmbeddedItemModelsPath { get; set; }
        public string? ExcludedClubExpectedItemModelsPath { get; set; }
        public string? UsedCustomItemModelsPath { get; set; }
        public string? UsedClubItemModelsPath { get; set; }
        public string? NotProperlyEmbeddedItemModelsPath { get; set; }
        public int ExpectedEmbeddedItemCount { get; set; }
        public int UsedCustomItemCount { get; set; }
        public string? Error { get; set; }
    }


    private sealed class EmbeddedZipArtifactSummary
    {
        public string? Directory { get; set; }
        public string? EntriesPath { get; set; }
        public string? ManifestPath { get; set; }
        public string? ExtractedDirectory { get; set; }
        public int EntryCount { get; set; }
        public int ExtractedFileCount { get; set; }
        public string? Error { get; set; }
    }


    private sealed class EmbeddedZipEntryArtifact
    {
        public int Index { get; set; }
        public string? OriginalPath { get; set; }
        public string? CanonicalModelPath { get; set; }
        public string? ExtractedRelativePath { get; set; }
        public long CompressedLength { get; set; }
        public long Length { get; set; }
    }


    private sealed class InspectionSuiteFile
    {
        public List<InspectionSuiteCase>? Cases { get; set; }
    }


    private sealed class InspectionSuiteCase
    {
        public string? Label { get; set; }
        public string? TmxMapId { get; set; }
        public string? MapPath { get; set; }
        public bool? ExpectHasProperlyEmbeddedBlocks { get; set; }
        public int? ExpectNotProperlyEmbeddedItemCount { get; set; }
        public int? ExpectMissingExpectedEmbeddedItemCount { get; set; }
        public string[]? Notes { get; set; }
    }


    private sealed class SuiteRunResult
    {
        public string? SuitePath { get; set; }
        public string? OutputDirectory { get; set; }
        public DateTime StartedAtUtc { get; set; }
        public DateTime CompletedAtUtc { get; set; }
        public int CaseCount { get; set; }
        public int ExpectationFailureCount { get; set; }
        public int ErrorCount { get; set; }
        public List<SuiteCaseResult>? Cases { get; set; }
    }


    private sealed class SuiteCaseResult
    {
        public int Index { get; set; }
        public string? Label { get; set; }
        public List<string>? Notes { get; set; }
        public bool ExpectationMatched { get; set; }
        public List<string>? FailureReasons { get; set; }
        public InspectionRunResult? Inspection { get; set; }
    }

}
