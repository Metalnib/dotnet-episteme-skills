using System.Diagnostics;
using Synopsis.Analysis.Graph;
using Synopsis.Analysis.Model;
using Synopsis.Output;

namespace Synopsis.Commands;

internal static class QueryCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: synopsis query impact --node <id> [--graph graph.json] [--json]\n       synopsis query paths --from <node> --to <node> [--graph graph.json] [--json]\n       synopsis query symbol --fqn <name> [--blast-radius] [--graph graph.json] [--json]\n       synopsis query ambiguous [--graph graph.json] [--limit 50] [--json]");
            return 1;
        }

        var timer = Stopwatch.StartNew();
        var jsonMode = CliArgs.HasFlag(args, "--json");
        var sub = args[1].ToLowerInvariant();
        var graphPath = CliArgs.Option(args, "--graph") ?? "graph.json";
        var result = await JsonExport.LoadAsync(graphPath);
        var query = new GraphQuery(result);

        return sub switch
        {
            "impact" => RunImpact(args, query, jsonMode, timer),
            "paths" => RunPaths(args, query, jsonMode, timer),
            "symbol" => RunSymbol(args, query, result, jsonMode, timer),
            "ambiguous" => RunAmbiguous(args, query, jsonMode, timer),
            _ => Error($"Unknown query subcommand '{sub}'.")
        };
    }

    private static int RunImpact(string[] args, GraphQuery query, bool jsonMode, Stopwatch timer)
    {
        var node = CliArgs.Option(args, "--node");
        if (string.IsNullOrWhiteSpace(node))
        {
            Console.Error.WriteLine("--node is required.");
            return 1;
        }

        var direction = CliArgs.Option(args, "--direction") ?? "downstream";
        var depth = CliArgs.IntOption(args, "--depth") ?? 6;
        var impact = query.FindImpact(node,
            upstream: string.Equals(direction, "upstream", StringComparison.OrdinalIgnoreCase),
            maxDepth: depth);

        if (jsonMode)
        {
            JsonOutput.WriteImpact("query impact", impact, timer);
            return 0;
        }

        Console.WriteLine($"Focus: {impact.FocusNode.DisplayName} ({impact.FocusNode.Type})");
        Console.WriteLine($"Nodes: {impact.Nodes.Length}, Edges: {impact.Edges.Length}");
        foreach (var edge in impact.Edges.AsSpan()[..Math.Min(40, impact.Edges.Length)])
            Console.WriteLine($"{edge.Type,-24} {edge.DisplayName} [{edge.Certainty}]");

        return 0;
    }

    private static int RunPaths(string[] args, GraphQuery query, bool jsonMode, Stopwatch timer)
    {
        var from = CliArgs.Option(args, "--from");
        var to = CliArgs.Option(args, "--to");
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
        {
            Console.Error.WriteLine("--from and --to are required.");
            return 1;
        }

        var paths = query.FindPaths(from, to);

        if (jsonMode)
        {
            JsonOutput.WritePaths("query paths", paths, timer);
            return 0;
        }

        Console.WriteLine($"Paths from {paths.From.DisplayName} to {paths.To.DisplayName}: {paths.Paths.Length}");
        foreach (var path in paths.Paths.AsSpan()[..Math.Min(10, paths.Paths.Length)])
            Console.WriteLine(string.Join(" -> ", path.Nodes.Select(n => n.DisplayName)));

        return 0;
    }

    private static int RunAmbiguous(string[] args, GraphQuery query, bool jsonMode, Stopwatch timer)
    {
        var limit = CliArgs.IntOption(args, "--limit") ?? 50;
        var report = query.GetAmbiguityReport();

        if (jsonMode)
        {
            JsonOutput.WriteAmbiguity("query ambiguous", report, timer);
            return 0;
        }

        Console.WriteLine("=== Ambiguous edges requiring review ===");
        Console.WriteLine($"  Unresolved edges : {report.UnresolvedEdges.Length}");
        Console.WriteLine($"  Ambiguous edges  : {report.AmbiguousEdges.Length}");
        Console.WriteLine($"  Unresolved symbols: {report.UnresolvedSymbols.Length}");
        Console.WriteLine();

        if (report.UnresolvedEdges.Length > 0)
        {
            Console.WriteLine($"--- Unresolved (certainty=0) [up to {limit}] ---");
            foreach (var edge in report.UnresolvedEdges.AsSpan()[..Math.Min(limit, report.UnresolvedEdges.Length)])
            {
                var loc = edge.Location is null ? "" : $" @ {edge.Location.FilePath}:{edge.Location.Line}";
                Console.WriteLine($"  [{edge.Type,-24}] {edge.DisplayName}{loc}");
            }
            if (report.UnresolvedEdges.Length > limit)
                Console.WriteLine($"  ... and {report.UnresolvedEdges.Length - limit} more.");
            Console.WriteLine();
        }

        if (report.AmbiguousEdges.Length > 0)
        {
            Console.WriteLine($"--- Ambiguous (certainty=1) [up to {limit}] ---");
            foreach (var g in report.AmbiguousEdges.GroupBy(e => e.Type).OrderBy(g => g.Key.ToString()))
                Console.WriteLine($"  {g.Key} ({g.Count()})");
            Console.WriteLine();
            foreach (var edge in report.AmbiguousEdges.AsSpan()[..Math.Min(limit, report.AmbiguousEdges.Length)])
            {
                var loc = edge.Location is null ? "" : $" @ {edge.Location.FilePath}:{edge.Location.Line}";
                Console.WriteLine($"  [{edge.Type,-24}] {edge.DisplayName}{loc}");
            }
            if (report.AmbiguousEdges.Length > limit)
                Console.WriteLine($"  ... and {report.AmbiguousEdges.Length - limit} more.");
        }

        if (report.UnresolvedSymbols.Length > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"--- Unresolved symbols [up to {limit}] ---");
            foreach (var w in report.UnresolvedSymbols.AsSpan()[..Math.Min(limit, report.UnresolvedSymbols.Length)])
            {
                var path = w.Path is null ? "" : $" ({w.Path})";
                Console.WriteLine($"  [{w.Code}] {w.Message}{path}");
            }
            if (report.UnresolvedSymbols.Length > limit)
                Console.WriteLine($"  ... and {report.UnresolvedSymbols.Length - limit} more.");
        }

        return 0;
    }

    private static int RunSymbol(string[] args, GraphQuery query, ScanResult graph, bool jsonMode, Stopwatch timer)
    {
        var fqn = CliArgs.Option(args, "--fqn");
        if (string.IsNullOrWhiteSpace(fqn))
        {
            Console.Error.WriteLine("--fqn is required.");
            return 1;
        }

        var wantBlastRadius = CliArgs.HasFlag(args, "--blast-radius");
        var depth = CliArgs.IntOption(args, "--depth") ?? 4;
        var direction = CliArgs.Option(args, "--direction") ?? "downstream";

        var node = query.ResolveNode(fqn);

        if (wantBlastRadius)
        {
            var upstream = string.Equals(direction, "upstream", StringComparison.OrdinalIgnoreCase);
            var impact = query.FindImpact(node.Id, upstream, maxDepth: depth);

            if (jsonMode)
            {
                JsonOutput.WriteImpact("query symbol", impact, timer);
                return 0;
            }

            Console.WriteLine($"Symbol: {node.DisplayName} ({node.Type})");
            Console.WriteLine($"Blast radius ({direction}, depth {depth}): {impact.Nodes.Length} nodes, {impact.Edges.Length} edges");
            Console.WriteLine();
            foreach (var edge in impact.Edges.AsSpan()[..Math.Min(40, impact.Edges.Length)])
                Console.WriteLine($"  {edge.Type,-24} {edge.DisplayName} [{edge.Certainty}]");
            if (impact.Edges.Length > 40)
                Console.WriteLine($"  ... and {impact.Edges.Length - 40} more edges");
        }
        else
        {
            if (jsonMode)
            {
                JsonOutput.WriteNode("query symbol", node, timer);
                return 0;
            }

            Console.WriteLine($"Id:         {node.Id}");
            Console.WriteLine($"Type:       {node.Type}");
            Console.WriteLine($"Name:       {node.DisplayName}");
            Console.WriteLine($"Certainty:  {node.Certainty}");
            Console.WriteLine($"Repository: {node.RepositoryName ?? "(none)"}");
            Console.WriteLine($"Project:    {node.ProjectName ?? "(none)"}");
            if (node.Location is not null)
                Console.WriteLine($"Location:   {node.Location.FilePath}:{node.Location.Line}");

            if (node.Metadata.Count > 0)
            {
                Console.WriteLine("Metadata:");
                foreach (var (key, value) in node.Metadata)
                    Console.WriteLine($"  {key}: {value}");
            }

            // Show direct edges
            var outgoing = graph.OutgoingEdges?.GetValueOrDefault(node.Id, []) ?? [];
            var incoming = graph.IncomingEdges?.GetValueOrDefault(node.Id, []) ?? [];

            if (outgoing.Length > 0)
            {
                Console.WriteLine($"\nOutgoing edges ({outgoing.Length}):");
                foreach (var edge in outgoing.AsSpan()[..Math.Min(20, outgoing.Length)])
                    Console.WriteLine($"  -> [{edge.Type}] {edge.DisplayName}");
            }

            if (incoming.Length > 0)
            {
                Console.WriteLine($"\nIncoming edges ({incoming.Length}):");
                foreach (var edge in incoming.AsSpan()[..Math.Min(20, incoming.Length)])
                    Console.WriteLine($"  <- [{edge.Type}] {edge.DisplayName}");
            }
        }

        return 0;
    }

    private static int Error(string message) { Console.Error.WriteLine(message); return 1; }
}
