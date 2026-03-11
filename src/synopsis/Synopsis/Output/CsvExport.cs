using System.Text;
using Synopsis.Analysis.Model;

namespace Synopsis.Output;

public static class CsvExport
{
    public static async Task SaveAsync(ScanResult result, string outputFolder, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputFolder);

        await File.WriteAllTextAsync(
            Path.Combine(outputFolder, "nodes.csv"), BuildNodesCsv(result), ct);
        await File.WriteAllTextAsync(
            Path.Combine(outputFolder, "edges.csv"), BuildEdgesCsv(result), ct);
    }

    private static string BuildNodesCsv(ScanResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("id,type,displayName,repositoryName,projectName,certainty");
        foreach (var node in result.Nodes)
        {
            sb.AppendLine(string.Join(',',
                Escape(node.Id), Escape(node.Type.ToString()), Escape(node.DisplayName),
                Escape(node.RepositoryName), Escape(node.ProjectName), Escape(node.Certainty.ToString())));
        }
        return sb.ToString();
    }

    private static string BuildEdgesCsv(ScanResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("id,sourceId,targetId,type,displayName,repositoryName,projectName,certainty");
        foreach (var edge in result.Edges)
        {
            sb.AppendLine(string.Join(',',
                Escape(edge.Id), Escape(edge.SourceId), Escape(edge.TargetId),
                Escape(edge.Type.ToString()), Escape(edge.DisplayName),
                Escape(edge.RepositoryName), Escape(edge.ProjectName), Escape(edge.Certainty.ToString())));
        }
        return sb.ToString();
    }

    private static string Escape(string? value) =>
        $"\"{(value ?? string.Empty).Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
