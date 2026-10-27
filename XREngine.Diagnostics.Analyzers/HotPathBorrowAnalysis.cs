using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Engine.Diagnostics;

/// <summary>Merges possible lexical loans at graph joins until loop states are stable.</summary>
internal sealed class HotPathBorrowAnalysis
{
    private readonly ControlFlowGraph _graph;
    private readonly SyntaxNodeAnalysisContext _context;
    private readonly Dictionary<ISymbol, ISymbol> _aliases = new(SymbolEqualityComparer.Default);
    private readonly Dictionary<CaptureId, IOperation?> _captures = new();
    private readonly Dictionary<IArgumentOperation, int> _loanIds = new();
    private readonly Dictionary<CaptureId, List<IArgumentOperation>> _capturedLoans = new();
    private readonly List<HotPathBorrowLoan> _loans = new();
    private readonly HashSet<TextSpan> _reported = new();
    private readonly List<int> _expired = new();

    public HotPathBorrowAnalysis(ControlFlowGraph graph, SyntaxNodeAnalysisContext context)
    {
        _graph = graph;
        _context = context;
    }

    public void Run()
    {
        Scan(_graph.OriginalOperation);
        foreach (BasicBlock block in _graph.Blocks)
        {
            foreach (IOperation operation in block.Operations)
                CollectCaptures(operation);
            if (block.BranchValue is { } value)
                CollectCaptures(value);
        }

        foreach (BasicBlock block in _graph.Blocks)
        {
            foreach (IOperation operation in block.Operations)
                CollectCapturedLoans(operation);
            if (block.BranchValue is { } value)
                CollectCapturedLoans(value);
        }

        var inputs = new HashSet<int>?[_graph.Blocks.Length];
        var queued = new bool[_graph.Blocks.Length];
        var pending = new Queue<int>();
        inputs[0] = new HashSet<int>();
        pending.Enqueue(0);
        queued[0] = true;
        while (pending.Count > 0)
        {
            CheckCancellation();
            int ordinal = pending.Dequeue();
            queued[ordinal] = false;
            BasicBlock block = _graph.Blocks[ordinal];
            var state = new HashSet<int>(inputs[ordinal]!);
            Transfer(block, state, report: false);
            Propagate(block, block.FallThroughSuccessor, state, inputs, queued, pending);
            Propagate(block, block.ConditionalSuccessor, state, inputs, queued, pending);
        }
        foreach (BasicBlock block in _graph.Blocks)
            if (inputs[block.Ordinal] is { } input && block.IsReachable)
                Transfer(block, new HashSet<int>(input), report: true);
    }

    private void Transfer(BasicBlock block, HashSet<int> state, bool report)
    {
        var walker = new HotPathBorrowTransfer(this, state, report);
        foreach (IOperation operation in block.Operations)
            walker.Visit(operation);
        if (block.BranchValue is { } branch)
            walker.Visit(branch);
    }

    private void Propagate(BasicBlock source, ControlFlowBranch? edge, HashSet<int> output,
        HashSet<int>?[] inputs, bool[] queued, Queue<int> pending)
    {
        if (edge?.Destination is not { IsReachable: true } destination)
            return;
        var incoming = new HashSet<int>(output);
        SyntaxNode? target = FirstSyntax(destination);
        if (target is not null)
            ExpireOutside(incoming, target);
        if (destination.Ordinal <= source.Ordinal)
            ExpireLoopBody(incoming, target);
        bool changed = inputs[destination.Ordinal] is null;
        HashSet<int> input = inputs[destination.Ordinal] ??= new HashSet<int>();
        foreach (int loan in incoming)
            changed |= input.Add(loan);
        if (changed && !queued[destination.Ordinal])
        {
            queued[destination.Ordinal] = true;
            pending.Enqueue(destination.Ordinal);
        }
    }

    private static SyntaxNode? FirstSyntax(BasicBlock block)
        => block.Operations.Length != 0 ? block.Operations[0].Syntax : block.BranchValue?.Syntax;

    public void ExpireOutside(HashSet<int> state, SyntaxNode syntax)
    {
        _expired.Clear();
        foreach (int id in state)
            if (!_loans[id].Scope.Span.Contains(syntax.SpanStart))
                _expired.Add(id);
        foreach (int id in _expired)
            state.Remove(id);
    }

    private void ExpireLoopBody(HashSet<int> state, SyntaxNode? target)
    {
        if (target is null)
            return;
        _expired.Clear();
        foreach (int id in state)
        {
            HotPathBorrowLoan loan = _loans[id];
            // A backward edge that re-enters a loop body starts a new lexical block lifetime.
            // Goto is rejected, so an internal jump cannot masquerade as loop re-entry.
            for (SyntaxNode? node = loan.Source.Parent; node is not null; node = node.Parent)
            {
                if (node is not (WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax or CommonForEachStatementSyntax))
                    continue;
                if (!node.Span.Contains(target.SpanStart))
                    continue;
                if (node.Span.Contains(loan.Scope.Span) && target.SpanStart <= loan.Source.SpanStart)
                    _expired.Add(id);
                break;
            }
        }
        foreach (int id in _expired)
            state.Remove(id);
    }

