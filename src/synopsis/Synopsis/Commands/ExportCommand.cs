using Synopsis.Analysis;
using Synopsis.Output;

namespace Synopsis.Commands;

internal static class ExportCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("Usage: synopsis export json|csv|jsonl <rootPath> -o <file|folder> [--exclude <path> ...]");
            return 1;
        }

        var format = args[1].ToLowerInvariant();
        var rootPath = args[2];
        var output = CliArgs.Option(args, "-o");
        if (string.IsNullOrWhiteSpace(output))
        {
            Console.Error.WriteLine("-o is required.");
            return 1;
        }

        var options = ScanCommand.CreateOptions(rootPath, args);
        var scanner = ScannerBuilder.Create();
        var result = await scanner.ScanAsync(rootPath, options, default, new ConsoleProgress());

        switch (format)
        {
            case "json":
                await JsonExport.SaveAsync(result, output);
                break;
            case "csv":
                await CsvExport.SaveAsync(result, output);
                break;
            case "jsonl":
                await JsonlWriter.WriteAsync(result, output);
                break;
            default:
                Console.Error.WriteLine($"Unsupported format '{format}'. Use json, csv, or jsonl.");
                return 1;
        }

        ScanCommand.PrintSummary(result, output);
        return 0;
    }
}
