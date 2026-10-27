using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Engine.Diagnostics;

/// <summary>Checks allocations and local ownership in explicitly marked methods.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HotPathAnalyzer : DiagnosticAnalyzer
{
    internal const string AttributeName = "Engine.Diagnostics.HotPathAttribute";
    internal static readonly DiagnosticDescriptor Allocation = new(
        "ENG0001", "Allocation in a hot path", "Hot path allocation: {0}",
        "Performance", DiagnosticSeverity.Error, isEnabledByDefault: true);
    internal static readonly DiagnosticDescriptor Ownership = new(
        "ENG0002", "Unsafe local ownership in a hot path", "Hot path ownership: {0}",
        "Ownership", DiagnosticSeverity.Error, isEnabledByDefault: true);
    internal static readonly DiagnosticDescriptor Unverified = new(
        "ENG0003", "Unverified hot-path behavior", "Hot path verification: {0}",
        "Verification", DiagnosticSeverity.Error, isEnabledByDefault: true);
    private static readonly ImmutableArray<DiagnosticDescriptor> Rules = [Allocation, Ownership, Unverified];

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => Rules;

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(static start =>
        {
            INamedTypeSymbol? marker = start.Compilation.GetTypeByMetadataName(AttributeName);
            if (marker is null)
                return;

            var symbols = new HotPathSymbols((CSharpCompilation)start.Compilation, marker, start.Options.AnalyzerConfigOptionsProvider);
            start.RegisterSyntaxNodeAction(node => AnalyzeMethod(node, symbols),
                SyntaxKind.MethodDeclaration, SyntaxKind.ConstructorDeclaration,
                SyntaxKind.OperatorDeclaration, SyntaxKind.ConversionOperatorDeclaration,
                SyntaxKind.GetAccessorDeclaration, SyntaxKind.SetAccessorDeclaration,
                SyntaxKind.InitAccessorDeclaration, SyntaxKind.AddAccessorDeclaration,
                SyntaxKind.RemoveAccessorDeclaration, SyntaxKind.LocalFunctionStatement,
                SyntaxKind.SimpleLambdaExpression, SyntaxKind.ParenthesizedLambdaExpression,
                SyntaxKind.AnonymousMethodExpression);
        });
    }

    private static void AnalyzeMethod(SyntaxNodeAnalysisContext context, HotPathSymbols symbols)
    {
        if (!CanHaveMarker(context.Node))
            return;
        IMethodSymbol? method = GetMethod(context.Node, context.SemanticModel, context.CancellationToken);
        if (method is null || !HasMarker(method, symbols.Marker))
            return;

        // The outer marked method owns the complete lexical subtree, including nested functions.
        for (SyntaxNode? parent = context.Node.Parent; parent is not null; parent = parent.Parent)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (CanHaveMarker(parent) && GetMethod(parent, context.SemanticModel, context.CancellationToken) is { } ancestor &&
                HasMarker(ancestor, symbols.Marker))
                return;
        }

        IOperation? operation = context.SemanticModel.GetOperation(context.Node, context.CancellationToken);
        if (operation is null && context.Node is BaseMethodDeclarationSyntax declaration &&
            ((SyntaxNode?)declaration.Body ?? declaration.ExpressionBody) is { } body)
            operation = context.SemanticModel.GetOperation(body, context.CancellationToken);
        if (operation is null)
            return;

        var walker = new HotPathOperationWalker(context, symbols);
        walker.Visit(operation);
        new HotPathContractWalker(context, symbols, method).Analyze(operation);
        HotPathBorrowWalker.Analyze(operation, context);
        if (walker.HasStackStorage)
            HotPathEscapeWalker.Analyze(operation, context);
    }

    private static bool CanHaveMarker(SyntaxNode node)
        => node switch
        {
            BaseMethodDeclarationSyntax method => method.AttributeLists.Count != 0 || method.Modifiers.Any(SyntaxKind.PartialKeyword),
            LocalFunctionStatementSyntax local => local.AttributeLists.Count != 0,
            AccessorDeclarationSyntax accessor => accessor.AttributeLists.Count != 0,
            LambdaExpressionSyntax lambda => lambda.AttributeLists.Count != 0,
            _ => false
        };

    private static IMethodSymbol? GetMethod(SyntaxNode node, SemanticModel model, System.Threading.CancellationToken cancellationToken)
        => node switch
        {
            BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax
                => model.GetDeclaredSymbol(node, cancellationToken) as IMethodSymbol,
            AnonymousFunctionExpressionSyntax => (model.GetOperation(node, cancellationToken) as IAnonymousFunctionOperation)?.Symbol,
            _ => null
        };

    private static bool HasMarker(IMethodSymbol method, INamedTypeSymbol marker)
    {
        foreach (AttributeData attribute in method.GetAttributes())
            if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, marker))
                return true;
        return false;
    }
}
