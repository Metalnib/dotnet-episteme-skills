using System.Diagnostics;
using System.Text.Json;
using Synopsis.Analysis.Graph;
using Synopsis.Output;

namespace Synopsis.Commands;

internal static class DiffCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("Usage: synopsis diff <before.json> <after.json> [--json]");
            return 1;
        }

        var timer = Stopwatch.StartNew();
        var jsonMode = CliArgs.HasFlag(args, "--json");
        var beforePath = args[1];
        var afterPath = args[2];

        var before = await JsonExport.LoadAsync(beforePath);
        var after = await JsonExport.LoadAsync(afterPath);
        var diff = GraphDiffer.Compare(before, after);

        if (jsonMode)
        {
            JsonOutput.WriteDiff("diff", diff, timer);
            return 0;
        }

        Console.WriteLine($"=== Graph Diff: {Path.GetFileName(beforePath)} -> {Path.GetFileName(afterPath)} ===");
        Console.WriteLine();

        Console.WriteLine("Statistics change:");
        PrintStatDelta("  Repositories", diff.BeforeStatistics.RepositoryCount, diff.AfterStatistics.RepositoryCount);
        PrintStatDelta("  Projects", diff.BeforeStatistics.ProjectCount, diff.AfterStatistics.ProjectCount);
        PrintStatDelta("  Endpoints", diff.BeforeStatistics.EndpointCount, diff.AfterStatistics.EndpointCount);
        PrintStatDelta("  Methods", diff.BeforeStatistics.MethodCount, diff.AfterStatistics.MethodCount);
        PrintStatDelta("  HTTP edges", diff.BeforeStatistics.HttpEdgeCount, diff.AfterStatistics.HttpEdgeCount);
        PrintStatDelta("  Tables", diff.BeforeStatistics.TableCount, diff.AfterStatistics.TableCount);
        PrintStatDelta("  Cross-repo", diff.BeforeStatistics.CrossRepoLinkCount, diff.AfterStatistics.CrossRepoLinkCount);
        PrintStatDelta("  Ambiguous", diff.BeforeStatistics.AmbiguousEdgeCount, diff.AfterStatistics.AmbiguousEdgeCount);
        Console.WriteLine();

        if (diff.AddedNodes.Length > 0)
        {
            Console.WriteLine($"Added nodes: {diff.AddedNodes.Length}");
            foreach (var n in diff.AddedNodes.AsSpan()[..Math.Min(20, diff.AddedNodes.Length)])
                Console.WriteLine($"  + [{n.Type}] {n.DisplayName}");
            if (diff.AddedNodes.Length > 20)
                Console.WriteLine($"  ... and {diff.AddedNodes.Length - 20} more");
            Console.WriteLine();
        }

        if (diff.RemovedNodes.Length > 0)
        {
            Console.WriteLine($"Removed nodes: {diff.RemovedNodes.Length}");
            foreach (var n in diff.RemovedNodes.AsSpan()[..Math.Min(20, diff.RemovedNodes.Length)])
                Console.WriteLine($"  - [{n.Type}] {n.DisplayName}");
            if (diff.RemovedNodes.Length > 20)
                Console.WriteLine($"  ... and {diff.RemovedNodes.Length - 20} more");
            Console.WriteLine();
        }

        if (diff.ChangedNodes.Length > 0)
        {
            Console.WriteLine($"Changed nodes: {diff.ChangedNodes.Length}");
            foreach (var c in diff.ChangedNodes.AsSpan()[..Math.Min(20, diff.ChangedNodes.Length)])
            {
                Console.WriteLine($"  ~ [{c.After.Type}] {c.After.DisplayName}");
                foreach (var change in c.Changes)
                    Console.WriteLine($"      {change}");
            }
            Console.WriteLine();
        }

        Console.WriteLine($"Added edges: {diff.AddedEdges.Length}");
        Console.WriteLine($"Removed edges: {diff.RemovedEdges.Length}");

        return 0;
    }

    private static void PrintStatDelta(string label, int before, int after)
    {
        var delta = after - before;
        var sign = delta > 0 ? "+" : "";
        Console.WriteLine(delta == 0
            ? $"{label}: {after}"
            : $"{label}: {before} -> {after} ({sign}{delta})");
    }
}
