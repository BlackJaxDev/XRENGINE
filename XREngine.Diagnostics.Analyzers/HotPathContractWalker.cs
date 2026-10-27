using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Engine.Diagnostics;

/// <summary>Rejects calls and language forms outside the checked hot-path subset.</summary>
internal sealed class HotPathContractWalker : OperationWalker
{
    private readonly SyntaxNodeAnalysisContext _context;
    private readonly HotPathSymbols _symbols;
    private readonly IMethodSymbol _method;
    private readonly HashSet<SyntaxNode> _reported = new();
    private bool _reportedPointer;

    public HotPathContractWalker(SyntaxNodeAnalysisContext context, HotPathSymbols symbols, IMethodSymbol method)
    {
        _context = context;
        _symbols = symbols;
        _method = method;
    }

    public void Analyze(IOperation root)
    {
        CheckSignature(_method, root.Syntax);
        CheckInitialization(_method.ContainingType, root.Syntax);
        if (_method.MethodKind == MethodKind.Constructor)
            CheckInstanceInitializers(_method.ContainingType, root.Syntax);
        Visit(root);
    }

    public override void Visit(IOperation? operation)
    {
        _context.CancellationToken.ThrowIfCancellationRequested();
        if (operation is null)
            return;
        if (operation.Type is IPointerTypeSymbol or IFunctionPointerTypeSymbol || operation is IAddressOfOperation)
            RejectPointer(operation.Syntax);
        switch (operation)
        {
            case IDynamicInvocationOperation:
            case IDynamicIndexerAccessOperation:
            case IDynamicMemberReferenceOperation:
            case IDynamicObjectCreationOperation:
            case IFunctionPointerInvocationOperation:
                Reject(operation.Syntax, "dynamic and indirect calls have no checked target");
                break;
            case IUsingOperation:
            case IUsingDeclarationOperation:
                Reject(operation.Syntax, "implicit disposal is not supported; call a checked Dispose method explicitly");
                break;
            case ILockOperation:
                Reject(operation.Syntax, "lock requires an explicit trusted boundary");
                break;
            case IInterpolatedStringHandlerCreationOperation:
                Reject(operation.Syntax, "interpolated-string handler lowering is not verified");
                break;
            case ICollectionExpressionOperation:
                Reject(operation.Syntax, "collection-expression lowering is not verified");
                break;
            case IRangeOperation:
            case IImplicitIndexerReferenceOperation:
            case IRecursivePatternOperation:
            case IListPatternOperation:
            case IDeconstructionAssignmentOperation:
            case ITupleBinaryOperation:
                Reject(operation.Syntax, "implicit slicing, pattern, and deconstruction calls are not verified");
                break;
            case ICoalesceOperation coalesce when !coalesce.ValueConversion.IsIdentity:
                Reject(operation.Syntax, "the null-coalescing value conversion is not verified");
                break;
            case ITypeOfOperation:
                Reject(operation.Syntax, "runtime type-object creation requires a trusted boundary");
                break;
            case IDeclarationPatternOperation pattern when
                pattern.InputType is { } input && pattern.NarrowedType is { } narrowed &&
                _symbols.Compilation.ClassifyConversion(input, narrowed).IsBoxing:
                Reject(operation.Syntax, "this declaration pattern can box its input");
                break;
            case IIsTypeOperation typeTest when typeTest.ValueOperand.Type is { } input &&
                _symbols.Compilation.ClassifyConversion(input, typeTest.TypeOperand).IsBoxing:
                Reject(operation.Syntax, "this type test can require boxing");
                break;
            case IInvalidOperation:
                Reject(operation.Syntax, "the compiler could not bind this operation");
                break;
        }
        base.Visit(operation);
    }

    public override void VisitInvocation(IInvocationOperation operation)
    {
        if (!_symbols.IsLinq(operation.TargetMethod))
            CheckCall(operation.TargetMethod, operation.Syntax, operation.IsVirtual);
        base.VisitInvocation(operation);
    }

    public override void VisitObjectCreation(IObjectCreationOperation operation)
    {
        if (operation.Type?.IsValueType == true && operation.Constructor is { } constructor)
        {
            if (!constructor.IsImplicitlyDeclared)
            {
                if (operation.Type.IsRefLikeType)
                    Reject(operation.Syntax, "ref-like constructors can create storage aliases and require unsupported alias summaries");
                CheckCall(constructor, operation.Syntax);
            }
            CheckInitialization(constructor.ContainingType, operation.Syntax);
            CheckInstanceInitializers(constructor.ContainingType, operation.Syntax);
        }
        base.VisitObjectCreation(operation);
    }

