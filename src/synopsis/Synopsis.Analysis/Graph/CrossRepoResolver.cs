using Synopsis.Analysis.Model;

namespace Synopsis.Analysis.Graph;

/// <summary>
/// Post-merge pass that links <see cref="NodeType.ExternalEndpoint"/>
/// nodes (emitted per-project by <c>HttpCallPass</c>) to the internal
/// <see cref="NodeType.Endpoint"/> nodes they actually target, and
/// emits <see cref="EdgeType.CrossesRepoBoundary"/> when the two live in
/// different repos.
/// </summary>
/// <remarks>
/// <para>
/// This used to live inside <c>HttpCallPass.ResolveInternalTarget</c> and
/// ran during per-project analysis against a <c>mainGraph</c> snapshot.
/// That couples resolution to the order projects are loaded and cannot
/// re-run incrementally. M2 moves it here so both
/// <c>WorkspaceScanner.ScanAsync</c> (full-scan path) and
/// <c>CombinedGraph.ReplaceRepositoryAsync</c> (incremental path) can call
/// it against the unified graph — the results are identical either way.
/// </para>
/// <para>
/// The resolver reads <c>clientName</c> and <c>clientBaseUrl</c> from
/// <c>ExternalEndpoint</c> metadata (populated by <c>HttpCallPass</c>), so
/// it does not have to walk back through the graph to reconstruct the
/// calling <c>HttpClient</c> node.
/// </para>
/// </remarks>
public static class CrossRepoResolver
{
    public static void Resolve(GraphBuilder graph)
    {
        // Snapshot before mutation to avoid surprising the iteration.
        var externalEndpoints = graph.Nodes
            .Where(n => n.Type == NodeType.ExternalEndpoint)
            .ToArray();

        foreach (var ee in externalEndpoints)
        {
            var path = ee.Metadata.GetValueOrDefault("path");
            if (string.IsNullOrWhiteSpace(path))
                continue;

            var clientName = ee.Metadata.GetValueOrDefault("clientName");
            var clientBaseUrl = ee.Metadata.GetValueOrDefault("clientBaseUrl");
            var absoluteUri = ee.Metadata.GetValueOrDefault("absoluteUri");
            Uri? requestUri = Uri.TryCreate(absoluteUri, UriKind.Absolute, out var u) ? u : null;

            var serviceHint = requestUri?.Host ?? clientName ?? ee.DisplayName;
            var candidates = graph.FindEndpointCandidates(path);

            // Phase 1: route match + service affinity. Client hint must
            // correlate with the target endpoint's repo/project name so we
            // don't cross-link unrelated services that happen to share a
            // route.
            var matched = candidates
                .Where(node =>
                {
                    var route = node.Metadata.GetValueOrDefault("route");
                    if (string.IsNullOrWhiteSpace(route)) return false;
                    if (!RouteMatches(path, route)
                        && !path.StartsWith(route, StringComparison.OrdinalIgnoreCase)
                        && !route.StartsWith(path, StringComparison.OrdinalIgnoreCase))
                        return false;
                    return MatchesServiceHint(node, serviceHint, clientBaseUrl);
                })
                .ToArray();

            // Phase 2: no affinity hit → fall back to pure route match, but
            // flag the whole set as Ambiguous so the reviewer knows the
            // match is structural rather than confirmed.
            if (matched.Length == 0)
            {
                matched = candidates
                    .Where(node =>
                    {
                        var route = node.Metadata.GetValueOrDefault("route");
                        return !string.IsNullOrWhiteSpace(route) && RouteMatches(path, route);
                    })
                    .ToArray();
            }

            if (matched.Length == 0)
                continue;

            var certainty = matched.Length == 1 ? Certainty.Inferred : Certainty.Ambiguous;
            foreach (var candidate in matched)
            {
                graph.AddEdge(ee.Id, candidate.Id,
                    certainty == Certainty.Ambiguous ? EdgeType.Ambiguous : EdgeType.ResolvesToService,
                    $"{ee.DisplayName} resolves to {candidate.DisplayName}",
                    ee.Location, ee.RepositoryName, ee.ProjectName, certainty);

                if (!string.Equals(ee.RepositoryName, candidate.RepositoryName, StringComparison.OrdinalIgnoreCase))
                    graph.AddEdge(ee.Id, candidate.Id, EdgeType.CrossesRepoBoundary,
                        $"{ee.ProjectName} calls across repo into {candidate.RepositoryName}",
                        ee.Location, ee.RepositoryName, ee.ProjectName, certainty);
            }
        }
    }

