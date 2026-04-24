using Synopsis.Analysis.Graph;
using Synopsis.Analysis.Model;

namespace Synopsis.Tests;

public sealed class BreakingChangeClassifierTests
{
    // --- NugetVersionBump ---

    [Fact]
    public void NugetVersionBump_MajorBump_IsHigh()
    {
        var before = MakeGraph(AddPackage("Serilog", "2.0.0"));
        var after = MakeGraph(AddPackage("Serilog", "3.0.0"));

        var result = BreakingChangeClassifier.Classify(before, after);

        var change = Assert.Single(result.Changes);
        Assert.Equal(BreakingChangeKind.NugetVersionBump, change.Kind);
        Assert.Equal(Severity.High, change.Severity);
        Assert.Equal("Serilog@2.0.0", change.BeforeSnippet);
        Assert.Equal("Serilog@3.0.0", change.AfterSnippet);
        Assert.Equal("Serilog", change.Metadata["packageId"]);
        Assert.Equal("2.0.0", change.Metadata["beforeVersion"]);
        Assert.Equal("3.0.0", change.Metadata["afterVersion"]);
    }

    [Fact]
    public void NugetVersionBump_MinorBump_IsLow()
    {
        var before = MakeGraph(AddPackage("Serilog", "3.0.0"));
        var after = MakeGraph(AddPackage("Serilog", "3.1.0"));

        var change = Assert.Single(BreakingChangeClassifier.Classify(before, after).Changes);
        Assert.Equal(Severity.Low, change.Severity);
    }

    [Fact]
    public void NugetVersionBump_PatchBump_IsLow()
    {
        var before = MakeGraph(AddPackage("Serilog", "3.1.1"));
        var after = MakeGraph(AddPackage("Serilog", "3.1.2"));

        Assert.Equal(Severity.Low,
            Assert.Single(BreakingChangeClassifier.Classify(before, after).Changes).Severity);
    }

    [Fact]
    public void NugetVersionBump_UnparseableVersion_IsMedium()
    {
        var before = MakeGraph(AddPackage("Serilog", "beta-preview"));
        var after = MakeGraph(AddPackage("Serilog", "ga"));

        Assert.Equal(Severity.Medium,
            Assert.Single(BreakingChangeClassifier.Classify(before, after).Changes).Severity);
    }

    [Fact]
    public void NugetVersionBump_MultiplePackagesBumped_EmitsOnePerPackage()
    {
        var before = MakeGraph(AddPackage("A", "1.0.0"), AddPackage("B", "2.0.0"));
        var after = MakeGraph(AddPackage("A", "2.0.0"), AddPackage("B", "3.0.0"));

        var changes = BreakingChangeClassifier.Classify(before, after).Changes;

        Assert.Equal(2, changes.Length);
        Assert.All(changes, c => Assert.Equal(BreakingChangeKind.NugetVersionBump, c.Kind));
    }

    [Fact]
    public void NugetVersionBump_AddedOnly_StaysUnclassified()
    {
        // Added but no matching removed → not a bump; counts as unclassified
        // addition (brand-new dependency, not a break on its own).
        var before = MakeGraph();
        var after = MakeGraph(AddPackage("Serilog", "3.0.0"));

        var result = BreakingChangeClassifier.Classify(before, after);
        Assert.Empty(result.Changes);
        Assert.Equal(1, result.Stats.UnclassifiedAdditions);
    }

    [Fact]
    public void NugetVersionBump_Downgrade_IsMedium()
    {
        // Major downgrade is suspicious — often worse signal than upgrade —
        // so bump to Medium rather than Low.
        var before = MakeGraph(AddPackage("Serilog", "3.0.0"));
        var after = MakeGraph(AddPackage("Serilog", "2.0.0"));

        var change = Assert.Single(BreakingChangeClassifier.Classify(before, after).Changes);
        Assert.Equal(Severity.Medium, change.Severity);
    }

