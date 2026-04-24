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

    /// <summary>
    /// Extract positional args (everything that is not a flag or the value of
    /// an option). <c>args[0]</c> is always the command verb and is skipped.
    /// <paramref name="flags"/> are standalone boolean flags; <paramref name="options"/>
    /// are options that consume the next token as their value.
    /// </summary>
    /// <remarks>
    /// Lets callers mix positionals and flags freely — e.g.
    /// <c>synopsis breaking-diff --json before.json after.json</c> parses
    /// the same as <c>synopsis breaking-diff before.json after.json --json</c>.
    /// </remarks>
    public static IReadOnlyList<string> Positionals(
        IReadOnlyList<string> args,
        IReadOnlySet<string>? flags = null,
        IReadOnlySet<string>? options = null)
    {
        var result = new List<string>();
        for (var i = 1; i < args.Count; i++)
        {
            var a = args[i];
            if (flags is not null && flags.Contains(a)) continue;
            if (options is not null && options.Contains(a))
            {
                i++;  // skip the option's value token
                continue;
            }
            result.Add(a);
        }
        return result;
    }
}
