using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Engine.Diagnostics;

/// <summary>Applies evaluation-ordered reads and loans to one graph block.</summary>
internal sealed class HotPathBorrowTransfer : OperationWalker
{
    private readonly HotPathBorrowAnalysis _analysis;
    private readonly HashSet<int> _state;
    private readonly bool _report;

    public HotPathBorrowTransfer(HotPathBorrowAnalysis analysis, HashSet<int> state, bool report)
    {
        _analysis = analysis;
        _state = state;
        _report = report;
    }

    public override void Visit(IOperation? operation)
    {
        _analysis.CheckCancellation();
        if (operation is null || operation is IFlowAnonymousFunctionOperation)
            return;
        _analysis.ExpireOutside(_state, operation.Syntax);
        base.Visit(operation);
    }

    public override void VisitArgument(IArgumentOperation operation)
    {
        Visit(operation.Value);
        if (operation.Parameter?.RefKind is not (RefKind.Ref or RefKind.Out))
            return;
        ISymbol? root = _analysis.Root(operation.Value);
        if (root is null)
        {
            if (_report)
                _analysis.Report(operation.Syntax, "mutable arguments must identify local or parameter storage; heap and ambiguous aliases are not supported");
            return;
        }
        _analysis.Borrow(operation, root, _state);
    }

    public override void VisitFlowCapture(IFlowCaptureOperation operation)
    {
        Visit(operation.Value);
        // The reference argument is evaluated before later arguments branch or call other methods.
        if (_analysis.CapturedLoans(operation.Id) is { } arguments && _analysis.Root(operation.Value) is { } root)
            foreach (IArgumentOperation argument in arguments)
                _analysis.Borrow(argument, root, _state);
    }

    public override void VisitLocalReference(ILocalReferenceOperation operation)
    {
        if (!operation.IsDeclaration)
            Read(operation);
    }

    public override void VisitParameterReference(IParameterReferenceOperation operation) => Read(operation);

    private void Read(IOperation operation)
    {
        if (_report && _analysis.Root(operation) is { } root && _analysis.IsBorrowed(root, _state))
            _analysis.Report(operation.Syntax, $"storage '{root.Name}' is used after a mutable borrow; end the borrowing block before reuse");
    }
}
