using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Synopsis.Analysis;
using Synopsis.Analysis.Graph;
using Synopsis.Analysis.Model;
using Synopsis.Output;

namespace Synopsis.Mcp;

internal sealed class McpTools
{
    private readonly CombinedGraph _combined;
    private readonly WorkspaceScanner _scanner;
    private readonly string? _workspaceRoot;

    // Fresh snapshot per call — _combined.Current is a Volatile.Read, so
    // readers never observe a half-rebuilt graph. Creating a GraphQuery is
    // cheap (just holds the reference); no point caching.
    private ScanResult Graph => _combined.Current;
    private GraphQuery Query() => new(_combined.Current);

    // Async dispatch: cheap reads return a completed Task so the read path
    // stays synchronous in practice, while mutating tools (reindex_*) can
    // truly await the scan without a sync-over-async GetResult() blocking
    // the handler thread for minutes.
    private static readonly FrozenDictionary<string, Func<McpTools, JsonElement?, CancellationToken, Task<JsonNode>>> Handlers =
        new Dictionary<string, Func<McpTools, JsonElement?, CancellationToken, Task<JsonNode>>>(StringComparer.Ordinal)
        {
            ["blast_radius"] = (t, p, _) => Task.FromResult(t.BlastRadius(p)),
            ["find_paths"] = (t, p, _) => Task.FromResult(t.FindPaths(p)),
            ["list_endpoints"] = (t, p, _) => Task.FromResult(t.ListEndpoints(p)),
            ["list_nodes"] = (t, p, _) => Task.FromResult(t.ListNodes(p)),
            ["node_detail"] = (t, p, _) => Task.FromResult(t.NodeDetail(p)),
            ["db_lineage"] = (t, p, _) => Task.FromResult(t.DbLineage(p)),
            ["cross_repo_edges"] = (t, p, _) => Task.FromResult(t.CrossRepoEdges(p)),
            ["ambiguous_review"] = (t, p, _) => Task.FromResult(t.AmbiguousReview(p)),
            ["scan_stats"] = (t, p, _) => Task.FromResult(t.ScanStats(p)),
            ["breaking_diff"] = (_, p, _) => Task.FromResult(BreakingDiff(p)),
            ["list_repositories"] = (t, _, _) => Task.FromResult(t.ListRepositories()),
            ["reindex_repository"] = (t, p, ct) => t.ReindexRepositoryAsync(p, ct),
            ["reindex_all"] = (t, _, ct) => t.ReindexAllAsync(ct),
            ["endpoint_callers"] = (t, p, _) => Task.FromResult(t.EndpointCallers(p)),
            ["package_dependents"] = (t, p, _) => Task.FromResult(t.PackageDependents(p)),
            ["table_entry_points"] = (t, p, _) => Task.FromResult(t.TableEntryPoints(p)),
            ["repo_dependency_matrix"] = (t, _, _) => Task.FromResult(t.RepoDependencyMatrix()),
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <param name="workspaceRoot">
    /// Optional sandbox root for <c>reindex_repository</c>. When set, the
    /// tool only accepts paths already registered in <see cref="CombinedGraph.KnownRepositories"/>
    /// or under this root. When null, only already-known repositories can be
    /// reindexed — protects against an MCP client pointing the scanner at
    /// arbitrary filesystem paths.
    /// </param>
    public McpTools(CombinedGraph combined, WorkspaceScanner scanner, string? workspaceRoot = null)
    {
        _combined = combined;
        _scanner = scanner;
        _workspaceRoot = workspaceRoot is null ? null : Path.GetFullPath(workspaceRoot);
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
        Tool("breaking_diff", "Classify breaking changes between two graph snapshots. Emits typed kinds (NugetVersionBump, EndpointRouteChange, EndpointVerbChange, ApiSignatureChange, TableRename) with severity, certainty, and affected node IDs.",
            """{"type":"object","properties":{"before":{"type":"string","description":"Path to the baseline graph.json"},"after":{"type":"string","description":"Path to the head graph.json"}},"required":["before","after"]}"""),
        Tool("endpoint_callers",
            "Find all callers of an HTTP endpoint across repos. Returns matched Endpoint nodes and ExternalEndpoint nodes whose route and verb match, with resolution certainty and resolved target IDs where available.",
            """{"type":"object","properties":{"route":{"type":"string","description":"Route path to match (partial), e.g. /api/orders"},"verb":{"type":"string","description":"HTTP verb filter, e.g. GET"}},"required":["route"]}"""),
        Tool("package_dependents",
            "Find all repos and projects that depend on a NuGet package. Returns matched Package nodes and each dependent project with its version. Use version for an exact-match filter.",
            """{"type":"object","properties":{"packageId":{"type":"string","description":"NuGet package ID (partial match), e.g. PrimeLabs.Contracts"},"version":{"type":"string","description":"Exact version to filter by (optional)"}},"required":["packageId"]}"""),
        Tool("table_entry_points",
            "Find HTTP endpoints that eventually read or write a database table. Traces upstream through EF Core lineage (MapsToTable, QueriesEntity, UsesDbContext) and call edges to surface entry-point Endpoint nodes.",
            """{"type":"object","properties":{"table":{"type":"string","description":"Table name (partial match), e.g. Orders"},"maxDepth":{"type":"integer","default":8,"description":"BFS depth cap (default 8)"}},"required":["table"]}"""),
        Tool("repo_dependency_matrix",
            "Show HTTP call dependency counts between repositories. Returns outbound call totals per repo and resolved cross-repo dependencies (pairs where both source and target repo are known).",
            """{"type":"object","properties":{}}"""),
        Tool("list_repositories", "List repositories tracked by the combined graph with their last-scanned timestamp and node/edge counts.",
            """{"type":"object","properties":{}}"""),
        Tool("reindex_repository", "Rescan one repository and atomically replace it in the combined graph. Scan invocations are serialized — concurrent calls queue and execute one at a time. The optional `ref` is recorded as metadata only; the actual file state comes from the filesystem.",
            """{"type":"object","properties":{"path":{"type":"string","description":"Absolute path to the repository root"},"ref":{"type":"string","description":"Optional git ref tag for metadata (commit SHA, branch name)"}},"required":["path"]}"""),
        Tool("reindex_all", "Rescan every tracked repository sequentially. Slow; use reindex_repository for single-repo updates.",
            """{"type":"object","properties":{}}"""),
    ];

    public bool CanHandle(string toolName) => Handlers.ContainsKey(toolName);

    public Task<JsonNode> InvokeAsync(string toolName, JsonElement? arguments, CancellationToken ct) =>
        Handlers.TryGetValue(toolName, out var handler)
            ? handler(this, arguments, ct)
            : throw new InvalidOperationException($"Unknown tool: {toolName}");

    private JsonNode BlastRadius(JsonElement? p)
    {
        var symbol = GetString(p, "symbol") ?? throw new ArgumentException("symbol is required");
        var direction = GetString(p, "direction") ?? "downstream";
        var depth = GetInt(p, "depth") ?? 6;
        var upstream = string.Equals(direction, "upstream", StringComparison.OrdinalIgnoreCase);
        var impact = Query().FindImpact(symbol, upstream, maxDepth: depth);
        return Serialize(impact, SynopsisJsonContext.Default.ImpactGraph);
    }

    private JsonNode FindPaths(JsonElement? p)
    {
        var from = GetString(p, "from") ?? throw new ArgumentException("from is required");
        var to = GetString(p, "to") ?? throw new ArgumentException("to is required");
        var depth = GetInt(p, "depth") ?? 8;
        var paths = Query().FindPaths(from, to, maxDepth: depth);
        return Serialize(paths, SynopsisJsonContext.Default.PathSet);
    }

    private JsonNode ListEndpoints(JsonElement? p)
    {
        var project = GetString(p, "project");
        var verb = GetString(p, "verb");
        var endpoints = Graph.Nodes.Where(n => n.Type == NodeType.Endpoint);
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

        var nodes = Graph.Nodes.AsEnumerable();
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
        // Capture one snapshot; every subsequent read uses it. A concurrent
        // reindex that swaps _current between the Volatile.Reads would
        // otherwise leave node and edges from different generations.
        var graph = Graph;
        var node = new GraphQuery(graph).ResolveNode(id);
        var outgoing = graph.OutgoingEdges?.GetValueOrDefault(node.Id, []) ?? [];
        var incoming = graph.IncomingEdges?.GetValueOrDefault(node.Id, []) ?? [];
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
        var graph = Graph;

        var dbNodes = graph.Nodes.Where(n =>
            n.Type is NodeType.DbContext or NodeType.Entity or NodeType.Table);

        if (!string.IsNullOrWhiteSpace(table))
            dbNodes = dbNodes.Where(n => n.Type == NodeType.Table
                && n.DisplayName.Contains(table, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(entity))
            dbNodes = dbNodes.Where(n => n.Type == NodeType.Entity
                && n.DisplayName.Contains(entity, StringComparison.OrdinalIgnoreCase));

        var nodeIds = dbNodes.Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var edges = graph.Edges.Where(e =>
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
        SerializeArray(Graph.Edges.Where(e => e.Type == EdgeType.CrossesRepoBoundary).ToArray());

    private JsonNode AmbiguousReview(JsonElement? p)
    {
        var limit = GetInt(p, "limit") ?? 50;
        var report = Query().GetAmbiguityReport();
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
        var graph = Graph;
        var result = new JsonObject
        {
            ["statistics"] = Serialize(graph.Statistics, SynopsisJsonContext.Default.ScanStatistics),
            ["metadata"] = Serialize(graph.Metadata, SynopsisJsonContext.Default.ScanInfo),
            ["nodeCount"] = graph.Nodes.Length,
            ["edgeCount"] = graph.Edges.Length,
            ["warningCount"] = graph.Warnings.Length,
        };
        return result;
    }


    private JsonNode ListRepositories()
    {
        var repos = new JsonArray();
        foreach (var record in _combined.Repositories)
        {
            JsonNode entry = new JsonObject
            {
                ["path"] = record.RepoPath,
                ["lastScannedAtUtc"] = record.LastScannedAtUtc.ToString("O"),
                ["nodeCount"] = record.NodeCount,
                ["edgeCount"] = record.EdgeCount,
            };
            repos.Add(entry);
        }
        return new JsonObject { ["repositories"] = repos };
    }

    private async Task<JsonNode> ReindexRepositoryAsync(JsonElement? p, CancellationToken ct)
    {
        var rawPath = GetString(p, "path") ?? throw new ArgumentException("path is required");
        var gitRef = GetString(p, "ref");  // metadata only — graph state comes from the filesystem

        var resolvedPath = ResolveAllowedPath(rawPath);

        // Truly async: no sync-over-async GetResult() blocking the handler
        // thread. ct is the per-connection token linked to server shutdown,
        // so a client drop or Ctrl+C cancels the scan promptly.
        var record = await _combined.ReindexAsync(resolvedPath, _scanner, options: null, ct: ct);

        var response = new JsonObject
        {
            ["ok"] = true,
            ["repoPath"] = record.RepoPath,
            ["lastScannedAtUtc"] = record.LastScannedAtUtc.ToString("O"),
            ["nodeCount"] = record.NodeCount,
            ["edgeCount"] = record.EdgeCount,
        };
        if (gitRef is not null)
            response["ref"] = gitRef;  // omit when absent rather than emit "ref": null
        return response;
    }

    private async Task<JsonNode> ReindexAllAsync(CancellationToken ct)
    {
        // Sequential to keep Roslyn workspace memory usage bounded. For an
        // N-repo fleet this is O(N) long — callers should prefer
        // reindex_repository when they know what changed.
        var results = new JsonArray();
        foreach (var path in _combined.KnownRepositories)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var record = await _combined.ReindexAsync(path, _scanner, options: null, ct: ct);
                JsonNode entry = new JsonObject
                {
                    ["ok"] = true,
                    ["repoPath"] = record.RepoPath,
                    ["lastScannedAtUtc"] = record.LastScannedAtUtc.ToString("O"),
                    ["nodeCount"] = record.NodeCount,
                    ["edgeCount"] = record.EdgeCount,
                };
                results.Add(entry);
            }
            catch (OperationCanceledException)
            {
                // Shutdown in progress — bail out instead of logging per-repo
                // "cancelled" lines.
                throw;
            }
            catch (Exception ex)
            {
                JsonNode entry = new JsonObject
                {
                    ["ok"] = false,
                    ["repoPath"] = path,
                    ["error"] = ex.Message,
                };
                results.Add(entry);
            }
        }
        return new JsonObject { ["repositories"] = results };
    }

    /// <summary>
    /// Resolve and validate a client-supplied repo path. Accepts:
    /// already-known repositories (identified by normalised path prefix)
    /// and, when a workspace root was configured, paths under that root.
    /// Rejects everything else — without this guard, an MCP client could
    /// point the scanner at any filesystem location and exfiltrate its
    /// graph via the other query tools.
    /// Symlinks are resolved for the workspace-root sandbox check so a
    /// link that escapes the root (e.g. /safe/evil → /outside) is caught.
    /// Known-repo lookup uses both the symlink-resolved and non-resolved
    /// form so repos registered via a path whose components are symlinks
    /// (e.g. /tmp on macOS → /private/tmp) are still matched correctly.
    /// </summary>
    private string ResolveAllowedPath(string rawPath)
    {
        // real = fully resolved (no symlinks); normalized = lexically clean only.
        var real = Paths.Normalize(Paths.ResolveReal(rawPath));
        var normalized = Paths.Normalize(Path.GetFullPath(rawPath));

        if (_combined.KnownRepositories.Any(k =>
                string.Equals(k, normalized, Paths.FileSystemComparison) ||
                string.Equals(k, real, Paths.FileSystemComparison)))
            return real;

        if (_workspaceRoot is not null && Paths.IsUnder(real, _workspaceRoot))
            return real;

        throw new InvalidOperationException(
            $"Path '{rawPath}' is not a known repository" +
            (_workspaceRoot is null
                ? " and no workspace root is configured."
                : $" and is not under the configured workspace root '{_workspaceRoot}'."));
    }

    private static JsonNode BreakingDiff(JsonElement? p)
    {
        var beforePath = GetString(p, "before") ?? throw new ArgumentException("before is required");
        var afterPath = GetString(p, "after") ?? throw new ArgumentException("after is required");

        var before = JsonExport.LoadAsync(beforePath).GetAwaiter().GetResult();
        var after = JsonExport.LoadAsync(afterPath).GetAwaiter().GetResult();

        var result = BreakingChangeClassifier.Classify(before, after);
        return Serialize(result, SynopsisJsonContext.Default.BreakingDiffResult);
    }

    private JsonNode EndpointCallers(JsonElement? p)
    {
        var route = GetString(p, "route") ?? throw new ArgumentException("route is required");
        var verb = GetString(p, "verb");
        var graph = Graph;

        var targets = graph.Nodes
            .Where(n => n.Type == NodeType.Endpoint
                && n.DisplayName.Contains(route, StringComparison.OrdinalIgnoreCase))
            .Where(n => verb is null ||
                n.Metadata.GetValueOrDefault("verb")?.Equals(verb, StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();

        var targetIds = targets.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);

        var resolvedEdges = graph.Edges
            .Where(e => e.Type is EdgeType.ResolvesToService or EdgeType.CrossesRepoBoundary
                && targetIds.Contains(e.TargetId))
            .ToLookup(e => e.SourceId, StringComparer.Ordinal);

        var callers = graph.Nodes
            .Where(n => n.Type == NodeType.ExternalEndpoint
                && n.DisplayName.Contains(route, StringComparison.OrdinalIgnoreCase))
            .Where(n => verb is null ||
                n.Metadata.GetValueOrDefault("verb")?.Equals(verb, StringComparison.OrdinalIgnoreCase) == true)
            .OrderBy(n => n.RepositoryName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var callersArray = new JsonArray();
        foreach (var caller in callers)
        {
            var resolved = resolvedEdges[caller.Id].FirstOrDefault();
            JsonNode entry = new JsonObject
            {
                ["caller"] = Serialize(caller, SynopsisJsonContext.Default.GraphNode),
                ["resolved"] = resolved is not null,
                ["certainty"] = (resolved?.Certainty ?? caller.Certainty).ToString(),
            };
            if (resolved is not null)
                ((JsonObject)entry)["resolvedToId"] = resolved.TargetId;
            callersArray.Add(entry);
        }

        return new JsonObject
        {
            ["endpoints"] = SerializeArray(targets),
            ["callers"] = callersArray,
            ["totalEndpoints"] = targets.Length,
            ["totalCallers"] = callers.Length,
            ["resolvedCallers"] = callers.Count(c => resolvedEdges[c.Id].Any()),
        };
    }

    private JsonNode PackageDependents(JsonElement? p)
    {
        var packageId = GetString(p, "packageId") ?? throw new ArgumentException("packageId is required");
        var version = GetString(p, "version");
        var graph = Graph;
        var nodesById = graph.NodesById!;

        var packages = graph.Nodes
            .Where(n => n.Type == NodeType.Package
                && n.Metadata.GetValueOrDefault("packageId")?.Contains(packageId, StringComparison.OrdinalIgnoreCase) == true)
            .Where(n => version is null ||
                string.Equals(n.Metadata.GetValueOrDefault("version"), version, StringComparison.Ordinal))
            .ToArray();

        var packageIdSet = packages.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);

        var depEdges = graph.Edges
            .Where(e => e.Type == EdgeType.DependsOnPackage && packageIdSet.Contains(e.TargetId))
            .OrderBy(e => nodesById.GetValueOrDefault(e.SourceId)?.RepositoryName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => nodesById.GetValueOrDefault(e.SourceId)?.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var dependents = new JsonArray();
        foreach (var edge in depEdges)
        {
            var src = nodesById.GetValueOrDefault(edge.SourceId);
            var pkg = nodesById.GetValueOrDefault(edge.TargetId);
            JsonNode entry = new JsonObject
            {
                ["repo"] = src?.RepositoryName ?? "?",
                ["project"] = src?.DisplayName ?? "?",
                ["packageId"] = pkg?.Metadata.GetValueOrDefault("packageId") ?? packageId,
                ["version"] = pkg?.Metadata.GetValueOrDefault("version") ?? "?",
                ["certainty"] = edge.Certainty.ToString(),
            };
            dependents.Add(entry);
        }

        return new JsonObject
        {
            ["packageId"] = packageId,
            ["matchedVersions"] = packages.Length,
            ["dependentCount"] = dependents.Count,
            ["dependents"] = dependents,
        };
    }

    private JsonNode TableEntryPoints(JsonElement? p)
    {
        var table = GetString(p, "table") ?? throw new ArgumentException("table is required");
        var maxDepth = GetInt(p, "maxDepth") ?? 8;
        var graph = Graph;

        var tables = graph.Nodes
            .Where(n => n.Type == NodeType.Table
                && n.DisplayName.Contains(table, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (tables.Length == 0)
            return new JsonObject
            {
                ["table"] = table,
                ["matchedTables"] = new JsonArray(),
                ["entryPoints"] = new JsonArray(),
                ["entryPointCount"] = 0,
            };

        // Targeted upstream BFS: only traverse data-access and call edges so
        // structural Contains/Defines edges don't pull every node in the project
        // into scope and inflate the entry-point list.
        var incoming = graph.IncomingEdges;
        var nodesById = graph.NodesById!;
        var emptyEdges = ImmutableArray<GraphEdge>.Empty;

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(string id, int depth)>();
        var entryPoints = new List<GraphNode>();

        foreach (var t in tables)
            if (visited.Add(t.Id))
                queue.Enqueue((t.Id, 0));

        while (queue.Count > 0)
        {
            var (currentId, depth) = queue.Dequeue();
            if (depth >= maxDepth)
                continue;
            var edges = incoming?.GetValueOrDefault(currentId, emptyEdges) ?? emptyEdges;
            foreach (var edge in edges)
            {
                if (edge.Type is not (EdgeType.MapsToTable or EdgeType.QueriesEntity
                        or EdgeType.UsesDbContext or EdgeType.Calls))
                    continue;
                if (!visited.Add(edge.SourceId))
                    continue;
                if (!nodesById.TryGetValue(edge.SourceId, out var src))
                    continue;

                if (src.Type == NodeType.Endpoint)
                    entryPoints.Add(src);
                else
                    queue.Enqueue((edge.SourceId, depth + 1));
            }
        }

        entryPoints.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(
            $"{a.RepositoryName}/{a.DisplayName}", $"{b.RepositoryName}/{b.DisplayName}"));

        return new JsonObject
        {
            ["table"] = table,
            ["matchedTables"] = SerializeArray(tables),
            ["entryPointCount"] = entryPoints.Count,
            ["entryPoints"] = SerializeArray(entryPoints.ToArray()),
        };
    }

    private JsonNode RepoDependencyMatrix()
    {
        var graph = Graph;
        var nodesById = graph.NodesById!;

        // Outbound HTTP call count per source repo (all CallsHttp edges)
        var outbound = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var edge in graph.Edges.Where(e => e.Type == EdgeType.CallsHttp))
        {
            var repo = nodesById.GetValueOrDefault(edge.SourceId)?.RepositoryName ?? "?";
            outbound[repo] = outbound.GetValueOrDefault(repo) + 1;
        }

        // Resolved cross-repo pairs: (from, to) → count + best certainty.
        // Count only CrossesRepoBoundary (not ResolvesToService) so each
        // resolved HTTP call contributes exactly one entry, avoiding double-count
        // when CrossRepoResolver emits both edge types for the same match.
        var resolved = new Dictionary<(string from, string to), (int count, Certainty best)>();
        foreach (var edge in graph.Edges.Where(e => e.Type == EdgeType.CrossesRepoBoundary))
        {
            var from = nodesById.GetValueOrDefault(edge.SourceId)?.RepositoryName ?? "?";
            var to = nodesById.GetValueOrDefault(edge.TargetId)?.RepositoryName ?? "?";
            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) continue;

            var key = (from, to);
            var (cnt, best) = resolved.GetValueOrDefault(key);
            resolved[key] = (cnt + 1, edge.Certainty > best ? edge.Certainty : best);
        }

        var outboundArray = new JsonArray();
        foreach (var (repo, count) in outbound.OrderByDescending(kv => kv.Value))
        {
            JsonNode entry = new JsonObject { ["repo"] = repo, ["callCount"] = count };
            outboundArray.Add(entry);
        }

        var resolvedArray = new JsonArray();
        foreach (var ((from, to), (count, best)) in resolved.OrderByDescending(kv => kv.Value.count))
        {
            JsonNode entry = new JsonObject
            {
                ["from"] = from,
                ["to"] = to,
                ["callCount"] = count,
                ["certainty"] = best.ToString(),
            };
            resolvedArray.Add(entry);
        }

        return new JsonObject
        {
            ["outboundByRepo"] = outboundArray,
            ["resolvedDependencies"] = resolvedArray,
        };
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
