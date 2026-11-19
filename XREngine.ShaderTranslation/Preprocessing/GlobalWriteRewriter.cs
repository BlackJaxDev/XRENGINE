using System.Text;
using System.Text.RegularExpressions;

namespace XREngine.ShaderTranslation.Preprocessing;

/// <summary>
/// Makes global variables writable when Unity code assigns to them.
/// </summary>
/// <remarks>
/// <para>
/// Unity compiles HLSL with the legacy FXC rules, which let a function write to a global variable.
/// The write changes a private copy for the current invocation. Slang reports an error for the same code.
/// For each written global, the rewriter renames the shader parameter to <c>name_xrUniform</c> and declares
/// <c>static T name = name_xrUniform;</c>. All other code keeps the original name, so reads and writes use
/// the private copy, which starts with the parameter value, as in Unity. <see cref="Compilation.GlslPostProcessor"/>
/// gives the parameter its original name again in the GLSL, so that material binding is not affected.
/// </para>
/// <para>
/// The rewriter finds globals at file scope and in constant buffers, including the Unity
/// <c>CBUFFER_START</c> macro. It handles numeric scalar, vector, and matrix types. A write is an assignment,
/// a compound assignment, or an increment or decrement in a function body, also through a swizzle or an index.
/// A local variable with the same name as a global can cause an unnecessary rewrite. This is safe, because
/// the private copy has the same value as the parameter until code writes to it.
/// </para>
/// <para>
/// The rewriter keeps the line count, so <c>#line</c> directives stay correct.
/// </para>
/// </remarks>
public static partial class GlobalWriteRewriter
{
    /// <summary>The suffix of a renamed shader parameter.</summary>
    public const string UniformSuffix = "_xrUniform";

    private static readonly HashSet<string> AssignmentOperators =
    [
        "=", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<=", ">>=", "++", "--",
    ];

    private static readonly HashSet<string> SkippedDeclarationKeywords =
    [
        "static", "const", "groupshared", "typedef", "struct", "class", "interface", "extension", "namespace",
        "cbuffer", "tbuffer", "using", "import", "__generic", "__exported", "enum", "return", "inline",
    ];

    private static readonly HashSet<string> TypeModifiers = ["uniform", "extern", "row_major", "column_major", "precise", "volatile", "snorm", "unorm"];

    private enum ScopeKind
    {
        Global,
        Type,
        Buffer,
        MacroBuffer,
        Function,
        Block,
        Initializer,
    }

    private sealed record GlobalDeclaration(string Name, string TypeText, string ArrayText, HlslToken NameToken, int InsertionPosition, int BufferId);

    /// <summary>Rewrites the written globals of a translation unit.</summary>
    public static GlobalWriteRewriteResult Rewrite(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<HlslToken> tokens = HlslLexer.Tokenize(text);
        List<GlobalDeclaration> declarations = [];
        HashSet<string> written = new(StringComparer.Ordinal);
        Dictionary<int, int> bufferEnds = [];
        Collect(tokens, declarations, written, bufferEnds);

        List<GlobalDeclaration> rewritten = declarations.FindAll(declaration => written.Contains(declaration.Name));
        if (rewritten.Count == 0)
            return new GlobalWriteRewriteResult(text, []);

        List<(int Position, int Sequence, int Length, string Replacement)> edits = [];
        foreach (GlobalDeclaration declaration in rewritten)
        {
            edits.Add((declaration.NameToken.Start, edits.Count, declaration.NameToken.Text.Length, declaration.Name + UniformSuffix));
            int insertion = declaration.BufferId >= 0 && bufferEnds.TryGetValue(declaration.BufferId, out int end) ? end : declaration.InsertionPosition;
            edits.Add((insertion, edits.Count, 0,
                $" static {declaration.TypeText} {declaration.Name}{declaration.ArrayText} = {declaration.Name}{UniformSuffix};"));
        }

        // Apply from the end, so earlier positions stay valid. Insertions at one position are applied
        // last to first, so that they appear in declaration order.
        edits.Sort((left, right) => left.Position != right.Position
            ? right.Position.CompareTo(left.Position)
            : right.Sequence.CompareTo(left.Sequence));
        StringBuilder builder = new(text);
        foreach ((int position, _, int length, string replacement) in edits)
            builder.Remove(position, length).Insert(position, replacement);

        List<string> names = [];
        foreach (GlobalDeclaration declaration in rewritten)
            if (!names.Contains(declaration.Name))
                names.Add(declaration.Name);
        return new GlobalWriteRewriteResult(builder.ToString(), names);
    }