    [Fact]
    public void NugetVersionBump_NegativeMajor_DoesNotParse()
    {
        // Regression: int.TryParse accepts "-1" by default; ensure we reject
        // signed leading digits so garbage versions route to Medium, not to
        // an accidental Severity.Low via (0 > -1).
        var before = MakeGraph(AddPackage("Serilog", "-1.0.0"));
        var after = MakeGraph(AddPackage("Serilog", "2.0.0"));

        var change = Assert.Single(BreakingChangeClassifier.Classify(before, after).Changes);
        Assert.Equal(Severity.Medium, change.Severity);
    }

    [Fact]
    public void PackageRemoved_UnmatchedRemoval_EmittedAsLow()
    {
        // Removing a package without a replacement is informational by
        // default; downstream skills can escalate. AffectedNodeIds carries
        // the removed node's ID so reporters can fetch the old snippet.
        var before = MakeGraph(AddPackage("Serilog", "3.1.1"));
        var after = MakeGraph();

        var change = Assert.Single(BreakingChangeClassifier.Classify(before, after).Changes);
        Assert.Equal(BreakingChangeKind.PackageRemoved, change.Kind);
        Assert.Equal(Severity.Low, change.Severity);
        Assert.Equal("Serilog@3.1.1", change.BeforeSnippet);
        Assert.Equal("(removed)", change.AfterSnippet);
        Assert.Single(change.AffectedNodeIds);
    }

    // --- Endpoint route / verb changes ---

    [Fact]
    public void EndpointRouteChange_SameHandlerDifferentRoute_IsCritical()
    {
        var before = MakeGraph(AddEndpoint("ep1", "GET", "/orders", handler: "method:M.Get"));
        var after = MakeGraph(AddEndpoint("ep2", "GET", "/v2/orders", handler: "method:M.Get"));

        var change = Assert.Single(BreakingChangeClassifier.Classify(before, after).Changes);
        Assert.Equal(BreakingChangeKind.EndpointRouteChange, change.Kind);
        Assert.Equal(Severity.Critical, change.Severity);
        Assert.Equal("/orders", change.BeforeSnippet);
        Assert.Equal("/v2/orders", change.AfterSnippet);
    }

    [Fact]
    public void EndpointVerbChange_SameHandlerDifferentVerb_IsCritical()
    {
        var before = MakeGraph(AddEndpoint("ep1", "POST", "/orders", handler: "method:M.Upsert"));
        var after = MakeGraph(AddEndpoint("ep2", "PUT", "/orders", handler: "method:M.Upsert"));

        var change = Assert.Single(BreakingChangeClassifier.Classify(before, after).Changes);
        Assert.Equal(BreakingChangeKind.EndpointVerbChange, change.Kind);
        Assert.Equal(Severity.Critical, change.Severity);
        Assert.Equal("POST /orders", change.BeforeSnippet);
        Assert.Equal("PUT /orders", change.AfterSnippet);
    }

    [Fact]
    public void EndpointChange_DifferentHandler_EmitsRemovedForResidual()
    {
        // Two endpoints with entirely different handlers — no pairing
        // possible. The removed endpoint becomes EndpointRemoved. The added
        // endpoint is a brand-new endpoint (not a break on its own) and
        // stays as an unclassified addition.
        var before = MakeGraph(AddEndpoint("ep1", "GET", "/orders", handler: "method:A.Get"));
        var after = MakeGraph(AddEndpoint("ep2", "GET", "/orders", handler: "method:B.Get"));

        var result = BreakingChangeClassifier.Classify(before, after);
        var change = Assert.Single(result.Changes);
        Assert.Equal(BreakingChangeKind.EndpointRemoved, change.Kind);
        Assert.Equal(Severity.Critical, change.Severity);
        Assert.Equal("GET /orders", change.BeforeSnippet);
        Assert.Equal("(removed)", change.AfterSnippet);
        Assert.Equal(1, result.Stats.UnclassifiedAdditions);
    }

