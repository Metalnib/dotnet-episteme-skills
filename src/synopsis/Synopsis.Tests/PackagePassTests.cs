using System.Xml.Linq;
using Synopsis.Analysis.Graph;
using Synopsis.Analysis.Model;
using Synopsis.Analysis.Roslyn.Passes;

namespace Synopsis.Tests;

public sealed class PackagePassTests
{
    // --- ParseAssets ---

    [Fact]
    public void ParseAssets_EmptyDocument_ReturnsEmpty()
    {
        var entries = PackagePass.ParseAssets("""{"version": 3}""");
        Assert.Empty(entries);
    }

    [Fact]
    public void ParseAssets_DirectAndTransitive_DistinguishedCorrectly()
    {
        const string json = """
        {
          "version": 3,
          "projectFileDependencyGroups": {
            "net10.0": ["Serilog >= 3.1.1"]
          },
          "targets": {
            "net10.0": {
              "Serilog/3.1.1": { "type": "package" },
              "Serilog.Abstractions/3.1.1": { "type": "package" }
            }
          },
          "libraries": {
            "Serilog/3.1.1": { "type": "package" },
            "Serilog.Abstractions/3.1.1": { "type": "package" }
          }
        }
        """;

        var entries = PackagePass.ParseAssets(json);
        Assert.Equal(2, entries.Count);

        var direct = entries.Single(e => e.PackageId == "Serilog");
        Assert.True(direct.IsDirect);
        Assert.Equal(Certainty.Exact, direct.Certainty);

        var transitive = entries.Single(e => e.PackageId == "Serilog.Abstractions");
        Assert.False(transitive.IsDirect);
        Assert.Equal(Certainty.Inferred, transitive.Certainty);
    }

    [Fact]
    public void ParseAssets_MultiTargeted_DeduplicatesAndMergesFrameworks()
    {
        const string json = """
        {
          "version": 3,
          "projectFileDependencyGroups": {
            "net10.0": ["Serilog >= 3.1.1"],
            "net9.0":  ["Serilog >= 3.1.1"]
          },
          "targets": {
            "net10.0": { "Serilog/3.1.1": { "type": "package" } },
            "net9.0":  { "Serilog/3.1.1": { "type": "package" } }
          },
          "libraries": { "Serilog/3.1.1": { "type": "package" } }
        }
        """;

        var entries = PackagePass.ParseAssets(json);
        var entry = Assert.Single(entries);
        Assert.Equal("Serilog", entry.PackageId);
        Assert.Equal(2, entry.Frameworks.Count);
        Assert.Contains("net10.0", entry.Frameworks);
        Assert.Contains("net9.0", entry.Frameworks);
    }

    [Fact]
    public void ParseAssets_DependencyNameWithLeadingWhitespace_ParsesCorrectly()
    {
        // Defensive: malformed input with leading whitespace in the
        // projectFileDependencyGroups entry. A naïve IndexOf(' ') would return
        // 0 and yield an empty name, which would miss the direct match.
        const string json = """
        {
          "version": 3,
          "projectFileDependencyGroups": {
            "net10.0": ["  Serilog >= 3.1.1"]
          },
          "targets": {
            "net10.0": { "Serilog/3.1.1": { "type": "package" } }
          }
        }
        """;

        var entries = PackagePass.ParseAssets(json);
        var entry = Assert.Single(entries);
        Assert.True(entry.IsDirect, "Leading whitespace in dep spec should not break direct detection");
    }

    [Fact]
    public void ParseAssets_BareDependencyName_StillMarksDirect()
    {
        // NuGet normally writes "Name >= Version" in projectFileDependencyGroups,
        // but a bare "Name" (no version constraint) appears in some scenarios.
        // The direct/transitive split must still resolve by name.
        const string json = """
        {
          "version": 3,
          "projectFileDependencyGroups": {
            "net10.0": ["Serilog"]
          },
          "targets": {
            "net10.0": {
              "Serilog/3.1.1": { "type": "package" },
              "Serilog.Sinks.Console/3.0.0": { "type": "package" }
            }
          }
        }
        """;

        var entries = PackagePass.ParseAssets(json);

        var direct = entries.Single(e => e.PackageId == "Serilog");
        Assert.True(direct.IsDirect);
        Assert.Equal(Certainty.Exact, direct.Certainty);

        var transitive = entries.Single(e => e.PackageId == "Serilog.Sinks.Console");
        Assert.False(transitive.IsDirect);
        Assert.Equal(Certainty.Inferred, transitive.Certainty);
    }

