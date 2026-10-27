using System.Collections.Immutable;
using Engine.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using NUnit.Framework;

namespace XREngine.UnitTests.HotPathAnalyzers;

/// <summary>Runs the generator and analyzer against real C# compilations.</summary>
[TestFixture]
public sealed class HotPathAnalyzerTests
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);
    private static readonly ImmutableArray<MetadataReference> References =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToImmutableArray();

    private static IEnumerable<TestCaseData> Cases()
    {
        yield return Case("Unmarked", "object M() => new object();", 0);
        yield return Case("ReferenceNew", "[HotPath] object M() => new object();", 1);
        yield return Case("TargetTypedNew", "[HotPath] object M() => new();", 1);
        yield return Case("ValueNew", "[HotPath] int M() => new int();", 0);
        yield return Case("ArrayNew", "[HotPath] int[] M() => new int[2];", 1);
        yield return Case("AnonymousNew", "[HotPath] object M() => new { X = 1 };", 1, 0, 1);
        yield return Case("BoxReturn", "[HotPath] object M(int x) => x;", 1);
        yield return Case("BoxExplicit", "[HotPath] object M(int x) => (object)x;", 1);
        yield return Case("BoxInterface", "[HotPath] IComparable M(int x) => x;", 1);
        yield return Case("BoxValueType", "[HotPath] ValueType M(int x) => x;", 1);
        yield return Case("BoxNullable", "[HotPath] object M(int? x) => x;", 1);
        yield return Case("BoxArgument", "[HotPath] static void Use(object x) {} [HotPath] void M(int x) { Use(x); }", 1);
        yield return Case("BoxGeneric", "[HotPath] object M<T>(T x) => x;", 1);
        yield return Case("NoBoxClass", "[HotPath] object M<T>(T x) where T : class => x;", 0);
        yield return Case("ConstrainedCall", "[HotPath] int M<T>(T x) where T : struct, IComparable<T> => x.CompareTo(x);", 0, 0, 1);
        yield return Case("CaptureLocal", "[HotPath] Func<int> M() { int x = 1; return () => x; }", 2, 0, 0);
        yield return Case("CaptureParameter", "[HotPath] Func<int> M(int x) => () => x;", 2, 0, 0);
        yield return Case("CaptureThis", "int field; [HotPath] Func<int> M() => () => field;", 2, 0, 0);
        yield return Case("CaptureAnonymous", "[HotPath] Func<int> M(int x) => delegate { return x; };", 2, 0, 0);
        yield return Case("StaticLambda", "[HotPath] Func<int> M() => static () => 1;", 1, 0, 0);
        yield return Case("NestedLambdaAllocation", "[HotPath] Func<object> M() => static () => new object();", 2, 0, 0);
        yield return Case("NestedLocalAllocation", "[HotPath] object M() { return F(); static object F() => new object(); }", 1);
        yield return Case("MarkedLocalOnly", "object M() { _ = new object(); return F(); [HotPath] static object F() => new object(); }", 1);
        yield return Case("NestedMarkersOnce", "[HotPath] object M() { return F(); [HotPath] static object F() => new object(); }", 1);
        yield return Case("LinqExtension", "[HotPath] bool M(int[] x) => x.Any();", 1);
        yield return Case("LinqStatic", "[HotPath] bool M(int[] x) => Enumerable.Any(x);", 1);
        yield return Case("SameNamedMethod", "[HotPath] static bool Any(int[] x) => true; [HotPath] bool M(int[] x) => Any(x);", 0);
        yield return Case("ArrayForeach", "[HotPath] void M(int[] x) { foreach (int v in x) { } }", 0);
        yield return Case("StringForeach", "[HotPath] void M(string x) { foreach (char v in x) { } }", 0);
        yield return Case("MultiArrayForeach", "[HotPath] void M(int[,] x) { foreach (int v in x) { } }", 0);
        yield return Case("ArrayCastForeach", "[HotPath] void M(int[] x) { foreach (int v in (IEnumerable<int>)x) { } }", 1, 0, 1);
        yield return Case("ListForeach", "[HotPath] void M(List<int> x) { foreach (int v in x) { } }", 0, 0, 1);
        yield return Case("InterfaceForeach", "[HotPath] void M(IEnumerable<int> x) { foreach (int v in x) { } }", 1, 0, 1);
        yield return Case("ForeachElementBox", "[HotPath] void M(int[] x) { foreach (object v in x) { } }", 1);
        yield return Case("SpanForeach", "[HotPath] void M(Span<int> x) { foreach (int v in x) { } }", 0, 0, 1);
        yield return Case("ParamsArray", "[HotPath] static void Use(params int[] x) {} [HotPath] void M() { Use(1, 2); }", 1);
        yield return Case("Stackalloc", "[HotPath] void M() { Span<int> x = stackalloc int[4]; x[0] = 1; }", 0);
        yield return Case("ReadBeforeBorrow", "[HotPath] static void Use(int a, ref int b) {} [HotPath] void M() { int x = 0; Use(x, ref x); }", 0);
        yield return Case("ReadAfterBorrow", "[HotPath] static void Use(ref int a, int b) {} [HotPath] void M() { int x = 0; Use(ref x, x); }", 0, 1);
        yield return Case("RepeatBorrow", "[HotPath] static void Use(ref int a, ref int b) {} [HotPath] void M() { int x = 0; Use(ref x, ref x); }", 0, 1);
        yield return Case("NextStatement", "[HotPath] static void Use(ref int x) {} [HotPath] int M() { int x = 0; Use(ref x); return x; }", 0, 1);
        yield return Case("BlockRelease", "[HotPath] static void Use(ref int x) {} [HotPath] int M() { int x = 0; { Use(ref x); } return x; }", 0);
        yield return Case("OuterLoanInChild", "[HotPath] static void Use(ref int x) {} [HotPath] int M() { int x = 0; Use(ref x); { return x; } }", 0, 1);
        yield return Case("OutLoan", "[HotPath] static void Use(out int x) { x = 1; } [HotPath] int M() { Use(out int x); return x; }", 0, 1);
        yield return Case("InNotLoan", "[HotPath] static void Use(in int x) {} [HotPath] int M() { int x = 0; Use(in x); return x; }", 0);
        yield return Case("AliasLoan", "[HotPath] static void Use(ref int x) {} [HotPath] int M() { int x = 0; ref int y = ref x; Use(ref y); return x; }", 0, 1);
        yield return Case("SeparateLocals", "[HotPath] static void Use(ref int x) {} [HotPath] int M() { int x = 0; int y = 1; Use(ref x); return y; }", 0);
        yield return Case("PointerReturn", "[HotPath] unsafe int* M() { int x = 0; int* p = &x; return p; }", 0, 2, 0);
        yield return Case("StackallocReturn", "[HotPath] unsafe int* M() { int* p = stackalloc int[2]; return p; }", 0, 2, 0);
        yield return Case("PointerAliasEscape", "unsafe int* saved; [HotPath] unsafe void M() { int* p = stackalloc int[2]; int* q = p; saved = q; }", 0, 2, 0);
        yield return Case("PointerOutEscape", "[HotPath] unsafe void M(out int* result) { int x = 0; result = &x; }", 0, 2, 0);
        yield return Case("BorrowedSpanReturn", "[HotPath] Span<int> M(Span<int> value) { Span<int> local = value; return local; }", 0, 1, 1);
        yield return Case("StackValueReturn", "[HotPath] int M() { Span<int> x = stackalloc int[1]; x[0] = 5; return x[0]; }", 0);
        yield return Case("PointerValueReturn", "[HotPath] unsafe int M() { int x = 0; int* p = &x; return *p; }", 0, 1, 0);
        yield return Case("PointerPassThrough", "[HotPath] unsafe int* M(int* p) => p;", 0, 1, 0);
        yield return Case("StringConcat", "[HotPath] string M(string x) => x + \"!\";", 1);
        yield return Case("ConstantConcat", "[HotPath] string M() => \"a\" + \"b\";", 0);
        yield return Case("Constructor", "[HotPath] C() { _ = new object(); }", 1);
        yield return Case("Accessor", "object P { [HotPath] get => new object(); }", 1);
        yield return Case("CollectionExpression", "[HotPath] int[] M() => [1, 2];", 1, 0, 1);
        yield return Case("InterpolatedString", "[HotPath] string M(int x) => $\"{x}\";", 1);
        yield return Case("InstanceDelegate", "int F() => 0; [HotPath] Func<int> M() => F;", 1);
        yield return Case("ExplicitLambdaDelegate", "[HotPath] Func<int> M() => new Func<int>(static () => 1);", 1);
        yield return Case("YieldBreak", "[HotPath] IEnumerable<int> M() { yield break; }", 1);
        yield return Case("AsyncMethod", "[HotPath] async System.Threading.Tasks.Task M() { await System.Threading.Tasks.Task.CompletedTask; }", 1, 0, 1);
        yield return Case("UserConversionNotBox", "struct S { public static implicit operator string(S x) => string.Empty; } [HotPath] string M(S x) => x;", 0, 0, 1);
        yield return Case("MarkedLambda", "Func<object> M() => [HotPath] () => new object();", 1);
        yield return Case("DistinctBlockLoans", "[HotPath] static void Use(ref int x) {} [HotPath] void M() { int x = 0; { Use(ref x); } { Use(ref x); } }", 0);
        yield return Case("StructFieldLoan", "struct S { public int X; } [HotPath] static void Use(ref int x) {} [HotPath] int M(S s) { S x = s; Use(ref x.X); return x.X; }", 0, 1);
        yield return Case("ConditionalEscape", "[HotPath] unsafe int* M(bool flag, int* other) { int x = 0; int* p = flag ? &x : other; return p; }", 0, 2, 0);
        yield return Case("IntegerPointerAlias", "unsafe nint saved; [HotPath] unsafe void M() { int x = 0; nint address = (nint)(&x); saved = address; }", 0, 2, 0);
        yield return Case("LocalStructStorage", "unsafe struct S { public int* P; } [HotPath] unsafe void M() { S s = default; int x = 0; s.P = &x; *s.P = 1; }", 0, 1, 0);
        yield return Case("StructStorageEscape", "unsafe struct S { public int* P; } [HotPath] unsafe S M() { S s = default; int x = 0; s.P = &x; return s; }", 0, 2, 0);
        yield return Case("UnknownHelper", "static object Helper() => new object(); [HotPath] object M() => Helper();", 0, 0, 1);
        yield return Case("CheckedHelperAllocation", "[HotPath] static object Helper() => new object(); [HotPath] object M() => Helper();", 1);
        yield return Case("CheckedTransitiveCall", "static int Leaf() => 1; [HotPath] static int Helper() => Leaf(); [HotPath] int M() => Helper();", 0, 0, 1);
        yield return Case("CheckedRecursion", "[HotPath] static int M(int n) => n == 0 ? 0 : M(n - 1);", 0);
        yield return Case("TrustedWrapper", "[HotPathTrusted(\"Reviewed scalar Math.Abs call for valid input\")] static int Helper(int x) => Math.Abs(x); [HotPath] int M(int x) => Helper(x);", 0);
        yield return Case("EmptyTrustReason", "[HotPathTrusted(\" \")] static int Helper(int x) => x; [HotPath] int M(int x) => Helper(x);", 0, 0, 1);
        yield return Case("UnknownFrameworkCall", "[HotPath] int M(int x) => Math.Abs(x);", 0, 0, 1);
        yield return Case("OpenVirtualCall", "[HotPath] public virtual int Helper() => 1; [HotPath] int M() => Helper();", 0, 0, 1);
        yield return Case("SealedConcreteCall", "sealed class D { [HotPath] public int Helper() => 1; } [HotPath] int M(D value) => value.Helper();", 0);
        yield return Case("UncheckedGetter", "int P => 1; [HotPath] int M() => P;", 0, 0, 1);
        yield return Case("CheckedGetter", "int P { [HotPath] get => 1; } [HotPath] int M() => P;", 0);
        yield return Case("UncheckedSetter", "int P { set {} } [HotPath] void M() { P = 1; }", 0, 0, 1);
        yield return Case("CheckedSetter", "int P { [HotPath] set {} } [HotPath] void M() { P = 1; }", 0);
        yield return Case("UncheckedStructConstructor", "struct S { public S(int x) {} } [HotPath] S M() => new S(1);", 0, 0, 1);
        yield return Case("CheckedStructConstructor", "struct S { [HotPath] public S(int x) {} } [HotPath] S M() => new S(1);", 0);
        yield return Case("UnknownGenericConstructor", "[HotPath] T M<T>() where T : struct => new T();", 0, 0, 1);
        yield return Case("UncheckedOperator", "struct S { public static S operator +(S x, S y) => x; } [HotPath] S M(S x) => x + x;", 0, 0, 1);
        yield return Case("CheckedOperator", "struct S { [HotPath] public static S operator +(S x, S y) => x; } [HotPath] S M(S x) => x + x;", 0);
        yield return Case("UnknownDelegateCall", "[HotPath] int M(Func<int> callback) => callback();", 0, 0, 1);
        yield return Case("DynamicCall", "[HotPath] void M(dynamic value) { value.DoWork(); }", 0, 0, 2);
        yield return Case("CapturedLocalCall", "[HotPath] int M() { int x = 1; return Helper(); int Helper() => x; }", 0, 0, 1);
        yield return Case("StaticLocalCall", "[HotPath] int M() { return Helper(1); static int Helper(int x) => x; }", 0);
        yield return Case("StaticInitializer", "static object Cache = new object(); [HotPath] int M() => 1;", 0, 0, 1);
        yield return Case("CheckedStaticConstructor", "static int Cache; [HotPath] static C() { Cache = 1; } [HotPath] int M() => Cache;", 0);
        yield return Case("SpanSliceRejected", "[HotPath] void M(Span<int> value) { value.Slice(1); }", 0, 0, 1);
        yield return Case("RefReturnRejected", "int field; [HotPath] ref int M() => ref field;", 0, 0, 1);
        yield return Case("StringIntrinsics", "[HotPath] int M(string value) => value.Length > 0 ? value[0] : 0;", 0);
        yield return Case("SpanIntrinsics", "[HotPath] void M(Span<int> value) { for (int i = 0; i < value.Length; i++) value[i]++; }", 0);
        yield return Case("ArraySliceRejected", "[HotPath] int[] M(int[] values) => values[1..];", 0, 0, 2);
        yield return Case("UsingRejected", "[HotPath] void M(IDisposable value) { using (value) {} }", 0, 0, 1);
        yield return Case("LockRejected", "[HotPath] void M(object value) { lock (value) {} }", 0, 0, 1);
        yield return Case("BranchExitBeforeRead", "[HotPath] static void Use(ref int x) {} [HotPath] int M(bool flag) { int x = 0; if (flag) { Use(ref x); return 0; } return x; }", 0);
        yield return Case("BorrowAcrossLoopBackedge", "[HotPath] static void Use(ref int x) {} [HotPath] void M(bool flag) { int x = 0; while (flag) Use(ref x); }", 0, 1);
        yield return Case("LoopBodyReleasesBorrow", "[HotPath] static void Use(ref int x) {} [HotPath] void M(bool flag) { int x = 0; while (flag) { Use(ref x); } }", 0);
        yield return Case("HeapBorrowRejected", "[HotPath] static void Use(ref int x) {} [HotPath] void M(int[] values) { Use(ref values[0]); }", 0, 1);
        yield return Case("ParameterLoan", "[HotPath] static void Use(ref int x) {} [HotPath] int M(int x) { Use(ref x); return x; }", 0, 1);
        yield return Case("TryRejected", "[HotPath] int M() { try { return 0; } finally {} }", 0, 1);
        yield return Case("ForeachHiddenConversion", "struct S { public static implicit operator int(S value) => 0; } [HotPath] void M(S[] values) { foreach (int value in values) {} }", 0, 0, 1);
        yield return Case("ForeachCheckedConversion", "struct S { [HotPath] public static implicit operator int(S value) => 0; } [HotPath] void M(S[] values) { foreach (int value in values) {} }", 0);
        yield return Case("TupleHiddenBoxing", "[HotPath] void M((int, int) value) { (object, object) boxed = value; }", 0, 0, 1);
        yield return Case("PatternHiddenBoxing", "[HotPath] object M<T>(T value) { if (value is object boxed) return boxed; return null; }", 0, 0, 1);
        yield return Case("ConstructorInitializerRejected", "object field = new object(); [HotPath] C() {}", 0, 0, 1);
        yield return Case("SpanAliasRejected", "[HotPath] int M(Span<int> value) { Span<int> alias = value; return alias.Length; }", 0, 1);
        yield return Case("CoalesceHiddenBoxing", "[HotPath] object M(int? value, object fallback) => value ?? fallback;", 0, 0, 1);
        yield return Case("TupleEqualityRejected", "[HotPath] bool M((int, int) x, (int, int) y) => x == y;", 0, 0, 1);
        yield return Case("ForeachDeconstructionRejected", "struct S { public void Deconstruct(out int x, out int y) { x = 0; y = 1; } } [HotPath] void M(S[] values) { foreach (var (x, y) in values) {} }", 0, 0, 1);
        yield return Case("CapturedRefBeforeConditionalRead", "[HotPath] static void Use(ref int x, int y) {} [HotPath] void M(bool flag) { int x = 0; Use(ref x, flag ? x : 0); }", 0, 1);
        yield return Case("ConditionalReadBeforeRef", "[HotPath] static void Use(int y, ref int x) {} [HotPath] void M(bool flag) { int x = 0; Use(flag ? x : 0, ref x); }", 0);
        yield return Case("CapturedRefWithUnrelatedConditional", "[HotPath] static void Use(ref int x, int y) {} [HotPath] void M(bool flag) { int x = 0; Use(ref x, flag ? 1 : 0); }", 0);
        yield return Case("ConstantLoopBodyReleasesBorrow", "[HotPath] static void Use(ref int x) {} [HotPath] void M() { int x = 0; while (true) { Use(ref x); } }", 0);
        yield return Case("ConditionalBorrowJoins", "[HotPath] static bool Use(ref int x) => true; [HotPath] int M(bool flag) { int x = 0; if (flag && Use(ref x)) return 0; return x; }", 0, 1);
        yield return Case("RefReassignmentRejected", "[HotPath] int M() { int x = 0; int y = 1; ref int z = ref x; z = ref y; return z; }", 0, 1);
        yield return Case("RefLikeFieldAliasRejected", "ref struct R { public Span<int> View; } [HotPath] void M(Span<int> value) { R r = default; r.View = value; }", 0, 1);
        yield return Case("ForeachTupleBoxingRejected", "[HotPath] void M((int, int)[] values) { foreach ((object, object) value in values) {} }", 0, 0, 1);
    }

    [Test]
    public void CompilerHostLoadsTheGenerator() => Assert.That(HotPathCompilerHostSmoke.Increment(1), Is.EqualTo(2));

    private static TestCaseData Case(string name, string members, int allocations, int ownership = 0, int unverified = 0)
        => new TestCaseData(members, allocations, ownership, unverified).SetName(name);

    [TestCaseSource(nameof(Cases))]
    public async Task EnforcesHotPathPolicy(string members, int allocations, int ownership, int unverified)
    {
        string source = "using System; using System.Collections.Generic; using System.Linq; using Engine.Diagnostics; class C { " + members + " }";
        Compilation compilation = Generate(source);
        Assert.That(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        ImmutableArray<Diagnostic> diagnostics = await Analyze(compilation);
        Assert.Multiple(() =>
        {
            Assert.That(diagnostics.Count(d => d.Id == "ENG0001"), Is.EqualTo(allocations), string.Join("\n", diagnostics));
            Assert.That(diagnostics.Count(d => d.Id == "ENG0002"), Is.EqualTo(ownership), string.Join("\n", diagnostics));
            Assert.That(diagnostics.Count(d => d.Id == "ENG0003"), Is.EqualTo(unverified), string.Join("\n", diagnostics));
            Assert.That(diagnostics.All(d => d.Id is "ENG0001" or "ENG0002" or "ENG0003"), Is.True, string.Join("\n", diagnostics));
            Assert.That(diagnostics.All(d => d.Severity == DiagnosticSeverity.Error), Is.True);
        });
    }

    [Test]
    public async Task IgnoresUnrelatedAttributeAndGeneratedCode()
    {
        Compilation unrelated = Generate("using System; class HotPathAttribute : Attribute {} class C { [HotPath] object M() => new object(); }");
        Assert.That(await Analyze(unrelated), Is.Empty);
        Compilation generated = Generate("// <auto-generated/>\nusing Engine.Diagnostics; class C { [HotPath] object M() => new object(); }", "Generated.g.cs");
        Assert.That(await Analyze(generated), Is.Empty);
    }

    [Test]
    public async Task InvalidStackSpanReturnHasOwnershipDiagnosticAndCompilerError()
    {
        Compilation compilation = Generate("using System; using Engine.Diagnostics; class C { [HotPath] Span<int> M() { Span<int> x = stackalloc int[1]; return x; } }");
        Assert.That(compilation.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error), Is.True);
        Assert.That((await Analyze(compilation)).Count(d => d.Id == "ENG0002"), Is.EqualTo(1));
    }

    [Test]
    public async Task ReportsTheConflictingReadLocation()
    {
        Compilation compilation = Generate("using Engine.Diagnostics; class C { [HotPath] static void Use(ref int x, int y) {} [HotPath] void M() { int x = 0; Use(ref x, x); } }");
        Diagnostic diagnostic = (await Analyze(compilation)).Single();
        Assert.That(diagnostic.Id, Is.EqualTo("ENG0002"));
        Assert.That(diagnostic.Location.SourceTree!.GetText().ToString(diagnostic.Location.SourceSpan), Is.EqualTo("x"));
    }

    [Test]
    public async Task HandlesIncompleteEditorInput()
    {
        Compilation compilation = Generate("using Engine.Diagnostics; abstract class C { [HotPath] public abstract void M(); [HotPath] void N() { int x = ; } }");
        Assert.That((await Analyze(compilation)).Any(d => d.Id == "AD0001"), Is.False);
    }

    [Test]
    public void GeneratorProducesBothContractsAndSupportsRepeatedRuns()
    {
        var compilation = Create("using Engine.Diagnostics; class C { [HotPath] void M() {} }", "Input.cs");
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new HotPathAttributeGenerator().AsSourceGenerator()], parseOptions: ParseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation first, out ImmutableArray<Diagnostic> diagnostics);
        Assert.That(diagnostics, Is.Empty);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation second, out diagnostics);
        Assert.Multiple(() =>
        {
            Assert.That(diagnostics, Is.Empty);
            Assert.That(driver.GetRunResult().GeneratedTrees, Has.Length.EqualTo(3));
            Assert.That(first.GetTypeByMetadataName("Engine.Diagnostics.HotPathAttribute"), Is.Not.Null);
            Assert.That(first.GetTypeByMetadataName("Engine.Diagnostics.HotPathTrustedAttribute"), Is.Not.Null);
            Assert.That(second.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        });
    }

    [Test]
    public async Task RejectsCallsIntoExcludedGeneratedBodies()
    {
        Compilation compilation = Generate("using Engine.Diagnostics; class C { [HotPath] int M() => Generated.Helper(); }");
        compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            "// <auto-generated/>\nusing Engine.Diagnostics; static class Generated { [HotPath] public static int Helper() { _ = new object(); return 1; } }",
            ParseOptions, "Generated.g.cs"));
        Assert.That(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        ImmutableArray<Diagnostic> diagnostics = await Analyze(compilation);
        Assert.That(diagnostics.Select(d => d.Id), Is.EqualTo(new[] { "ENG0003" }));
    }

    [Test]
    public async Task GeneratorIsolatesMarkersFromFriendAssemblies()
    {
        Compilation library = Generate("""
            [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("HotPathTest")]
            public static class Library
            {
                [Engine.Diagnostics.HotPath]
                public static int Read() => 1;
            }
            """).WithAssemblyName("FriendLibrary");
        using var assembly = new MemoryStream();
        Assert.That(library.Emit(assembly).Success, Is.True);

        Compilation caller = Generate("""
            using Engine.Diagnostics;
            class C
            {
                [HotPath] object Allocate() => new object();
                [HotPath] int Read() => ReadScalar();
                [HotPathTrusted("Scalar return; no allocation or retention")]
                private static int ReadScalar() => 1;
            }
            """).AddReferences(MetadataReference.CreateFromImage(assembly.ToArray()));

        Assert.That(caller.GetDiagnostics().Where(d => d.Severity is DiagnosticSeverity.Warning or DiagnosticSeverity.Error), Is.Empty);
        Assert.That((await Analyze(caller)).Select(d => d.Id), Is.EqualTo(new[] { "ENG0001" }));
    }

    [Test]
    public async Task DoesNotTrustMetadataOnlyHotPathAnnotations()
    {
        Compilation library = Generate("using Engine.Diagnostics; public static class Library { [HotPath] public static int Helper() => 1; }")
            .WithAssemblyName("ExternalLibrary");
        using var assembly = new MemoryStream();
        Assert.That(library.Emit(assembly).Success, Is.True);
        Compilation caller = Generate("using Engine.Diagnostics; class C { [HotPath] int M() => Library.Helper(); }")
            .AddReferences(MetadataReference.CreateFromImage(assembly.ToArray()));
        Assert.That(caller.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error), Is.Empty);
        Assert.That((await Analyze(caller)).Select(d => d.Id), Is.EqualTo(new[] { "ENG0003" }));
    }

    [Test]
    public async Task ConcurrentAnalysisHasIndependentState()
    {
        Compilation compilation = Generate("using Engine.Diagnostics; class C { [HotPath] static void Use(ref int x) {} [HotPath] int M() { int x = 0; Use(ref x); return x; } }");
        var runs = new Task<ImmutableArray<Diagnostic>>[8];
        for (int index = 0; index < runs.Length; index++)
            runs[index] = Analyze(compilation);
        foreach (ImmutableArray<Diagnostic> diagnostics in await Task.WhenAll(runs))
            Assert.That(diagnostics.Select(d => d.Id), Is.EqualTo(new[] { "ENG0002" }));
    }

    private static CSharpCompilation Create(string source, string path)
        => CSharpCompilation.Create("HotPathTest", [CSharpSyntaxTree.ParseText(source, ParseOptions, path)], References,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

    private static Compilation Generate(string source, string path = "Input.cs")
    {
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new HotPathAttributeGenerator().AsSourceGenerator()], parseOptions: ParseOptions);
        driver.RunGeneratorsAndUpdateCompilation(Create(source, path), out Compilation output, out ImmutableArray<Diagnostic> diagnostics);
        Assert.That(diagnostics, Is.Empty);
        return output;
    }

    private static Task<ImmutableArray<Diagnostic>> Analyze(Compilation compilation)
        => compilation.WithAnalyzers([new HotPathAnalyzer()]).GetAnalyzerDiagnosticsAsync();
}
