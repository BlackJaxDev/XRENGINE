using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Engine.Diagnostics;

/// <summary>Checks explicit operations and compiler-inserted conversions.</summary>
internal sealed class HotPathOperationWalker : OperationWalker
{
    private readonly SyntaxNodeAnalysisContext _context;
    private readonly HotPathSymbols _symbols;

    public bool HasStackStorage { get; private set; }

    public HotPathOperationWalker(SyntaxNodeAnalysisContext context, HotPathSymbols symbols)
    {
        _context = context;
        _symbols = symbols;
    }

    public override void Visit(IOperation? operation)
    {
        _context.CancellationToken.ThrowIfCancellationRequested();
        if (operation is IAddressOfOperation ||
            operation?.Syntax is StackAllocArrayCreationExpressionSyntax or ImplicitStackAllocArrayCreationExpressionSyntax)
            HasStackStorage = true;
        base.Visit(operation);
    }

    public override void VisitObjectCreation(IObjectCreationOperation operation)
    {
        if (operation.Type?.IsReferenceType == true)
            Report(operation, "reference-type construction");
        base.VisitObjectCreation(operation);
    }

    public override void VisitTypeParameterObjectCreation(ITypeParameterObjectCreationOperation operation)
    {
        if (operation.Type?.IsValueType != true)
            Report(operation, "generic construction can allocate a reference type");
        base.VisitTypeParameterObjectCreation(operation);
    }

    public override void VisitDynamicObjectCreation(IDynamicObjectCreationOperation operation)
    {
        if (operation.Type?.IsReferenceType == true)
            Report(operation, "reference-type construction with dynamic arguments");
        base.VisitDynamicObjectCreation(operation);
    }

    public override void VisitAnonymousObjectCreation(IAnonymousObjectCreationOperation operation)
    {
        Report(operation, "anonymous-object construction");
        base.VisitAnonymousObjectCreation(operation);
    }

    public override void VisitArrayCreation(IArrayCreationOperation operation)
    {
        Report(operation, "array construction, including expanded params arguments");
        base.VisitArrayCreation(operation);
    }

    public override void VisitConversion(IConversionOperation operation)
    {
        if (operation.Operand.Type is { } source && operation.Type is { } target &&
            _symbols.Compilation.ClassifyConversion(source, target).IsBoxing)
            Report(operation, "boxing conversion");
        if (_symbols.IsExpressionTree(operation.Type))
            Report(operation, "expression-tree construction");
        base.VisitConversion(operation);
    }

    public override void VisitInvocation(IInvocationOperation operation)
    {
        if (_symbols.IsLinq(operation.TargetMethod))
            Report(operation, "LINQ calls are forbidden by the hot-path policy");
        base.VisitInvocation(operation);
    }

    public override void VisitForEachLoop(IForEachLoopOperation operation)
    {
        if (operation.Syntax is CommonForEachStatementSyntax syntax)
        {
            ForEachStatementInfo info = _context.SemanticModel.GetForEachStatementInfo(syntax);
            ITypeSymbol? collectionType = _context.SemanticModel.GetTypeInfo(syntax.Expression, _context.CancellationToken).Type;
            // Arrays and strings use indexed lowering even when semantic info exposes an enumerator.
            bool indexed = collectionType is IArrayTypeSymbol || collectionType?.SpecialType == SpecialType.System_String;
            if (!indexed && info.GetEnumeratorMethod?.ReturnType.IsReferenceType == true)
                Report(operation, "enumeration uses a reference-type enumerator");
            if (info.ElementConversion.IsBoxing || info.CurrentConversion.IsBoxing)
                Report(operation, "foreach boxes an element");
        }
        base.VisitForEachLoop(operation);
    }

    public override void VisitAnonymousFunction(IAnonymousFunctionOperation operation)
    {
        DataFlowAnalysis? flow = _context.SemanticModel.AnalyzeDataFlow(operation.Syntax);
        if (flow?.Succeeded == true)
        {
            foreach (ISymbol captured in flow.CapturedInside)
            {
                bool declaredInside = false;
                foreach (SyntaxReference declaration in captured.DeclaringSyntaxReferences)
                    if (declaration.SyntaxTree == operation.Syntax.SyntaxTree && operation.Syntax.Span.Contains(declaration.Span))
                        declaredInside = true;
                if (declaredInside)
                    continue;
                Report(operation, "anonymous function captures state");
                break;
            }
        }
        if (operation.Symbol.IsAsync)
            Report(operation, "async anonymous function can allocate a state machine");
        base.VisitAnonymousFunction(operation);
    }

    public override void VisitDelegateCreation(IDelegateCreationOperation operation)
    {
        Report(operation, "delegate construction, including first-use delegate caches");
        base.VisitDelegateCreation(operation);
    }

    public override void VisitBinaryOperator(IBinaryOperation operation)
    {
        if (operation.OperatorKind == BinaryOperatorKind.Add && operation.Type?.SpecialType == SpecialType.System_String &&
            !operation.ConstantValue.HasValue)
            Report(operation, "string concatenation");
        base.VisitBinaryOperator(operation);
    }

    public override void VisitCompoundAssignment(ICompoundAssignmentOperation operation)
    {
        if (operation.OperatorKind == BinaryOperatorKind.Add && operation.Type?.SpecialType == SpecialType.System_String)
            Report(operation, "string concatenation");
        base.VisitCompoundAssignment(operation);
    }

    public override void VisitInterpolatedString(IInterpolatedStringOperation operation)
    {
        if (!operation.ConstantValue.HasValue && !HasHandlerParent(operation))
            Report(operation, "interpolated string construction");
        base.VisitInterpolatedString(operation);
    }

    public override void VisitWith(IWithOperation operation)
    {
        if (operation.Type?.IsReferenceType == true)
            Report(operation, "record clone");
        base.VisitWith(operation);
    }

    public override void VisitCollectionExpression(ICollectionExpressionOperation operation)
    {
        if (operation.Type?.IsReferenceType == true)
            Report(operation, "collection expression with a reference-type target");
        base.VisitCollectionExpression(operation);
    }

    public override void VisitMethodBodyOperation(IMethodBodyOperation operation)
    {
        if (_context.SemanticModel.GetDeclaredSymbol(operation.Syntax, _context.CancellationToken) is IMethodSymbol { IsAsync: true })
            Report(operation, "async method can allocate a state machine");
        base.VisitMethodBodyOperation(operation);
    }

    public override void VisitLocalFunction(ILocalFunctionOperation operation)
    {
        if (operation.Symbol.IsAsync)
            Report(operation, "async local function can allocate a state machine");
        base.VisitLocalFunction(operation);
    }

    public override void VisitReturn(IReturnOperation operation)
    {
        if (operation.Kind is OperationKind.YieldReturn or OperationKind.YieldBreak)
            Report(operation, "iterator state machine");
        base.VisitReturn(operation);
    }

    private static bool HasHandlerParent(IOperation operation)
    {
        for (IOperation? parent = operation.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is IInterpolatedStringHandlerCreationOperation)
                return true;
            if (parent is not IInterpolatedStringAdditionOperation)
                break;
        }
        return false;
    }

    private void Report(IOperation operation, string reason)
        => _context.ReportDiagnostic(Diagnostic.Create(HotPathAnalyzer.Allocation, operation.Syntax.GetLocation(), reason));
}
