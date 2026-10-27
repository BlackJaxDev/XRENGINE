using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Engine.Diagnostics;

/// <summary>Runs a forward may-analysis of mutable loans over each function graph.</summary>
internal static class HotPathBorrowWalker
{
    public static void Analyze(IOperation root, SyntaxNodeAnalysisContext context)
    {
        try
        {
            IOperation top = root;
            while (top.Parent is not null)
                top = top.Parent;
            ControlFlowGraph? graph = top switch
            {
                IMethodBodyOperation method => ControlFlowGraph.Create(method, context.CancellationToken),
                IConstructorBodyOperation constructor => ControlFlowGraph.Create(constructor, context.CancellationToken),
                IBlockOperation block => ControlFlowGraph.Create(block, context.CancellationToken),
                _ => null
            };
            IMethodSymbol? nested = root switch
            {
                ILocalFunctionOperation local => local.Symbol,
                IAnonymousFunctionOperation anonymous => anonymous.Symbol,
                _ => null
            };
            if (graph is not null && nested is not null)
                graph = FindGraph(graph, nested, context);
            if (graph is null)
            {
                ReportGraphFailure(root, context);
                return;
            }
            AnalyzeGraph(graph, context);
        }
        catch (ArgumentException)
        {
            ReportGraphFailure(root, context);
        }
        catch (InvalidOperationException)
        {
            ReportGraphFailure(root, context);
        }
    }

    private static void AnalyzeGraph(ControlFlowGraph graph, SyntaxNodeAnalysisContext context)
    {
        new HotPathBorrowAnalysis(graph, context).Run();
        foreach (IMethodSymbol local in graph.LocalFunctions)
            AnalyzeGraph(graph.GetLocalFunctionControlFlowGraph(local, context.CancellationToken), context);
        foreach (IFlowAnonymousFunctionOperation anonymous in GetAnonymousFunctions(graph, context))
            AnalyzeGraph(graph.GetAnonymousFunctionControlFlowGraph(anonymous, context.CancellationToken), context);
    }

    private static ControlFlowGraph? FindGraph(ControlFlowGraph graph, IMethodSymbol target, SyntaxNodeAnalysisContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        foreach (IMethodSymbol local in graph.LocalFunctions)
        {
            ControlFlowGraph child = graph.GetLocalFunctionControlFlowGraph(local, context.CancellationToken);
            if (SymbolEqualityComparer.Default.Equals(local, target))
                return child;
            if (FindGraph(child, target, context) is { } found)
                return found;
        }
        foreach (IFlowAnonymousFunctionOperation anonymous in GetAnonymousFunctions(graph, context))
        {
            ControlFlowGraph child = graph.GetAnonymousFunctionControlFlowGraph(anonymous, context.CancellationToken);
            if (SymbolEqualityComparer.Default.Equals(anonymous.Symbol, target))
                return child;
            if (FindGraph(child, target, context) is { } found)
                return found;
        }
        return null;
    }

    private static List<IFlowAnonymousFunctionOperation> GetAnonymousFunctions(ControlFlowGraph graph, SyntaxNodeAnalysisContext context)
    {
        var result = new List<IFlowAnonymousFunctionOperation>();
        foreach (BasicBlock block in graph.Blocks)
        {
            foreach (IOperation operation in block.Operations)
                CollectAnonymous(operation, result, context);
            if (block.BranchValue is { } value)
                CollectAnonymous(value, result, context);
        }
        return result;
    }

    private static void CollectAnonymous(IOperation operation, List<IFlowAnonymousFunctionOperation> result, SyntaxNodeAnalysisContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        if (operation is IFlowAnonymousFunctionOperation anonymous)
        {
            result.Add(anonymous);
            return;
        }
        foreach (IOperation child in operation.ChildOperations)
            CollectAnonymous(child, result, context);
    }

    private static void ReportGraphFailure(IOperation root, SyntaxNodeAnalysisContext context)
        => context.ReportDiagnostic(Diagnostic.Create(HotPathAnalyzer.Ownership, root.Syntax.GetLocation(),
            "the compiler could not form a supported control-flow graph"));
}
