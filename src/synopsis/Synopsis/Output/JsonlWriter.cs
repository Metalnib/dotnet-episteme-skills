using System.Text.Json;
using Synopsis.Analysis.Model;

namespace Synopsis.Output;

public static class JsonlWriter
{
    // JSONL: one compact JSON object per line
    private static readonly JsonSerializerOptions Compact = new(SynopsisJsonContext.Default.Options)
    {
        WriteIndented = false
    };

    public static async Task WriteAsync(ScanResult result, string outputPath, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await using var writer = new StreamWriter(outputPath);

        foreach (var node in result.Nodes)
        {
            ct.ThrowIfCancellationRequested();
            var nodeJson = JsonSerializer.Serialize(node, SynopsisJsonContext.Default.GraphNode);
            await writer.WriteLineAsync($"{{\"type\":\"node\",\"data\":{nodeJson}}}");
        }

        foreach (var edge in result.Edges)
        {
            ct.ThrowIfCancellationRequested();
            var edgeJson = JsonSerializer.Serialize(edge, SynopsisJsonContext.Default.GraphEdge);
            await writer.WriteLineAsync($"{{\"type\":\"edge\",\"data\":{edgeJson}}}");
        }

        var metaJson = JsonSerializer.Serialize(result.Metadata, SynopsisJsonContext.Default.ScanInfo);
        await writer.WriteLineAsync($"{{\"type\":\"metadata\",\"data\":{metaJson}}}");

        var statsJson = JsonSerializer.Serialize(result.Statistics, SynopsisJsonContext.Default.ScanStatistics);
        await writer.WriteLineAsync($"{{\"type\":\"stats\",\"data\":{statsJson}}}");

        foreach (var warning in result.Warnings)
        {
            ct.ThrowIfCancellationRequested();
            var warnJson = JsonSerializer.Serialize(warning, SynopsisJsonContext.Default.ScanWarning);
            await writer.WriteLineAsync($"{{\"type\":\"warning\",\"data\":{warnJson}}}");
        }
    }
}
