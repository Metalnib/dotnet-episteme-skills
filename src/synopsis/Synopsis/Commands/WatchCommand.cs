using Synopsis.Analysis;
using Synopsis.Analysis.Model;
using Synopsis.Output;

namespace Synopsis.Commands;

internal static class WatchCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: synopsis watch <rootPath> [-o graph.json] [--debounce-ms 1500]");
            return 1;
        }

        var rootPath = args[1];
        var output = CliArgs.Option(args, "-o") ?? "graph.json";
        var debounceMs = CliArgs.IntOption(args, "--debounce-ms") ?? 1500;
        if (debounceMs < 100)
        {
            Console.Error.WriteLine("--debounce-ms must be at least 100.");
            return 1;
        }

        var scanner = ScannerBuilder.Create();
        ScanOptions MakeOptions() => ScanCommand.CreateOptions(rootPath, args);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var options = MakeOptions();
        await RunScanAsync(scanner, MakeOptions, output, "initial", cts.Token);

        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = new Lock();
        var reasons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var watcher = new FileSystemWatcher(options.RootPath)
        {
            IncludeSubdirectories = true,
            Filter = "*",
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.CreationTime
        };

        void Trigger(string kind, string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var snap = options;
            if (!ShouldRescan(path, snap)) return;
            lock (sync)
            {
                reasons.Add($"{kind}: {Path.GetRelativePath(snap.RootPath, Path.GetFullPath(path))}");
                signal.TrySetResult();
            }
        }

        watcher.Changed += (_, e) => Trigger(e.ChangeType.ToString(), e.FullPath);
        watcher.Created += (_, e) => Trigger(e.ChangeType.ToString(), e.FullPath);
        watcher.Deleted += (_, e) => Trigger(e.ChangeType.ToString(), e.FullPath);
        watcher.Renamed += (_, e) => { Trigger("Renamed", e.OldFullPath); Trigger("Renamed", e.FullPath); };
        watcher.EnableRaisingEvents = true;

        Console.Error.WriteLine($"[watch] Watching {options.RootPath} (debounce {debounceMs}ms). Ctrl+C to stop.");

        try
        {
            while (!cts.IsCancellationRequested)
            {
                await signal.Task.WaitAsync(cts.Token);
                await Task.Delay(debounceMs, cts.Token);

                HashSet<string> batch;
                lock (sync)
                {
                    batch = new HashSet<string>(reasons, StringComparer.OrdinalIgnoreCase);
                    reasons.Clear();
                    signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }

                var summary = string.Join(", ", batch.Take(5));
                if (batch.Count > 5) summary += $", +{batch.Count - 5} more";

                await RunScanAsync(scanner, MakeOptions, output, summary, cts.Token);
                options = MakeOptions();
            }
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("[watch] Stopped.");
        }

        return 0;
    }

    private static async Task RunScanAsync(WorkspaceScanner scanner, Func<ScanOptions> makeOptions,
        string output, string reason, CancellationToken ct)
    {
        var options = makeOptions();
        Console.Error.WriteLine($"[watch] Scanning: {reason}");
        var result = await scanner.ScanAsync(options.RootPath, options, ct, new ConsoleProgress());
        await JsonExport.SaveAsync(result, output, ct);
        ScanCommand.PrintSummary(result, output);
    }

    private static bool ShouldRescan(string path, ScanOptions options)
    {
        var full = Path.GetFullPath(path);
        if (!Paths.IsUnder(full, options.RootPath)) return false;
        if (Path.GetFileName(full).Equals(".synopsisignore", StringComparison.OrdinalIgnoreCase))
            return true;
        if (Paths.IsExcluded(full, options.RootPath, options.ExcludedPaths)) return false;

        var ext = Path.GetExtension(full);
        return ext.Equals(".cs", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".props", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".targets", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".json", StringComparison.OrdinalIgnoreCase);
    }
}
