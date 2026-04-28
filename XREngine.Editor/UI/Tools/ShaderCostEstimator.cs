using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace XREngine.Editor.UI.Tools;

public sealed record OperationInfo(
    string Identifier,
    string DisplayName,
    string Category,
    int CycleCost,
    OperationKind Kind);

public sealed record OperationResult(
    OperationInfo Info,
    int Occurrences,
    int CycleCostPerOccurrence,
    int EstimatedCycles)
{
    public string Identifier => Info.Identifier;
    public string Category => Info.Category;
}

public enum OperationKind
{
    Function,
    Keyword,
    Operator
}

public sealed class ShaderCostReport
{
    public IReadOnlyList<OperationResult> Operations { get; init; } = Array.Empty<OperationResult>();
    public IReadOnlyDictionary<string, int> CategoryTotals { get; init; } = new Dictionary<string, int>();
    public int TotalCostPerInvocation { get; init; }
    public int TotalCostPerFrame { get; init; }
    public int InvocationsPerFrame { get; init; }

    public override string ToString() =>
        $"Invocations: {InvocationsPerFrame}, Cost/Invocation: {TotalCostPerInvocation}, Cost/Frame: {TotalCostPerFrame}";
}

public sealed class GlslCostEstimatorOptions
{
    public int InvocationsPerFrame { get; init; } = 1;
    public IReadOnlyDictionary<string, int>? CycleOverrides { get; init; }
}

public sealed class GlslCostEstimator
{
    private static readonly OperationCatalog Catalog = OperationCatalog.Build();

    public ShaderCostReport Analyze(string glslSource, GlslCostEstimatorOptions? options = null)
    {
        options ??= new GlslCostEstimatorOptions();
        if (string.IsNullOrWhiteSpace(glslSource))
            throw new ArgumentException("GLSL source is empty.", nameof(glslSource));
        if (options.InvocationsPerFrame <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.InvocationsPerFrame));

        string sanitized = StripCommentsAndStrings(glslSource);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        CountFunctions(sanitized, counts);
        CountKeywords(sanitized, counts);
        CountOperators(sanitized, counts);

        var operations = new List<OperationResult>(counts.Count);
        var categoryTotals = new Dictionary<string, int>(StringComparer.Ordinal);
        int perInvocation = 0;

        foreach (var kvp in counts)
        {
            if (!Catalog.TryGetInfo(kvp.Key, out var info))
                continue;

            int cycleCost = ResolveCycleCost(info, options);
            int opCost = cycleCost * kvp.Value;

            perInvocation += opCost;
            categoryTotals[info.Category] = categoryTotals.TryGetValue(info.Category, out var existing)
                ? existing + opCost
                : opCost;

            operations.Add(new OperationResult(info, kvp.Value, cycleCost, opCost));
        }

        operations.Sort((a, b) => b.EstimatedCycles.CompareTo(a.EstimatedCycles));
        int perFrame = checked(perInvocation * options.InvocationsPerFrame);

