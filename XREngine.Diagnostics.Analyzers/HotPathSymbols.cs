using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Engine.Diagnostics;

/// <summary>Stores compilation-owned symbols. No symbols survive the compilation.</summary>
internal sealed class HotPathSymbols
{
    public HotPathSymbols(CSharpCompilation compilation, INamedTypeSymbol marker, AnalyzerConfigOptionsProvider options)
    {
        Compilation = compilation;
        Marker = marker;
        Contracts = new HotPathContracts(compilation, marker, options);
        Enumerable = compilation.GetTypeByMetadataName("System.Linq.Enumerable");
        Queryable = compilation.GetTypeByMetadataName("System.Linq.Queryable");
        ParallelEnumerable = compilation.GetTypeByMetadataName("System.Linq.ParallelEnumerable");
        Expression = compilation.GetTypeByMetadataName("System.Linq.Expressions.Expression`1");
    }

    public CSharpCompilation Compilation { get; }
    public INamedTypeSymbol Marker { get; }
    public HotPathContracts Contracts { get; }
    private INamedTypeSymbol? Enumerable { get; }
    private INamedTypeSymbol? Queryable { get; }
    private INamedTypeSymbol? ParallelEnumerable { get; }
    private INamedTypeSymbol? Expression { get; }

    public bool IsLinq(IMethodSymbol method)
    {
        INamedTypeSymbol type = (method.ReducedFrom ?? method).ContainingType.OriginalDefinition;
        return SymbolEqualityComparer.Default.Equals(type, Enumerable) ||
            SymbolEqualityComparer.Default.Equals(type, Queryable) ||
            SymbolEqualityComparer.Default.Equals(type, ParallelEnumerable);
    }

    public bool IsExpressionTree(ITypeSymbol? type)
        => type is INamedTypeSymbol named && SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, Expression);
}
