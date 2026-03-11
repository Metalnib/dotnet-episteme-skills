using System.Collections.Frozen;
using Synopsis.Analysis.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Synopsis.Analysis.Roslyn;

public static partial class SymbolCatalogFactory
{
    private static readonly FrozenSet<string> DiRegistrationMethods = FrozenSet.ToFrozenSet(
        ["AddScoped", "AddSingleton", "AddTransient", "TryAddScoped", "TryAddSingleton", "TryAddTransient"],
        StringComparer.Ordinal);

    public static Roslyn.SymbolCatalog Build(IEnumerable<LoadedProject> projects, ScanOptions options)
    {
        var catalog = new SymbolCatalog();

        foreach (var project in projects)
        {
            foreach (var syntaxTree in project.Compilation.SyntaxTrees)
            {
                if (!options.IncludeGeneratedFiles && Symbols.IsGeneratedFile(syntaxTree.FilePath))
                    continue;

                var model = project.Compilation.GetSemanticModel(syntaxTree);
                var root = syntaxTree.GetRoot();

                foreach (var typeSyntax in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(typeSyntax) is not INamedTypeSymbol typeSymbol)
                        continue;

                    var typeRef = new TypeRef(
                        Symbols.TypeId(typeSymbol),
                        Symbols.TypeDisplayName(typeSymbol),
                        Symbols.ToLocation(typeSymbol),
                        project.RepositoryName,
                        project.ProjectName);

                    foreach (var iface in typeSymbol.AllInterfaces)
                    {
                        var ifaceId = Symbols.TypeId(iface);
                        GetOrAdd(catalog.ImplementationsByInterfaceId, ifaceId).Add(typeRef);

                        foreach (var ifaceMember in iface.GetMembers().OfType<IMethodSymbol>())
                        {
                            if (typeSymbol.FindImplementationForInterfaceMember(ifaceMember) is not IMethodSymbol impl)
                                continue;

                            GetOrAdd(catalog.ImplementationMethodsByInterfaceMethodId, Symbols.MethodId(ifaceMember))
                                .Add(new MethodRef(
                                    Symbols.MethodId(impl),
                                    Symbols.TypeId(typeSymbol),
                                    Symbols.MethodDisplayName(impl),
                                    Symbols.ToLocation(impl),
                                    project.RepositoryName,
                                    project.ProjectName));
                        }
                    }
                }

                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol invokedSymbol)
                        continue;
                    if (!DiRegistrationMethods.Contains(invokedSymbol.Name))
                        continue;

                    var binding = TryCreateBinding(invocation, invokedSymbol, model, project);
                    if (binding is not null)
                        GetOrAdd(catalog.RegisteredBindingsByInterfaceId, binding.InterfaceId).Add(binding);
                }
            }
        }

        return catalog;
    }

    private static ServiceBinding? TryCreateBinding(
        InvocationExpressionSyntax invocation,
        IMethodSymbol invokedSymbol,
        SemanticModel model,
        LoadedProject project)
    {
        INamedTypeSymbol? interfaceSymbol = null;
        INamedTypeSymbol? implSymbol = null;

        if (invokedSymbol.TypeArguments.Length == 2)
        {
            interfaceSymbol = invokedSymbol.TypeArguments[0] as INamedTypeSymbol;
            implSymbol = invokedSymbol.TypeArguments[1] as INamedTypeSymbol;
        }
        else if (invokedSymbol.TypeArguments.Length == 1)
        {
            interfaceSymbol = invokedSymbol.TypeArguments[0] as INamedTypeSymbol;
            implSymbol = interfaceSymbol;
        }
        else if (invocation.ArgumentList.Arguments.Count >= 2)
        {
            interfaceSymbol = ExtractTypeOf(invocation.ArgumentList.Arguments[0].Expression, model);
            implSymbol = ExtractTypeOf(invocation.ArgumentList.Arguments[1].Expression, model);
        }

        if ((interfaceSymbol is null || implSymbol is null)
            && invocation.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax genericName }
            && genericName.TypeArgumentList.Arguments.Count >= 2)
        {
            interfaceSymbol = model.GetTypeInfo(genericName.TypeArgumentList.Arguments[0]).Type as INamedTypeSymbol;
            implSymbol = model.GetTypeInfo(genericName.TypeArgumentList.Arguments[1]).Type as INamedTypeSymbol;
        }

        if (interfaceSymbol is null || implSymbol is null)
            return null;

        return new ServiceBinding(
            Symbols.TypeId(interfaceSymbol),
            Symbols.TypeId(implSymbol),
            project.RepositoryName ?? string.Empty,
            project.ProjectName,
            Symbols.ToLocation(invocation));
    }

    private static INamedTypeSymbol? ExtractTypeOf(ExpressionSyntax expression, SemanticModel model) =>
        expression is TypeOfExpressionSyntax typeOf
            ? model.GetTypeInfo(typeOf.Type).Type as INamedTypeSymbol
            : model.GetTypeInfo(expression).Type as INamedTypeSymbol;

    private static List<T> GetOrAdd<T>(Dictionary<string, List<T>> source, string key)
    {
        if (!source.TryGetValue(key, out var list))
        {
            list = [];
            source[key] = list;
        }
        return list;
    }
}