    [Fact]
    public void ParseAssets_SkipsProjectReferences()
    {
        const string json = """
        {
          "version": 3,
          "projectFileDependencyGroups": { "net10.0": [] },
          "targets": {
            "net10.0": {
              "MyLib/1.0.0": { "type": "project" },
              "Serilog/3.1.1": { "type": "package" }
            }
          }
        }
        """;

        var entries = PackagePass.ParseAssets(json);
        var entry = Assert.Single(entries);
        Assert.Equal("Serilog", entry.PackageId);
    }

    // --- ParseCsproj ---

    [Fact]
    public void ParseCsproj_InlineVersion_IsInferredAndDirect()
    {
        var csproj = XDocument.Parse("""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Serilog" Version="3.1.1" />
          </ItemGroup>
        </Project>
        """);

        var entries = PackagePass.ParseCsproj(csproj, new Dictionary<string, string>());

        var entry = Assert.Single(entries);
        Assert.Equal("Serilog", entry.PackageId);
        Assert.Equal("3.1.1", entry.Version);
        Assert.True(entry.IsDirect);
        Assert.Equal(Certainty.Inferred, entry.Certainty);
        Assert.Equal(PackagePass.PackageSource.CsprojInline, entry.Source);
        Assert.Equal(["net10.0"], entry.Frameworks);
    }

