using System.Collections.Immutable;
using System.Globalization;
using Synopsis.Analysis.Model;

namespace Synopsis.Analysis.Graph;

/// <summary>
/// Classifies a raw <see cref="GraphDiff"/> into typed
/// <see cref="BreakingChange"/> entries. Deterministic — no LLM, no
/// heuristics beyond graph facts. Consumers (skills, chat adapters) rely
/// on its output as ground truth.
/// </summary>
/// <remarks>
/// <para>
/// The classifier works off both <see cref="ScanResult"/>s and the
/// <see cref="GraphDiff"/> derived from them. Many kinds present as
/// <b>Add+Remove pairs</b> because the changed field is part of the node
/// ID hash; the classifier correlates those pairs via stable logical keys
/// held in node metadata (<c>packageId</c>, <c>handler</c>,
/// <c>displayName</c>+parameter count, etc.).
/// </para>
/// <para>
/// Return-type-only signature changes on Method nodes have stable IDs
/// (the doc-comment ID does not include return type), so the classifier
/// compares metadata directly via <see cref="ScanResult.NodesById"/>.
/// </para>
/// <para>
/// After each type-specific pairing step, unmatched removals of the same
/// type are emitted as the corresponding <c>*Removed</c> kind — removing
/// a public endpoint / method / table / package without a replacement is
/// often the most breaking scenario and must not be silently dropped.
/// </para>
/// </remarks>
public static class BreakingChangeClassifier
{
    public static BreakingDiffResult Classify(ScanResult before, ScanResult after)
    {
        // Defensive: cheap, idempotent. Silent skip on a non-adjacency-built
        // snapshot would produce wrong "no changes found" results.
        before = before.NodesById is null ? before.WithAdjacency() : before;
        after = after.NodesById is null ? after.WithAdjacency() : after;

        var diff = GraphDiffer.Compare(before, after);
        var results = new List<BreakingChange>();
        var consumedAdded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var consumedRemoved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ClassifyPackageChanges(diff, results, consumedAdded, consumedRemoved);
        ClassifyEndpointChanges(diff, results, consumedAdded, consumedRemoved);
        ClassifyMethodChanges(diff, before, after, results, consumedAdded, consumedRemoved);
        ClassifyTableChanges(diff, before, after, results, consumedAdded, consumedRemoved);

        var unclassifiedAdditions = diff.AddedNodes.Length - consumedAdded.Count;
        var unclassifiedRemovals = diff.RemovedNodes.Length - consumedRemoved.Count;

        var stats = new DiffStats(
            AddedNodes: diff.AddedNodes.Length,
            RemovedNodes: diff.RemovedNodes.Length,
            ChangedNodes: diff.ChangedNodes.Length,
            AddedEdges: diff.AddedEdges.Length,
            RemovedEdges: diff.RemovedEdges.Length,
            Classified: results.Count,
            UnclassifiedAdditions: unclassifiedAdditions,
            UnclassifiedRemovals: unclassifiedRemovals);

        return new BreakingDiffResult([.. results], stats);
    }


