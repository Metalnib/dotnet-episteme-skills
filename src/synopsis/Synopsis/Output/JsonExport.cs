using System.Text.Json;
using Synopsis.Analysis.Model;

namespace Synopsis.Output;

public static class JsonExport
{
    public static async Task SaveAsync(ScanResult result, string outputPath, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await using var stream = File.Create(outputPath);
        await JsonSerializer.SerializeAsync(stream, result, SynopsisJsonContext.Default.ScanResult, ct);
    }

    public static async Task<ScanResult> LoadAsync(string inputPath, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(inputPath);
        var result = await JsonSerializer.DeserializeAsync(stream, SynopsisJsonContext.Default.ScanResult, ct);
        return result?.WithAdjacency()
            ?? throw new InvalidOperationException($"Could not deserialize graph from '{inputPath}'.");
    }
}