    private static void Collect(List<HlslToken> tokens, List<GlobalDeclaration> declarations, HashSet<string> written, Dictionary<int, int> bufferEnds)
    {
        Stack<(ScopeKind Kind, int BufferId)> scopes = new();
        scopes.Push((ScopeKind.Global, -1));
        int statementStart = 0;
        int nextBufferId = 0;
        for (int index = 0; index < tokens.Count; index++)
        {
            HlslToken token = tokens[index];
            (ScopeKind kind, int bufferId) = scopes.Peek();
            bool declarationScope = kind is ScopeKind.Global or ScopeKind.Buffer or ScopeKind.MacroBuffer;

            if (declarationScope && token.Kind == HlslTokenKind.Identifier && IsBufferStartMacro(token.Text))
            {
                index = SkipParentheses(tokens, index + 1);
                scopes.Push((ScopeKind.MacroBuffer, nextBufferId++));
                statementStart = index + 1;
                continue;
            }

            if (kind == ScopeKind.MacroBuffer && token.Kind == HlslTokenKind.Identifier && IsBufferEndMacro(token.Text))
            {
                index = index + 1 < tokens.Count && tokens[index + 1].IsSymbol("(") ? SkipParentheses(tokens, index + 1) : index;
                if (index + 1 < tokens.Count && tokens[index + 1].IsSymbol(";"))
                    index++;
                scopes.Pop();
                bufferEnds[bufferId] = tokens[index].End;
                statementStart = index + 1;
                continue;
            }

            if (token.IsSymbol("{"))
            {
                ScopeKind opened = kind is ScopeKind.Function or ScopeKind.Block
                    ? ScopeKind.Block
                    : ClassifyBrace(tokens, statementStart, index, kind);
                scopes.Push((opened, opened == ScopeKind.Buffer ? nextBufferId++ : bufferId));
                if (opened != ScopeKind.Initializer)
                    statementStart = index + 1;
                continue;
            }

            if (token.IsSymbol("}"))
            {
                if (scopes.Count > 1)
                    scopes.Pop();
                if (kind == ScopeKind.Initializer)
                    continue;

                int end = index;
                if (kind == ScopeKind.Buffer && index + 1 < tokens.Count && tokens[index + 1].IsSymbol(";"))
                    end = ++index;
                if (kind == ScopeKind.Buffer)
                    bufferEnds[bufferId] = tokens[end].End;
                statementStart = index + 1;
                continue;
            }

            if (token.IsSymbol(";"))
            {
                if (declarationScope)
                    ReadDeclaration(tokens, statementStart, index, kind == ScopeKind.Global ? -1 : bufferId, declarations);
                if (kind != ScopeKind.Initializer)
                    statementStart = index + 1;
                continue;
            }

            if (kind is ScopeKind.Function or ScopeKind.Block && token.Kind == HlslTokenKind.Identifier && IsWrite(tokens, index))
                written.Add(token.Text);
        }
    }

    private static ScopeKind ClassifyBrace(List<HlslToken> tokens, int start, int end, ScopeKind current)
    {
        bool hasParenthesis = false;
        for (int index = start; index < end; index++)
        {
            HlslToken token = tokens[index];
            if (token.Kind == HlslTokenKind.Identifier && token.Text is "cbuffer" or "tbuffer")
                return current == ScopeKind.Global ? ScopeKind.Buffer : ScopeKind.Type;
            if (token.Kind == HlslTokenKind.Identifier && token.Text is "struct" or "class" or "interface" or "extension" or "namespace" or "enum")
                return ScopeKind.Type;
            if (token.IsSymbol("="))
                return ScopeKind.Initializer;
            if (token.IsSymbol("("))
                hasParenthesis = true;
        }

        return hasParenthesis ? ScopeKind.Function : ScopeKind.Type;
    }

