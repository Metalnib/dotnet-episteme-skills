using System.Collections.Immutable;
using System.Diagnostics;
using Synopsis.Analysis;
using Synopsis.Analysis.Graph;
using Synopsis.Analysis.Model;
using Synopsis.Git;
using Synopsis.Output;

namespace Synopsis.Commands;

internal static class GitScanCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: synopsis git-scan <rootPath> --base <branch> [--head HEAD] [--depth 4] [-o git-impact.json] [--json]");
            return 1;
        }

        var timer = Stopwatch.StartNew();
        var jsonMode = CliArgs.HasFlag(args, "--json");
        var rootPath = args[1];
        var baseBranch = CliArgs.Option(args, "--base");
        var headRef = CliArgs.Option(args, "--head") ?? "HEAD";
        var depth = CliArgs.IntOption(args, "--depth") ?? 4;
        var output = CliArgs.Option(args, "-o");

        if (string.IsNullOrWhiteSpace(baseBranch))
        {
            Console.Error.WriteLine("--base is required.");
            return 1;
        }

        // 1. Get changed files from git
        Console.Error.WriteLine($"[git-scan] Getting changes: {baseBranch}...{headRef}");
        var changedFiles = await GitDiff.GetChangedFilesAsync(rootPath, baseBranch, headRef);
        Console.Error.WriteLine($"[git-scan] {changedFiles.Length} relevant file(s) changed.");

        if (changedFiles.Length == 0)
        {
            Console.Error.WriteLine("[git-scan] No relevant changes found.");
            if (jsonMode)
                Console.WriteLine("""{"command":"git-scan","ok":true,"result":{"changedFiles":[],"directlyAffectedNodes":[]}}""");
            return 0;
        }

        // 2. Full scan
        var options = ScanCommand.CreateOptions(rootPath, args);
        var scanner = ScannerBuilder.Create();
        var progress = jsonMode ? null : new ConsoleProgress();
        var graph = await scanner.ScanAsync(rootPath, options, default, progress);

        // 3. Find directly affected nodes
        var affected = GitDiff.FindAffectedNodes(graph, changedFiles);
        Console.Error.WriteLine($"[git-scan] {affected.Length} node(s) directly affected.");

        // 4. Expand blast radius from each affected node
        var query = new GraphQuery(graph);
        var allImpactNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allImpactEdges = new List<GraphEdge>();

        foreach (var node in affected)
        {
            try
            {
                var impact = query.FindImpact(node.Id, upstream: false, maxDepth: depth);
                foreach (var n in impact.Nodes)
                    allImpactNodes.Add(n.Id);
                allImpactEdges.AddRange(impact.Edges);
            }
            catch
            {
                // Node may not be in adjacency if it's a type-level node without edges
            }
        }

        var impactNodes = allImpactNodes
            .Select(id => graph.NodesById![id])
            .OrderBy(n => n.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        var impactEdges = allImpactEdges
            .DistinctBy(e => e.Id)
            .ToImmutableArray();

        var focus = affected.Length > 0 ? affected[0] : impactNodes[0];
        var blastRadius = new ImpactGraph(focus, impactNodes, impactEdges);

        // 5. Output
        if (output is not null)
        {
            var result = new GitImpactResult(baseBranch, headRef, changedFiles, affected, blastRadius, graph.Metadata);
            await JsonExport.SaveAsync(new ScanResult
            {
                Nodes = impactNodes,
                Edges = impactEdges,
                Metadata = graph.Metadata,
                Warnings = graph.Warnings,
                Unresolved = graph.Unresolved,
                Statistics = graph.Statistics
            }, output);
            Console.Error.WriteLine($"[git-scan] Impact graph written to {output}");
        }

        if (jsonMode)
        {
            JsonOutput.WriteImpact("git-scan", blastRadius, timer);
        }
        else
        {
            Console.WriteLine($"Changed files: {changedFiles.Length}");
            foreach (var f in changedFiles.AsSpan()[..Math.Min(10, changedFiles.Length)])
                Console.WriteLine($"  {Paths.ToRelative(rootPath, f)}");
            if (changedFiles.Length > 10)
                Console.WriteLine($"  ... and {changedFiles.Length - 10} more");

            Console.WriteLine($"\nDirectly affected nodes: {affected.Length}");
            foreach (var n in affected.AsSpan()[..Math.Min(15, affected.Length)])
                Console.WriteLine($"  [{n.Type}] {n.DisplayName}");

            Console.WriteLine($"\nBlast radius: {impactNodes.Length} nodes, {impactEdges.Length} edges");
        }

        return 0;
    }
}
