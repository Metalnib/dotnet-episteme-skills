namespace Synopsis;

internal static class SynopsisSchema
{
    /// <summary>
    /// JSON result-envelope version (CLI <c>--json</c> and the MCP handshake).
    /// Bump only on a backward-incompatible result-shape change, so a wire
    /// consumer can detect the contract instead of silently misparsing.
    /// </summary>
    public const int EnvelopeVersion = 1;
}