    private static void ReadDeclaration(List<HlslToken> tokens, int start, int end, int bufferId, List<GlobalDeclaration> declarations)
    {
        int index = start;
        StringBuilder type = new();
        while (index < end && tokens[index].Kind == HlslTokenKind.Identifier && TypeModifiers.Contains(tokens[index].Text))
        {
            if (tokens[index].Text is not ("uniform" or "extern"))
                type.Append(tokens[index].Text).Append(' ');
            index++;
        }

        if (index >= end || tokens[index].Kind != HlslTokenKind.Identifier || SkippedDeclarationKeywords.Contains(tokens[index].Text))
            return;

        if (NumericTypePattern().IsMatch(tokens[index].Text))
        {
            type.Append(tokens[index].Text);
            index++;
        }
        else if (tokens[index].Text is "vector" or "matrix" && index + 1 < end && tokens[index + 1].IsSymbol("<"))
        {
            int close = index + 1;
            while (close < end && !tokens[close].IsSymbol(">"))
                close++;
            if (close >= end)
                return;
            for (int part = index; part <= close; part++)
                type.Append(tokens[part].Text);
            index = close + 1;
        }
        else
        {
            return;
        }

        int insertion = tokens[end].End;
        while (index < end)
        {
            if (tokens[index].Kind != HlslTokenKind.Identifier)
                return;

            HlslToken name = tokens[index++];
            if (index < end && tokens[index].IsSymbol("("))
                return;

            StringBuilder array = new();
            while (index < end && tokens[index].IsSymbol("["))
            {
                int close = SkipBrackets(tokens, index, "[", "]");
                for (int part = index; part <= close && part < end; part++)
                    array.Append(tokens[part].Text);
                index = close + 1;
            }

            declarations.Add(new GlobalDeclaration(name.Text, type.ToString(), array.ToString(), name, insertion, bufferId));

            // Skip a semantic, a register binding, or an initializer up to the next declarator.
            int depth = 0;
            while (index < end)
            {
                HlslToken token = tokens[index];
                if (token.IsSymbol("(") || token.IsSymbol("[") || token.IsSymbol("{"))
                    depth++;
                else if (token.IsSymbol(")") || token.IsSymbol("]") || token.IsSymbol("}"))
                    depth--;
                else if (depth == 0 && token.IsSymbol(","))
                    break;
                index++;
            }

            index++;
        }
    }

    private static bool IsWrite(List<HlslToken> tokens, int index)
    {
        if (index > 0)
        {
            HlslToken previous = tokens[index - 1];
            if (previous.IsSymbol(".") || previous.IsSymbol("->") || previous.IsSymbol("::"))
                return false;
            if (previous.IsSymbol("++") || previous.IsSymbol("--"))
                return true;
        }

        int next = index + 1;
        while (next < tokens.Count)
        {
            if (tokens[next].IsSymbol(".") && next + 1 < tokens.Count && tokens[next + 1].Kind == HlslTokenKind.Identifier)
            {
                next += 2;
                continue;
            }

            if (tokens[next].IsSymbol("["))
            {
                next = SkipBrackets(tokens, next, "[", "]") + 1;
                continue;
            }

            break;
        }

        return next < tokens.Count && tokens[next].Kind == HlslTokenKind.Symbol && AssignmentOperators.Contains(tokens[next].Text);
    }

    private static int SkipParentheses(List<HlslToken> tokens, int index)
        => index < tokens.Count && tokens[index].IsSymbol("(") ? SkipBrackets(tokens, index, "(", ")") : index - 1;

    private static int SkipBrackets(List<HlslToken> tokens, int index, string open, string close)
    {
        int depth = 0;
        for (; index < tokens.Count; index++)
        {
            if (tokens[index].IsSymbol(open))
                depth++;
            else if (tokens[index].IsSymbol(close) && --depth == 0)
                return index;
        }

        return tokens.Count - 1;
    }

    private static bool IsBufferStartMacro(string name)
        => name is "CBUFFER_START" or "UNITY_INSTANCING_BUFFER_START" or "UNITY_INSTANCING_CBUFFER_START";

    private static bool IsBufferEndMacro(string name)
        => name is "CBUFFER_END" or "UNITY_INSTANCING_BUFFER_END" or "UNITY_INSTANCING_CBUFFER_END";

    [GeneratedRegex(@"^(?:float|half|fixed|double|int|uint|bool|dword|min16float|min10float|min16int|min12int|min16uint)(?:[1-4](?:x[1-4])?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericTypePattern();
}