    public ISymbol? Root(IOperation operation, int depth = 0)
    {
        if (depth > _captures.Count + _aliases.Count + 32)
            return null;
        ISymbol? symbol = operation switch
        {
            ILocalReferenceOperation local => local.Local,
            IParameterReferenceOperation parameter => parameter.Parameter,
            IConversionOperation conversion => Root(conversion.Operand, depth + 1),
            IParenthesizedOperation parentheses => Root(parentheses.Operand, depth + 1),
            IDeclarationExpressionOperation declaration => Root(declaration.Expression, depth + 1),
            IFieldReferenceOperation { Instance.Type.IsValueType: true } field => Root(field.Instance, depth + 1),
            IFlowCaptureReferenceOperation capture when _captures.TryGetValue(capture.Id, out IOperation? value) && value is not null
                => Root(value, depth + 1),
            _ => null
        };
        for (int remaining = _aliases.Count; symbol is not null && remaining > 0 && _aliases.TryGetValue(symbol, out ISymbol? alias); remaining--)
            symbol = alias;
        return symbol;
    }

    public bool IsBorrowed(ISymbol symbol, HashSet<int> state)
    {
        foreach (int id in state)
            if (SymbolEqualityComparer.Default.Equals(symbol, _loans[id].Root))
                return true;
        return false;
    }

    public void Borrow(IArgumentOperation argument, ISymbol root, HashSet<int> state)
    {
        if (!_loanIds.TryGetValue(argument, out int id))
        {
            id = _loans.Count;
            _loanIds.Add(argument, id);
            _loans.Add(new HotPathBorrowLoan(root, Scope(argument.Syntax), argument.Syntax));
        }
        state.Add(id);
    }

    private SyntaxNode Scope(SyntaxNode syntax)
    {
        for (SyntaxNode? node = syntax.Parent; node is not null; node = node.Parent)
            if (node is BlockSyntax or BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax or AccessorDeclarationSyntax)
                return node;
        return _graph.OriginalOperation.Syntax;
    }

    public void Report(SyntaxNode syntax, string reason)
    {
        if (_reported.Add(syntax.Span))
            _context.ReportDiagnostic(Diagnostic.Create(HotPathAnalyzer.Ownership, syntax.GetLocation(), reason));
    }

    public void CheckCancellation() => _context.CancellationToken.ThrowIfCancellationRequested();

    private void Scan(IOperation operation)
    {
        CheckCancellation();
        if (operation is ILocalFunctionOperation or IAnonymousFunctionOperation)
            return;
        if (operation is ITryOperation)
            Report(operation.Syntax, "try/catch/finally borrow transitions are not supported");
        if (operation.Syntax is GotoStatementSyntax)
            Report(operation.Syntax, "goto can cross lexical borrow lifetimes and is not supported");
        if (operation is IVariableDeclaratorOperation { Initializer: { } initializer } declaration)
        {
            if (declaration.Symbol.RefKind != RefKind.None)
            {
                if (Root(initializer.Value) is { } root)
                    _aliases[declaration.Symbol] = root;
                else
                    Report(declaration.Syntax, "this ref alias does not identify supported local or parameter storage");
            }
            else if (declaration.Symbol.Type.IsRefLikeType && Root(initializer.Value) is not null)
                Report(declaration.Syntax, "copying a ref-like view requires alias summaries and is not supported");
        }
        if (operation is ISimpleAssignmentOperation assignment)
        {
            if (assignment.IsRef)
                Report(assignment.Syntax, "ref reassignment is not supported");
            else if (assignment.Value.Type?.IsRefLikeType == true && Root(assignment.Value) is not null)
                Report(assignment.Syntax, "copying a ref-like view requires alias summaries and is not supported");
        }
        foreach (IOperation child in operation.ChildOperations)
            Scan(child);
    }

    public List<IArgumentOperation>? CapturedLoans(CaptureId id)
        => _capturedLoans.TryGetValue(id, out List<IArgumentOperation>? arguments) ? arguments : null;

    private void CollectCapturedLoans(IOperation operation)
    {
        CheckCancellation();
        if (operation is IFlowAnonymousFunctionOperation)
            return;
        if (operation is IArgumentOperation { Value: IFlowCaptureReferenceOperation capture } argument &&
            argument.Parameter?.RefKind is RefKind.Ref or RefKind.Out)
        {
            if (!_capturedLoans.TryGetValue(capture.Id, out List<IArgumentOperation>? arguments))
            {
                arguments = new List<IArgumentOperation>();
                _capturedLoans.Add(capture.Id, arguments);
            }
            arguments.Add(argument);
        }
        foreach (IOperation child in operation.ChildOperations)
            CollectCapturedLoans(child);
    }

    private void CollectCaptures(IOperation operation)
    {
        CheckCancellation();
        if (operation is IFlowAnonymousFunctionOperation)
            return;
        if (operation is IFlowCaptureOperation capture)
        {
            if (_captures.ContainsKey(capture.Id))
                _captures[capture.Id] = null;
            else
                _captures.Add(capture.Id, capture.Value);
        }
        foreach (IOperation child in operation.ChildOperations)
            CollectCaptures(child);
    }
}
