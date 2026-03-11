using System.Diagnostics;
using Synopsis.Analysis;
using Synopsis.Analysis.Model;
using Synopsis.Analysis.Scanning;
using Synopsis.Output;

namespace Synopsis.Commands;

internal static class ScanCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: synopsis scan <rootPath> [-o graph.json] [--exclude <path> ...] [--json]");
            return 1;
        }

        var timer = Stopwatch.StartNew();
        var jsonMode = CliArgs.HasFlag(args, "--json");
        var rootPath = args[1];
        var output = CliArgs.Option(args, "-o") ?? "graph.json";
        var options = CreateOptions(rootPath, args);
        var scanner = ScannerBuilder.Create();
        var progress = jsonMode ? null : new ConsoleProgress();
        var result = await scanner.ScanAsync(rootPath, options, default, progress);
        await JsonExport.SaveAsync(result, output);

        if (jsonMode)
        {
            JsonOutput.WriteScanSummary("scan", output, result.Statistics, result.Metadata, timer);
        }
        else
        {
            PrintSummary(result, output);
        }

        return 0;
    }

    internal static ScanOptions CreateOptions(string rootPath, IReadOnlyList<string> args)
    {
        var normalizedRoot = Path.GetFullPath(rootPath);
        var excluded = CliArgs.Options(args, "--exclude");
        var excludeFiles = CliArgs.Options(args, "--exclude-file");
        var fileExcluded = IgnoreFile.Load(normalizedRoot, excludeFiles.Count > 0 ? excludeFiles : null);
        var combined = excluded.Concat(fileExcluded)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ScanOptions(normalizedRoot, ExcludedPaths: combined);
    }

    internal static void PrintSummary(ScanResult result, string output)
    {
        Console.WriteLine($"Graph written to {output}");
        Console.WriteLine($"Repositories: {result.Statistics.RepositoryCount}");
        Console.WriteLine($"Projects: {result.Statistics.ProjectCount}");
        Console.WriteLine($"Endpoints: {result.Statistics.EndpointCount}");
        Console.WriteLine($"Methods: {result.Statistics.MethodCount}");
        Console.WriteLine($"HTTP edges: {result.Statistics.HttpEdgeCount}");
        Console.WriteLine($"Tables: {result.Statistics.TableCount}");
        Console.WriteLine($"Cross-repo links: {result.Statistics.CrossRepoLinkCount}");
        Console.WriteLine($"Ambiguous edges: {result.Statistics.AmbiguousEdgeCount}");
    }
}
