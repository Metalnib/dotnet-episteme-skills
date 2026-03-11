using System.Text.RegularExpressions;
using Synopsis.Analysis.Model;
using Synopsis.Analysis.Graph;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Synopsis.Analysis.Roslyn.Passes;

internal sealed partial class DataAccessPass : IAnalysisPass
{
    public string Name => "data-access";

    public void Analyze(LoadedProject project, GraphBuilder graph, GraphBuilder? mainGraph, CancellationToken ct)
    {
        var entityTableMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tree in project.Compilation.SyntaxTrees)
        {
            if (!project.Workspace.Options.IncludeGeneratedFiles && Symbols.IsGeneratedFile(tree.FilePath))
                continue;

            var model = project.Compilation.GetSemanticModel(tree);
            var root = tree.GetRoot(ct);

            foreach (var typeSyntax in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(typeSyntax, ct) is not INamedTypeSymbol typeSymbol)
                    continue;

                if (Symbols.InheritsFrom(typeSymbol, "Microsoft.EntityFrameworkCore.DbContext"))
                {
                    AddDbContextNode(project, graph, typeSymbol);
                    RegisterDbSetEntities(project, graph, typeSymbol, entityTableMap);
                    RegisterToTableMappings(project, graph, model, typeSyntax, entityTableMap);
                }
                else if (TryGetTableFromAttributes(typeSymbol) is { } tableName)
                {
                    AddEntityTableMapping(project, graph, typeSymbol, tableName, Certainty.Exact);
                    entityTableMap[Symbols.TypeId(typeSymbol)] = tableName;
                }
            }

