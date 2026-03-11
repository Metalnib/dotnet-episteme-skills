namespace Synopsis.Commands;

internal static class CliArgs
{
    public static string? Option(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
            if (string.Equals(args[i], name, StringComparison.Ordinal))
                return args[i + 1];
        return null;
    }

    public static IReadOnlyList<string> Options(IReadOnlyList<string> args, string name)
    {
        var values = new List<string>();
        for (var i = 0; i < args.Count - 1; i++)
            if (string.Equals(args[i], name, StringComparison.Ordinal))
                values.Add(args[i + 1]);
        return values;
    }

    public static int? IntOption(IReadOnlyList<string> args, string name)
    {
        var value = Option(args, name);
        if (value is null) return null;
        if (int.TryParse(value, out var parsed)) return parsed;
        throw new InvalidOperationException($"Option '{name}' requires an integer value.");
    }

    public static bool HasFlag(IReadOnlyList<string> args, string name) =>
        args.Any(a => string.Equals(a, name, StringComparison.Ordinal));
}