        return new ShaderCostReport
        {
            Operations = operations,
            CategoryTotals = categoryTotals,
            TotalCostPerInvocation = perInvocation,
            TotalCostPerFrame = perFrame,
            InvocationsPerFrame = options.InvocationsPerFrame
        };
    }

    private static int ResolveCycleCost(OperationInfo info, GlslCostEstimatorOptions options)
    {
        if (options.CycleOverrides != null &&
            options.CycleOverrides.TryGetValue(info.Identifier, out var overrideCost))
        {
            return overrideCost;
        }

        return info.CycleCost;
    }

    private static void CountFunctions(string source, IDictionary<string, int> sink)
    {
        if (Catalog.FunctionRegex is not Regex fnRegex)
            return;

        foreach (Match match in fnRegex.Matches(source))
            Increment(sink, match.Groups["fn"].Value);
    }

    private static void CountKeywords(string source, IDictionary<string, int> sink)
    {
        if (Catalog.KeywordRegex is not Regex kwRegex)
            return;

        foreach (Match match in kwRegex.Matches(source))
            Increment(sink, match.Groups["kw"].Value);
    }

    private static void CountOperators(string source, IDictionary<string, int> sink)
    {
        var span = source.AsSpan();
        int length = span.Length;

        for (int i = 0; i < length;)
        {
            if (char.IsWhiteSpace(span[i]))
            {
                i++;
                continue;
            }

            if (TryMatchMultiCharOperator(span, ref i, sink))
                continue;

            char c = span[i];
            if (Catalog.SingleCharOperators.Contains(c) &&
                !(IsExponentSign(span, i) && (c == '+' || c == '-')))
            {
                Increment(sink, c.ToString());
            }

            i++;
        }
    }

    private static bool TryMatchMultiCharOperator(ReadOnlySpan<char> span, ref int index, IDictionary<string, int> sink)
    {
        foreach (string op in Catalog.MultiCharOperators)
        {
            int remaining = span.Length - index;
            if (remaining < op.Length)
                continue;

            if (!span.Slice(index, op.Length).SequenceEqual(op))
                continue;

            Increment(sink, op);
            index += op.Length;
            return true;
        }

        return false;
    }

    private static bool IsExponentSign(ReadOnlySpan<char> span, int index)
    {
        if (index == 0 || index + 1 >= span.Length)
            return false;

        char c = span[index];
        if (c != '+' && c != '-')
            return false;

        char prev = span[index - 1];
        if (prev != 'e' && prev != 'E' && prev != 'p' && prev != 'P')
            return false;

        char beforePrev = index - 2 >= 0 ? span[index - 2] : '\0';
        if (!char.IsDigit(beforePrev))
            return false;

        return char.IsDigit(span[index + 1]);
    }

    private static string StripCommentsAndStrings(string source)
    {
        var builder = new StringBuilder(source.Length);
        bool inLine = false;
        bool inBlock = false;
        bool inString = false;

        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            char next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (inLine)
            {
                if (c == '\n')
                {
                    inLine = false;
                    builder.Append(c);
                }

                continue;
            }

            if (inBlock)
            {
                if (c == '*' && next == '/')
                {
                    inBlock = false;
                    i++;
                }

                continue;
            }

            if (inString)
            {
                if (c == '\\' && next != '\0')
                {
                    i++;
                    continue;
                }

                if (c == '"')
                    inString = false;

                continue;
            }

            if (c == '/' && next == '/')
            {
                inLine = true;
                i++;
                continue;
            }

            if (c == '/' && next == '*')
            {
                inBlock = true;
                i++;
                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static void Increment(IDictionary<string, int> sink, string key)
    {
        sink[key] = sink.TryGetValue(key, out var current) ? current + 1 : 1;
    }

    private sealed class OperationCatalog
    {
        public Regex? FunctionRegex { get; }
        public Regex? KeywordRegex { get; }
        public string[] MultiCharOperators { get; }
        public HashSet<char> SingleCharOperators { get; }
        private readonly IReadOnlyDictionary<string, OperationInfo> _all;

        private OperationCatalog(
            IReadOnlyDictionary<string, OperationInfo> functions,
            IReadOnlyDictionary<string, OperationInfo> keywords,
            IReadOnlyDictionary<string, OperationInfo> operators)
        {
            var all = new Dictionary<string, OperationInfo>(functions.Count + keywords.Count + operators.Count, StringComparer.Ordinal);
            foreach (var kv in functions)
                all[kv.Key] = kv.Value;
            foreach (var kv in keywords)
                all[kv.Key] = kv.Value;
            foreach (var kv in operators)
                all[kv.Key] = kv.Value;
            _all = all;

            FunctionRegex = BuildWordRegex(functions.Keys, "fn", requireCall: true);
            KeywordRegex = BuildWordRegex(keywords.Keys, "kw", requireCall: false);
            MultiCharOperators = operators.Keys
                .Where(static k => k.Length > 1)
                .OrderByDescending(static k => k.Length)
                .ToArray();
            SingleCharOperators = new HashSet<char>(operators.Keys
                .Where(static k => k.Length == 1)
                .Select(static k => k[0]));
        }

        public static OperationCatalog Build()
        {
            var functions = BuildDictionary(GetFunctionDefinitions());
            var keywords = BuildDictionary(GetKeywordDefinitions());
            var operators = BuildDictionary(GetOperatorDefinitions());
            return new OperationCatalog(functions, keywords, operators);
        }

        public bool TryGetInfo(string id, out OperationInfo info) => _all.TryGetValue(id, out info!);

        private static Regex? BuildWordRegex(IEnumerable<string> identifiers, string groupName, bool requireCall)
        {
            var tokens = identifiers
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(static id => id.Length)
                .Select(Regex.Escape)
                .ToArray();

            if (tokens.Length == 0)
                return null;

            string suffix = requireCall ? @"\s*(?=\()" : string.Empty;
            string pattern = $@"\b(?<{groupName}>({string.Join("|", tokens)}))\b{suffix}";
            return new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled);
        }

        private static IReadOnlyDictionary<string, OperationInfo> BuildDictionary(IEnumerable<OperationInfo> infos)
        {
            var dict = new Dictionary<string, OperationInfo>(StringComparer.Ordinal);
            foreach (var info in infos)
                dict[info.Identifier] = info;
            return dict;
        }

        private static IEnumerable<OperationInfo> GetFunctionDefinitions()
        {
            foreach (var info in CreateOps("Trigonometry", 4, OperationKind.Function, "radians", "degrees"))
                yield return info;
            foreach (var info in CreateOps("Trigonometry", 8, OperationKind.Function, "sin", "cos", "tan", "sinh", "cosh", "tanh"))
                yield return info;
            foreach (var info in CreateOps("Trigonometry", 10, OperationKind.Function, "asin", "acos", "atan", "asinh", "acosh", "atanh"))
                yield return info;
            foreach (var info in CreateOps("Trigonometry", 12, OperationKind.Function, "atan2"))
                yield return info;

            foreach (var info in CreateOps("Exponential", 14, OperationKind.Function, "pow"))
                yield return info;
            foreach (var info in CreateOps("Exponential", 10, OperationKind.Function, "exp", "exp2", "log", "log2"))
                yield return info;
            foreach (var info in CreateOps("Exponential", 6, OperationKind.Function, "sqrt"))
                yield return info;
            foreach (var info in CreateOps("Exponential", 7, OperationKind.Function, "inversesqrt"))
                yield return info;

            foreach (var info in CreateOps("Common", 1, OperationKind.Function, "abs", "sign", "isnan", "isinf"))
                yield return info;
            foreach (var info in CreateOps("Common", 2, OperationKind.Function, "floor", "trunc", "round", "roundEven", "ceil", "fract"))
                yield return info;
            foreach (var info in CreateOps("Common", 4, OperationKind.Function, "mod", "modf"))
                yield return info;
            foreach (var info in CreateOps("Common", 1, OperationKind.Function, "min", "max"))
                yield return info;
            foreach (var info in CreateOps("Common", 2, OperationKind.Function, "clamp", "mix", "step"))
                yield return info;
            foreach (var info in CreateOps("Common", 4, OperationKind.Function, "smoothstep"))
                yield return info;
            foreach (var info in CreateOps("Common", 3, OperationKind.Function, "frexp", "ldexp"))
                yield return info;
            foreach (var info in CreateOps("Common", 2, OperationKind.Function, "fma"))
                yield return info;

            foreach (var info in CreateOps("Geometry", 4, OperationKind.Function, "length", "normalize"))
                yield return info;
            foreach (var info in CreateOps("Geometry", 5, OperationKind.Function, "distance", "reflect", "faceforward"))
                yield return info;
            foreach (var info in CreateOps("Geometry", 3, OperationKind.Function, "dot"))
                yield return info;
            foreach (var info in CreateOps("Geometry", 6, OperationKind.Function, "cross", "fwidth", "fwidthFine", "fwidthCoarse"))
                yield return info;
            foreach (var info in CreateOps("Geometry", 8, OperationKind.Function, "refract"))
                yield return info;

            foreach (var info in CreateOps("Matrix", 6, OperationKind.Function, "outerProduct", "transpose", "matrixCompMult"))
                yield return info;
            foreach (var info in CreateOps("Matrix", 12, OperationKind.Function, "determinant"))
                yield return info;
            foreach (var info in CreateOps("Matrix", 20, OperationKind.Function, "inverse"))
                yield return info;

            foreach (var info in CreateOps("Vector Relational", 1, OperationKind.Function,
                         "lessThan", "lessThanEqual", "greaterThan", "greaterThanEqual", "equal", "notEqual", "not"))
                yield return info;
            foreach (var info in CreateOps("Vector Relational", 2, OperationKind.Function, "any", "all"))
                yield return info;

            foreach (var info in CreateOps("Bit Operations", 4, OperationKind.Function, "uaddCarry", "usubBorrow"))
                yield return info;
            foreach (var info in CreateOps("Bit Operations", 5, OperationKind.Function, "umulExtended", "imulExtended"))
                yield return info;
            foreach (var info in CreateOps("Bit Operations", 3, OperationKind.Function,
                         "bitfieldExtract", "bitfieldInsert", "bitfieldReverse", "bitCount", "findLSB", "findMSB"))
                yield return info;

            foreach (var info in CreateOps("Conversions", 1, OperationKind.Function,
                         "floatBitsToInt", "floatBitsToUint", "intBitsToFloat", "uintBitsToFloat"))
                yield return info;
            foreach (var info in CreateOps("Conversions", 4, OperationKind.Function,
                         "packUnorm2x16", "packSnorm2x16", "packUnorm4x8", "packSnorm4x8",
                         "unpackUnorm2x16", "unpackSnorm2x16", "unpackUnorm4x8", "unpackSnorm4x8",
                         "packHalf2x16", "unpackHalf2x16", "packDouble2x32", "unpackDouble2x32"))
                yield return info;

            foreach (var info in CreateOps("Derivatives", 6, OperationKind.Function,
                         "dFdx", "dFdxFine", "dFdxCoarse", "dFdy", "dFdyFine", "dFdyCoarse"))
                yield return info;

            foreach (var info in CreateOps("Noise", 20, OperationKind.Function, "noise1", "noise2", "noise3", "noise4"))
                yield return info;

            foreach (var info in CreateOps("Interpolation", 6, OperationKind.Function,
                         "interpolateAtCentroid", "interpolateAtSample", "interpolateAtOffset"))
                yield return info;

            foreach (var info in CreateOps("Texture Query", 8, OperationKind.Function, "textureQueryLod", "textureQueryLevels"))
                yield return info;
            foreach (var info in CreateOps("Texture Query", 4, OperationKind.Function, "textureSize", "textureSamples"))
                yield return info;
            foreach (var info in CreateOps("Texture Sampling", 40, OperationKind.Function, "texture", "textureProj"))
                yield return info;
            foreach (var info in CreateOps("Texture Sampling", 42, OperationKind.Function, "textureOffset", "textureProjOffset"))
                yield return info;
            foreach (var info in CreateOps("Texture Sampling", 44, OperationKind.Function, "textureLod", "textureProjLod"))
                yield return info;
            foreach (var info in CreateOps("Texture Sampling", 46, OperationKind.Function, "textureLodOffset", "textureProjLodOffset"))
                yield return info;
            foreach (var info in CreateOps("Texture Sampling", 45, OperationKind.Function, "textureGrad", "textureProjGrad"))
                yield return info;
            foreach (var info in CreateOps("Texture Sampling", 47, OperationKind.Function, "textureGradOffset", "textureProjGradOffset"))
                yield return info;
            foreach (var info in CreateOps("Texture Sampling", 35, OperationKind.Function, "texelFetch", "texelFetchOffset"))
                yield return info;
            foreach (var info in CreateOps("Texture Sampling", 48, OperationKind.Function,
                         "textureGather", "textureGatherOffset", "textureGatherOffsets",
                         "textureGatherLod", "textureGatherLodOffset", "textureGatherLodOffsets"))
                yield return info;

            foreach (var info in CreateOps("Image Operations", 4, OperationKind.Function, "imageSize"))
                yield return info;
            foreach (var info in CreateOps("Image Operations", 35, OperationKind.Function, "imageLoad"))
                yield return info;
            foreach (var info in CreateOps("Image Operations", 40, OperationKind.Function, "imageStore"))
                yield return info;
            foreach (var info in CreateOps("Image Atomics", 50, OperationKind.Function,
                         "imageAtomicAdd", "imageAtomicMin", "imageAtomicMax",
                         "imageAtomicAnd", "imageAtomicOr", "imageAtomicXor",
                         "imageAtomicExchange", "imageAtomicCompSwap",
                         "imageAtomicIncWrap", "imageAtomicDecWrap"))
                yield return info;

            foreach (var info in CreateOps("Atomics", 45, OperationKind.Function,
                         "atomicAdd", "atomicMin", "atomicMax", "atomicAnd", "atomicOr",
                         "atomicXor", "atomicExchange", "atomicCompSwap"))
                yield return info;
            foreach (var info in CreateOps("Atomic Counters", 40, OperationKind.Function,
                         "atomicCounter", "atomicCounterIncrement", "atomicCounterDecrement"))
                yield return info;

            foreach (var info in CreateOps("Barriers", 15, OperationKind.Function, "barrier"))
                yield return info;
            foreach (var info in CreateOps("Barriers", 20, OperationKind.Function,
                         "memoryBarrier", "memoryBarrierAtomicCounter", "memoryBarrierBuffer",
                         "memoryBarrierImage", "memoryBarrierShared", "memoryBarrierTexture",
                         "groupMemoryBarrier"))
                yield return info;
            foreach (var info in CreateOps("Barriers", 25, OperationKind.Function, "controlBarrier"))
                yield return info;

            foreach (var info in CreateOps("Geometry Emission", 10, OperationKind.Function, "EmitVertex"))
                yield return info;
            foreach (var info in CreateOps("Geometry Emission", 6, OperationKind.Function, "EndPrimitive"))
                yield return info;
            foreach (var info in CreateOps("Geometry Emission", 12, OperationKind.Function, "EmitStreamVertex"))
                yield return info;
            foreach (var info in CreateOps("Geometry Emission", 8, OperationKind.Function, "EndStreamPrimitive"))
                yield return info;

            foreach (var info in CreateOps("Subpass", 30, OperationKind.Function, "subpassLoad", "subpassLoadMS"))
                yield return info;
        }

        private static IEnumerable<OperationInfo> GetKeywordDefinitions()
        {
            foreach (var info in CreateOps("Control Flow", 2, OperationKind.Keyword, "if", "else", "switch", "case", "default"))
                yield return info;
            foreach (var info in CreateOps("Control Flow", 5, OperationKind.Keyword, "for", "while"))
                yield return info;
            foreach (var info in CreateOps("Control Flow", 4, OperationKind.Keyword, "do"))
                yield return info;
            foreach (var info in CreateOps("Control Flow", 2, OperationKind.Keyword, "break", "continue"))
                yield return info;
            foreach (var info in CreateOps("Control Flow", 3, OperationKind.Keyword, "return"))
                yield return info;
            foreach (var info in CreateOps("Control Flow", 6, OperationKind.Keyword, "discard"))
                yield return info;
        }

        private static IEnumerable<OperationInfo> GetOperatorDefinitions()
        {
            foreach (var info in CreateOps("Arithmetic", 1, OperationKind.Operator, "+", "-"))
                yield return info;
            foreach (var info in CreateOps("Arithmetic", 2, OperationKind.Operator, "*"))
                yield return info;
            foreach (var info in CreateOps("Arithmetic", 4, OperationKind.Operator, "/", "%"))
                yield return info;
            foreach (var info in CreateOps("Arithmetic", 1, OperationKind.Operator, "++", "--"))
                yield return info;
            foreach (var info in CreateOps("Arithmetic", 2, OperationKind.Operator, "+=", "-=", "*=", "/=", "%="))
                yield return info;

            foreach (var info in CreateOps("Assignment", 1, OperationKind.Operator, "="))
                yield return info;
            foreach (var info in CreateOps("Assignment", 3, OperationKind.Operator, "<<=", ">>=", "&=", "|=", "^="))
                yield return info;

            foreach (var info in CreateOps("Comparison", 1, OperationKind.Operator, "==", "!=", "<", "<=", ">", ">="))
                yield return info;

            foreach (var info in CreateOps("Logical", 2, OperationKind.Operator, "&&", "||"))
                yield return info;
            foreach (var info in CreateOps("Logical", 3, OperationKind.Operator, "^^"))
                yield return info;
            foreach (var info in CreateOps("Logical", 1, OperationKind.Operator, "!"))
                yield return info;
            foreach (var info in CreateOps("Logical", 3, OperationKind.Operator, "?"))
                yield return info;

            foreach (var info in CreateOps("Bitwise", 2, OperationKind.Operator, "<<", ">>"))
                yield return info;
            foreach (var info in CreateOps("Bitwise", 1, OperationKind.Operator, "&", "|", "^", "~"))
                yield return info;
        }

        private static IEnumerable<OperationInfo> CreateOps(string category, int cycles, OperationKind kind, params string[] names)
        {
            foreach (string name in names)
                yield return new OperationInfo(name, name, category, cycles, kind);
        }
    }
}
