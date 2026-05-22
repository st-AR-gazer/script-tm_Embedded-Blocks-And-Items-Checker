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
    private static bool ReportShouldFail(EmbeddedReport? report)
        => report is null
            || !string.IsNullOrWhiteSpace(report.Error)
            || report.MissingExpectedEmbeddedItemCount > 0
            || report.NotProperlyEmbeddedItemCount > 0;
}