    private static void ClassifyPackageChanges(GraphDiff diff,
        List<BreakingChange> results,
        HashSet<string> consumedAdded, HashSet<string> consumedRemoved)
    {
        var addedPackages = diff.AddedNodes.Where(n => n.Type == NodeType.Package).ToArray();
        var removedPackages = diff.RemovedNodes.Where(n => n.Type == NodeType.Package).ToArray();

        string PackageKey(GraphNode n) =>
            n.Metadata.GetValueOrDefault("packageId") ?? n.DisplayName;

        var addedByKey = addedPackages.ToLookup(PackageKey, StringComparer.OrdinalIgnoreCase);
        var removedByKey = removedPackages.ToLookup(PackageKey, StringComparer.OrdinalIgnoreCase);

        foreach (var group in addedByKey)
        {
            var added = group.ToArray();
            var removed = removedByKey[group.Key].ToArray();
            if (removed.Length == 0) continue;

            // Positional zip over min(added, removed). If groups are
            // asymmetric or size > 1, pairing is heuristic — mark Ambiguous.
            var pairCount = Math.Min(added.Length, removed.Length);
            var heuristic = added.Length > 1 || removed.Length > 1 || added.Length != removed.Length;

            for (var i = 0; i < pairCount; i++)
            {
                var a = added[i];
                var r = removed[i];
                consumedAdded.Add(a.Id);
                consumedRemoved.Add(r.Id);

                var oldVersion = r.Metadata.GetValueOrDefault("version") ?? "?";
                var newVersion = a.Metadata.GetValueOrDefault("version") ?? "?";
                var severity = ClassifyVersionBumpSeverity(oldVersion, newVersion);
                var certainty = heuristic
                    ? Certainty.Ambiguous
                    : MinCertainty(r.Certainty, a.Certainty);

                results.Add(new BreakingChange(
                    Kind: BreakingChangeKind.NugetVersionBump,
                    Severity: severity,
                    Certainty: certainty,
                    AffectedNodeIds: [a.Id, r.Id],
                    BeforeSnippet: $"{group.Key}@{oldVersion}",
                    AfterSnippet: $"{group.Key}@{newVersion}",
                    SourceLocation: null,
                    Metadata: new Dictionary<string, string?>
                    {
                        ["packageId"] = group.Key,
                        ["beforeVersion"] = oldVersion,
                        ["afterVersion"] = newVersion,
                    }));
            }
        }

        // Unmatched removals → PackageRemoved.
        foreach (var r in removedPackages)
        {
            if (consumedRemoved.Contains(r.Id)) continue;
            consumedRemoved.Add(r.Id);

            var packageId = r.Metadata.GetValueOrDefault("packageId") ?? r.DisplayName;
            var oldVersion = r.Metadata.GetValueOrDefault("version") ?? "?";

            results.Add(new BreakingChange(
                Kind: BreakingChangeKind.PackageRemoved,
                Severity: Severity.Low,  // removal is usually intentional; downstream skill may escalate
                Certainty: r.Certainty,
                AffectedNodeIds: [r.Id],
                BeforeSnippet: $"{packageId}@{oldVersion}",
                AfterSnippet: "(removed)",
                SourceLocation: null,
                Metadata: new Dictionary<string, string?>
                {
                    ["packageId"] = packageId,
                    ["beforeVersion"] = oldVersion,
                }));
        }
    }

    private static Severity ClassifyVersionBumpSeverity(string before, string after)
    {
        // Upgrade across major: High. Downgrade across major: Medium — suspicious,
        // often worse signal than upgrade. Minor/patch: Low. Unparseable: Medium.
        if (TryParseMajor(before, out var beforeMajor) && TryParseMajor(after, out var afterMajor))
        {
            if (afterMajor > beforeMajor) return Severity.High;
            if (afterMajor < beforeMajor) return Severity.Medium;
            return Severity.Low;
        }
        return Severity.Medium;
    }

    private static bool TryParseMajor(string version, out int major)
    {
        major = 0;
        var dot = version.IndexOf('.');
        var head = dot > 0 ? version[..dot] : version;
        // Strip pre-release suffix: "1-beta" → "1".
        var dash = head.IndexOf('-');
        if (dash > 0) head = head[..dash];
        // Reject signs so "-1.0.0" doesn't parse as -1.
        return int.TryParse(head, NumberStyles.None, CultureInfo.InvariantCulture, out major);
    }


