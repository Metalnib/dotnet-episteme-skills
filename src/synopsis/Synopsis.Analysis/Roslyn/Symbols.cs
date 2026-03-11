using Synopsis.Analysis.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Synopsis.Analysis.Roslyn;

public static class Symbols
{
    public static bool IsSource(ISymbol symbol) =>
        symbol.Locations.Any(loc => loc.IsInSource && !string.IsNullOrWhiteSpace(loc.SourceTree?.FilePath));

    public static SourceLocation? ToLocation(ISymbol symbol) =>
        symbol.Locations.Select(ToLocation).FirstOrDefault(loc => loc is not null);

    public static SourceLocation? ToLocation(SyntaxNode node) => ToLocation(node.GetLocation());

    public static SourceLocation? ToLocation(Location? location)
    {
        if (location is null || !location.IsInSource || string.IsNullOrWhiteSpace(location.SourceTree?.FilePath))
            return null;

        var span = location.GetLineSpan();
        return new SourceLocation(
            Paths.Normalize(span.Path),
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1);
    }

    public static string TypeId(INamedTypeSymbol symbol) =>
        $"type:{symbol.GetDocumentationCommentId() ?? symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}";

    public static string MethodId(IMethodSymbol symbol) =>
        $"method:{symbol.GetDocumentationCommentId() ?? symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}";

    public static string SyntheticMethodId(string projectName, SyntaxNode node, string nameHint) =>
        NodeId.From("method", projectName, nameHint, node.SyntaxTree.FilePath,
            node.SpanStart.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static string TypeDisplayName(INamedTypeSymbol symbol) =>
        symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

    public static string MethodDisplayName(IMethodSymbol symbol) =>
        $"{symbol.ContainingType.Name}.{symbol.Name}";

    public static NodeType ClassifyType(INamedTypeSymbol symbol)
    {
        if (symbol.TypeKind == TypeKind.Interface)
            return NodeType.Interface;

        if (InheritsFrom(symbol, "Microsoft.EntityFrameworkCore.DbContext"))
            return NodeType.DbContext;

        if (InheritsFrom(symbol, "Microsoft.AspNetCore.Mvc.ControllerBase")
            || symbol.Name.EndsWith("Controller", StringComparison.Ordinal))
            return NodeType.Controller;

        if (symbol.Name.EndsWith("Service", StringComparison.Ordinal)
            || symbol.Name.EndsWith("Repository", StringComparison.Ordinal)
            || symbol.Name.EndsWith("Manager", StringComparison.Ordinal)
            || symbol.Name.EndsWith("Handler", StringComparison.Ordinal)
            || symbol.Name.EndsWith("Client", StringComparison.Ordinal))
            return NodeType.Service;

        return NodeType.Implementation;
    }

    public static bool InheritsFrom(INamedTypeSymbol? symbol, string fullyQualifiedBaseType)
    {
        var current = symbol;
        while (current is not null)
        {
            var name = current.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                .Replace("global::", string.Empty);
            if (string.Equals(name, fullyQualifiedBaseType, StringComparison.Ordinal))
                return true;
            current = current.BaseType;
        }
        return false;
    }

    public static bool IsGeneratedFile(string filePath) =>
        filePath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || filePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
        || filePath.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase);
}