    [Fact]
    public void EndpointRemoved_UnmatchedRemoval_EmittedAsCritical()
    {
        var before = MakeGraph(AddEndpoint("ep1", "GET", "/orders", handler: "method:M.Get"));
        var after = MakeGraph();

        var change = Assert.Single(BreakingChangeClassifier.Classify(before, after).Changes);
        Assert.Equal(BreakingChangeKind.EndpointRemoved, change.Kind);
        Assert.Equal(Severity.Critical, change.Severity);
        Assert.Equal("GET /orders", change.BeforeSnippet);
        Assert.Equal("(removed)", change.AfterSnippet);
    }

    // --- ApiSignatureChange ---

    [Fact]
    public void ApiSignatureChange_ParameterTypeEdit_SameArity_IsPairedAsHigh()
    {
        // Same arity, parameter type changed (int → long). Doc-comment IDs
        // differ, so the method appears in both Added and Removed, and the
        // classifier pairs them via (displayName, arity=1).
        var before = MakeGraph(AddMethod("method:old-id", "Orders.GetAll",
            fullName: "Orders.GetAll(int id)", returns: "Order"));
        var after = MakeGraph(AddMethod("method:new-id", "Orders.GetAll",
            fullName: "Orders.GetAll(long id)", returns: "Order"));

        var change = Assert.Single(BreakingChangeClassifier.Classify(before, after).Changes);
        Assert.Equal(BreakingChangeKind.ApiSignatureChange, change.Kind);
        Assert.Equal(Severity.High, change.Severity);
        Assert.Equal("parameters-or-name", change.Metadata["change"]);
    }

    [Fact]
    public void ApiSignatureChange_ArityChange_BecomesRemovedPlusAddition()
    {
        // Adding a parameter changes arity: old arity-0 method has no arity-0
        // counterpart in the new graph, so it becomes ApiRemoved. The new
        // arity-1 method has no before-side arity-1 match, so it stays as an
        // unclassified addition. Arguably equivalent to the old "signature
        // change" framing but semantically more precise: one API is gone, a
        // different one appeared.
        var before = MakeGraph(AddMethod("method:old-id", "Orders.GetAll",
            fullName: "Orders.GetAll()", returns: "IEnumerable<Order>"));
        var after = MakeGraph(AddMethod("method:new-id", "Orders.GetAll",
            fullName: "Orders.GetAll(int tenantId)", returns: "IEnumerable<Order>"));

        var result = BreakingChangeClassifier.Classify(before, after);
        var change = Assert.Single(result.Changes);
        Assert.Equal(BreakingChangeKind.ApiRemoved, change.Kind);
        Assert.Equal("Orders.GetAll()", change.BeforeSnippet);
        Assert.Equal(1, result.Stats.UnclassifiedAdditions);
    }

    [Fact]
    public void ApiSignatureChange_ReturnTypeOnly_IsDetectedFromChangedNode()
    {
        // Same method ID (return type not in doc comment ID), metadata
        // differs on `returns`.
        var before = MakeGraph(AddMethod("method:same-id", "Orders.Count",
            fullName: "Orders.Count()", returns: "int"));
        var after = MakeGraph(AddMethod("method:same-id", "Orders.Count",
            fullName: "Orders.Count()", returns: "long"));

        var change = Assert.Single(BreakingChangeClassifier.Classify(before, after).Changes);
        Assert.Equal(BreakingChangeKind.ApiSignatureChange, change.Kind);
        Assert.Equal(Severity.High, change.Severity);
        Assert.Equal("return-type", change.Metadata["change"]);
        Assert.Equal("int", change.Metadata["beforeReturns"]);
        Assert.Equal("long", change.Metadata["afterReturns"]);
    }

