namespace Synopsis.Mcp;

/// <summary>
/// Diagnostics sink for the MCP server. Always writes to stderr (stdout is
/// reserved for the protocol); optionally tees to a log file configured via
/// <c>--log-file</c> or the <c>SYNOPSIS_LOG_FILE</c> environment variable so
/// external monitors can tail daemon diagnostics.
/// </summary>
internal static class McpLog
{
    private static readonly object Gate = new();
    private static StreamWriter? _file;

    /// <summary>Opens the log file for append. No-op when no path is configured.</summary>
    public static void Configure(string? path)
    {
        path = string.IsNullOrWhiteSpace(path)
            ? Environment.GetEnvironmentVariable("SYNOPSIS_LOG_FILE")
            : path;
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var fullPath = Path.GetFullPath(path);
            if (Path.GetDirectoryName(fullPath) is { Length: > 0 } dir)
                Directory.CreateDirectory(dir);
            // FileShare.ReadWrite: tail -F readers and a second server instance
            // appending must not lock each other out.
            var stream = new FileStream(fullPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            lock (Gate)
            {
                _file?.Dispose();
                _file = new StreamWriter(stream) { AutoFlush = true };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"[mcp] Warning: cannot open log file '{path}': {ex.Message}");
        }
    }

    /// <summary>Writes to stderr and, when configured, appends a UTC-timestamped line to the log file.</summary>
    public static void Write(string message)
    {
        Console.Error.WriteLine(message);
        lock (Gate)
        {
            if (_file is null)
                return;
            try
            {
                _file.WriteLine($"{DateTime.UtcNow:O} {message}");
            }
            catch (IOException)
            {
                // The log file must never take the server down.
            }
        }
    }

    /// <summary>Closes the log file. Used by tests; the process otherwise holds it for its lifetime.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _file?.Dispose();
            _file = null;
        }
    }
}
