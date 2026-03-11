using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Synopsis.Analysis.Model;

public enum NodeType
{
    Workspace,
    Repository,
    Solution,
    Project,
    Service,
    Controller,
    Endpoint,
    Method,
    Interface,
    Implementation,
    HttpClient,
    ExternalService,
    ExternalEndpoint,
    DbContext,
    Entity,
    Table,
    ConfigurationKey
}

public enum EdgeType
{
    Contains,
    Defines,
    Exposes,
    Implements,
    Injects,
    Calls,
    UsesHttpClient,
    CallsHttp,
    ResolvesToService,
    UsesDbContext,
    QueriesEntity,
    MapsToTable,
    DependsOn,
    CrossesRepoBoundary,
    Ambiguous
}

public sealed record GraphNode(
    string Id,
    NodeType Type,
    string DisplayName,
    SourceLocation? Location,
    string? RepositoryName,
    string? ProjectName,
    Certainty Certainty,
    IReadOnlyDictionary<string, string?> Metadata);

public sealed record GraphEdge(
    string Id,
    string SourceId,
    string TargetId,
    EdgeType Type,
    string DisplayName,
    SourceLocation? Location,
    string? RepositoryName,
    string? ProjectName,
    Certainty Certainty,
    IReadOnlyDictionary<string, string?> Metadata);

public sealed record ScanResult
{
    public required ImmutableArray<GraphNode> Nodes { get; init; }
    public required ImmutableArray<GraphEdge> Edges { get; init; }
    public required ScanInfo Metadata { get; init; }
    public required ImmutableArray<ScanWarning> Warnings { get; init; }
    public required ImmutableArray<ScanWarning> Unresolved { get; init; }
    public required ScanStatistics Statistics { get; init; }

    // Pre-built adjacency - not serialized, rebuilt on load via WithAdjacency()
    [JsonIgnore] public FrozenDictionary<string, GraphNode>? NodesById { get; init; }
    [JsonIgnore] public FrozenDictionary<string, ImmutableArray<GraphEdge>>? OutgoingEdges { get; init; }
    [JsonIgnore] public FrozenDictionary<string, ImmutableArray<GraphEdge>>? IncomingEdges { get; init; }

    public ScanResult WithAdjacency()
    {
        var nodesById = Nodes.ToFrozenDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);

        var outgoing = Edges
            .GroupBy(e => e.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(g => g.Key, g => g.ToImmutableArray(), StringComparer.OrdinalIgnoreCase);

        var incoming = Edges
            .GroupBy(e => e.TargetId, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(g => g.Key, g => g.ToImmutableArray(), StringComparer.OrdinalIgnoreCase);

        return this with
        {
            NodesById = nodesById,
            OutgoingEdges = outgoing,
            IncomingEdges = incoming
        };
    }
}

public sealed record ImpactGraph(
    GraphNode FocusNode,
    ImmutableArray<GraphNode> Nodes,
    ImmutableArray<GraphEdge> Edges);

public sealed record GraphPath(
    ImmutableArray<GraphNode> Nodes,
    ImmutableArray<GraphEdge> Edges);

public sealed record PathSet(
    GraphNode From,
    GraphNode To,
    ImmutableArray<GraphPath> Paths);

public sealed record AmbiguityReport(
    ImmutableArray<GraphEdge> UnresolvedEdges,
    ImmutableArray<GraphEdge> AmbiguousEdges,
    ImmutableArray<ScanWarning> UnresolvedSymbols);

public static class NodeId
{
    public static string From(string prefix, params ReadOnlySpan<string?> parts)
    {
        var payload = string.Join('|',
            parts.ToArray().Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(payload), hash);
        return $"{prefix}:{Convert.ToHexStringLower(hash[..8])}";
    }
}
