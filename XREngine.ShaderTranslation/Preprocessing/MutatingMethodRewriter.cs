namespace XREngine.ShaderTranslation.Preprocessing;

/// <summary>
/// Adds <c>[mutating]</c> to struct methods that change fields of their own struct.
/// </summary>
/// <remarks>
/// FXC lets a struct method assign to the fields of its struct, for example <c>void Init(float4 m) { mask = m; }</c>.
/// Slang makes <c>this</c> read-only in a method without <c>[mutating]</c> and reports the assignment as an error.
/// The rewriter finds the methods that write a field directly, through <c>this.</c>, or by calling another such
/// method of the same struct, and adds <c>[mutating]</c> before them. Other methods do not change, because Slang
/// does not let code call a <c>[mutating]</c> method on a read-only value. A local variable with the name of a
/// field can mark a method that does not need the attribute; this is safe for calls on writable values.
/// Static methods do not change. The rewrite keeps the line count.
/// </remarks>
public static class MutatingMethodRewriter
{
    private const string Attribute = "[mutating] ";

    private static readonly HashSet<string> AssignmentOperators =
    [
        "=", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<=", ">>=", "++", "--",
    ];

    private sealed class Method(string name, int insertion, int bodyOpen, int bodyClose)
    {
        public string Name { get; } = name;
        public int Insertion { get; } = insertion;
        public int BodyOpen { get; } = bodyOpen;
        public int BodyClose { get; } = bodyClose;
        public bool Mutating { get; set; }
    }

    /// <summary>Rewrites the struct methods of a translation unit.</summary>
    public static string Rewrite(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!text.Contains("struct", StringComparison.Ordinal))
            return text;

        List<HlslToken> tokens = HlslLexer.Tokenize(text);
        List<int> insertions = [];
        for (int index = 0; index + 2 < tokens.Count; index++)
        {
            if (!tokens[index].IsIdentifier("struct") || tokens[index + 1].Kind != HlslTokenKind.Identifier)
                continue;

            int open = index + 2;
            while (open < tokens.Count && !tokens[open].IsSymbol("{") && !tokens[open].IsSymbol(";"))
                open++;
            if (open >= tokens.Count || !tokens[open].IsSymbol("{"))
                continue;

            int close = FindMatch(tokens, open, "{", "}");
            if (close < 0)
                break;

            CollectStruct(tokens, open, close, insertions);
            index = close;
        }

        if (insertions.Count == 0)
            return text;

        insertions.Sort();
        System.Text.StringBuilder builder = new(text.Length + insertions.Count * Attribute.Length);
        int position = 0;
        foreach (int insertion in insertions)
        {
            builder.Append(text, position, insertion - position).Append(Attribute);
            position = insertion;
        }

