using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Engine.Diagnostics;

/// <summary>Tracks possible stack-storage provenance through local value dependencies.</summary>
internal sealed class HotPathEscapeWalker : OperationWalker
{
    private readonly SyntaxNodeAnalysisContext _context;
    private readonly Dictionary<ISymbol, HashSet<ISymbol>> _dependents = new(SymbolEqualityComparer.Default);
    private readonly HashSet<ISymbol> _stackLocals = new(SymbolEqualityComparer.Default);
    private readonly Queue<ISymbol> _pending = new();
    private readonly List<IOperation> _sinks = new();

    private HotPathEscapeWalker(SyntaxNodeAnalysisContext context) => _context = context;

    public static void Analyze(IOperation root, SyntaxNodeAnalysisContext context)
    {
        var walker = new HotPathEscapeWalker(context);
        walker.Visit(root);
        while (walker._pending.Count > 0)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            ISymbol source = walker._pending.Dequeue();
            if (walker._dependents.TryGetValue(source, out HashSet<ISymbol>? targets))
                foreach (ISymbol target in targets)
                    walker.MarkStack(target);
        }
        foreach (IOperation sink in walker._sinks)
            if (walker.HasStackSource(sink))
                context.ReportDiagnostic(Diagnostic.Create(HotPathAnalyzer.Ownership, sink.Syntax.GetLocation(),
                    "a value derived from local stack storage can escape through this return or store"));
    }

    public override void Visit(IOperation? operation)
    {
        _context.CancellationToken.ThrowIfCancellationRequested();
        base.Visit(operation);
    }

    public override void VisitVariableDeclarator(IVariableDeclaratorOperation operation)
    {
        if (operation.Initializer is { } initializer)
            AddDependencies(operation.Symbol, initializer.Value);
        base.VisitVariableDeclarator(operation);
    }

    public override void VisitSimpleAssignment(ISimpleAssignmentOperation operation)
    {
        if (LocalStorage(operation.Target) is { } local)
        {
            AddDependencies(local, operation.Value);
            if (local.RefKind != RefKind.None && !operation.IsRef)
                _sinks.Add(operation.Value);
        }
        else
        {
            // This includes fields, properties, array elements, pointer stores, and ref/out parameters.
            _sinks.Add(operation.Value);
        }
        base.VisitSimpleAssignment(operation);
    }

    public override void VisitReturn(IReturnOperation operation)
    {
        if (operation.ReturnedValue is { } value)
            _sinks.Add(value);
        base.VisitReturn(operation);
    }

    public override void VisitAnonymousFunction(IAnonymousFunctionOperation operation)
        => Analyze(operation.Body, _context);

    public override void VisitLocalFunction(ILocalFunctionOperation operation)
    {
        if (operation.Body is not null)
            Analyze(operation.Body, _context);
    }

    private void MarkStack(ISymbol symbol)
    {
        if (_stackLocals.Add(symbol))
            _pending.Enqueue(symbol);
    }

    private static ILocalSymbol? LocalStorage(IOperation target)
        => target switch
        {
            ILocalReferenceOperation local => local.Local,
            IFieldReferenceOperation { Instance.Type.IsValueType: true } field => LocalStorage(field.Instance),
            _ => null
        };

    private void AddDependencies(ISymbol target, IOperation value)
    {
        _context.CancellationToken.ThrowIfCancellationRequested();
        if (IsStackSource(value))
        {
            MarkStack(target);
            return;
        }
        if (value is ILocalReferenceOperation local)
        {
            if (!_dependents.TryGetValue(local.Local, out HashSet<ISymbol>? targets))
            {
                targets = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
                _dependents.Add(local.Local, targets);
            }
            targets.Add(target);
            return;
        }
        if (!CanCarryStorage(value))
            return;
        foreach (IOperation child in value.ChildOperations)
            AddDependencies(target, child);
    }

    private bool HasStackSource(IOperation value)
    {
        _context.CancellationToken.ThrowIfCancellationRequested();
        if (IsStackSource(value))
            return true;
        if (value is ILocalReferenceOperation local)
            return _stackLocals.Contains(local.Local);
        if (!CanCarryStorage(value))
            return false;
        foreach (IOperation child in value.ChildOperations)
            if (HasStackSource(child))
                return true;
        return false;
    }

    private static bool CanCarryStorage(IOperation operation)
    {
        // A load through a pointer or span produces a value, not the address of that value.
        if (operation is IAnonymousFunctionOperation or ILocalFunctionOperation or IArrayElementReferenceOperation)
            return false;
        if (operation is IPropertyReferenceOperation or IFieldReferenceOperation or IInvocationOperation)
            return operation.Type is IPointerTypeSymbol || operation.Type?.IsRefLikeType == true;
        return operation is IConversionOperation or IConditionalOperation or IBinaryOperation or
            IArgumentOperation or IObjectCreationOperation or IParenthesizedOperation ||
            operation.Type is IPointerTypeSymbol || operation.Type?.IsRefLikeType == true;
    }

    private static bool IsStackSource(IOperation operation)
    {
        if (operation.Syntax is StackAllocArrayCreationExpressionSyntax or ImplicitStackAllocArrayCreationExpressionSyntax)
            return true;
        if (operation is IAddressOfOperation address)
            return IsLocalStorage(address.Reference);
        return false;
    }

    private static bool IsLocalStorage(IOperation reference)
        => reference switch
        {
            ILocalReferenceOperation => true,
            IParameterReferenceOperation parameter => parameter.Parameter.RefKind == RefKind.None,
            IFieldReferenceOperation { Instance.Type.IsValueType: true } field => IsLocalStorage(field.Instance),
            _ => false
        };
}
