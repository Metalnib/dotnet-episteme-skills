using Synopsis.Commands;

return await Run(args);

static async Task<int> Run(string[] args)
{
    if (args.Length == 0)
    {
        PrintHelp();
        return 1;
    }

    try
    {
        return args[0].ToLowerInvariant() switch
        {
            "scan" => await ScanCommand.RunAsync(args),
            "watch" => await WatchCommand.RunAsync(args),
            "export" => await ExportCommand.RunAsync(args),
            "query" => await QueryCommand.RunAsync(args),
            "git-scan" => await GitScanCommand.RunAsync(args),
            "diff" => await DiffCommand.RunAsync(args),
            "breaking-diff" => await BreakingDiffCommand.RunAsync(args),
            "mcp" => await McpCommand.RunAsync(args),
            _ => UnknownCommand(args[0])
        };
    }
    catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

static int UnknownCommand(string command)
{
    Console.Error.WriteLine($"Unknown command '{command}'.");
    PrintHelp();
    return 1;
}

static void PrintHelp()
{
    Console.WriteLine("synopsis - static dependency and blast-radius explorer for .NET workspaces");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  synopsis scan <rootPath> [-o graph.json] [--exclude <path> ...] [--json]");
    Console.WriteLine("  synopsis watch <rootPath> [-o graph.json] [--debounce-ms 1500]");
    Console.WriteLine("  synopsis export json|csv|jsonl <rootPath> -o <file|folder>");
    Console.WriteLine("  synopsis query impact --node <id> [--direction upstream|downstream] [--graph graph.json] [--json]");
    Console.WriteLine("  synopsis query paths --from <node> --to <node> [--graph graph.json] [--json]");
    Console.WriteLine("  synopsis query symbol --fqn <name> [--blast-radius] [--depth 4] [--graph graph.json] [--json]");
    Console.WriteLine("  synopsis query ambiguous [--graph graph.json] [--limit 50] [--json]");
    Console.WriteLine("  synopsis git-scan <rootPath> --base <branch> [--head HEAD] [--depth 4] [--json]");
    Console.WriteLine("  synopsis diff <before.json> <after.json> [--json]");
    Console.WriteLine("  synopsis breaking-diff <before.json> <after.json> [--json] [-o report.json]");
    Console.WriteLine("  synopsis mcp (--root <rootPath> | --graph <graph.json>) [--socket <path> | --tcp <addr>]");
}
