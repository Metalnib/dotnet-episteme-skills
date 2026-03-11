using Synopsis.Analysis;
using Synopsis.Mcp;
using Synopsis.Output;

namespace Synopsis.Commands;

internal static class McpCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var rootPath = CliArgs.Option(args, "--root");
        var graphPath = CliArgs.Option(args, "--graph");

        if (string.IsNullOrWhiteSpace(rootPath) && string.IsNullOrWhiteSpace(graphPath))
        {
            Console.Error.WriteLine("Usage: synopsis mcp --root <rootPath> | --graph <graph.json>");
            return 1;
        }

        Analysis.Model.ScanResult graph;

        if (!string.IsNullOrWhiteSpace(graphPath) && File.Exists(graphPath))
        {
            Console.Error.WriteLine($"[mcp] Loading graph from {graphPath}");
            graph = await JsonExport.LoadAsync(graphPath);
        }
        else if (!string.IsNullOrWhiteSpace(rootPath))
        {
            Console.Error.WriteLine($"[mcp] Scanning {rootPath}...");
            var options = ScanCommand.CreateOptions(rootPath, args);
            var scanner = ScannerBuilder.Create();
            graph = await scanner.ScanAsync(rootPath, options, default, new ConsoleProgress());
            Console.Error.WriteLine($"[mcp] Scan complete: {graph.Statistics.ProjectCount} projects, {graph.Nodes.Length} nodes, {graph.Edges.Length} edges");
        }
        else
        {
            Console.Error.WriteLine($"Graph file '{graphPath}' not found.");
            return 1;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var server = new McpServer(graph);
        await server.RunAsync(cts.Token);
        return 0;
    }
}
