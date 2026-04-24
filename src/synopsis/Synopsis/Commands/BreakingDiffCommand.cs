using System.Diagnostics;
using System.Text.Json;
using Synopsis.Analysis.Graph;
using Synopsis.Analysis.Model;
using Synopsis.Output;

namespace Synopsis.Commands;

/// <summary>
/// <c>synopsis breaking-diff &lt;before.json&gt; &lt;after.json&gt;</c> — run the
/// <see cref="BreakingChangeClassifier"/> against two graph snapshots and
/// emit classified breaking changes (typed kinds + severity + affected
/// nodes). Feeds the <c>dotnet-techne-cross-repo-impact</c> skill.
/// </summary>
internal static class BreakingDiffCommand
{
    private static readonly IReadOnlySet<string> Flags = new HashSet<string>(StringComparer.Ordinal) { "--json" };
    private static readonly IReadOnlySet<string> Options = new HashSet<string>(StringComparer.Ordinal) { "-o" };

    public static async Task<int> RunAsync(string[] args)
    {
        var positionals = CliArgs.Positionals(args, Flags, Options);
        if (positionals.Count < 2)
        {
            Console.Error.WriteLine("Usage: synopsis breaking-diff <before.json> <after.json> [--json] [-o report.json]");
            return 1;
        }

        var timer = Stopwatch.StartNew();
        var jsonMode = CliArgs.HasFlag(args, "--json");
        var beforePath = positionals[0];
        var afterPath = positionals[1];
        var output = CliArgs.Option(args, "-o");

        var before = await JsonExport.LoadAsync(beforePath);
        var after = await JsonExport.LoadAsync(afterPath);

        var result = BreakingChangeClassifier.Classify(before, after);

        if (output is not null)
        {
            await File.WriteAllTextAsync(output,
                JsonSerializer.Serialize(result, SynopsisJsonContext.Default.BreakingDiffResult));
            Console.Error.WriteLine($"[breaking-diff] Report written to {output}");
        }

        if (jsonMode)
        {
            JsonOutput.WriteBreakingDiff("breaking-diff", result, timer);
            return 0;
        }

        PrintHuman(result, beforePath, afterPath);
        return 0;
    }

    private static void PrintHuman(BreakingDiffResult result, string beforePath, string afterPath)
    {
        Console.WriteLine($"=== Breaking-diff: {Path.GetFileName(beforePath)} -> {Path.GetFileName(afterPath)} ===");
        Console.WriteLine();

        var byKind = result.Changes.GroupBy(c => c.Kind).OrderBy(g => g.Key);
        var bySeverity = result.Changes.GroupBy(c => c.Severity).ToDictionary(g => g.Key, g => g.Count());

        Console.WriteLine($"Classified: {result.Stats.Classified} change(s)");
        Console.WriteLine($"  Critical: {bySeverity.GetValueOrDefault(Severity.Critical)}");
        Console.WriteLine($"  High:     {bySeverity.GetValueOrDefault(Severity.High)}");
        Console.WriteLine($"  Medium:   {bySeverity.GetValueOrDefault(Severity.Medium)}");
        Console.WriteLine($"  Low:      {bySeverity.GetValueOrDefault(Severity.Low)}");
        Console.WriteLine();
        Console.WriteLine($"Unclassified additions: {result.Stats.UnclassifiedAdditions}");
        Console.WriteLine($"Unclassified removals:  {result.Stats.UnclassifiedRemovals}");
        Console.WriteLine();

        if (result.Changes.Length == 0)
        {
            Console.WriteLine("No classified breaking changes detected.");
            return;
        }

        foreach (var group in byKind)
        {
            Console.WriteLine($"[{group.Key}] ({group.Count()})");
            foreach (var change in group.Take(10))
                Console.WriteLine($"  {change.Severity,-8}  {change.BeforeSnippet}  ->  {change.AfterSnippet}");
            if (group.Count() > 10)
                Console.WriteLine($"  ... and {group.Count() - 10} more");
            Console.WriteLine();
        }
    }
}
