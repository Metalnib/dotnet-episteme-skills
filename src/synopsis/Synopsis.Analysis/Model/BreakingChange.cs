using System.Collections.Immutable;

namespace Synopsis.Analysis.Model;

/// <summary>
/// A single classified breaking change between two graph snapshots. Emitted
/// by <see cref="Graph.BreakingChangeClassifier"/>. Consumers (the
/// <c>dotnet-techne-cross-repo-impact</c> skill, chat adapters, etc.) treat
/// this as the deterministic ground truth for "what in this PR is a break".
/// </summary>
public sealed record BreakingChange(
    BreakingChangeKind Kind,
    Severity Severity,
    Certainty Certainty,
    ImmutableArray<string> AffectedNodeIds,
    string BeforeSnippet,
    string AfterSnippet,
    SourceLocation? SourceLocation,
    IReadOnlyDictionary<string, string?> Metadata);

/// <summary>
/// Kinds of break the classifier can emit.
/// <para>
/// Paired "change" kinds (version bump, route change, signature change,
/// table rename) appear when a before + after node pair share a stable
/// logical key. Matching "removed" kinds are emitted for residual
/// unmatched removals after pairing — removals without a replacement are
/// often the most breaking scenario and must not be silently dropped.
/// </para>
/// <para>
/// The last three kinds (<see cref="DtoShapeChange"/>,
/// <see cref="EntityColumnChange"/>, <see cref="SerializationContractChange"/>)
/// are defined so the public contract is stable, but their detection
/// requires richer graph capture (column-level Entity metadata, DTO field
/// tracking, attribute-level contract tracking) that is not present yet.
/// Consumers will not currently see these emitted.
/// </para>
/// </summary>
public enum BreakingChangeKind
{
    // Paired change kinds (detected in P0).
    NugetVersionBump,
    EndpointRouteChange,
    EndpointVerbChange,
    ApiSignatureChange,
    TableRename,

    // Removal kinds (detected in P0; emitted from unmatched removals).
    PackageRemoved,
    EndpointRemoved,
    ApiRemoved,
    TableRemoved,

    // Defined but not yet emitted; require passes to capture more detail.
    DtoShapeChange,
    EntityColumnChange,
    SerializationContractChange,
}

/// <summary>
/// Base severity assigned by the classifier. Downstream skills may escalate
/// one level on top of this (e.g. when no compatible downstream PR is
/// found), but they never down-escalate below the classifier's output.
/// </summary>
public enum Severity
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3,
}

/// <summary>Result of a breaking-diff run: the classified changes + summary counts.</summary>
public sealed record BreakingDiffResult(
    ImmutableArray<BreakingChange> Changes,
    DiffStats Stats);

/// <summary>
/// Summary of the diff beneath the classifier, for debugging and report headers.
/// </summary>
public sealed record DiffStats(
    int AddedNodes,
    int RemovedNodes,
    int ChangedNodes,
    int AddedEdges,
    int RemovedEdges,
    int Classified,
    int UnclassifiedAdditions,
    int UnclassifiedRemovals);