    private static void ClassifyEndpointChanges(GraphDiff diff,
        List<BreakingChange> results,
        HashSet<string> consumedAdded, HashSet<string> consumedRemoved)
    {
        var addedEndpoints = diff.AddedNodes.Where(n => n.Type == NodeType.Endpoint).ToArray();
        var removedEndpoints = diff.RemovedNodes.Where(n => n.Type == NodeType.Endpoint).ToArray();

        // Correlate by handler metadata — the one field that identifies the
        // same logical endpoint across route/verb edits.
        var addedByHandler = addedEndpoints
            .Where(n => !string.IsNullOrWhiteSpace(n.Metadata.GetValueOrDefault("handler")))
            .ToLookup(n => n.Metadata["handler"]!, StringComparer.OrdinalIgnoreCase);

        var removedByHandler = removedEndpoints
            .Where(n => !string.IsNullOrWhiteSpace(n.Metadata.GetValueOrDefault("handler")))
            .ToLookup(n => n.Metadata["handler"]!, StringComparer.OrdinalIgnoreCase);

        foreach (var group in addedByHandler)
        {
            var added = group.ToArray();
            var removed = removedByHandler[group.Key].ToArray();
            if (removed.Length == 0) continue;

            var pairCount = Math.Min(added.Length, removed.Length);
            var heuristic = added.Length > 1 || removed.Length > 1 || added.Length != removed.Length;

            for (var i = 0; i < pairCount; i++)
            {
                var a = added[i];
                var r = removed[i];

                var oldRoute = r.Metadata.GetValueOrDefault("route") ?? "?";
                var newRoute = a.Metadata.GetValueOrDefault("route") ?? "?";
                var oldVerb = r.Metadata.GetValueOrDefault("verb") ?? "?";
                var newVerb = a.Metadata.GetValueOrDefault("verb") ?? "?";

                // Given endpoint ID = hash(project, verb, route, handler),
                // same handler + same verb + same route would mean same ID
                // and thus no add/remove pair to begin with. If we ever hit
                // that branch it's an invariant violation worth skipping.
                BreakingChangeKind kind;
                string beforeSnippet, afterSnippet;
                if (!string.Equals(oldVerb, newVerb, StringComparison.OrdinalIgnoreCase))
                {
                    kind = BreakingChangeKind.EndpointVerbChange;
                    beforeSnippet = $"{oldVerb} {oldRoute}";
                    afterSnippet = $"{newVerb} {newRoute}";
                }
                else if (!string.Equals(oldRoute, newRoute, StringComparison.OrdinalIgnoreCase))
                {
                    kind = BreakingChangeKind.EndpointRouteChange;
                    beforeSnippet = oldRoute;
                    afterSnippet = newRoute;
                }
                else
                {
                    continue;  // invariant violation — see comment above
                }

                consumedAdded.Add(a.Id);
                consumedRemoved.Add(r.Id);
                var certainty = heuristic
                    ? Certainty.Ambiguous
                    : MinCertainty(r.Certainty, a.Certainty);

                results.Add(new BreakingChange(
                    Kind: kind,
                    Severity: Severity.Critical,
                    Certainty: certainty,
                    AffectedNodeIds: [a.Id, r.Id],
                    BeforeSnippet: beforeSnippet,
                    AfterSnippet: afterSnippet,
                    SourceLocation: a.Location,
                    Metadata: new Dictionary<string, string?>
                    {
                        ["handler"] = group.Key,
                        ["beforeVerb"] = oldVerb,
                        ["afterVerb"] = newVerb,
                        ["beforeRoute"] = oldRoute,
                        ["afterRoute"] = newRoute,
                    }));
            }
        }

        // Unmatched removals → EndpointRemoved.
        foreach (var r in removedEndpoints)
        {
            if (consumedRemoved.Contains(r.Id)) continue;
            consumedRemoved.Add(r.Id);

            var oldRoute = r.Metadata.GetValueOrDefault("route") ?? "?";
            var oldVerb = r.Metadata.GetValueOrDefault("verb") ?? "?";

            results.Add(new BreakingChange(
                Kind: BreakingChangeKind.EndpointRemoved,
                Severity: Severity.Critical,  // clients will hit 404
                Certainty: r.Certainty,
                AffectedNodeIds: [r.Id],
                BeforeSnippet: $"{oldVerb} {oldRoute}",
                AfterSnippet: "(removed)",
                SourceLocation: r.Location,
                Metadata: new Dictionary<string, string?>
                {
                    ["handler"] = r.Metadata.GetValueOrDefault("handler"),
                    ["beforeVerb"] = oldVerb,
                    ["beforeRoute"] = oldRoute,
                }));
        }
    }