    [Fact]
    public void ApiSignatureChange_NewOverload_DoesNotCrossPairOverloads()
    {
        // Two Get overloads exist before and remain with stable IDs after
        // (doc-comment IDs are deterministic — unchanged methods keep IDs
        // across scans). After adds a third arity-2 overload. The new
        // overload is in diff.AddedNodes with arity 2; the existing
        // overloads are NOT in the diff at all.
        //
        // Before arity keying was introduced, the display-name-only grouping
        // would have seen three "Orders.Get" added (if IDs happened to be
        // different) and could pair them arbitrarily against the old ones.
        // With (displayName, arity) keying there is nothing to pair against
        // an arity-2 addition, and the new overload stays unclassified.
        var before = MakeGraph(
            AddMethod("method:get-int", "Orders.Get",
                fullName: "Orders.Get(int id)", returns: "Order"),
            AddMethod("method:get-string", "Orders.Get",
                fullName: "Orders.Get(string slug)", returns: "Order"));

        var after = MakeGraph(
            AddMethod("method:get-int", "Orders.Get",
                fullName: "Orders.Get(int id)", returns: "Order"),     // unchanged
            AddMethod("method:get-string", "Orders.Get",
                fullName: "Orders.Get(string slug)", returns: "Order"), // unchanged
            AddMethod("method:get-int-ct", "Orders.Get",
                fullName: "Orders.Get(int id, CancellationToken ct)", returns: "Order"));

        var result = BreakingChangeClassifier.Classify(before, after);
        Assert.Empty(result.Changes);   // no spurious ApiSignatureChange
        Assert.Equal(1, result.Stats.UnclassifiedAdditions);
    }

    [Fact]
    public void ApiSignatureChange_AritiesDifferAcrossOverloads_BecomeRemovedPlusAdditions()
    {
        // Both overloads change arity (add CancellationToken). (displayName,
        // arity) keying separates the before arity-1 methods from the after
        // arity-2 methods → no pairs. Tail-safe: removed methods emit
        // ApiRemoved, added ones stay unclassified.
        var before = MakeGraph(
            AddMethod("method:b1", "Orders.Get",
                fullName: "Orders.Get(int id)", returns: "Order"),
            AddMethod("method:b2", "Orders.Get",
                fullName: "Orders.Get(string slug)", returns: "Order"));

        var after = MakeGraph(
            AddMethod("method:a1", "Orders.Get",
                fullName: "Orders.Get(int id, CancellationToken ct)", returns: "Order"),
            AddMethod("method:a2", "Orders.Get",
                fullName: "Orders.Get(string slug, CancellationToken ct)", returns: "Order"));

        var result = BreakingChangeClassifier.Classify(before, after);
        Assert.Equal(2, result.Changes.Count(c => c.Kind == BreakingChangeKind.ApiRemoved));
        Assert.Equal(2, result.Stats.UnclassifiedAdditions);
    }

    [Fact]
    public void ApiSignatureChange_SameArityMultipleOverloads_PairAsAmbiguous()
    {
        // Two overloads where arity is preserved on both sides but parameter
        // types change → arity key matches. Group size >1 → positional
        // pairing is heuristic → certainty Ambiguous signals the reviewer
        // that the specific (int→long vs int→Guid) mapping isn't provable.
        var before = MakeGraph(
            AddMethod("method:b1", "Orders.Get",
                fullName: "Orders.Get(int id)", returns: "Order"),
            AddMethod("method:b2", "Orders.Get",
                fullName: "Orders.Get(string slug)", returns: "Order"));

        var after = MakeGraph(
            AddMethod("method:a1", "Orders.Get",
                fullName: "Orders.Get(long id)", returns: "Order"),
            AddMethod("method:a2", "Orders.Get",
                fullName: "Orders.Get(Guid slug)", returns: "Order"));

        var changes = BreakingChangeClassifier.Classify(before, after).Changes
            .Where(c => c.Kind == BreakingChangeKind.ApiSignatureChange)
            .ToArray();

        Assert.Equal(2, changes.Length);
        Assert.All(changes, c => Assert.Equal(Certainty.Ambiguous, c.Certainty));
    }

    [Fact]
    public void ApiRemoved_UnmatchedRemoval_EmittedAsHigh()
    {
        var before = MakeGraph(AddMethod("method:m", "Orders.GetAll",
            fullName: "Orders.GetAll(int tenantId)", returns: "IEnumerable<Order>"));
        var after = MakeGraph();

        var change = Assert.Single(BreakingChangeClassifier.Classify(before, after).Changes);
        Assert.Equal(BreakingChangeKind.ApiRemoved, change.Kind);
        Assert.Equal(Severity.High, change.Severity);
        Assert.Equal("Orders.GetAll(int tenantId)", change.BeforeSnippet);
        Assert.Equal("(removed)", change.AfterSnippet);
    }

