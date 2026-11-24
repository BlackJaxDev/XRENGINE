using System.Text;

namespace XREngine.UnitTests.Editor;

/// <summary>
/// Applies the C# conditional directives of generated source for a set of define constants. It reads <c>#if</c>,
/// <c>#elif</c>, <c>#else</c>, and <c>#endif</c> with expressions of symbols, <c>!</c>, <c>&amp;&amp;</c>, and
/// <c>||</c>, which is what the launcher generator writes.
/// </summary>
internal static class PreprocessedSource
{
    /// <summary>Gives the active code for the define constants, with all white space removed.</summary>
    public static string Active(string source, IReadOnlySet<string> defines)
    {
        // Each frame holds: the parent is active, a branch was taken, the current branch is active.
        Stack<(bool ParentActive, bool Taken, bool Active)> frames = new();
        bool active = true;
        StringBuilder result = new();
        foreach (string rawLine in source.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("#if ", StringComparison.Ordinal))
            {
                bool condition = Evaluate(line[4..], defines);
                frames.Push((active, condition, active && condition));
                active = active && condition;
                continue;
            }
            if (line.StartsWith("#elif ", StringComparison.Ordinal))
            {
                (bool parent, bool taken, _) = frames.Pop();
                bool condition = !taken && Evaluate(line[6..], defines);
                frames.Push((parent, taken || condition, parent && condition));
                active = parent && condition;
                continue;
            }
            if (line == "#else")
            {
                (bool parent, bool taken, _) = frames.Pop();
                frames.Push((parent, true, parent && !taken));
                active = parent && !taken;
                continue;
            }
            if (line == "#endif")
            {
                active = frames.Pop().ParentActive;
                continue;
            }
            if (active)
                result.Append(line);
        }

        if (frames.Count != 0)
            throw new InvalidOperationException("The source has an #if with no #endif.");

        return Squash(result.ToString());
    }

    /// <summary>Removes all white space from the text.</summary>
    public static string Squash(string text)
    {
        StringBuilder result = new(text.Length);
        foreach (char c in text)
        {
            if (!char.IsWhiteSpace(c))
                result.Append(c);
        }
        return result.ToString();
    }

    private static bool Evaluate(string expression, IReadOnlySet<string> defines)
    {
        foreach (string alternative in expression.Split("||"))
        {
            bool all = true;
            foreach (string term in alternative.Split("&&"))
            {
                string symbol = term.Trim();
                bool negate = symbol.StartsWith('!');
                if (negate)
                    symbol = symbol[1..].Trim();
                if (defines.Contains(symbol) == negate)
                {
                    all = false;
                    break;
                }
            }
            if (all)
                return true;
        }
        return false;
    }
}
