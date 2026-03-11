using System.Diagnostics;
using System.Text.Json;
using Synopsis.Analysis.Graph;
using Synopsis.Analysis.Model;

namespace Synopsis.Output;

/// <summary>
/// Writes structured JSON to stdout when --json flag is used.
/// All human-readable output goes to stderr.
/// </summary>
public static class JsonOutput
{
    public static void WriteImpact(string command, ImpactGraph impact, Stopwatch timer) =>
        WriteEnvelope(command, JsonSerializer.SerializeToUtf8Bytes(impact, SynopsisJsonContext.Default.ImpactGraph), timer);

    public static void WritePaths(string command, PathSet paths, Stopwatch timer) =>
        WriteEnvelope(command, JsonSerializer.SerializeToUtf8Bytes(paths, SynopsisJsonContext.Default.PathSet), timer);

    public static void WriteAmbiguity(string command, AmbiguityReport report, Stopwatch timer) =>
        WriteEnvelope(command, JsonSerializer.SerializeToUtf8Bytes(report, SynopsisJsonContext.Default.AmbiguityReport), timer);

    public static void WriteDiff(string command, GraphDiff diff, Stopwatch timer) =>
        WriteEnvelope(command, JsonSerializer.SerializeToUtf8Bytes(diff, SynopsisJsonContext.Default.GraphDiff), timer);

    public static void WriteNode(string command, GraphNode node, Stopwatch timer) =>
        WriteEnvelope(command, JsonSerializer.SerializeToUtf8Bytes(node, SynopsisJsonContext.Default.GraphNode), timer);

    public static void WriteScanSummary(string command, string output, ScanStatistics stats, ScanInfo info, Stopwatch timer)
    {
        var resultJson = $$"""{"output":"{{output}}","statistics":{{JsonSerializer.Serialize(stats, SynopsisJsonContext.Default.ScanStatistics)}},"metadata":{{JsonSerializer.Serialize(info, SynopsisJsonContext.Default.ScanInfo)}}}""";
        WriteEnvelopeRaw(command, resultJson, timer);
    }

    public static void WriteError(string command, string error, Stopwatch timer)
    {
        Console.WriteLine($$"""{"command":"{{Escape(command)}}","ok":false,"error":"{{Escape(error)}}","ms":{{timer.ElapsedMilliseconds}}}""");
    }

    private static void WriteEnvelope(string command, byte[] resultUtf8, Stopwatch timer)
    {
        var resultJson = System.Text.Encoding.UTF8.GetString(resultUtf8);
        WriteEnvelopeRaw(command, resultJson, timer);
    }

    private static void WriteEnvelopeRaw(string command, string resultJson, Stopwatch timer)
    {
        Console.WriteLine($$"""{"command":"{{Escape(command)}}","ok":true,"result":{{resultJson}},"ms":{{timer.ElapsedMilliseconds}}}""");
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