    private static void ClassifyMethodChanges(GraphDiff diff,
        ScanResult before, ScanResult after, List<BreakingChange> results,
        HashSet<string> consumedAdded, HashSet<string> consumedRemoved)
    {
        var addedMethods = diff.AddedNodes.Where(n => n.Type == NodeType.Method).ToArray();
        var removedMethods = diff.RemovedNodes.Where(n => n.Type == NodeType.Method).ToArray();

        // Case 1: parameter or name edits → different method IDs, paired by
        // (display name, parameter count). Arity keying dramatically reduces
        // cross-pairing of overloads, though it is still imperfect —
        // identical-arity overloads can only be paired positionally and get
        // Certainty.Ambiguous.
        var addedByKey = addedMethods.ToLookup(MethodKey, StringComparer.Ordinal);
        var removedByKey = removedMethods.ToLookup(MethodKey, StringComparer.Ordinal);

        foreach (var group in addedByKey)
        {
            var added = group.ToArray();
            var removed = removedByKey[group.Key].ToArray();
            if (removed.Length == 0) continue;

            var pairCount = Math.Min(added.Length, removed.Length);
            var heuristic = added.Length > 1 || removed.Length > 1 || added.Length != removed.Length;

            for (var i = 0; i < pairCount; i++)
            {
                var a = added[i];
                var r = removed[i];
                consumedAdded.Add(a.Id);
                consumedRemoved.Add(r.Id);

                var certainty = heuristic
                    ? Certainty.Ambiguous
                    : MinCertainty(r.Certainty, a.Certainty);

                results.Add(new BreakingChange(
                    Kind: BreakingChangeKind.ApiSignatureChange,
                    Severity: Severity.High,
                    Certainty: certainty,
                    AffectedNodeIds: [a.Id, r.Id],
                    BeforeSnippet: SignatureSnippet(r),
                    AfterSnippet: SignatureSnippet(a),
                    SourceLocation: a.Location,
                    Metadata: new Dictionary<string, string?>
                    {
                        ["methodDisplay"] = a.DisplayName,
                        ["change"] = "parameters-or-name",
                    }));
            }
        }

        // Case 2: return-type-only edits keep the node ID stable — compare
        // metadata directly via NodesById. GraphDiffer does not diff
        // metadata, so ChangedNodes would miss this.
        foreach (var afterNode in after.Nodes)
        {
            if (afterNode.Type != NodeType.Method) continue;
            if (consumedAdded.Contains(afterNode.Id)) continue;  // already paired via Case 1
            if (!before.NodesById!.TryGetValue(afterNode.Id, out var beforeNode)) continue;

            var beforeReturns = beforeNode.Metadata.GetValueOrDefault("returns");
            var afterReturns = afterNode.Metadata.GetValueOrDefault("returns");
            if (string.IsNullOrWhiteSpace(beforeReturns) || string.IsNullOrWhiteSpace(afterReturns)) continue;
            if (string.Equals(beforeReturns, afterReturns, StringComparison.Ordinal)) continue;

            results.Add(new BreakingChange(
                Kind: BreakingChangeKind.ApiSignatureChange,
                Severity: Severity.High,
                Certainty: MinCertainty(beforeNode.Certainty, afterNode.Certainty),
                AffectedNodeIds: [afterNode.Id],
                BeforeSnippet: $"{afterNode.DisplayName} → {beforeReturns}",
                AfterSnippet: $"{afterNode.DisplayName} → {afterReturns}",
                SourceLocation: afterNode.Location,
                Metadata: new Dictionary<string, string?>
                {
                    ["methodDisplay"] = afterNode.DisplayName,
                    ["change"] = "return-type",
                    ["beforeReturns"] = beforeReturns,
                    ["afterReturns"] = afterReturns,
                }));
        }

        // Unmatched removals → ApiRemoved.
        foreach (var r in removedMethods)
        {
            if (consumedRemoved.Contains(r.Id)) continue;
            consumedRemoved.Add(r.Id);

            results.Add(new BreakingChange(
                Kind: BreakingChangeKind.ApiRemoved,
                Severity: Severity.High,  // compile-time break for callers
                Certainty: r.Certainty,
                AffectedNodeIds: [r.Id],
                BeforeSnippet: SignatureSnippet(r),
                AfterSnippet: "(removed)",
                SourceLocation: r.Location,
                Metadata: new Dictionary<string, string?>
                {
                    ["methodDisplay"] = r.DisplayName,
                }));
        }
    }

    /// <summary>Method key: <c>displayName|paramCount</c> (arity from fullName).</summary>
    private static string MethodKey(GraphNode method)
    {
        var paramCount = ParameterCount(method);
        return paramCount is null
            ? $"{method.DisplayName}|?"
            : $"{method.DisplayName}|{paramCount.Value}";
    }

