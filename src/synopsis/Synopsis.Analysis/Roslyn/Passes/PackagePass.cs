using System.Text.Json;
using System.Xml.Linq;
using Synopsis.Analysis.Graph;
using Synopsis.Analysis.Model;

namespace Synopsis.Analysis.Roslyn.Passes;

/// <summary>
/// Emits NuGet <see cref="NodeType.Package"/> nodes and
/// <see cref="EdgeType.DependsOnPackage"/> edges from project package
/// references.
///
/// Source priority:
/// 1. <c>obj/project.assets.json</c> (post-restore; authoritative, includes
///    transitive deps). Certainty: <see cref="Certainty.Exact"/> for direct
///    references, <see cref="Certainty.Inferred"/> for transitives.
/// 2. Fallback: parse <c>.csproj</c> XML; resolve CPM versions from
///    <c>Directory.Packages.props</c> walking up from the project directory.
///    Certainty: <see cref="Certainty.Inferred"/>, with
///    <see cref="Certainty.Ambiguous"/> for floating version ranges and
///    <see cref="Certainty.Unresolved"/> when CPM declares the package with
///    no matching <c>PackageVersion</c> entry.
/// </summary>
internal sealed class PackagePass : IAnalysisPass
{
    public string Name => "packages";

    public void Analyze(LoadedProject project, GraphBuilder graph, GraphBuilder? mainGraph, CancellationToken ct)
    {
        var csprojPath = project.Project.FilePath;
        if (string.IsNullOrWhiteSpace(csprojPath))
            return;

        var projectId = WorkspaceScanner.ProjectNodeId(csprojPath);
        var projectDir = Path.GetDirectoryName(csprojPath);
        if (string.IsNullOrWhiteSpace(projectDir))
            return;

        ct.ThrowIfCancellationRequested();

        var assetsPath = Path.Combine(projectDir, "obj", "project.assets.json");

        // Try assets.json first; fall back to csproj+CPM if the file is absent,
        // unreadable (TOCTOU race with a concurrent `dotnet restore` wiping obj/,
        // permission denied, etc.), or malformed.
        var entries = TryReadAssets(assetsPath)
            ?? ParseCsprojWithCpm(projectDir, csprojPath);

        EmitEntries(project, graph, projectId, entries);
    }

