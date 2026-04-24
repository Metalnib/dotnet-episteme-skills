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
        var socketPath = CliArgs.Option(args, "--socket");
        var tcpAddr = CliArgs.Option(args, "--tcp");

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

        var server = new McpServer(graph);
        await using (transport)
        {
            await server.RunAsync(transport, cts.Token);
        }
        return 0;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: synopsis mcp (--root <rootPath> | --graph <graph.json>) [--socket <path> | --tcp <addr>]");
        Console.Error.WriteLine("  --socket <path>   listen on a Unix domain socket (daemon mode).");
        Console.Error.WriteLine("  --tcp <addr>      listen on TCP (host:port, :port, or port). Default host: 127.0.0.1.");
        Console.Error.WriteLine("  (default)         read one request stream from stdin, respond on stdout.");
    }
}
