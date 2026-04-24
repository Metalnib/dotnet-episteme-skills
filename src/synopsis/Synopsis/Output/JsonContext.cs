using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Synopsis.Analysis.Graph;
using Synopsis.Analysis.Model;
using Synopsis.Git;

namespace Synopsis.Output;

[JsonSerializable(typeof(ScanResult))]
[JsonSerializable(typeof(ImpactGraph))]
[JsonSerializable(typeof(PathSet))]
[JsonSerializable(typeof(AmbiguityReport))]
[JsonSerializable(typeof(GraphDiff))]
[JsonSerializable(typeof(NodeChange))]
[JsonSerializable(typeof(BreakingChange))]
[JsonSerializable(typeof(BreakingDiffResult))]
[JsonSerializable(typeof(DiffStats))]
[JsonSerializable(typeof(ImmutableArray<BreakingChange>))]
[JsonSerializable(typeof(GitImpactResult))]
[JsonSerializable(typeof(ImmutableArray<NodeChange>))]
[JsonSerializable(typeof(ImmutableArray<string>))]
[JsonSerializable(typeof(GraphNode))]
[JsonSerializable(typeof(GraphEdge))]
[JsonSerializable(typeof(GraphPath))]
[JsonSerializable(typeof(ScanInfo))]
[JsonSerializable(typeof(ScanStatistics))]
[JsonSerializable(typeof(ScanWarning))]
[JsonSerializable(typeof(SourceLocation))]
[JsonSerializable(typeof(Timing))]
[JsonSerializable(typeof(ImmutableArray<GraphNode>))]
[JsonSerializable(typeof(ImmutableArray<GraphEdge>))]
[JsonSerializable(typeof(ImmutableArray<GraphPath>))]
[JsonSerializable(typeof(ImmutableArray<ScanWarning>))]
[JsonSerializable(typeof(ImmutableArray<Timing>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string?>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, string?>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    WriteIndented = true)]
internal partial class SynopsisJsonContext : JsonSerializerContext;

public static class Json
{
    public static readonly JsonSerializerOptions Options = SynopsisJsonContext.Default.Options;
}
