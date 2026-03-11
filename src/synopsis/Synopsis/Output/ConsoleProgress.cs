using Synopsis.Analysis.Model;

namespace Synopsis.Output;

internal sealed class ConsoleProgress : IProgress<ProgressEvent>
{
    private const int MaxRepeatedFailures = 3;
    private readonly Dictionary<string, int> _failureCounts = new(StringComparer.Ordinal);

    public void Report(ProgressEvent value)
    {
        if (SuppressRepeatedFailure(value))
            return;

        var prefix = value.Current.HasValue && value.Total.HasValue
            ? $"[{value.Stage} {value.Current}/{value.Total}]"
            : $"[{value.Stage}]";

        Console.Error.WriteLine($"{prefix} {value.Message}");

        if (string.Equals(value.Stage, "scan", StringComparison.Ordinal)
            && value.Message.StartsWith("Scan complete", StringComparison.Ordinal))
        {
            PrintSuppressedSummary();
        }
    }

    private bool SuppressRepeatedFailure(ProgressEvent value)
    {
        if (!value.Message.StartsWith("Failed to ", StringComparison.Ordinal))
            return false;

        var sep = value.Message.IndexOf(": ", StringComparison.Ordinal);
        var signature = sep >= 0 ? value.Message[(sep + 2)..] : value.Message;

        _failureCounts.TryGetValue(signature, out var count);
        count++;
        _failureCounts[signature] = count;

        if (count <= MaxRepeatedFailures) return false;

        if (count == MaxRepeatedFailures + 1)
        {
            var prefix = value.Current.HasValue && value.Total.HasValue
                ? $"[{value.Stage} {value.Current}/{value.Total}]"
                : $"[{value.Stage}]";
            Console.Error.WriteLine($"{prefix} Suppressing repeated failures: {signature}");
        }

        return true;
    }

    private void PrintSuppressedSummary()
    {
        foreach (var (signature, count) in _failureCounts
            .Where(p => p.Value > MaxRepeatedFailures)
            .OrderByDescending(p => p.Value))
        {
            Console.Error.WriteLine($"[warnings] Suppressed {count - MaxRepeatedFailures} repeated: {signature}");
        }
    }
}
