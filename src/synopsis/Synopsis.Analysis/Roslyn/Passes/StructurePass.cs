using Synopsis.Analysis.Model;
using Synopsis.Analysis.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Synopsis.Analysis.Roslyn.Passes;

internal sealed class StructurePass : IAnalysisPass
{
    public string Name => "structure";

    public void Analyze(LoadedProject project, GraphBuilder graph, GraphBuilder? mainGraph, CancellationToken ct)
    {
        var projectId = WorkspaceScanner.ProjectNodeId(project.Project.FilePath ?? project.ProjectName);
        var solutionId = project.SolutionPath is null ? null : WorkspaceScanner.SolutionNodeId(project.SolutionPath);

        graph.AddNode(projectId, NodeType.Project, project.Project.Name,
            repositoryName: project.RepositoryName, projectName: project.ProjectName,
            certainty: Certainty.Exact,
            metadata: new Dictionary<string, string?>
            {
                ["filePath"] = project.Project.FilePath,
                ["assemblyName"] = project.Compilation.AssemblyName,
                ["solutionPath"] = project.SolutionPath
            });

        if (solutionId is not null)
            graph.AddEdge(solutionId, projectId, EdgeType.Contains,
                $"{Path.GetFileNameWithoutExtension(project.SolutionPath)} contains {project.ProjectName}",
                repositoryName: project.RepositoryName, projectName: project.ProjectName);

        AddProjectReferences(project, graph, projectId);
        AnalyzeSyntaxTrees(project, graph, projectId, ct);
    }

    private static void AddProjectReferences(LoadedProject project, GraphBuilder graph, string projectId)
    {
        foreach (var projectRef in project.Project.ProjectReferences)
        {
            var referenced = project.Workspace.FindProject(projectRef.ProjectId);
            if (referenced is null) continue;

            var refId = WorkspaceScanner.ProjectNodeId(referenced.Project.FilePath ?? referenced.ProjectName);
            graph.AddEdge(projectId, refId, EdgeType.DependsOn,
                $"{project.ProjectName} references {referenced.ProjectName}",
                repositoryName: project.RepositoryName, projectName: project.ProjectName);

            if (!string.Equals(project.RepositoryName, referenced.RepositoryName, StringComparison.OrdinalIgnoreCase))
                graph.AddEdge(projectId, refId, EdgeType.CrossesRepoBoundary,
                    $"{project.ProjectName} crosses into {referenced.ProjectName}",
                    repositoryName: project.RepositoryName, projectName: project.ProjectName);
        }
    }

    private static void AnalyzeSyntaxTrees(LoadedProject project, GraphBuilder graph, string projectId, CancellationToken ct)
    {
        foreach (var tree in project.Compilation.SyntaxTrees)
        {
            ct.ThrowIfCancellationRequested();
            if (!project.Workspace.Options.IncludeGeneratedFiles && Symbols.IsGeneratedFile(tree.FilePath))
                continue;

            var model = project.Compilation.GetSemanticModel(tree);
            var root = tree.GetRoot(ct);

            foreach (var typeSyntax in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(typeSyntax, ct) is not INamedTypeSymbol typeSymbol)
                    continue;
                AddTypeNode(project, graph, projectId, typeSymbol, typeSyntax);
                AddInjectionEdges(project, graph, typeSymbol);
            }

            foreach (var methodSyntax in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
                AddMethodGraph(project, graph, projectId, model, methodSyntax, ct);
        }
    }