    private static bool RouteMatches(string requestPath, string routeTemplate)
    {
        var reqSegments = requestPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var routeSegments = routeTemplate.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (reqSegments.Length != routeSegments.Length) return false;

        for (var i = 0; i < reqSegments.Length; i++)
        {
            var seg = routeSegments[i];
            if (seg.StartsWith('{') && seg.EndsWith('}')) continue;
            if (!string.Equals(reqSegments[i], seg, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>
    /// Does the target endpoint's repo/project name correlate with the
    /// calling HTTP client's service hint? Examples:
    /// <list type="bullet">
    ///   <item><description><c>CatalogClient</c> with base URL
    ///     <c>http://catalog-api</c> → matches endpoints in repo
    ///     <c>catalog-api</c> or project <c>Catalog.Api</c>.</description></item>
    ///   <item><description><c>orders-api</c> client → matches
    ///     <c>OrdersService</c>.</description></item>
    /// </list>
    /// </summary>
    private static bool MatchesServiceHint(GraphNode endpoint, string serviceHint, string? baseUrl)
    {
        var repo = endpoint.RepositoryName;
        var project = endpoint.ProjectName;

        if (repo is not null && (
            serviceHint.Contains(repo, StringComparison.OrdinalIgnoreCase)
            || repo.Contains(serviceHint, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (project is not null && (
            serviceHint.Contains(project, StringComparison.OrdinalIgnoreCase)
            || project.Contains(serviceHint, StringComparison.OrdinalIgnoreCase)))
            return true;

        var hintStem = NormalizeServiceName(serviceHint);
        if (hintStem.Length >= 3)
        {
            if (repo is not null && NormalizeServiceName(repo).Contains(hintStem, StringComparison.OrdinalIgnoreCase))
                return true;
            if (project is not null && NormalizeServiceName(project).Contains(hintStem, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (baseUrl is not null && Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            var host = uri.Host;
            if (repo is not null && (
                host.Contains(repo, StringComparison.OrdinalIgnoreCase)
                || repo.Contains(host, StringComparison.OrdinalIgnoreCase)))
                return true;

            var hostStem = NormalizeServiceName(host);
            if (hostStem.Length >= 3)
            {
                if (repo is not null && NormalizeServiceName(repo).Contains(hostStem, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (project is not null && NormalizeServiceName(project).Contains(hostStem, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Strip common suffixes (<c>Client</c>, <c>Service</c>, <c>Api</c>,
    /// <c>Gateway</c>, <c>Proxy</c>, <c>Handler</c>, <c>Server</c>) and
    /// separators (<c>-</c>, <c>.</c>, <c>_</c>) to produce a stem.
    /// </summary>
    private static string NormalizeServiceName(string name)
    {
        var result = name;
        ReadOnlySpan<string> suffixes = ["Client", "Service", "Api", "Gateway", "Proxy", "Handler", "Server"];
        foreach (var suffix in suffixes)
        {
            if (result.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && result.Length > suffix.Length)
                result = result[..^suffix.Length];
        }

        ReadOnlySpan<string> prefixes = ["repo-", "svc-", "service-"];
        foreach (var prefix in prefixes)
        {
            if (result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && result.Length > prefix.Length)
                result = result[prefix.Length..];
        }

        return result.Replace("-", "", StringComparison.Ordinal)
            .Replace(".", "", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal)
            .ToLowerInvariant();
    }
}