            foreach (var methodSyntax in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
                AnalyzeMethod(project, graph, model, methodSyntax, entityTableMap, ct);
        }
    }

    private static void AddDbContextNode(LoadedProject project, GraphBuilder graph, INamedTypeSymbol dbContextSymbol)
    {
        graph.AddNode(Symbols.TypeId(dbContextSymbol), NodeType.DbContext, dbContextSymbol.Name,
            Symbols.ToLocation(dbContextSymbol), project.RepositoryName, project.ProjectName, Certainty.Exact,
            new Dictionary<string, string?> { ["fullName"] = dbContextSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) });
    }

    private static void RegisterDbSetEntities(LoadedProject project, GraphBuilder graph,
        INamedTypeSymbol dbContextSymbol, Dictionary<string, string> entityTableMap)
    {
        foreach (var prop in dbContextSymbol.GetMembers().OfType<IPropertySymbol>())
        {
            if (prop.Type is not INamedTypeSymbol propType
                || !string.Equals(propType.Name, "DbSet", StringComparison.Ordinal)
                || propType.TypeArguments.Length != 1
                || propType.TypeArguments[0] is not INamedTypeSymbol entitySymbol)
                continue;

            var table = entityTableMap.GetValueOrDefault(Symbols.TypeId(entitySymbol))
                ?? TryGetTableFromAttributes(entitySymbol)
                ?? entitySymbol.Name;

            AddEntityTableMapping(project, graph, entitySymbol, table, Certainty.Inferred);
        }
    }

    private static void RegisterToTableMappings(LoadedProject project, GraphBuilder graph,
        SemanticModel model, TypeDeclarationSyntax typeSyntax, Dictionary<string, string> entityTableMap)
    {
        foreach (var invocation in typeSyntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol method)
                continue;
            if (!string.Equals(method.Name, "Entity", StringComparison.Ordinal) || method.TypeArguments.Length != 1)
                continue;
            if (method.TypeArguments[0] is not INamedTypeSymbol entitySymbol)
                continue;

            var toTable = invocation.Parent?.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
                .FirstOrDefault(c => model.GetSymbolInfo(c).Symbol is IMethodSymbol m
                    && string.Equals(m.Name, "ToTable", StringComparison.Ordinal));

            var tableName = toTable is not null
                ? TryGetString(toTable.ArgumentList.Arguments.FirstOrDefault()?.Expression, model)
                : entitySymbol.Name;

            if (tableName is null) continue;

            entityTableMap[Symbols.TypeId(entitySymbol)] = tableName;
            AddEntityTableMapping(project, graph, entitySymbol, tableName,
                toTable is null ? Certainty.Inferred : Certainty.Exact);
        }
    }

    private static void AddEntityTableMapping(LoadedProject project, GraphBuilder graph,
        INamedTypeSymbol entitySymbol, string tableName, Certainty certainty)
    {
        var entityId = Symbols.TypeId(entitySymbol);
        var tableId = NodeId.From("table", project.RepositoryName ?? project.ProjectName, tableName);

        graph.AddNode(entityId, NodeType.Entity, entitySymbol.Name,
            Symbols.ToLocation(entitySymbol), project.RepositoryName, project.ProjectName, certainty,
            new Dictionary<string, string?> { ["fullName"] = entitySymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) });

        graph.AddNode(tableId, NodeType.Table, tableName,
            repositoryName: project.RepositoryName, projectName: project.ProjectName,
            certainty: certainty, metadata: new Dictionary<string, string?> { ["table"] = tableName });

        graph.AddEdge(entityId, tableId, EdgeType.MapsToTable,
            $"{entitySymbol.Name} maps to {tableName}",
            Symbols.ToLocation(entitySymbol), project.RepositoryName, project.ProjectName, certainty);
    }

    private static void AnalyzeMethod(LoadedProject project, GraphBuilder graph,
        SemanticModel model, BaseMethodDeclarationSyntax methodSyntax,
        Dictionary<string, string> entityTableMap, CancellationToken ct)
    {
        if (model.GetDeclaredSymbol(methodSyntax, ct) is not IMethodSymbol methodSymbol)
            return;

        var methodId = Symbols.MethodId(methodSymbol);

        foreach (var node in methodSyntax.DescendantNodes())
        {
            switch (node)
            {
                case MemberAccessExpressionSyntax memberAccess:
                    TrackDbContextAccess(project, graph, model, methodSymbol, methodId, memberAccess);
                    break;
                case IdentifierNameSyntax identifier:
                    TrackEntityUsage(project, graph, model, methodSymbol, methodId, identifier, entityTableMap);
                    break;
                case InvocationExpressionSyntax invocation:
                    TrackRawSql(project, graph, model, methodSymbol, methodId, invocation);
                    break;
            }
        }
    }

    private static void TrackDbContextAccess(LoadedProject project, GraphBuilder graph,
        SemanticModel model, IMethodSymbol methodSymbol, string methodId,
        MemberAccessExpressionSyntax memberAccess)
    {
        if (model.GetTypeInfo(memberAccess.Expression).Type is not INamedTypeSymbol receiverType
            || !Symbols.InheritsFrom(receiverType, "Microsoft.EntityFrameworkCore.DbContext"))
            return;

        var dbContextId = Symbols.TypeId(receiverType);
        graph.AddNode(dbContextId, NodeType.DbContext, receiverType.Name,
            Symbols.ToLocation(receiverType), project.RepositoryName, project.ProjectName, Certainty.Exact);

        graph.AddEdge(methodId, dbContextId, EdgeType.UsesDbContext,
            $"{methodSymbol.Name} uses {receiverType.Name}",
            Symbols.ToLocation(memberAccess), project.RepositoryName, project.ProjectName);
    }

    private static void TrackEntityUsage(LoadedProject project, GraphBuilder graph,
        SemanticModel model, IMethodSymbol methodSymbol, string methodId,
        IdentifierNameSyntax identifier, Dictionary<string, string> entityTableMap)
    {
        if (model.GetTypeInfo(identifier).Type is not INamedTypeSymbol type)
            return;

        if (Symbols.InheritsFrom(type, "Microsoft.EntityFrameworkCore.DbContext"))
        {
            var id = Symbols.TypeId(type);
            graph.AddNode(id, NodeType.DbContext, type.Name,
                Symbols.ToLocation(type), project.RepositoryName, project.ProjectName, Certainty.Exact);
            graph.AddEdge(methodId, id, EdgeType.UsesDbContext,
                $"{methodSymbol.Name} uses {type.Name}",
                Symbols.ToLocation(identifier), project.RepositoryName, project.ProjectName);
            return;
        }

        if (type.Name == "DbSet" && type.TypeArguments.Length == 1
            && type.TypeArguments[0] is INamedTypeSymbol entitySymbol)
        {
            var entityId = Symbols.TypeId(entitySymbol);
            graph.AddNode(entityId, NodeType.Entity, entitySymbol.Name,
                Symbols.ToLocation(entitySymbol), project.RepositoryName, project.ProjectName, Certainty.Inferred);

            graph.AddEdge(methodId, entityId, EdgeType.QueriesEntity,
                $"{methodSymbol.Name} queries {entitySymbol.Name}",
                Symbols.ToLocation(identifier), project.RepositoryName, project.ProjectName, Certainty.Inferred);

            if (entityTableMap.TryGetValue(entityId, out var tableName))
            {
                var tableId = NodeId.From("table", project.RepositoryName ?? project.ProjectName, tableName);
                graph.AddNode(tableId, NodeType.Table, tableName,
                    repositoryName: project.RepositoryName, projectName: project.ProjectName, certainty: Certainty.Inferred);
                graph.AddEdge(methodId, tableId, EdgeType.DependsOn,
                    $"{methodSymbol.Name} touches {tableName}",
                    Symbols.ToLocation(identifier), project.RepositoryName, project.ProjectName, Certainty.Inferred);
            }
        }
    }

    private static void TrackRawSql(LoadedProject project, GraphBuilder graph,
        SemanticModel model, IMethodSymbol methodSymbol, string methodId,
        InvocationExpressionSyntax invocation)
    {
        if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol invokedSymbol)
            return;
        if (!invokedSymbol.Name.Contains("Sql", StringComparison.Ordinal))
            return;

        var sqlText = invocation.ArgumentList.Arguments
            .Select(a => TryGetString(a.Expression, model))
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));

        if (string.IsNullOrWhiteSpace(sqlText))
            return;

        foreach (Match match in SqlTablePattern().Matches(sqlText))
        {
            var tableName = match.Groups["table"].Value;
            var tableId = NodeId.From("table", project.RepositoryName ?? project.ProjectName, tableName);

            graph.AddNode(tableId, NodeType.Table, tableName,
                repositoryName: project.RepositoryName, projectName: project.ProjectName,
                certainty: Certainty.Ambiguous,
                metadata: new Dictionary<string, string?> { ["rawSql"] = sqlText });

            graph.AddEdge(methodId, tableId, EdgeType.DependsOn,
                $"{methodSymbol.Name} references {tableName} in SQL",
                Symbols.ToLocation(invocation), project.RepositoryName, project.ProjectName, Certainty.Ambiguous,
                new Dictionary<string, string?> { ["sql"] = sqlText });
        }
    }

    private static string? TryGetTableFromAttributes(INamedTypeSymbol entitySymbol)
    {
        foreach (var attr in entitySymbol.GetAttributes())
        {
            if (string.Equals(attr.AttributeClass?.Name, "TableAttribute", StringComparison.Ordinal))
                return attr.ConstructorArguments.FirstOrDefault().Value?.ToString();
        }
        return null;
    }

    private static string? TryGetString(ExpressionSyntax? expression, SemanticModel model)
    {
        if (expression is null) return null;
        var constant = model.GetConstantValue(expression);
        return constant.HasValue && constant.Value is not null ? constant.Value.ToString() : null;
    }

    [GeneratedRegex(@"\b(?:from|join|update|into|table)\s+(?<table>[A-Za-z0-9_\.\[\]]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex SqlTablePattern();
}
