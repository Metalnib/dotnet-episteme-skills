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
            Console.Error.WriteLine($"[mcp] Restored {combined.KnownRepositories.Count} repository/repositories from {stateDir}.");

        if (!string.IsNullOrWhiteSpace(graphPath) && File.Exists(graphPath))
        {
            Console.Error.WriteLine($"[mcp] Loading graph from {graphPath}");
            var legacy = await JsonExport.LoadAsync(graphPath);
            // Legacy single-graph mode: seed one entry keyed by the graph
            // file's root path. Incremental reindex on individual repos
            // still works afterwards.
            await combined.ReplaceRepositoryAsync(legacy.Metadata.RootPath, legacy, default);
        }
        else if (!string.IsNullOrWhiteSpace(rootPath))
        {
            Console.Error.WriteLine($"[mcp] Scanning {rootPath}...");
            var options = ScanCommand.CreateOptions(rootPath, args);
            var discovery = WorkspaceDiscovery.Discover(rootPath, default, options);
            var result = await scanner.ScanAsync(rootPath, options, default, new ConsoleProgress());

            if (discovery.Repositories.Length == 0)
            {
                // No .git markers under rootPath — treat the whole workspace
                // as one logical "repo" keyed by the root path.
                await combined.ReplaceRepositoryAsync(rootPath, result, default);
            }
            else
            {
                // Partition the single-scan result into per-repo subsets so
                // subsequent reindex_repository calls on individual repos
                // replace the right entry instead of stacking duplicates.
                var perRepo = PartitionByRepository(result, discovery);
                foreach (var (repoPath, subset) in perRepo)
                    await combined.ReplaceRepositoryAsync(repoPath, subset, default);
                Console.Error.WriteLine($"[mcp] Registered {perRepo.Count} repositor{(perRepo.Count == 1 ? "y" : "ies")} from workspace.");
            }

            Console.Error.WriteLine($"[mcp] Scan complete: {result.Statistics.ProjectCount} projects, {result.Nodes.Length} nodes, {result.Edges.Length} edges");
        }
        else if (combined.KnownRepositories.Count == 0)
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
            Console.Error.WriteLine($"[mcp] Failed to open transport: {ex.Message}");
            return 1;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var server = new McpServer(combined, scanner, workspaceRoot: rootPath);
        await using (transport)
        {
            await server.RunAsync(transport, cts.Token);
        }
        return 0;
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
            byRepo[repo.Name] = new GraphBuilder();
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

        var out_ = new Dictionary<string, ScanResult>(StringComparer.OrdinalIgnoreCase);
        var warnings = big.Warnings;
        foreach (var (name, b) in byRepo)
        {
            var repoPath = nameToPath[name];
            var info = new ScanInfo(repoPath, big.Metadata.StartedAtUtc, big.Metadata.CompletedAtUtc,
                ImmutableArray<Timing>.Empty, big.Metadata.Properties);
            out_[repoPath] = b.Build(info, warnings);
        }
        return out_;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: synopsis mcp (--root <rootPath> | --graph <graph.json>) [--socket <path> | --tcp <addr>] [--state-dir <path>]");
        Console.Error.WriteLine("  --socket <path>   listen on a Unix domain socket (daemon mode).");
        Console.Error.WriteLine("  --tcp <addr>      listen on TCP (host:port, :port, or port). Default host: 127.0.0.1.");
        Console.Error.WriteLine("  --state-dir <path> persist per-repo graphs under <path> (otherwise in-memory only).");
        Console.Error.WriteLine("  (default)         read one request stream from stdin, respond on stdout.");
    }
}
