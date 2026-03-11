using System.Collections.Immutable;
using System.Text.Json;

namespace Synopsis.Analysis.Model;

public enum Certainty
{
    Unresolved = 0,
    Ambiguous = 1,
    Inferred = 2,
    Exact = 3
}

public sealed record SourceLocation(string FilePath, int? Line = null, int? Column = null);

public sealed record ScanWarning(
    string Code,
    string Message,
    string? Path = null,
    Certainty Certainty = Certainty.Unresolved,
    IReadOnlyDictionary<string, string?>? Metadata = null);

public sealed record Timing(string Stage, TimeSpan Elapsed);

public sealed record ProgressEvent(
    string Stage,
    string Message,
    int? Current = null,
    int? Total = null);

public sealed record ScanStatistics(
    int RepositoryCount,
    int SolutionCount,
    int ProjectCount,
    int EndpointCount,
    int MethodCount,
    int HttpEdgeCount,
    int TableCount,
    int CrossRepoLinkCount,
    int AmbiguousEdgeCount);

public sealed record ScanInfo(
    string RootPath,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    ImmutableArray<Timing> Timings,
    IReadOnlyDictionary<string, string> Properties);

public sealed record ScanOptions(
    string RootPath,
    int MaxTraversalDepth = 64,
    int MaxPathSearchDepth = 10,
    bool IncludeGeneratedFiles = false,
    bool IncludeAmbiguousEdges = true,
    IReadOnlyList<string>? ExcludedPaths = null)
{
    public static ScanOptions For(string rootPath) =>
        new(Paths.Normalize(rootPath));
}

public sealed record FoundRepo(string Name, string RootPath);

public sealed record FoundSolution(string Name, string FullPath, string? RepositoryName, string? RepositoryPath);

public sealed record FoundProject(string Name, string FullPath, string? RepositoryName, string? RepositoryPath);

public sealed record FoundConfig(
    string Key,
    string? Value,
    string FilePath,
    string? RepositoryName,
    string? RepositoryPath);

public sealed record DiscoveryResult(
    string RootPath,
    ImmutableArray<FoundRepo> Repositories,
    ImmutableArray<FoundSolution> Solutions,
    ImmutableArray<FoundProject> Projects,
    ImmutableArray<FoundConfig> ConfigurationValues,
    ImmutableArray<ScanWarning> Warnings);

public static class ConfigReader
{
    public static IEnumerable<KeyValuePair<string, string?>> Flatten(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var document = JsonDocument.Parse(stream);
        return FlattenElement(document.RootElement, parentKey: null).ToArray();
    }

    private static IEnumerable<KeyValuePair<string, string?>> FlattenElement(JsonElement element, string? parentKey)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var key = string.IsNullOrWhiteSpace(parentKey)
                        ? property.Name
                        : $"{parentKey}:{property.Name}";

                    foreach (var nested in FlattenElement(property.Value, key))
                        yield return nested;
                }
                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    var key = $"{parentKey}[{index++}]";
                    foreach (var nested in FlattenElement(item, key))
                        yield return nested;
                }
                break;

            default:
                yield return new KeyValuePair<string, string?>(parentKey ?? string.Empty, element.ToString());
                break;
        }
    }
}