    public override void VisitTypeParameterObjectCreation(ITypeParameterObjectCreationOperation operation)
    {
        Reject(operation.Syntax, "generic construction has no fixed constructor body");
        base.VisitTypeParameterObjectCreation(operation);
    }

    public override void VisitPropertyReference(IPropertyReferenceOperation operation)
    {
        bool write = operation.Parent is IAssignmentOperation assignment && ReferenceEquals(assignment.Target, operation);
        bool read = !write || operation.Property.SetMethod is null || operation.Parent is ICompoundAssignmentOperation or ICoalesceAssignmentOperation;
        if (operation.Parent is IIncrementOrDecrementOperation)
            write = true;
        bool virtualDispatch = operation.Instance?.Syntax is not BaseExpressionSyntax;
        if (read && operation.Property.GetMethod is { } getter)
            CheckCall(getter, operation.Syntax, virtualDispatch);
        if (write && operation.Property.SetMethod is { } setter)
            CheckCall(setter, operation.Syntax, virtualDispatch);
        base.VisitPropertyReference(operation);
    }

    public override void VisitFieldReference(IFieldReferenceOperation operation)
    {
        if (operation.Field.IsStatic && !operation.Field.IsConst)
            CheckInitialization(operation.Field.ContainingType, operation.Syntax);
        base.VisitFieldReference(operation);
    }

    public override void VisitEventAssignment(IEventAssignmentOperation operation)
    {
        Reject(operation.Syntax, "event subscription can allocate a multicast delegate");
        base.VisitEventAssignment(operation);
    }

    public override void VisitConversion(IConversionOperation operation)
    {
        if (operation.Type is INamedTypeSymbol { IsTupleType: true } && !operation.Conversion.IsIdentity)
            Reject(operation.Syntax, "tuple element conversions require separate boxing and call checks");
        if (operation.OperatorMethod is { } method)
            CheckCall(method, operation.Syntax);
        base.VisitConversion(operation);
    }

    public override void VisitBinaryOperator(IBinaryOperation operation)
    {
        if (operation.OperatorMethod is { } method)
        {
            CheckCall(method, operation.Syntax);
            if (operation.OperatorKind is BinaryOperatorKind.ConditionalAnd or BinaryOperatorKind.ConditionalOr)
                Reject(operation.Syntax, "user-defined short-circuit operators have implicit true/false calls");
        }
        if (operation.Type?.TypeKind == TypeKind.Delegate)
            Reject(operation.Syntax, "delegate combination can allocate");
        base.VisitBinaryOperator(operation);
    }

    public override void VisitUnaryOperator(IUnaryOperation operation)
    {
        if (operation.OperatorMethod is { } method)
            CheckCall(method, operation.Syntax);
        base.VisitUnaryOperator(operation);
    }

    public override void VisitIncrementOrDecrement(IIncrementOrDecrementOperation operation)
    {
        if (operation.OperatorMethod is { } method)
            CheckCall(method, operation.Syntax);
        base.VisitIncrementOrDecrement(operation);
    }

    public override void VisitCompoundAssignment(ICompoundAssignmentOperation operation)
    {
        if (operation.OperatorMethod is { } method)
            CheckCall(method, operation.Syntax);
        if (operation.InConversion.MethodSymbol is { } input)
            CheckCall(input, operation.Syntax);
        if (operation.OutConversion.MethodSymbol is { } output)
            CheckCall(output, operation.Syntax);
        if (operation.Type?.TypeKind == TypeKind.Delegate)
            Reject(operation.Syntax, "delegate combination can allocate");
        base.VisitCompoundAssignment(operation);
    }

    public override void VisitForEachLoop(IForEachLoopOperation operation)
    {
        if (operation.Syntax is CommonForEachStatementSyntax syntax)
        {
            if (syntax is ForEachVariableStatementSyntax)
                Reject(operation.Syntax, "foreach deconstruction can call an unchecked Deconstruct method");
            ForEachStatementInfo info = _context.SemanticModel.GetForEachStatementInfo(syntax);
            if (info.ElementConversion.IsTupleConversion || info.CurrentConversion.IsTupleConversion)
                Reject(operation.Syntax, "foreach tuple conversions can hide boxing and user-defined calls");
            if (info.ElementConversion.MethodSymbol is { } elementConversion)
                CheckCall(elementConversion, operation.Syntax);
            if (info.CurrentConversion.MethodSymbol is { } currentConversion)
                CheckCall(currentConversion, operation.Syntax);
            ITypeSymbol? type = _context.SemanticModel.GetTypeInfo(syntax.Expression, _context.CancellationToken).Type;
            if (type is not IArrayTypeSymbol && type?.SpecialType != SpecialType.System_String)
                Reject(operation.Syntax, "enumerator acquisition, iteration, and disposal require checked explicit calls; use indexed iteration");
        }
        base.VisitForEachLoop(operation);
    }

