using System.Collections.Frozen;
using Synopsis.Analysis.Model;
using Synopsis.Analysis.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Synopsis.Analysis.Roslyn.Passes;

internal sealed class HttpCallPass : IAnalysisPass
{
    private static readonly FrozenDictionary<string, string> HttpMethodVerbs = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GetAsync"] = "GET", ["GetFromJsonAsync"] = "GET",
        ["PostAsync"] = "POST", ["PostAsJsonAsync"] = "POST",
        ["PutAsync"] = "PUT", ["PutAsJsonAsync"] = "PUT",
        ["DeleteAsync"] = "DELETE", ["PatchAsync"] = "PATCH",
        ["SendAsync"] = "SEND"
    }.ToFrozenDictionary(StringComparer.Ordinal);

    public string Name => "http-calls";

    public void Analyze(LoadedProject project, GraphBuilder graph, GraphBuilder? mainGraph, CancellationToken ct)
    {
        foreach (var tree in project.Compilation.SyntaxTrees)
        {
            if (!project.Workspace.Options.IncludeGeneratedFiles && Symbols.IsGeneratedFile(tree.FilePath))
                continue;

            var model = project.Compilation.GetSemanticModel(tree);
            var root = tree.GetRoot(ct);

            foreach (var methodSyntax in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
                AnalyzeMethod(project, graph, mainGraph, model, methodSyntax, ct);
        }
    }

    private static void AnalyzeMethod(LoadedProject project, GraphBuilder graph, GraphBuilder? mainGraph,
        SemanticModel model, BaseMethodDeclarationSyntax methodSyntax, CancellationToken ct)
    {
        if (model.GetDeclaredSymbol(methodSyntax, ct) is not IMethodSymbol methodSymbol)
            return;

        var methodId = Symbols.MethodId(methodSymbol);
        var localClients = BuildLocalClientMap(project, model, methodSyntax, ct);

        foreach (var invocation in methodSyntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol invokedSymbol)
                continue;
            if (!IsHttpCall(invokedSymbol))
                continue;

            var client = ResolveClient(project, model, invocation, invokedSymbol, localClients);

            graph.AddNode(client.Id, NodeType.HttpClient, client.DisplayName,
                client.Location, project.RepositoryName, project.ProjectName, client.Certainty,
                new Dictionary<string, string?> { ["name"] = client.Name, ["baseUrl"] = client.BaseUrl });

            graph.AddEdge(methodId, client.Id, EdgeType.UsesHttpClient,
                $"{methodSymbol.ContainingType.Name}.{methodSymbol.Name} uses {client.DisplayName}",
                Symbols.ToLocation(invocation), project.RepositoryName, project.ProjectName, client.Certainty);

            var requestPath = ExtractRequestPath(invocation, model, invokedSymbol);
            var verb = HttpMethodVerbs.GetValueOrDefault(invokedSymbol.Name,
                invokedSymbol.Name.Replace("Async", string.Empty, StringComparison.Ordinal));
            var requestUri = CombineUri(client.BaseUrl, requestPath);
            var serviceName = requestUri?.Host ?? client.Name ?? client.DisplayName;
            var serviceId = NodeId.From("external-service", serviceName ?? project.ProjectName);
            var endpointId = NodeId.From("external-endpoint", verb, requestUri?.ToString() ?? requestPath ?? client.DisplayName);

            graph.AddNode(serviceId, NodeType.ExternalService, serviceName ?? "external-service",
                Symbols.ToLocation(invocation), project.RepositoryName, project.ProjectName,
                requestUri is null ? Certainty.Ambiguous : Certainty.Inferred,
                new Dictionary<string, string?> { ["host"] = requestUri?.Host });

            graph.AddNode(endpointId, NodeType.ExternalEndpoint,
                $"{verb} {requestUri?.PathAndQuery ?? requestPath ?? "/unknown"}",
                Symbols.ToLocation(invocation), project.RepositoryName, project.ProjectName,
                requestPath is null ? Certainty.Unresolved : Certainty.Inferred,
                new Dictionary<string, string?>
                {
                    ["verb"] = verb,
                    ["path"] = requestPath,
                    ["absoluteUri"] = requestUri?.ToString(),
                    // Denormalised for CrossRepoResolver: saves it from
                    // walking edges back to the HttpClient node.
                    ["clientName"] = client.Name,
                    ["clientBaseUrl"] = client.BaseUrl,
                });

            graph.AddEdge(client.Id, serviceId, EdgeType.CallsHttp,
                $"{client.DisplayName} targets {serviceName}",
                Symbols.ToLocation(invocation), project.RepositoryName, project.ProjectName,
                requestUri is null ? Certainty.Ambiguous : Certainty.Inferred);

            graph.AddEdge(serviceId, endpointId, EdgeType.CallsHttp,
                $"{serviceName} serves {verb} {requestUri?.PathAndQuery ?? requestPath ?? "/unknown"}",
                Symbols.ToLocation(invocation), project.RepositoryName, project.ProjectName,
                requestPath is null ? Certainty.Unresolved : Certainty.Inferred);

            // Cross-repo resolution (ExternalEndpoint → internal Endpoint) is
            // no longer done here — see Synopsis.Analysis.Graph.CrossRepoResolver,
            // which runs post-merge over the full combined graph so the
            // result is identical whether the caller is a full-workspace
            // scan or CombinedGraph's incremental re-merge.
        }
    }

    private static Dictionary<ISymbol, HttpClientInfo> BuildLocalClientMap(
        LoadedProject project, SemanticModel model,
        BaseMethodDeclarationSyntax methodSyntax, CancellationToken ct)
    {
        var map = new Dictionary<ISymbol, HttpClientInfo>(SymbolEqualityComparer.Default);

        foreach (var variable in methodSyntax.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (variable.Initializer?.Value is null) continue;
            if (model.GetDeclaredSymbol(variable, ct) is not ISymbol local) continue;

            var info = TryCreateFromExpression(project, model, variable.Initializer.Value, variable.Identifier.ValueText, ct);
            if (info is not null)
                map[local] = info;
        }

        return map;
    }

    private static HttpClientInfo ResolveClient(LoadedProject project, SemanticModel model,
        InvocationExpressionSyntax invocation, IMethodSymbol invokedSymbol,
        Dictionary<ISymbol, HttpClientInfo> localClients)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            var receiverSymbol = model.GetSymbolInfo(memberAccess.Expression).Symbol;
            if (receiverSymbol is not null && localClients.TryGetValue(receiverSymbol, out var local))
                return local;

            var receiverType = model.GetTypeInfo(memberAccess.Expression).Type;
            if (receiverType?.Name == "HttpClient")
                return CreateInfo(project, memberAccess.Expression.ToString(), memberAccess.Expression.ToString(),
                    FindBaseUrl(project, memberAccess.Expression.ToString()),
                    Symbols.ToLocation(invocation), Certainty.Inferred);
        }

        return CreateInfo(project, invokedSymbol.ContainingType.Name, invokedSymbol.ContainingType.Name,
            FindBaseUrl(project, invokedSymbol.ContainingType.Name),
            Symbols.ToLocation(invocation), Certainty.Ambiguous);
    }

    private static HttpClientInfo? TryCreateFromExpression(LoadedProject project, SemanticModel model,
        ExpressionSyntax expression, string fallback, CancellationToken ct)
    {
        if (expression is InvocationExpressionSyntax invocation
            && model.GetSymbolInfo(invocation, ct).Symbol is IMethodSymbol symbol
            && string.Equals(symbol.Name, "CreateClient", StringComparison.Ordinal)
            && symbol.ContainingType.Name == "IHttpClientFactory")
        {
            var name = invocation.ArgumentList.Arguments.Count > 0
                ? EndpointPass.TryGetStringValue(invocation.ArgumentList.Arguments[0].Expression, model)
                : fallback;
            return CreateInfo(project, name ?? fallback, name ?? fallback,
                FindBaseUrl(project, name ?? fallback),
                Symbols.ToLocation(invocation), Certainty.Inferred);
        }

        if (expression is ObjectCreationExpressionSyntax objectCreation
            && model.GetSymbolInfo(objectCreation, ct).Symbol is IMethodSymbol ctor
            && ctor.ContainingType.Name == "HttpClient")
        {
            return CreateInfo(project, fallback, fallback, FindBaseUrl(project, fallback),
                Symbols.ToLocation(objectCreation), Certainty.Ambiguous);
        }

        return null;
    }

    private static bool IsHttpCall(IMethodSymbol method)
    {
        if (!HttpMethodVerbs.ContainsKey(method.Name))
            return false;
        var typeName = method.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return typeName.Contains("HttpClient", StringComparison.Ordinal)
            || typeName.Contains("HttpClientJsonExtensions", StringComparison.Ordinal);
    }

    private static string? ExtractRequestPath(InvocationExpressionSyntax invocation, SemanticModel model, IMethodSymbol method)
    {
        if (method.Name == "SendAsync") return null;
        return invocation.ArgumentList.Arguments.Count > 0
            ? EndpointPass.TryGetStringValue(invocation.ArgumentList.Arguments[0].Expression, model)
            : null;
    }

    private static string? FindBaseUrl(LoadedProject project, string hint)
    {
        static bool LooksLikeUrl(string? v) => Uri.TryCreate(v, UriKind.Absolute, out _);

        var repoConfigs = project.ConfigurationValues.ToArray();
        return repoConfigs
            .Where(v => v.Key.Contains("url", StringComparison.OrdinalIgnoreCase)
                || v.Key.Contains("baseurl", StringComparison.OrdinalIgnoreCase))
            .Where(v => v.Key.Contains(hint, StringComparison.OrdinalIgnoreCase)
                || (v.Value?.Contains(hint, StringComparison.OrdinalIgnoreCase) ?? false))
            .Select(v => v.Value)
            .FirstOrDefault(LooksLikeUrl)
        ?? repoConfigs
            .Where(v => v.Key.Contains("url", StringComparison.OrdinalIgnoreCase))
            .Select(v => v.Value)
            .FirstOrDefault(LooksLikeUrl);
    }

    private static Uri? CombineUri(string? baseUrl, string? requestPath)
    {
        if (!string.IsNullOrWhiteSpace(requestPath)
            && requestPath.Contains("://", StringComparison.Ordinal)
            && Uri.TryCreate(requestPath, UriKind.Absolute, out var absolute))
            return absolute;

        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
            && !string.IsNullOrWhiteSpace(requestPath)
            && Uri.TryCreate(baseUri, requestPath, out var combined))
            return combined;

        return null;
    }

    private static HttpClientInfo CreateInfo(LoadedProject project, string displayName, string name,
        string? baseUrl, SourceLocation? location, Certainty certainty) =>
        new(NodeId.From("http-client", project.ProjectName, name), displayName, name, baseUrl, location, certainty);

    private sealed record HttpClientInfo(string Id, string DisplayName, string? Name, string? BaseUrl,
        SourceLocation? Location, Certainty Certainty);
}
