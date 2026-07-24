namespace Synopsis.Mcp;

/// <summary>
/// Tracks the startup workspace scan so the MCP server can answer the
/// protocol handshake immediately and report indexing progress instead of
/// blocking <c>initialize</c> until Roslyn finishes (MCP clients time out
/// long before a large-workspace scan completes).
/// </summary>
internal sealed class IndexingState
{
    private volatile bool _inProgress;
    private volatile string? _error;

    private IndexingState(bool inProgress, DateTimeOffset? startedAtUtc)
    {
        _inProgress = inProgress;
        StartedAtUtc = startedAtUtc;
    }

    /// <summary>No startup scan pending (graph mode, or state-only start).</summary>
    public static IndexingState Idle { get; } = new(false, null);

    public static IndexingState Started() => new(true, DateTimeOffset.UtcNow);

    public DateTimeOffset? StartedAtUtc { get; }
    public bool InProgress => _inProgress;
    public string? Error => _error;

    public void Complete() => _inProgress = false;

    public void Fail(string error)
    {
        _error = error;
        _inProgress = false;
    }
}