        builder.Append(text, position, text.Length - position);
        return builder.ToString();
    }

    private static void CollectStruct(List<HlslToken> tokens, int open, int close, List<int> insertions)
    {
        HashSet<string> fields = new(StringComparer.Ordinal);
        List<Method> methods = [];
        int statementStart = open + 1;
        for (int index = open + 1; index < close; index++)
        {
            HlslToken token = tokens[index];
            if (token.IsSymbol(";"))
            {
                CollectFields(tokens, statementStart, index, fields);
                statementStart = index + 1;
                continue;
            }

            if (token.IsSymbol("{"))
            {
                // A nested type or an initializer list: skip it as one unit.
                int end = FindMatch(tokens, index, "{", "}");
                if (end < 0)
                    return;
                index = end;
                continue;
            }

            if (token.Kind != HlslTokenKind.Identifier || index + 1 >= close || !tokens[index + 1].IsSymbol("(") || index == statementStart)
                continue;

            int parametersEnd = FindMatch(tokens, index + 1, "(", ")");
            if (parametersEnd < 0)
                return;

            int bodyOpen = parametersEnd + 1;
            // Skip a return semantic such as ': SV_Target'.
            while (bodyOpen < close && !tokens[bodyOpen].IsSymbol("{") && !tokens[bodyOpen].IsSymbol(";"))
                bodyOpen++;
            if (bodyOpen >= close || !tokens[bodyOpen].IsSymbol("{"))
            {
                index = parametersEnd;
                continue;
            }

            int bodyClose = FindMatch(tokens, bodyOpen, "{", "}");
            if (bodyClose < 0)
                return;

            if (!HasModifier(tokens, statementStart, index))
                methods.Add(new Method(token.Text, tokens[statementStart].Start, bodyOpen, bodyClose));

            index = bodyClose;
            statementStart = bodyClose + 1;
        }

        if (fields.Count == 0 || methods.Count == 0)
            return;

        foreach (Method method in methods)
            method.Mutating = WritesField(tokens, method, fields);

        // A method that calls a mutating method of the same struct also mutates.
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (Method method in methods)
            {
                if (method.Mutating || !CallsMutating(tokens, method, methods))
                    continue;
                method.Mutating = true;
                changed = true;
            }
        }

        foreach (Method method in methods)
            if (method.Mutating)
                insertions.Add(method.Insertion);
    }

    private static void CollectFields(List<HlslToken> tokens, int start, int end, HashSet<string> fields)
    {
        for (int index = start; index < end; index++)
            if (tokens[index].IsSymbol("(") || tokens[index].IsIdentifier("static") || tokens[index].IsIdentifier("typedef"))
                return;

        for (int index = start + 1; index <= end; index++)
        {
            if (tokens[index - 1].Kind != HlslTokenKind.Identifier)
                continue;
            HlslToken next = tokens[index];
            if (next.IsSymbol(";") || next.IsSymbol(",") || next.IsSymbol("[") || next.IsSymbol(":") || next.IsSymbol("="))
                fields.Add(tokens[index - 1].Text);
        }
    }

    private static bool HasModifier(List<HlslToken> tokens, int start, int nameIndex)
    {
        for (int index = start; index < nameIndex; index++)
            if (tokens[index].IsIdentifier("static") || tokens[index].IsSymbol("["))
                return true;

        return false;
    }

    private static bool WritesField(List<HlslToken> tokens, Method method, HashSet<string> fields)
    {
        for (int index = method.BodyOpen + 1; index < method.BodyClose; index++)
        {
            HlslToken token = tokens[index];
            if (token.Kind != HlslTokenKind.Identifier || !fields.Contains(token.Text))
                continue;

            if (index > 0 && (tokens[index - 1].IsSymbol(".") || tokens[index - 1].IsSymbol("->")))
            {
                bool throughThis = index > 1 && tokens[index - 2].IsIdentifier("this");
                if (!throughThis)
                    continue;
            }

            if (IsWrite(tokens, index, method.BodyClose))
                return true;
        }

        return false;
    }

    private static bool CallsMutating(List<HlslToken> tokens, Method method, List<Method> methods)
    {
        for (int index = method.BodyOpen + 1; index + 1 < method.BodyClose; index++)
        {
            HlslToken token = tokens[index];
            if (token.Kind != HlslTokenKind.Identifier || !tokens[index + 1].IsSymbol("("))
                continue;
            if (index > 0 && tokens[index - 1].IsSymbol(".") && !(index > 1 && tokens[index - 2].IsIdentifier("this")))
                continue;

            foreach (Method other in methods)
                if (other.Mutating && other.Name == token.Text)
                    return true;
        }

        return false;
    }

    private static bool IsWrite(List<HlslToken> tokens, int index, int limit)
    {
        HlslToken previous = tokens[index - 1];
        int before = previous.IsSymbol(".") ? index - 3 : index - 1;
        if (before >= 0 && (tokens[before].IsSymbol("++") || tokens[before].IsSymbol("--")))
            return true;

        int next = index + 1;
        while (next < limit)
        {
            if (tokens[next].IsSymbol(".") && next + 1 < limit && tokens[next + 1].Kind == HlslTokenKind.Identifier)
            {
                next += 2;
                continue;
            }

            if (tokens[next].IsSymbol("["))
            {
                int close = FindMatch(tokens, next, "[", "]");
                if (close < 0)
                    return false;
                next = close + 1;
                continue;
            }

            break;
        }

        return next < limit && tokens[next].Kind == HlslTokenKind.Symbol && AssignmentOperators.Contains(tokens[next].Text);
    }

    private static int FindMatch(List<HlslToken> tokens, int open, string openSymbol, string closeSymbol)
    {
        int depth = 0;
        for (int index = open; index < tokens.Count; index++)
        {
            if (tokens[index].IsSymbol(openSymbol))
                depth++;
            else if (tokens[index].IsSymbol(closeSymbol) && --depth == 0)
                return index;
        }

        return -1;
    }
}
