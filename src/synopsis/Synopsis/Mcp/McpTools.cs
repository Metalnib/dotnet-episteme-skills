using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using Synopsis.Analysis.Graph;
using Synopsis.Analysis.Model;
using Synopsis.Output;

namespace Synopsis.Mcp;

internal sealed class McpTools
{
    private readonly ScanResult _graph;
    private readonly GraphQuery _query;

    private static readonly FrozenDictionary<string, Func<McpTools, JsonElement?, JsonNode>> Handlers =
        new Dictionary<string, Func<McpTools, JsonElement?, JsonNode>>(StringComparer.Ordinal)
        {
            ["blast_radius"] = (t, p) => t.BlastRadius(p),
            ["find_paths"] = (t, p) => t.FindPaths(p),
            ["list_endpoints"] = (t, p) => t.ListEndpoints(p),
            ["list_nodes"] = (t, p) => t.ListNodes(p),
            ["node_detail"] = (t, p) => t.NodeDetail(p),
            ["db_lineage"] = (t, p) => t.DbLineage(p),
            ["cross_repo_edges"] = (t, p) => t.CrossRepoEdges(p),
            ["ambiguous_review"] = (t, p) => t.AmbiguousReview(p),
            ["scan_stats"] = (t, p) => t.ScanStats(p),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    public McpTools(ScanResult graph)
    {
        _graph = graph;
        _query = new GraphQuery(graph);
    }

    public static IReadOnlyList<McpToolDefinition> GetDefinitions() =>
    [
        Tool("blast_radius", "Get upstream or downstream impact subgraph for a symbol/node",
            """{"type":"object","properties":{"symbol":{"type":"string","description":"Node ID or display name"},"direction":{"type":"string","enum":["upstream","downstream"],"default":"downstream"},"depth":{"type":"integer","default":6}},"required":["symbol"]}"""),
        Tool("find_paths", "Find all paths between two nodes",
            """{"type":"object","properties":{"from":{"type":"string","description":"Source node ID or name"},"to":{"type":"string","description":"Target node ID or name"},"depth":{"type":"integer","default":8}},"required":["from","to"]}"""),
        Tool("list_endpoints", "List HTTP endpoints, optionally filtered",
            """{"type":"object","properties":{"project":{"type":"string"},"verb":{"type":"string"}}}"""),
        Tool("list_nodes", "List graph nodes, optionally filtered by type/project/query",
            """{"type":"object","properties":{"type":{"type":"string"},"project":{"type":"string"},"query":{"type":"string"},"limit":{"type":"integer","default":50}}}"""),
        Tool("node_detail", "Get full detail for a single node including edges",
            """{"type":"object","properties":{"id":{"type":"string","description":"Node ID or display name"}},"required":["id"]}"""),
        Tool("db_lineage", "Get EF Core data lineage: DbContext -> Entity -> Table",
            """{"type":"object","properties":{"table":{"type":"string"},"entity":{"type":"string"}}}"""),
        Tool("cross_repo_edges", "List all edges that cross repository boundaries",
            """{"type":"object","properties":{}}"""),
        Tool("ambiguous_review", "Audit unresolved and ambiguous edges",
            """{"type":"object","properties":{"limit":{"type":"integer","default":50}}}"""),
        Tool("scan_stats", "Get scan statistics and metadata",
            """{"type":"object","properties":{}}"""),
    ];

    public bool CanHandle(string toolName) => Handlers.ContainsKey(toolName);

    public JsonNode Invoke(string toolName, JsonElement? arguments) =>
        Handlers.TryGetValue(toolName, out var handler)
            ? handler(this, arguments)
            : throw new InvalidOperationException($"Unknown tool: {toolName}");

    private JsonNode BlastRadius(JsonElement? p)
    {
        var symbol = GetString(p, "symbol") ?? throw new ArgumentException("symbol is required");
        var direction = GetString(p, "direction") ?? "downstream";
        var depth = GetInt(p, "depth") ?? 6;
        var upstream = string.Equals(direction, "upstream", StringComparison.OrdinalIgnoreCase);
        var impact = _query.FindImpact(symbol, upstream, maxDepth: depth);
        return Serialize(impact, SynopsisJsonContext.Default.ImpactGraph);
    }

    private JsonNode FindPaths(JsonElement? p)
    {
        var from = GetString(p, "from") ?? throw new ArgumentException("from is required");
        var to = GetString(p, "to") ?? throw new ArgumentException("to is required");
        var depth = GetInt(p, "depth") ?? 8;
        var paths = _query.FindPaths(from, to, maxDepth: depth);
        return Serialize(paths, SynopsisJsonContext.Default.PathSet);
    }

    private JsonNode ListEndpoints(JsonElement? p)
    {
        var project = GetString(p, "project");
        var verb = GetString(p, "verb");
        var endpoints = _graph.Nodes.Where(n => n.Type == NodeType.Endpoint);
        if (!string.IsNullOrWhiteSpace(project))
            endpoints = endpoints.Where(n => n.ProjectName?.Contains(project, StringComparison.OrdinalIgnoreCase) == true);
        if (!string.IsNullOrWhiteSpace(verb))
            endpoints = endpoints.Where(n => n.Metadata.GetValueOrDefault("verb")?.Equals(verb, StringComparison.OrdinalIgnoreCase) == true);
        return SerializeArray(endpoints.ToArray());
    }

    private JsonNode ListNodes(JsonElement? p)
    {
        var type = GetString(p, "type");
        var project = GetString(p, "project");
        var query = GetString(p, "query");
        var limit = GetInt(p, "limit") ?? 50;

        var nodes = _graph.Nodes.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(type) && Enum.TryParse<NodeType>(type, ignoreCase: true, out var nt))
            nodes = nodes.Where(n => n.Type == nt);
        if (!string.IsNullOrWhiteSpace(project))
            nodes = nodes.Where(n => n.ProjectName?.Contains(project, StringComparison.OrdinalIgnoreCase) == true);
        if (!string.IsNullOrWhiteSpace(query))
            nodes = nodes.Where(n => n.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase));
        return SerializeArray(nodes.Take(limit).ToArray());
    }

    private JsonNode NodeDetail(JsonElement? p)
    {
        var id = GetString(p, "id") ?? throw new ArgumentException("id is required");
        var node = _query.ResolveNode(id);
        var outgoing = _graph.OutgoingEdges?.GetValueOrDefault(node.Id, []) ?? [];
        var incoming = _graph.IncomingEdges?.GetValueOrDefault(node.Id, []) ?? [];
        var result = new JsonObject
        {
            ["node"] = Serialize(node, SynopsisJsonContext.Default.GraphNode),
            ["outgoingEdges"] = SerializeArray(outgoing.ToArray()),
            ["incomingEdges"] = SerializeArray(incoming.ToArray()),
        };
        return result;
    }

    private JsonNode DbLineage(JsonElement? p)
    {
        var table = GetString(p, "table");
        var entity = GetString(p, "entity");

        var dbNodes = _graph.Nodes.Where(n =>
            n.Type is NodeType.DbContext or NodeType.Entity or NodeType.Table);

        if (!string.IsNullOrWhiteSpace(table))
            dbNodes = dbNodes.Where(n => n.Type == NodeType.Table
                && n.DisplayName.Contains(table, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(entity))
            dbNodes = dbNodes.Where(n => n.Type == NodeType.Entity
                && n.DisplayName.Contains(entity, StringComparison.OrdinalIgnoreCase));

        var nodeIds = dbNodes.Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var edges = _graph.Edges.Where(e =>
            e.Type is EdgeType.UsesDbContext or EdgeType.QueriesEntity or EdgeType.MapsToTable
            && (nodeIds.Contains(e.SourceId) || nodeIds.Contains(e.TargetId)));

        var result = new JsonObject
        {
            ["nodes"] = SerializeArray(dbNodes.ToArray()),
            ["edges"] = SerializeArray(edges.ToArray()),
        };
        return result;
    }

    private JsonNode CrossRepoEdges(JsonElement? _) =>
        SerializeArray(_graph.Edges.Where(e => e.Type == EdgeType.CrossesRepoBoundary).ToArray());

    private JsonNode AmbiguousReview(JsonElement? p)
    {
        var limit = GetInt(p, "limit") ?? 50;
        var report = _query.GetAmbiguityReport();
        var result = new JsonObject
        {
            ["unresolvedCount"] = report.UnresolvedEdges.Length,
            ["ambiguousCount"] = report.AmbiguousEdges.Length,
            ["unresolvedSymbolCount"] = report.UnresolvedSymbols.Length,
            ["unresolvedEdges"] = SerializeArray(report.UnresolvedEdges.Take(limit).ToArray()),
            ["ambiguousEdges"] = SerializeArray(report.AmbiguousEdges.Take(limit).ToArray()),
        };
        return result;
    }

    private JsonNode ScanStats(JsonElement? _)
    {
        var result = new JsonObject
        {
            ["statistics"] = Serialize(_graph.Statistics, SynopsisJsonContext.Default.ScanStatistics),
            ["metadata"] = Serialize(_graph.Metadata, SynopsisJsonContext.Default.ScanInfo),
            ["nodeCount"] = _graph.Nodes.Length,
            ["edgeCount"] = _graph.Edges.Length,
            ["warningCount"] = _graph.Warnings.Length,
        };
        return result;
    }

    // Helpers
    private static string? GetString(JsonElement? p, string key) =>
        p?.TryGetProperty(key, out var v) == true && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? GetInt(JsonElement? p, string key) =>
        p?.TryGetProperty(key, out var v) == true && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static JsonNode Serialize<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        JsonNode.Parse(JsonSerializer.Serialize(value, typeInfo))!;

    private static JsonArray SerializeArray(GraphNode[] nodes) =>
        new(nodes.Select(n => JsonNode.Parse(JsonSerializer.Serialize(n, SynopsisJsonContext.Default.GraphNode))).ToArray());

    private static JsonArray SerializeArray(GraphEdge[] edges) =>
        new(edges.Select(e => JsonNode.Parse(JsonSerializer.Serialize(e, SynopsisJsonContext.Default.GraphEdge))).ToArray());

    private static McpToolDefinition Tool(string name, string description, string schemaJson) =>
        new(name, description, JsonNode.Parse(schemaJson)!);
}