    public override void VisitLocalFunction(ILocalFunctionOperation operation)
    {
        CheckSignature(operation.Symbol, operation.Syntax);
        base.VisitLocalFunction(operation);
    }

    public override void VisitAnonymousFunction(IAnonymousFunctionOperation operation)
    {
        CheckSignature(operation.Symbol, operation.Syntax);
        base.VisitAnonymousFunction(operation);
    }

    private void CheckSignature(IMethodSymbol method, SyntaxNode syntax)
    {
        if (method.ReturnsByRef || method.ReturnsByRefReadonly || method.ReturnType.IsRefLikeType)
            Reject(syntax, "ref and ref-like returns require alias summaries and are not supported");
        if (method.ReturnType is IPointerTypeSymbol or IFunctionPointerTypeSymbol)
            RejectPointer(syntax);
        foreach (IParameterSymbol parameter in method.Parameters)
        {
            if (parameter.Type is IPointerTypeSymbol or IFunctionPointerTypeSymbol)
                RejectPointer(syntax);
            if (parameter.Type.IsRefLikeType && parameter.RefKind is RefKind.Ref or RefKind.Out)
                Reject(syntax, "ref/out ref-like parameters can export aliases and are not supported");
        }
    }

    private void CheckCall(IMethodSymbol method, SyntaxNode syntax, bool virtualDispatch = false)
    {
        if (method.MethodKind is MethodKind.DelegateInvoke or MethodKind.FunctionPointerSignature ||
            method.ContainingType.TypeKind == TypeKind.Interface ||
            (virtualDispatch && (method.IsVirtual || method.IsOverride || method.IsAbstract) &&
             !method.IsSealed && !method.ContainingType.IsSealed))
        {
            Reject(syntax, "the call target can change at runtime; use a checked concrete target or a trusted wrapper");
            return;
        }
        if (_symbols.Contracts.IsIntrinsic(method))
            return;
        if (method.ReturnsByRef || method.ReturnsByRefReadonly || method.ReturnType.IsRefLikeType)
        {
            Reject(syntax, "the call can return a storage alias; this return contract is not supported");
            return;
        }
        foreach (IParameterSymbol parameter in method.Parameters)
            if (parameter.Type.IsRefLikeType && parameter.RefKind is RefKind.Ref or RefKind.Out)
            {
                Reject(syntax, "the call can export a ref-like alias through ref/out");
                return;
            }
        if (_symbols.Contracts.IsTrusted(method))
            return;
        if (!_symbols.Contracts.IsChecked(method, _context.CancellationToken))
        {
            Reject(syntax, $"'{method.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)}' needs a checked [HotPath] body or a [HotPathTrusted] contract with a reason");
            return;
        }
        CheckInitialization(method.ContainingType, syntax);
    }

    private void CheckInitialization(INamedTypeSymbol type, SyntaxNode syntax)
    {
        if (!_symbols.Contracts.HasCheckedInitialization(type, _context.CancellationToken))
            Reject(syntax, $"type '{type.Name}' has unchecked static initialization");
    }

    private void CheckInstanceInitializers(INamedTypeSymbol type, SyntaxNode syntax)
    {
        foreach (ISymbol member in type.GetMembers())
            if (!member.IsStatic && HotPathContracts.HasInitializer(member, _context.CancellationToken))
                Reject(syntax, "instance field and property initializers are outside the checked constructor body");
    }

    private void RejectPointer(SyntaxNode syntax)
    {
        if (_reportedPointer)
            return;
        _reportedPointer = true;
        _context.ReportDiagnostic(Diagnostic.Create(HotPathAnalyzer.Ownership, syntax.GetLocation(),
            "raw pointer and function-pointer operations require a trusted wrapper outside the checked method"));
    }

    private void Reject(SyntaxNode syntax, string reason)
    {
        if (_reported.Add(syntax))
            _context.ReportDiagnostic(Diagnostic.Create(HotPathAnalyzer.Unverified, syntax.GetLocation(), reason));
    }
}