    private static void AddTypeNode(LoadedProject project, GraphBuilder graph, string projectId,
        INamedTypeSymbol typeSymbol, TypeDeclarationSyntax typeSyntax)
    {
        var nodeType = Symbols.ClassifyType(typeSymbol);
        var typeId = Symbols.TypeId(typeSymbol);

        graph.AddNode(typeId, nodeType, Symbols.TypeDisplayName(typeSymbol),
            Symbols.ToLocation(typeSymbol), project.RepositoryName, project.ProjectName, Certainty.Exact,
            new Dictionary<string, string?>
            {
                ["fullName"] = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                ["namespace"] = typeSymbol.ContainingNamespace?.ToDisplayString(),
                ["kind"] = typeSymbol.TypeKind.ToString()
            });

        graph.AddEdge(projectId, typeId, EdgeType.Defines,
            $"{project.ProjectName} defines {typeSymbol.Name}",
            Symbols.ToLocation(typeSyntax), project.RepositoryName, project.ProjectName);

        foreach (var iface in typeSymbol.Interfaces)
        {
            var ifaceId = Symbols.TypeId(iface);
            graph.AddNode(ifaceId, NodeType.Interface, Symbols.TypeDisplayName(iface),
                Symbols.ToLocation(iface), project.RepositoryName, project.ProjectName, Certainty.Exact,
                new Dictionary<string, string?> { ["fullName"] = iface.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) });

            graph.AddEdge(typeId, ifaceId, EdgeType.Implements,
                $"{typeSymbol.Name} implements {iface.Name}",
                Symbols.ToLocation(typeSyntax), project.RepositoryName, project.ProjectName);
        }
    }

    private static void AddInjectionEdges(LoadedProject project, GraphBuilder graph, INamedTypeSymbol typeSymbol)
    {
        var sourceTypeId = Symbols.TypeId(typeSymbol);
        foreach (var ctor in typeSymbol.InstanceConstructors.Where(c => c.DeclaredAccessibility == Accessibility.Public))
        {
            foreach (var param in ctor.Parameters)
            {
                if (param.Type is not INamedTypeSymbol paramType || !Symbols.IsSource(paramType))
                    continue;

                var paramTypeId = Symbols.TypeId(paramType);
                graph.AddNode(paramTypeId, Symbols.ClassifyType(paramType), Symbols.TypeDisplayName(paramType),
                    Symbols.ToLocation(paramType), project.RepositoryName, project.ProjectName, Certainty.Exact);

                graph.AddEdge(sourceTypeId, paramTypeId, EdgeType.Injects,
                    $"{typeSymbol.Name} injects {paramType.Name}",
                    Symbols.ToLocation(ctor), project.RepositoryName, project.ProjectName);
            }
        }
    }

    private static void AddMethodGraph(LoadedProject project, GraphBuilder graph, string projectId,
        SemanticModel model, BaseMethodDeclarationSyntax methodSyntax, CancellationToken ct)
    {
        if (model.GetDeclaredSymbol(methodSyntax, ct) is not IMethodSymbol methodSymbol)
            return;
        if (methodSymbol.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove)
            return;

        var containingType = methodSymbol.ContainingType;
        var typeId = Symbols.TypeId(containingType);
        var methodId = Symbols.MethodId(methodSymbol);

        graph.AddNode(methodId, NodeType.Method, Symbols.MethodDisplayName(methodSymbol),
            Symbols.ToLocation(methodSymbol), project.RepositoryName, project.ProjectName, Certainty.Exact,
            new Dictionary<string, string?>
            {
                ["fullName"] = methodSymbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
                ["returns"] = methodSymbol.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
            });

        graph.AddEdge(typeId, methodId, EdgeType.Defines,
            $"{containingType.Name} defines {methodSymbol.Name}",
            Symbols.ToLocation(methodSyntax), project.RepositoryName, project.ProjectName);

        if (methodSyntax.Body is null && methodSyntax.ExpressionBody is null)
            return;

        foreach (var invocation in methodSyntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol targetSymbol)
                continue;
            AddCallEdge(project, graph, methodSymbol, methodId, targetSymbol, invocation);
        }
    }

    private static void AddCallEdge(LoadedProject project, GraphBuilder graph,
        IMethodSymbol caller, string callerId, IMethodSymbol target, InvocationExpressionSyntax invocation)
    {
        target = (IMethodSymbol)target.OriginalDefinition;
        var targetLocation = Symbols.ToLocation(target) ?? Symbols.ToLocation(invocation);

        if (Symbols.IsSource(target))
        {
            var targetId = Symbols.MethodId(target);
            graph.AddNode(targetId, NodeType.Method, Symbols.MethodDisplayName(target),
                targetLocation, project.RepositoryName, project.ProjectName, Certainty.Exact);

            graph.AddEdge(callerId, targetId, EdgeType.Calls,
                $"{caller.ContainingType.Name}.{caller.Name} calls {target.ContainingType.Name}.{target.Name}",
                Symbols.ToLocation(invocation), project.RepositoryName, project.ProjectName);

            if (target.ContainingType.TypeKind != TypeKind.Interface)
                return;
        }

        if (target.ContainingType.TypeKind != TypeKind.Interface)
            return;

        var ifaceMethodId = Symbols.MethodId(target);
        var registeredImpls = project.Workspace.Catalog.RegisteredBindingsByInterfaceId
            .GetValueOrDefault(Symbols.TypeId((INamedTypeSymbol)target.ContainingType), []);

        var implMethods = registeredImpls.Count > 0
            ? project.Workspace.Catalog.ImplementationMethodsByInterfaceMethodId
                .GetValueOrDefault(ifaceMethodId, [])
                .Where(m => registeredImpls.Any(r => string.Equals(r.ImplementationId, m.ContainingTypeId, StringComparison.OrdinalIgnoreCase)))
                .ToArray()
            : project.Workspace.Catalog.ImplementationMethodsByInterfaceMethodId
                .GetValueOrDefault(ifaceMethodId, [])
                .ToArray();

        if (implMethods.Length == 0)
            return;

        var certainty = implMethods.Length == 1 ? Certainty.Inferred : Certainty.Ambiguous;
        foreach (var impl in implMethods)
        {
            graph.AddNode(impl.Id, NodeType.Method, impl.DisplayName,
                impl.Location, impl.RepositoryName, impl.ProjectName, certainty);

            graph.AddEdge(callerId, impl.Id,
                certainty == Certainty.Ambiguous ? EdgeType.Ambiguous : EdgeType.Calls,
                $"{caller.ContainingType.Name}.{caller.Name} resolves {target.Name}",
                Symbols.ToLocation(invocation), project.RepositoryName, project.ProjectName, certainty,
                new Dictionary<string, string?>
                {
                    ["interfaceMethod"] = ifaceMethodId,
                    ["resolution"] = registeredImpls.Count > 0 ? "service-registration" : "interface-implementation"
                });
        }
    }
}