    [Fact]
    public void PairedChange_AffectedNodeIds_IncludesBothAddedAndRemoved()
    {
        // Tooling jumping to "before" or "after" sides needs both IDs.
        var before = MakeGraph(AddPackage("Serilog", "2.0.0"));
        var after = MakeGraph(AddPackage("Serilog", "3.0.0"));

        var change = Assert.Single(BreakingChangeClassifier.Classify(before, after).Changes);
        Assert.Equal(2, change.AffectedNodeIds.Length);
    }

    [Fact]
    public void ApiSignatureChange_ReturnTypeUnchanged_NotEmitted()
    {
        // Top-level field changed but returns identical → no finding.
        // (Certainty change is the most common "changed but not breaking".)
        var before = MakeBuilder().Chain(b => b.AddNode("method:m", NodeType.Method, "M",
            certainty: Certainty.Ambiguous,
            metadata: new Dictionary<string, string?> { ["returns"] = "int" })).Build();
        var after = MakeBuilder().Chain(b => b.AddNode("method:m", NodeType.Method, "M",
            certainty: Certainty.Exact,
            metadata: new Dictionary<string, string?> { ["returns"] = "int" })).Build();

        Assert.Empty(BreakingChangeClassifier.Classify(before, after).Changes);
    }

    // --- TableRename ---

    [Fact]
    public void TableRename_EntityRemapsToNewTable_IsCritical()
    {
        // Before: Entity E -MapsToTable-> Table "Orders"
        // After:  Entity E -MapsToTable-> Table "UserOrders"
        // Table node ID = hash(repo|name), so Add+Remove pair.
        var before = MakeBuilder().Chain(b =>
        {
            b.AddNode("entity:E", NodeType.Entity, "Order");
            b.AddNode("table:old", NodeType.Table, "Orders");
            b.AddEdge("entity:E", "table:old", EdgeType.MapsToTable, "Order maps to Orders");
        }).Build();

        var after = MakeBuilder().Chain(b =>
        {
            b.AddNode("entity:E", NodeType.Entity, "Order");
            b.AddNode("table:new", NodeType.Table, "UserOrders");
            b.AddEdge("entity:E", "table:new", EdgeType.MapsToTable, "Order maps to UserOrders");
        }).Build();

        var change = Assert.Single(BreakingChangeClassifier.Classify(before, after).Changes);
        Assert.Equal(BreakingChangeKind.TableRename, change.Kind);
        Assert.Equal(Severity.Critical, change.Severity);
        Assert.Equal("Orders", change.BeforeSnippet);
        Assert.Equal("UserOrders", change.AfterSnippet);
        Assert.Equal("entity:E", change.Metadata["entity"]);
    }

    [Fact]
    public void TableRemoved_NoEntityRemap_EmittedAsCritical()
    {
        // Entity disappears entirely (or its mapping is removed). Resulting
        // removed Table has no matching added Table via the same entity → no
        // TableRename; emit TableRemoved.
        var before = MakeBuilder().Chain(b =>
        {
            b.AddNode("entity:E", NodeType.Entity, "Order");
            b.AddNode("table:old", NodeType.Table, "Orders");
            b.AddEdge("entity:E", "table:old", EdgeType.MapsToTable, "Order maps to Orders");
        }).Build();

        var after = MakeBuilder().Build();  // entity + table gone

        var changes = BreakingChangeClassifier.Classify(before, after).Changes;
        var tableRemoved = Assert.Single(changes.Where(c => c.Kind == BreakingChangeKind.TableRemoved));
        Assert.Equal(Severity.Critical, tableRemoved.Severity);
        Assert.Equal("Orders", tableRemoved.BeforeSnippet);
        Assert.Equal("(removed)", tableRemoved.AfterSnippet);
    }

    // --- Stats / empty cases ---

