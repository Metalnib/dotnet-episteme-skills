using System.Collections.Frozen;
using Synopsis.Analysis.Model;
using Synopsis.Analysis.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Synopsis.Analysis.Roslyn.Passes;

internal sealed class EndpointPass : IAnalysisPass
{
    private static readonly FrozenDictionary<string, string> AttributeVerbs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["HttpGetAttribute"] = "GET",
        ["HttpPostAttribute"] = "POST",
        ["HttpPutAttribute"] = "PUT",
        ["HttpDeleteAttribute"] = "DELETE",
        ["HttpPatchAttribute"] = "PATCH",
        ["HttpHeadAttribute"] = "HEAD",
        ["HttpOptionsAttribute"] = "OPTIONS"
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, string> MinimalApiVerbs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["MapGet"] = "GET",
        ["MapPost"] = "POST",
        ["MapPut"] = "PUT",
        ["MapDelete"] = "DELETE",
        ["MapPatch"] = "PATCH"
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public string Name => "endpoints";

    public void Analyze(LoadedProject project, GraphBuilder graph, GraphBuilder? mainGraph, CancellationToken ct)
    {
        foreach (var tree in project.Compilation.SyntaxTrees)
        {
            if (!project.Workspace.Options.IncludeGeneratedFiles && Symbols.IsGeneratedFile(tree.FilePath))
                continue;

            var model = project.Compilation.GetSemanticModel(tree);
            var root = tree.GetRoot(ct);

            foreach (var controller in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(controller, ct) is not INamedTypeSymbol controllerSymbol)
                    continue;
                if (Symbols.ClassifyType(controllerSymbol) != NodeType.Controller)
                    continue;
                AddControllerEndpoints(project, graph, model, controller, controllerSymbol, ct);
            }

            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                AddMinimalApiEndpoint(project, graph, model, invocation, ct);
        }
    }

    private static void AddControllerEndpoints(LoadedProject project, GraphBuilder graph,
        SemanticModel model, ClassDeclarationSyntax controllerSyntax,
        INamedTypeSymbol controllerSymbol, CancellationToken ct)
    {
        var controllerId = Symbols.TypeId(controllerSymbol);
        var classRoute = GetRouteTemplate(controllerSymbol, model, controllerSyntax);

        foreach (var methodSyntax in controllerSyntax.Members.OfType<MethodDeclarationSyntax>())
        {
            if (model.GetDeclaredSymbol(methodSyntax, ct) is not IMethodSymbol methodSymbol)
                continue;

            var verb = GetHttpVerb(methodSymbol);
            if (verb is null) continue;

            var methodRoute = GetRouteTemplate(methodSymbol, model, methodSyntax);
            var route = NormalizeRoute(controllerSymbol.Name, methodSymbol.Name, classRoute, methodRoute);
            var endpointId = NodeId.From("endpoint", project.ProjectName, verb, route, Symbols.MethodId(methodSymbol));
            var display = $"{verb} {route}";

            graph.AddNode(endpointId, NodeType.Endpoint, display,
                Symbols.ToLocation(methodSyntax), project.RepositoryName, project.ProjectName, Certainty.Exact,
                new Dictionary<string, string?>
                {
                    ["verb"] = verb, ["route"] = route,
                    ["handler"] = Symbols.MethodId(methodSymbol),
                    ["controller"] = controllerSymbol.Name
                });

            graph.AddEdge(controllerId, endpointId, EdgeType.Exposes,
                $"{controllerSymbol.Name} exposes {display}",
                Symbols.ToLocation(methodSyntax), project.RepositoryName, project.ProjectName);

            graph.AddEdge(endpointId, Symbols.MethodId(methodSymbol), EdgeType.Calls,
                $"{display} invokes {controllerSymbol.Name}.{methodSymbol.Name}",
                Symbols.ToLocation(methodSyntax), project.RepositoryName, project.ProjectName);
        }
    }

    private static void AddMinimalApiEndpoint(LoadedProject project, GraphBuilder graph,
        SemanticModel model, InvocationExpressionSyntax invocation, CancellationToken ct)
    {
        if (model.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol methodSymbol)
            return;
        if (!MinimalApiVerbs.TryGetValue(methodSymbol.Name, out var verb))
            return;
        if (invocation.ArgumentList.Arguments.Count < 2)
            return;

        var route = TryGetStringValue(invocation.ArgumentList.Arguments[0].Expression, model) ?? "/unknown";
        var handler = invocation.ArgumentList.Arguments[1].Expression;
        string? handlerId = null;
        var certainty = Certainty.Exact;

        if (model.GetSymbolInfo(handler, ct).Symbol is IMethodSymbol handlerMethod)
        {
            handlerId = Symbols.MethodId(handlerMethod);
        }
        else if (handler is LambdaExpressionSyntax lambda)
        {
            handlerId = Symbols.SyntheticMethodId(project.ProjectName, lambda, $"{verb} {route}");
            graph.AddNode(handlerId, NodeType.Method, $"lambda {verb} {route}",
                Symbols.ToLocation(lambda), project.RepositoryName, project.ProjectName, Certainty.Inferred,
                new Dictionary<string, string?> { ["synthetic"] = "true" });

            foreach (var nested in lambda.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetSymbolInfo(nested, ct).Symbol is not IMethodSymbol target || !Symbols.IsSource(target))
                    continue;

                var targetId = Symbols.MethodId(target);
                graph.AddNode(targetId, NodeType.Method, Symbols.MethodDisplayName(target),
                    Symbols.ToLocation(target), project.RepositoryName, project.ProjectName, Certainty.Exact);

                graph.AddEdge(handlerId, targetId, EdgeType.Calls,
                    $"lambda {verb} {route} calls {target.Name}",
                    Symbols.ToLocation(nested), project.RepositoryName, project.ProjectName, Certainty.Inferred);
            }
        }
        else
        {
            var eid = NodeId.From("endpoint", project.ProjectName, verb, route, invocation.SyntaxTree.FilePath, invocation.SpanStart.ToString());
            graph.AddNode(eid, NodeType.Endpoint, $"{verb} {route}",
                Symbols.ToLocation(invocation), project.RepositoryName, project.ProjectName, Certainty.Ambiguous);
            return;
        }

        var endpointId = NodeId.From("endpoint", project.ProjectName, verb, route, handlerId);
        graph.AddNode(endpointId, NodeType.Endpoint, $"{verb} {route}",
            Symbols.ToLocation(invocation), project.RepositoryName, project.ProjectName, certainty,
            new Dictionary<string, string?>
            {
                ["verb"] = verb, ["route"] = route, ["minimalApi"] = "true", ["handler"] = handlerId
            });

        if (handlerId is not null)
            graph.AddEdge(endpointId, handlerId, EdgeType.Calls,
                $"{verb} {route} invokes handler",
                Symbols.ToLocation(invocation), project.RepositoryName, project.ProjectName, certainty);
    }

    private static string? GetHttpVerb(IMethodSymbol method)
    {
        foreach (var attr in method.GetAttributes())
        {
            if (attr.AttributeClass?.Name is { } name && AttributeVerbs.TryGetValue(name, out var verb))
                return verb;

            if (string.Equals(attr.AttributeClass?.Name, "AcceptVerbsAttribute", StringComparison.OrdinalIgnoreCase)
                && attr.ConstructorArguments.FirstOrDefault() is { Kind: TypedConstantKind.Array } verbs
                && verbs.Values.FirstOrDefault().Value is string acceptVerb)
                return acceptVerb.ToUpperInvariant();
        }
        return null;
    }

    private static string? GetRouteTemplate(ISymbol symbol, SemanticModel model, SyntaxNode syntaxNode)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            if (string.Equals(attr.AttributeClass?.Name, "RouteAttribute", StringComparison.OrdinalIgnoreCase))
                return attr.ConstructorArguments.FirstOrDefault().Value?.ToString();

            if (attr.AttributeClass?.Name is { } name && AttributeVerbs.ContainsKey(name)
                && attr.ConstructorArguments.Length == 1)
                return attr.ConstructorArguments[0].Value?.ToString();
        }

        var routeAttr = syntaxNode.DescendantNodes().OfType<AttributeSyntax>()
            .FirstOrDefault(a => a.Name.ToString().Contains("Route", StringComparison.OrdinalIgnoreCase));

        return routeAttr?.ArgumentList?.Arguments.Count > 0
            ? TryGetStringValue(routeAttr.ArgumentList.Arguments[0].Expression, model)
            : null;
    }

    private static string NormalizeRoute(string controllerName, string actionName, string? classRoute, string? methodRoute)
    {
        var baseRoute = string.IsNullOrWhiteSpace(classRoute) ? string.Empty : classRoute.Trim('/');
        var actionRoute = string.IsNullOrWhiteSpace(methodRoute) ? string.Empty : methodRoute.Trim('/');
        var route = string.Join('/', new[] { baseRoute, actionRoute }.Where(v => !string.IsNullOrWhiteSpace(v)));

        route = route
            .Replace("[controller]", controllerName.Replace("Controller", string.Empty, StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase)
            .Replace("[action]", actionName, StringComparison.OrdinalIgnoreCase);

        return "/" + route.Trim('/');
    }

    internal static string? TryGetStringValue(ExpressionSyntax expression, SemanticModel model)
    {
        var constant = model.GetConstantValue(expression);
        if (constant.HasValue && constant.Value is not null)
            return constant.Value.ToString();

        if (expression is InterpolatedStringExpressionSyntax interpolated)
        {
            var parts = new List<string>();
            foreach (var content in interpolated.Contents)
            {
                switch (content)
                {
                    case InterpolatedStringTextSyntax text:
                        parts.Add(text.TextToken.ValueText);
                        break;
                    case InterpolationSyntax interpolation:
                        var val = model.GetConstantValue(interpolation.Expression);
                        if (!val.HasValue || val.Value is null) return null;
                        parts.Add(val.Value.ToString() ?? string.Empty);
                        break;
                }
            }
            return string.Concat(parts);
        }

        return null;
    }
}