    private static int? ParameterCount(GraphNode method)
    {
        var full = method.Metadata.GetValueOrDefault("fullName");
        if (string.IsNullOrWhiteSpace(full)) return null;

        var open = full.IndexOf('(');
        if (open < 0) return null;
        var close = full.LastIndexOf(')');
        if (close <= open) return null;

        var inside = full.AsSpan(open + 1, close - open - 1).Trim();
        if (inside.Length == 0) return 0;

        // Count commas at depth 0 so we don't miscount generics like IDictionary<K,V>.
        int depth = 0, commas = 0;
        foreach (var c in inside)
        {
            if (c is '<' or '[') depth++;
            else if (c is '>' or ']') depth--;
            else if (c == ',' && depth == 0) commas++;
        }
        return commas + 1;
    }

    private static string SignatureSnippet(GraphNode method)
    {
        var full = method.Metadata.GetValueOrDefault("fullName");
        return !string.IsNullOrWhiteSpace(full) ? full : method.DisplayName;
    }


    private static void ClassifyTableChanges(GraphDiff diff,
        ScanResult before, ScanResult after, List<BreakingChange> results,
        HashSet<string> consumedAdded, HashSet<string> consumedRemoved)
    {
        var addedTables = diff.AddedNodes.Where(n => n.Type == NodeType.Table).ToArray();
        var removedTables = diff.RemovedNodes.Where(n => n.Type == NodeType.Table).ToArray();

        // O(1) lookups instead of linear scans on every edge.
        var addedTableIds = addedTables.Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removedTableIds = removedTables.Select(n => n.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // TODO: takes only the first mapping per entity. Owned entities, TPT
        // and TPC inheritance patterns map a single entity to multiple tables;
        // we drop those on the floor here. Acceptable for P0 but worth
        // expanding when we see it in the wild.
        var removedMapsTo = before.Edges
            .Where(e => e.Type == EdgeType.MapsToTable && removedTableIds.Contains(e.TargetId))
            .GroupBy(e => e.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().TargetId, StringComparer.OrdinalIgnoreCase);

        var addedMapsTo = after.Edges
            .Where(e => e.Type == EdgeType.MapsToTable && addedTableIds.Contains(e.TargetId))
            .GroupBy(e => e.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().TargetId, StringComparer.OrdinalIgnoreCase);

        var removedById = removedTables.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        var addedById = addedTables.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var (entityId, removedTableId) in removedMapsTo)
        {
            if (!addedMapsTo.TryGetValue(entityId, out var addedTableId)) continue;
            if (!removedById.TryGetValue(removedTableId, out var removedTable)) continue;
            if (!addedById.TryGetValue(addedTableId, out var addedTable)) continue;

            consumedRemoved.Add(removedTable.Id);
            consumedAdded.Add(addedTable.Id);

            results.Add(new BreakingChange(
                Kind: BreakingChangeKind.TableRename,
                Severity: Severity.Critical,
                Certainty: MinCertainty(removedTable.Certainty, addedTable.Certainty),
                AffectedNodeIds: [addedTable.Id, removedTable.Id],
                BeforeSnippet: removedTable.DisplayName,
                AfterSnippet: addedTable.DisplayName,
                SourceLocation: addedTable.Location,
                Metadata: new Dictionary<string, string?>
                {
                    ["entity"] = entityId,
                    ["beforeTable"] = removedTable.DisplayName,
                    ["afterTable"] = addedTable.DisplayName,
                }));
        }

        // Unmatched removals → TableRemoved.
        foreach (var r in removedTables)
        {
            if (consumedRemoved.Contains(r.Id)) continue;
            consumedRemoved.Add(r.Id);

            results.Add(new BreakingChange(
                Kind: BreakingChangeKind.TableRemoved,
                Severity: Severity.Critical,  // queries will fail
                Certainty: r.Certainty,
                AffectedNodeIds: [r.Id],
                BeforeSnippet: r.DisplayName,
                AfterSnippet: "(removed)",
                SourceLocation: r.Location,
                Metadata: new Dictionary<string, string?>
                {
                    ["beforeTable"] = r.DisplayName,
                }));
        }
    }


    /// <summary>Returns the lower (weaker) of two certainties.</summary>
    private static Certainty MinCertainty(Certainty a, Certainty b) =>
        a < b ? a : b;
}