    [Fact]
    public void Classify_NoDiff_EmptyChanges()
    {
        var graph = MakeGraph(AddPackage("Serilog", "3.1.1"));
        var result = BreakingChangeClassifier.Classify(graph, graph);

        Assert.Empty(result.Changes);
        Assert.Equal(0, result.Stats.Classified);
    }

    [Fact]
    public void Classify_MixedKinds_StatsReflectTotals()
    {
        var before = MakeBuilder().Chain(b =>
        {
            AddPackageTo(b, "Serilog", "2.0.0");
            AddEndpointTo(b, "ep-before", "GET", "/a", handler: "method:H");
        }).Build();

        var after = MakeBuilder().Chain(b =>
        {
            AddPackageTo(b, "Serilog", "3.0.0");
            AddEndpointTo(b, "ep-after", "GET", "/b", handler: "method:H");
        }).Build();

        var result = BreakingChangeClassifier.Classify(before, after);

        Assert.Equal(2, result.Changes.Length);
        Assert.Contains(result.Changes, c => c.Kind == BreakingChangeKind.NugetVersionBump);
        Assert.Contains(result.Changes, c => c.Kind == BreakingChangeKind.EndpointRouteChange);
        Assert.Equal(2, result.Stats.Classified);
    }

    // --- Certainty propagation ---

    [Fact]
    public void Certainty_ReportsWeakerOfBeforeAndAfter()
    {
        var before = MakeBuilder().Chain(b => AddPackageTo(b, "Serilog", "2.0.0",
            certainty: Certainty.Inferred)).Build();
        var after = MakeBuilder().Chain(b => AddPackageTo(b, "Serilog", "3.0.0",
            certainty: Certainty.Exact)).Build();

        var change = Assert.Single(BreakingChangeClassifier.Classify(before, after).Changes);
        Assert.Equal(Certainty.Inferred, change.Certainty);   // weaker side wins
    }

    // --- Helpers ---

    private static GraphBuilder MakeBuilder() => new();

    private static ScanResult MakeGraph(params Action<GraphBuilder>[] setups)
    {
        var builder = new GraphBuilder();
        foreach (var setup in setups)
            setup(builder);
        var info = new ScanInfo("/root", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], new Dictionary<string, string>());
        return builder.Build(info, []);
    }

    private static Action<GraphBuilder> AddPackage(string packageId, string version) =>
        b => AddPackageTo(b, packageId, version);

    private static Action<GraphBuilder> AddEndpoint(string nodeId, string verb, string route, string handler) =>
        b => AddEndpointTo(b, nodeId, verb, route, handler);

    private static Action<GraphBuilder> AddMethod(string nodeId, string displayName,
        string fullName, string returns) =>
        b => b.AddNode(nodeId, NodeType.Method, displayName,
            metadata: new Dictionary<string, string?>
            {
                ["fullName"] = fullName,
                ["returns"] = returns,
            });

    private static void AddPackageTo(GraphBuilder b, string packageId, string version,
        Certainty certainty = Certainty.Exact)
    {
        var id = NodeId.From("package", packageId.ToLowerInvariant(), version.ToLowerInvariant());
        b.AddNode(id, NodeType.Package, $"{packageId}@{version}", certainty: certainty,
            metadata: new Dictionary<string, string?>
            {
                ["packageId"] = packageId,
                ["version"] = version,
            });
    }

    private static void AddEndpointTo(GraphBuilder b, string nodeId, string verb, string route, string handler)
    {
        b.AddNode(nodeId, NodeType.Endpoint, $"{verb} {route}",
            metadata: new Dictionary<string, string?>
            {
                ["verb"] = verb,
                ["route"] = route,
                ["handler"] = handler,
            });
    }
}

internal static class GraphBuilderTestExtensions
{
    /// <summary>Fluent helper so tests read top-to-bottom.</summary>
    internal static GraphBuilder Chain(this GraphBuilder b, Action<GraphBuilder> setup)
    {
        setup(b);
        return b;
    }

    internal static ScanResult Build(this GraphBuilder b)
    {
        var info = new ScanInfo("/root", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [], new Dictionary<string, string>());
        return b.Build(info, []);
    }
}