    [Fact]
    public void ParseCsproj_FloatingVersion_IsAmbiguous()
    {
        var csproj = XDocument.Parse("""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Serilog" Version="3.*" />
            <PackageReference Include="Newtonsoft.Json" Version="[13.0,14.0)" />
          </ItemGroup>
        </Project>
        """);

        var entries = PackagePass.ParseCsproj(csproj, new Dictionary<string, string>());

        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal(Certainty.Ambiguous, e.Certainty));
    }

    [Fact]
    public void ParseCsproj_BracketedExactPin_IsNotFloating()
    {
        // NuGet accepts "[1.2.3]" as an explicit pin (inclusive single-version
        // range). It must not be flagged as floating.
        var csproj = XDocument.Parse("""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Serilog" Version="[3.1.1]" />
          </ItemGroup>
        </Project>
        """);

        var entry = Assert.Single(PackagePass.ParseCsproj(csproj, new Dictionary<string, string>()));
        Assert.Equal(Certainty.Inferred, entry.Certainty);
        Assert.Equal("[3.1.1]", entry.Version);
    }

    [Fact]
    public void ParseCsproj_CpmResolved_UsesMap_AndTagsSourceCpm()
    {
        var csproj = XDocument.Parse("""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Serilog" />
          </ItemGroup>
        </Project>
        """);
        var cpm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Serilog"] = "3.1.1",
        };

        var entry = Assert.Single(PackagePass.ParseCsproj(csproj, cpm));
        Assert.Equal("3.1.1", entry.Version);
        Assert.Equal(Certainty.Inferred, entry.Certainty);
        Assert.Equal(PackagePass.PackageSource.CsprojWithCpm, entry.Source);
    }

    [Fact]
    public void ParseCsproj_MixedInlineAndCpm_TagsSourcePerEntry()
    {
        // Inline version wins over CPM; bare reference resolves via CPM. Each
        // entry's source must reflect where its version actually came from,
        // even when the project also has a Directory.Packages.props.
        var csproj = XDocument.Parse("""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Serilog" Version="3.1.1" />
            <PackageReference Include="Newtonsoft.Json" />
          </ItemGroup>
        </Project>
        """);
        var cpm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Serilog"] = "9.9.9",          // should NOT win; inline takes precedence
            ["Newtonsoft.Json"] = "13.0.3", // should resolve the bare reference
        };

        var entries = PackagePass.ParseCsproj(csproj, cpm);

        var serilog = entries.Single(e => e.PackageId == "Serilog");
        Assert.Equal("3.1.1", serilog.Version);
        Assert.Equal(PackagePass.PackageSource.CsprojInline, serilog.Source);

        var nj = entries.Single(e => e.PackageId == "Newtonsoft.Json");
        Assert.Equal("13.0.3", nj.Version);
        Assert.Equal(PackagePass.PackageSource.CsprojWithCpm, nj.Source);
    }

    [Fact]
    public void ParseCsproj_CpmMissing_IsUnresolved()
    {
        var csproj = XDocument.Parse("""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Serilog" />
          </ItemGroup>
        </Project>
        """);

        var entries = PackagePass.ParseCsproj(csproj, new Dictionary<string, string>());

        var entry = Assert.Single(entries);
        Assert.Equal(Certainty.Unresolved, entry.Certainty);
    }

    [Fact]
    public void ParseCsproj_UpdateOnly_IsSkipped()
    {
        // `PackageReference Update="..."` modifies metadata on an existing
        // item (typically inherited from the SDK targeting pack or
        // Directory.Build.props). Without full MSBuild evaluation we cannot
        // verify the base Include exists, so we must not emit phantom direct
        // references. Only real `Include` items count.
        var csproj = XDocument.Parse("""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Serilog" Version="3.1.1" />
            <PackageReference Update="Microsoft.AspNetCore.App" Version="8.0.5" />
            <PackageReference Update="System.Text.Json" ExcludeAssets="runtime" />
          </ItemGroup>
        </Project>
        """);

        var entries = PackagePass.ParseCsproj(csproj, new Dictionary<string, string>());
        var entry = Assert.Single(entries);
        Assert.Equal("Serilog", entry.PackageId);
    }

    [Fact]
    public void ParseCsproj_ReadsMultipleTargetFrameworks()
    {
        var csproj = XDocument.Parse("""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><TargetFrameworks>net9.0;net10.0</TargetFrameworks></PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Serilog" Version="3.1.1" />
          </ItemGroup>
        </Project>
        """);

        var entry = Assert.Single(PackagePass.ParseCsproj(csproj, new Dictionary<string, string>()));
        Assert.Equal(["net9.0", "net10.0"], entry.Frameworks);
    }

    // --- TryReadAssets (error paths; happy path covered via ParseAssets tests) ---

    [Fact]
    public void TryReadAssets_MissingFile_ReturnsNull()
    {
        // Absent file → I/O read fails → null so caller falls back. No throw.
        var missing = Path.Combine(Path.GetTempPath(), $"definitely-not-here-{Guid.NewGuid():N}.json");
        Assert.Null(PackagePass.TryReadAssets(missing));
    }

    [Fact]
    public void TryReadAssets_MalformedJson_ReturnsNull()
    {
        // Parse failure must not bubble up and kill the scan.
        var path = Path.Combine(Path.GetTempPath(), $"malformed-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ this is not valid json ");
        try
        {
            Assert.Null(PackagePass.TryReadAssets(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TryReadAssets_ValidFile_Returns_Entries()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ok-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
        {
          "version": 3,
          "projectFileDependencyGroups": { "net10.0": ["Serilog >= 3.1.1"] },
          "targets": { "net10.0": { "Serilog/3.1.1": { "type": "package" } } }
        }
        """);
        try
        {
            var entries = PackagePass.TryReadAssets(path);
            Assert.NotNull(entries);
            var entry = Assert.Single(entries);
            Assert.Equal("Serilog", entry.PackageId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // --- LoadCpmVersions ---

    [Fact]
    public void LoadCpmVersions_ExtractsPackageVersions()
    {
        var props = XDocument.Parse("""
        <Project>
          <ItemGroup>
            <PackageVersion Include="Serilog" Version="3.1.1" />
            <PackageVersion Include="Newtonsoft.Json" Version="13.0.3" />
          </ItemGroup>
        </Project>
        """);

        var cpm = PackagePass.LoadCpmVersions(props);

        Assert.Equal(2, cpm.Count);
        Assert.Equal("3.1.1", cpm["Serilog"]);
        Assert.Equal("13.0.3", cpm["newtonsoft.json"]);   // case-insensitive lookup
    }

    // --- EmitEntries ---

    [Fact]
    public void EmitEntries_CreatesStableNodeIdAcrossProjects()
    {
        var graph = new GraphBuilder();
        var entry = new PackagePass.PackageEntry(
            PackageId: "Serilog", Version: "3.1.1", IsDirect: true,
            Frameworks: ["net10.0"],
            Source: PackagePass.PackageSource.Assets,
            Certainty: Certainty.Exact);

        PackagePass.EmitEntries(graph, "project:aaa", "svc-a", "repo-a", [entry]);
        PackagePass.EmitEntries(graph, "project:bbb", "svc-b", "repo-b", [entry]);

        var packageNodes = graph.Nodes.Where(n => n.Type == NodeType.Package).ToArray();
        Assert.Single(packageNodes);           // deduped — same Serilog/3.1.1 across two projects
        Assert.Equal("Serilog@3.1.1", packageNodes[0].DisplayName);

        var edges = graph.Edges.Where(e => e.Type == EdgeType.DependsOnPackage).ToArray();
        Assert.Equal(2, edges.Length);         // two Project → Package edges, one per project
    }

    [Fact]
    public void EmitEntries_NodeCarriesIdentityOnly_EdgeCarriesRelationalFacts()
    {
        var graph = new GraphBuilder();
        var entry = new PackagePass.PackageEntry(
            PackageId: "Serilog", Version: "3.1.1", IsDirect: false,
            Frameworks: ["net10.0", "net9.0"],
            Source: PackagePass.PackageSource.Assets,
            Certainty: Certainty.Inferred);

        PackagePass.EmitEntries(graph, "project:aaa", "svc-a", "repo-a", [entry]);

        // Node holds identity fields only — these are invariant fleet-wide.
        var node = graph.Nodes.Single(n => n.Type == NodeType.Package);
        Assert.Equal("Serilog", node.Metadata["packageId"]);
        Assert.Equal("3.1.1", node.Metadata["version"]);
        Assert.False(node.Metadata.ContainsKey("isTransitive"),
            "isTransitive is per-project; must live on the edge, not the node");
        Assert.False(node.Metadata.ContainsKey("source"),
            "source is per-project; must live on the edge, not the node");
        Assert.False(node.Metadata.ContainsKey("frameworks"),
            "frameworks are per-project; must live on the edge, not the node");
        Assert.Equal(Certainty.Inferred, node.Certainty);

        // Edge carries the per-project relational facts.
        var edge = graph.Edges.Single(e => e.Type == EdgeType.DependsOnPackage);
        Assert.Equal("true", edge.Metadata["isTransitive"]);
        Assert.Equal("project.assets.json", edge.Metadata["source"]);
        Assert.Equal("net10.0,net9.0", edge.Metadata["frameworks"]);
    }

    [Fact]
    public void EmitEntries_ConcurrentMergePreservesPerEdgeTruth()
    {
        // Regression test for the non-determinism bug: two projects reference
        // the same package but disagree on isTransitive and source. Per-project
        // builders merge into a single graph. With metadata on the node, the
        // first-wins merge would make the visible isTransitive/source depend on
        // merge order. With metadata on the edge, each project's relationship
        // to the shared Package node stays independently correct.

        var projectA = new GraphBuilder();
        var projectB = new GraphBuilder();

        var directFromAssets = new PackagePass.PackageEntry(
            PackageId: "Serilog", Version: "3.1.1", IsDirect: true,
            Frameworks: ["net10.0"],
            Source: PackagePass.PackageSource.Assets,
            Certainty: Certainty.Exact);

        var transitiveFromCsproj = new PackagePass.PackageEntry(
            PackageId: "Serilog", Version: "3.1.1", IsDirect: false,
            Frameworks: ["net9.0"],
            Source: PackagePass.PackageSource.CsprojInline,
            Certainty: Certainty.Inferred);

        PackagePass.EmitEntries(projectA, "project:aaa", "svc-a", "repo-a", [directFromAssets]);
        PackagePass.EmitEntries(projectB, "project:bbb", "svc-b", "repo-b", [transitiveFromCsproj]);

        // Merge in both orders to be sure order does not leak into node metadata.
        var merged1 = new GraphBuilder();
        merged1.Merge(projectA);
        merged1.Merge(projectB);

        var merged2 = new GraphBuilder();
        merged2.Merge(projectB);
        merged2.Merge(projectA);

        foreach (var merged in new[] { merged1, merged2 })
        {
            var node = Assert.Single(merged.Nodes, n => n.Type == NodeType.Package);
            Assert.Equal("Serilog", node.Metadata["packageId"]);
            Assert.Equal("3.1.1", node.Metadata["version"]);

            // Per-project facts live on the two separate edges and are preserved.
            var edgeFromA = merged.Edges.Single(e =>
                e.Type == EdgeType.DependsOnPackage && e.SourceId == "project:aaa");
            var edgeFromB = merged.Edges.Single(e =>
                e.Type == EdgeType.DependsOnPackage && e.SourceId == "project:bbb");

            Assert.Equal("false", edgeFromA.Metadata["isTransitive"]);
            Assert.Equal("project.assets.json", edgeFromA.Metadata["source"]);
            Assert.Equal("net10.0", edgeFromA.Metadata["frameworks"]);

            Assert.Equal("true", edgeFromB.Metadata["isTransitive"]);
            Assert.Equal("csproj-inline", edgeFromB.Metadata["source"]);
            Assert.Equal("net9.0", edgeFromB.Metadata["frameworks"]);
        }
    }
}
