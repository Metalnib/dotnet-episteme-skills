using System.Collections.Immutable;
using Synopsis.Analysis;
using Synopsis.Analysis.Graph;
using Synopsis.Analysis.Model;
using Synopsis.Analysis.Scanning;
using Synopsis.Mcp;
using Synopsis.Output;

namespace Synopsis.Commands;

internal static class McpCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var rootPath = CliArgs.Option(args, "--root");
        var graphPath = CliArgs.Option(args, "--graph");
        var socketPath = CliArgs.Option(args, "--socket");
        var tcpAddr = CliArgs.Option(args, "--tcp");
        var stateDir = CliArgs.Option(args, "--state-dir");
        var logFile = CliArgs.Option(args, "--log-file");

        McpLog.Configure(logFile);

        if (string.IsNullOrWhiteSpace(rootPath) && string.IsNullOrWhiteSpace(graphPath))
        {
            PrintUsage();
            return 1;
        }

        if (!string.IsNullOrWhiteSpace(socketPath) && !string.IsNullOrWhiteSpace(tcpAddr))
        {
            Console.Error.WriteLine("--socket and --tcp are mutually exclusive.");
            return 1;
        }

        var scanner = ScannerBuilder.Create();
        IGraphStateStore store = string.IsNullOrWhiteSpace(stateDir)
            ? new MemoryStateStore()
            : new JsonFileStateStore(stateDir);
        var combined = new CombinedGraph(store);

        // Hydrate from persisted state first — incremental reindex calls
        // then only pay for what actually changed since the last save.
        await combined.LoadAsync(default);
        if (combined.KnownRepositories.Count > 0)
            McpLog.Write($"[mcp] Restored {combined.KnownRepositories.Count} repository/repositories from {stateDir}.");

        var loadedFromGraph = false;
        if (!string.IsNullOrWhiteSpace(graphPath) && File.Exists(graphPath))
        {
            McpLog.Write($"[mcp] Loading graph from {graphPath}");
            var legacy = await JsonExport.LoadAsync(graphPath);
            // Legacy single-graph mode: seed one entry keyed by the graph
            // file's root path. Incremental reindex on individual repos
            // still works afterwards.
            await combined.ReplaceRepositoryAsync(legacy.Metadata.RootPath, legacy, default);
            loadedFromGraph = true;
        }
        else if (string.IsNullOrWhiteSpace(rootPath) && combined.KnownRepositories.Count == 0)
        {
            Console.Error.WriteLine($"Graph file '{graphPath}' not found.");
            return 1;
        }

        IMcpTransport transport;
        try
        {
            if (!string.IsNullOrWhiteSpace(socketPath))
                transport = new UnixSocketTransport(socketPath);
            else if (!string.IsNullOrWhiteSpace(tcpAddr))
                transport = TcpTransport.Create(tcpAddr);
            else
                transport = new StdioTransport();
        }
        catch (Exception ex) when (ex is ArgumentException or System.Net.Sockets.SocketException or IOException)
        {
            McpLog.Write($"[mcp] Failed to open transport: {ex.Message}");
            return 1;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // The workspace scan runs in the background so the server answers
        // initialize immediately — MCP clients time out long before Roslyn
        // finishes a large workspace. Hydrated state (if any) serves queries
        // meanwhile; an empty graph reports "indexing in progress".
        var indexing = IndexingState.Idle;
        var scanTask = Task.CompletedTask;
        // --graph wins over --root (they were mutually exclusive branches
        // before the scan went async): a loaded graph means no startup scan.
        if (!loadedFromGraph && !string.IsNullOrWhiteSpace(rootPath))
        {
            indexing = IndexingState.Started();
            McpLog.Write($"[mcp] Scanning {rootPath} in the background; server is available immediately.");
            scanTask = Task.Run(() => ScanWorkspaceAsync(rootPath, args, scanner, combined, indexing, cts.Token), cts.Token);
        }

        var server = new McpServer(combined, scanner, workspaceRoot: rootPath, indexing);
        await using (transport)
        {
            await server.RunAsync(transport, cts.Token);
        }

        // Shutdown: stop a still-running scan and observe its outcome so the
        // process never exits with an unobserved task exception.
        cts.Cancel();
        try { await scanTask; } catch { /* logged inside ScanWorkspaceAsync */ }
        return 0;
    }

    private static async Task ScanWorkspaceAsync(string rootPath, string[] args, WorkspaceScanner scanner,
        CombinedGraph combined, IndexingState indexing, CancellationToken ct)
    {
        try
        {
            // Under the graph's scan lock: a concurrent reindex_repository
            // would otherwise race MSBuildWorkspace, which is not reentrant.
            await combined.RunLockedScanAsync(async lockedCt =>
            {
                var options = ScanCommand.CreateOptions(rootPath, args);
                var discovery = WorkspaceDiscovery.Discover(rootPath, lockedCt, options);
                var result = await scanner.ScanAsync(rootPath, options, lockedCt, new ConsoleProgress());

                if (discovery.Repositories.Length == 0)
                {
                    // No .git markers under rootPath — treat the whole workspace
                    // as one logical "repo" keyed by the root path.
                    await combined.ReplaceRepositoryAsync(rootPath, result, lockedCt);
                }
                else
                {
                    // Partition the single-scan result into per-repo subsets so
                    // subsequent reindex_repository calls on individual repos
                    // replace the right entry instead of stacking duplicates.
                    var perRepo = PartitionByRepository(result, discovery);
                    foreach (var (repoPath, subset) in perRepo)
                        await combined.ReplaceRepositoryAsync(repoPath, subset, lockedCt);
                    McpLog.Write($"[mcp] Registered {perRepo.Count} repositor{(perRepo.Count == 1 ? "y" : "ies")} from workspace.");
                }

                McpLog.Write($"[mcp] Scan complete: {result.Statistics.ProjectCount} projects, {result.Nodes.Length} nodes, {result.Edges.Length} edges");
            }, ct);
            indexing.Complete();
        }
        catch (OperationCanceledException)
        {
            McpLog.Write("[mcp] Initial scan cancelled by shutdown.");
            indexing.Fail("scan cancelled by shutdown");
        }
        catch (Exception ex)
        {
            // Full exception: this catch is the only record of the failure —
            // the awaiting side deliberately swallows the task's fault.
            McpLog.Write($"[mcp] Initial scan FAILED: {ex}");
            indexing.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Split a workspace-level <see cref="ScanResult"/> into per-repository
    /// subsets keyed by repo root path. Nodes and edges that have a
    /// <see cref="GraphNode.RepositoryName"/> are placed in their owning
    /// repo's subset; ownerless nodes (<see cref="NodeType.Workspace"/>,
    /// <see cref="NodeType.Package"/>, anything else that spans repos) are
    /// duplicated into every repo's subset so cross-repo edges still
    /// resolve after the combined-graph rebuild dedupes by node ID.
    /// </summary>
    private static IReadOnlyDictionary<string, ScanResult> PartitionByRepository(
        ScanResult big, DiscoveryResult discovery)
    {
        var byRepo = new Dictionary<string, GraphBuilder>(StringComparer.OrdinalIgnoreCase);
        var nameToPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var repo in discovery.Repositories)
        {
            if (!byRepo.TryAdd(repo.Name, new GraphBuilder()))
            {
                McpLog.Write($"[mcp] Warning: duplicate repository name '{repo.Name}' at {repo.RootPath} — skipping.");
                continue;
            }
            nameToPath[repo.Name] = repo.RootPath;
        }

        var sharedNodes = new List<GraphNode>();
        var sharedEdges = new List<GraphEdge>();

        foreach (var node in big.Nodes)
        {
            if (node.RepositoryName is { } owner && byRepo.TryGetValue(owner, out var b))
                b.AddNode(node.Id, node.Type, node.DisplayName, node.Location,
                    node.RepositoryName, node.ProjectName, node.Certainty, node.Metadata);
            else
                sharedNodes.Add(node);
        }

        foreach (var edge in big.Edges)
        {
            if (edge.RepositoryName is { } owner && byRepo.TryGetValue(owner, out var b))
                b.AddEdge(edge.SourceId, edge.TargetId, edge.Type, edge.DisplayName,
                    edge.Location, edge.RepositoryName, edge.ProjectName, edge.Certainty, edge.Metadata);
            else
                sharedEdges.Add(edge);
        }

        // Replay shared nodes/edges into every per-repo builder. Dedup at
        // CombinedGraph merge time turns that into a single node per ID.
        foreach (var b in byRepo.Values)
        {
            foreach (var n in sharedNodes)
                b.AddNode(n.Id, n.Type, n.DisplayName, n.Location,
                    n.RepositoryName, n.ProjectName, n.Certainty, n.Metadata);
            foreach (var e in sharedEdges)
                b.AddEdge(e.SourceId, e.TargetId, e.Type, e.DisplayName,
                    e.Location, e.RepositoryName, e.ProjectName, e.Certainty, e.Metadata);
        }

        // Partition warnings to their owning repo by path prefix; orphans
        // (no path, or path not under any repo) go to every partition.
        var warningsByName = byRepo.Keys
            .ToDictionary(k => k, _ => new List<ScanWarning>(), StringComparer.OrdinalIgnoreCase);
        var orphanWarnings = new List<ScanWarning>();
        foreach (var warning in big.Warnings)
        {
            var owner = warning.Path is not null
                ? discovery.Repositories
                    .Where(r => Paths.IsUnder(warning.Path, r.RootPath))
                    .OrderByDescending(r => r.RootPath.Length)
                    .Select(r => r.Name)
                    .FirstOrDefault()
                : null;
            if (owner is not null && warningsByName.TryGetValue(owner, out var bucket))
                bucket.Add(warning);
            else
                orphanWarnings.Add(warning);
        }

        var out_ = new Dictionary<string, ScanResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, b) in byRepo)
        {
            var repoPath = nameToPath[name];
            var repoWarnings = warningsByName[name].Concat(orphanWarnings).ToImmutableArray();
            var info = new ScanInfo(repoPath, big.Metadata.StartedAtUtc, big.Metadata.CompletedAtUtc,
                ImmutableArray<Timing>.Empty, big.Metadata.Properties);
            out_[repoPath] = b.Build(info, repoWarnings);
        }
        return out_;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: synopsis mcp (--root <rootPath> | --graph <graph.json>) [--socket <path> | --tcp <addr>] [--state-dir <path>] [--log-file <path>]");
        Console.Error.WriteLine("  --socket <path>   listen on a Unix domain socket (daemon mode).");
        Console.Error.WriteLine("  --tcp <addr>      listen on TCP (host:port, :port, or port). Default host: 127.0.0.1.");
        Console.Error.WriteLine("  --state-dir <path> persist per-repo graphs under <path> (otherwise in-memory only).");
        Console.Error.WriteLine("  --log-file <path> append diagnostics to <path> in addition to stderr (env: SYNOPSIS_LOG_FILE).");
        Console.Error.WriteLine("  (default)         read one request stream from stdin, respond on stdout.");
    }
}