    /// <summary>
    /// Try to read and parse <c>project.assets.json</c>. Returns
    /// <see langword="null"/> on any expected read or parse failure so the
    /// caller can fall back to the csproj/CPM path. Unexpected errors and
    /// cancellation propagate as normal.
    /// </summary>
    public static IReadOnlyList<PackageEntry>? TryReadAssets(string assetsPath)
    {
        string json;
        try
        {
            json = File.ReadAllText(assetsPath);
        }
        catch (Exception ex) when (
            ex is IOException
               or UnauthorizedAccessException
               or System.Security.SecurityException)
        {
            return null;
        }

        try
        {
            return ParseAssets(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<PackageEntry> ParseCsprojWithCpm(string projectDir, string csprojPath)
    {
        XDocument csproj;
        try
        {
            csproj = XDocument.Load(csprojPath);
        }
        catch (Exception ex) when (
            ex is IOException
               or UnauthorizedAccessException
               or System.Security.SecurityException
               or System.Xml.XmlException)
        {
            return [];
        }

        var cpmPath = FindDirectoryPackagesProps(projectDir);
        IReadOnlyDictionary<string, string> cpm = new Dictionary<string, string>();
        if (cpmPath is not null)
        {
            try
            {
                cpm = LoadCpmVersions(XDocument.Load(cpmPath));
            }
            catch (Exception ex) when (
                ex is IOException
                   or UnauthorizedAccessException
                   or System.Security.SecurityException
                   or System.Xml.XmlException)
            {
                // Best-effort CPM resolution; proceed without it.
            }
        }

        return ParseCsproj(csproj, cpm);
    }

    // --- Pure parsing helpers (static, unit-testable, no I/O) ---

    /// <summary>
    /// Parse a <c>project.assets.json</c> string and extract per-package
    /// entries for every target framework.
    /// </summary>
    public static IReadOnlyList<PackageEntry> ParseAssets(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object)
            return [];

        // Direct deps live in projectFileDependencyGroups[<tfm>][] as strings
        // of the form "PackageName >= 1.2.3".
        var directByTfm = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (root.TryGetProperty("projectFileDependencyGroups", out var groups)
            && groups.ValueKind == JsonValueKind.Object)
        {
            foreach (var group in groups.EnumerateObject())
            {
                var direct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (group.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in group.Value.EnumerateArray())
                    {
                        var raw = entry.GetString();
                        if (string.IsNullOrWhiteSpace(raw)) continue;
                        direct.Add(ExtractDependencyName(raw));
                    }
                }
                directByTfm[group.Name] = direct;
            }
        }

        // Accumulate one entry per unique (packageId, version) pair, tracking
        // all TFMs that resolved it and whether ANY TFM treats it as direct.
        var byKey = new Dictionary<string, PackageEntryBuilder>(StringComparer.OrdinalIgnoreCase);

        foreach (var targetProp in targets.EnumerateObject())
        {
            var tfm = targetProp.Name;
            var direct = directByTfm.GetValueOrDefault(tfm, []);
            if (targetProp.Value.ValueKind != JsonValueKind.Object) continue;

            foreach (var lib in targetProp.Value.EnumerateObject())
            {
                // Skip project-to-project references (type="project"); only
                // packages here.
                if (!lib.Value.TryGetProperty("type", out var typeProp)
                    || typeProp.GetString() != "package")
                    continue;

                var slash = lib.Name.IndexOf('/');
                if (slash < 0) continue;
                var packageId = lib.Name[..slash];
                var version = lib.Name[(slash + 1)..];
                var key = $"{packageId.ToLowerInvariant()}|{version.ToLowerInvariant()}";

                if (!byKey.TryGetValue(key, out var builder))
                {
                    builder = new PackageEntryBuilder(packageId, version);
                    byKey[key] = builder;
                }
                builder.Frameworks.Add(tfm);
                if (direct.Contains(packageId))
                    builder.IsDirect = true;
            }
        }

        return byKey.Values
            .Select(b => new PackageEntry(
                PackageId: b.PackageId,
                Version: b.Version,
                IsDirect: b.IsDirect,
                Frameworks: [.. b.Frameworks],
                Source: PackageSource.Assets,
                Certainty: b.IsDirect ? Certainty.Exact : Certainty.Inferred))
            .ToArray();
    }

    /// <summary>
    /// Parse a <c>.csproj</c> XML document's <c>PackageReference</c> items.
    /// When an item has no inline <c>Version</c>, look it up in
    /// <paramref name="cpmVersions"/>. Source classification is per-entry:
    /// inline versions get <see cref="PackageSource.CsprojInline"/>, versions
    /// resolved from the CPM map get <see cref="PackageSource.CsprojWithCpm"/>,
    /// unresolved references stay tagged <see cref="PackageSource.CsprojInline"/>
    /// since that's where the reference came from — the certainty is what
    /// signals the unresolved state.
    /// </summary>
    /// <remarks>
    /// TODO: <c>Condition="..."</c> attributes on individual
    /// <c>PackageReference</c> items (TFM-conditional references) are
    /// currently ignored — every reference is attributed to every TFM in
    /// the csproj. This over-claims for multi-targeted projects with
    /// TFM-conditional packages. Acceptable for MVP; revisit if it causes
    /// noisy blast-radius queries in practice.
    /// </remarks>
    public static IReadOnlyList<PackageEntry> ParseCsproj(
        XDocument csproj,
        IReadOnlyDictionary<string, string> cpmVersions)
    {
        if (csproj.Root is null) return [];

        var tfms = ReadTargetFrameworks(csproj);
        var entries = new List<PackageEntry>();

        foreach (var item in csproj.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
        {
            // Only real references count. `Update` / `Remove` modify an
            // already-declared item (often inherited from the SDK or
            // Directory.Build.props); without full MSBuild evaluation we
            // cannot verify the base Include exists, so we skip rather than
            // emit a phantom direct reference.
            var packageId = item.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(packageId)) continue;

            var inlineVersion = item.Attribute("Version")?.Value
                ?? item.Element(item.Name.Namespace + "Version")?.Value;

            Certainty certainty;
            string resolvedVersion;
            PackageSource source;
            if (!string.IsNullOrWhiteSpace(inlineVersion))
            {
                resolvedVersion = inlineVersion;
                certainty = IsFloatingVersion(inlineVersion) ? Certainty.Ambiguous : Certainty.Inferred;
                source = PackageSource.CsprojInline;
            }
            else if (cpmVersions.TryGetValue(packageId, out var cpmVersion))
            {
                resolvedVersion = cpmVersion;
                certainty = IsFloatingVersion(cpmVersion) ? Certainty.Ambiguous : Certainty.Inferred;
                source = PackageSource.CsprojWithCpm;
            }
            else
            {
                resolvedVersion = "(unresolved)";
                certainty = Certainty.Unresolved;
                source = PackageSource.CsprojInline;
            }

            entries.Add(new PackageEntry(
                PackageId: packageId,
                Version: resolvedVersion,
                IsDirect: true, // csproj entries are always direct
                Frameworks: tfms,
                Source: source,
                Certainty: certainty));
        }

        return entries;
    }

    /// <summary>
    /// Load central package version map from a <c>Directory.Packages.props</c>
    /// document. Returns a name→version dictionary.
    /// </summary>
    public static IReadOnlyDictionary<string, string> LoadCpmVersions(XDocument props)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (props.Root is null) return map;

        foreach (var item in props.Descendants().Where(e => e.Name.LocalName == "PackageVersion"))
        {
            var id = item.Attribute("Include")?.Value;
            var version = item.Attribute("Version")?.Value
                ?? item.Element(item.Name.Namespace + "Version")?.Value;
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(version))
                map[id] = version;
        }
        return map;
    }

    /// <summary>
    /// Walk up from <paramref name="startDir"/> looking for the nearest
    /// <c>Directory.Packages.props</c>. Returns null if none found before
    /// reaching the filesystem root.
    /// </summary>
    public static string? FindDirectoryPackagesProps(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Directory.Packages.props");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    // --- Emission ---

    private static void EmitEntries(LoadedProject project, GraphBuilder graph, string projectId,
        IReadOnlyList<PackageEntry> entries) =>
        EmitEntries(graph, projectId, project.ProjectName, project.RepositoryName, entries);

    /// <summary>
    /// Emit <see cref="NodeType.Package"/> nodes and
    /// <see cref="EdgeType.DependsOnPackage"/> edges for a list of resolved
    /// entries. Separate from the I/O-driven overload so tests can feed
    /// entries directly without a Roslyn workspace.
    /// </summary>
    /// <remarks>
    /// Node metadata is deliberately limited to identity fields
    /// (<c>packageId</c>, <c>version</c>) — those are invariant across the
    /// fleet. Relational facts (<c>isTransitive</c>, <c>source</c>,
    /// <c>frameworks</c>) live on the edge because they vary per project.
    /// If we stored them on the node, the first-wins metadata merge in
    /// <see cref="GraphBuilder.AddNode"/> would make the visible values
    /// depend on parallel-merge order — non-deterministic and wrong.
    /// </remarks>
    public static void EmitEntries(GraphBuilder graph, string projectId,
        string projectName, string? repositoryName, IReadOnlyList<PackageEntry> entries)
    {
        foreach (var entry in entries)
        {
            var packageNodeId = NodeId.From("package",
                entry.PackageId.ToLowerInvariant(), entry.Version.ToLowerInvariant());

            // Node certainty uses GraphBuilder's max-wins merge: if one project
            // sees Serilog@3.1.1 via project.assets.json (Exact) and another
            // only via a csproj inline reference (Inferred), the merged node
            // ends up Exact. That is intentional — the package is what it is;
            // any project with authoritative evidence pins the identity.
            graph.AddNode(packageNodeId, NodeType.Package, $"{entry.PackageId}@{entry.Version}",
                location: null,
                repositoryName: null,  // packages belong to no repo — shared across the fleet
                projectName: null,
                certainty: entry.Certainty,
                metadata: new Dictionary<string, string?>
                {
                    ["packageId"] = entry.PackageId,
                    ["version"] = entry.Version,
                });

            graph.AddEdge(projectId, packageNodeId, EdgeType.DependsOnPackage,
                $"{projectName} depends on {entry.PackageId}@{entry.Version}",
                location: null,
                repositoryName: repositoryName,
                projectName: projectName,
                certainty: entry.Certainty,
                metadata: new Dictionary<string, string?>
                {
                    ["isTransitive"] = entry.IsDirect ? "false" : "true",
                    ["source"] = SourceToMetadata(entry.Source),
                    ["frameworks"] = entry.Frameworks.Count > 0 ? string.Join(",", entry.Frameworks) : null,
                });
        }
    }

    // --- Helpers ---

    private static string ExtractDependencyName(string depSpec)
    {
        // "PackageName >= 1.2.3" or "PackageName >= [1.2.3, )" or bare
        // "PackageName". Trim first so leading whitespace does not make
        // IndexOf(' ') return 0 (which would collapse the whole string
        // into an empty name).
        var trimmed = depSpec.Trim();
        var spaceIdx = trimmed.IndexOf(' ');
        return spaceIdx > 0 ? trimmed[..spaceIdx] : trimmed;
    }

    private static IReadOnlyList<string> ReadTargetFrameworks(XDocument csproj)
    {
        var tf = csproj.Descendants().FirstOrDefault(e => e.Name.LocalName == "TargetFramework")?.Value;
        if (!string.IsNullOrWhiteSpace(tf))
            return [tf.Trim()];

        var tfs = csproj.Descendants().FirstOrDefault(e => e.Name.LocalName == "TargetFrameworks")?.Value;
        if (!string.IsNullOrWhiteSpace(tfs))
            return tfs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return [];
    }

    private static bool IsFloatingVersion(string version)
    {
        // Float: "1.*" or "1.2.*".
        // Range: "[1.0,2.0)", "(1.0,)", "(,2.0)" — all have comma or open paren.
        // Pinned exact: "1.2.3" OR the bracketed form "[1.2.3]" (pinned, not floating).
        return version.Contains('*', StringComparison.Ordinal)
            || version.Contains(',', StringComparison.Ordinal)
            || version.StartsWith('(');
    }

    private static string SourceToMetadata(PackageSource source) => source switch
    {
        PackageSource.Assets => "project.assets.json",
        PackageSource.CsprojInline => "csproj-inline",
        PackageSource.CsprojWithCpm => "directory-packages-props",
        _ => "unknown"
    };

    // --- Types ---

    /// <summary>Resolved package reference extracted from a source file.</summary>
    public sealed record PackageEntry(
        string PackageId,
        string Version,
        bool IsDirect,
        IReadOnlyList<string> Frameworks,
        PackageSource Source,
        Certainty Certainty);

    public enum PackageSource
    {
        Assets,
        CsprojInline,
        CsprojWithCpm,
    }

    private sealed class PackageEntryBuilder(string packageId, string version)
    {
        public string PackageId { get; } = packageId;
        public string Version { get; } = version;
        public bool IsDirect { get; set; }
        public HashSet<string> Frameworks { get; } = new(StringComparer.Ordinal);
    }
}
