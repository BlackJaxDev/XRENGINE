namespace XREngine.ShaderTranslation.ShaderLab;

/// <summary>
/// Splits ShaderLab structure text into tokens and captures program blocks as single tokens.
/// </summary>
/// <remarks>
/// Program blocks (<c>CGPROGRAM</c> to <c>ENDCG</c> and the HLSL and GLSL forms) are not tokenized.
/// Their text stays unchanged, so line directives can map compiler diagnostics to the source file.
/// </remarks>
public static class ShaderLabTokenizer
{
    /// <summary>Gets the largest source text that the tokenizer accepts, in characters.</summary>
    public const int MaxSourceLength = 16 * 1024 * 1024;

    /// <summary>Tokenizes a complete <c>.shader</c> file.</summary>
    /// <param name="text">The file text.</param>
    /// <param name="sourcePath">The path used in exception messages.</param>
    /// <returns>The tokens, ending with one <see cref="ShaderLabTokenKind.End"/> token.</returns>
    public static List<ShaderLabToken> Tokenize(string text, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxSourceLength)
            throw new ShaderLabParseException(sourcePath, 1, $"The shader file is larger than {MaxSourceLength} characters.");

        List<ShaderLabToken> tokens = new(256);
        int position = 0;
        int line = 1;
        while (true)
        {
            SkipTrivia(text, sourcePath, ref position, ref line);
            if (position >= text.Length)
            {
                tokens.Add(new ShaderLabToken(ShaderLabTokenKind.End, string.Empty, line, position, position));
                return tokens;
            }

            char c = text[position];
            int start = position;
            int startLine = line;
            if (c == '"')
            {
                position++;
                while (position < text.Length && text[position] != '"')
                {
                    if (text[position] == '\n')
                        line++;
                    if (text[position] == '\\' && position + 1 < text.Length)
                        position++;
                    position++;
                }

                if (position >= text.Length)
                    throw new ShaderLabParseException(sourcePath, startLine, "A string has no closing quote.");

                position++;
                tokens.Add(new ShaderLabToken(ShaderLabTokenKind.String, text[(start + 1)..(position - 1)], startLine, start, position));
                continue;
            }

            if (IsNumberStart(text, position))
            {
                position = ScanNumber(text, position);
                if (position < text.Length && IsIdentifierPart(text[position]))
                {
                    // "2D", "3D", and "2DArray" are type names that start with a digit.
                    while (position < text.Length && IsIdentifierPart(text[position]))
                        position++;
                    tokens.Add(new ShaderLabToken(ShaderLabTokenKind.Identifier, text[start..position], startLine, start, position));
                }
                else
                {
                    tokens.Add(new ShaderLabToken(ShaderLabTokenKind.Number, text[start..position], startLine, start, position));
                }

                continue;
            }

            if (IsIdentifierStart(c))
            {
                while (position < text.Length && IsIdentifierPart(text[position]))
                    position++;
                string word = text[start..position];
                if (TryGetProgramMarker(word, out ShaderLabProgramLanguage language, out bool isInclude, out string endMarker))
                {
                    tokens.Add(CaptureProgram(text, sourcePath, word, language, isInclude, endMarker, start, ref position, ref line));
                    continue;
                }

                tokens.Add(new ShaderLabToken(ShaderLabTokenKind.Identifier, word, startLine, start, position));
                continue;
            }

            position++;
            tokens.Add(new ShaderLabToken(ShaderLabTokenKind.Symbol, c.ToString(), startLine, start, position));
        }
    }

    private static ShaderLabToken CaptureProgram(
        string text,
        string sourcePath,
        string marker,
        ShaderLabProgramLanguage language,
        bool isInclude,
        string endMarker,
        int start,
        ref int position,
        ref int line)
    {
        int startLine = line;
        int codeStart = position;
        int codeLine = line;
        int search = position;
        while (true)
        {
            int found = text.IndexOf(endMarker, search, StringComparison.Ordinal);
            if (found < 0)
                throw new ShaderLabParseException(sourcePath, startLine, $"'{marker}' has no matching '{endMarker}'.");

            int after = found + endMarker.Length;
            bool wordStart = found == 0 || !IsIdentifierPart(text[found - 1]);
            bool wordEnd = after >= text.Length || !IsIdentifierPart(text[after]);
            if (wordStart && wordEnd)
            {
                string code = text[codeStart..found];
                foreach (char ch in code)
                    if (ch == '\n')
                        line++;
                position = after;
                ShaderLabProgramBlock block = new(language, isInclude, code, codeLine);
                return new ShaderLabToken(ShaderLabTokenKind.ProgramBlock, marker, startLine, start, position, block);
            }

            search = after;
        }
    }

    private static bool TryGetProgramMarker(string word, out ShaderLabProgramLanguage language, out bool isInclude, out string endMarker)
    {
        switch (word)
        {
            case "CGPROGRAM":
                (language, isInclude, endMarker) = (ShaderLabProgramLanguage.Cg, false, "ENDCG");
                return true;
            case "CGINCLUDE":
                (language, isInclude, endMarker) = (ShaderLabProgramLanguage.Cg, true, "ENDCG");
                return true;
            case "HLSLPROGRAM":
                (language, isInclude, endMarker) = (ShaderLabProgramLanguage.Hlsl, false, "ENDHLSL");
                return true;
            case "HLSLINCLUDE":
                (language, isInclude, endMarker) = (ShaderLabProgramLanguage.Hlsl, true, "ENDHLSL");
                return true;
            case "GLSLPROGRAM":
                (language, isInclude, endMarker) = (ShaderLabProgramLanguage.Glsl, false, "ENDGLSL");
                return true;
            case "GLSLINCLUDE":
                (language, isInclude, endMarker) = (ShaderLabProgramLanguage.Glsl, true, "ENDGLSL");
                return true;
            default:
                (language, isInclude, endMarker) = (default, false, string.Empty);
                return false;
        }
    }

    private static void SkipTrivia(string text, string sourcePath, ref int position, ref int line)
    {
        while (position < text.Length)
        {
            char c = text[position];
            if (c == '\n')
            {
                line++;
                position++;
                continue;
            }

            if (char.IsWhiteSpace(c) || c == '﻿')
            {
                position++;
                continue;
            }

            if (c == '/' && position + 1 < text.Length && text[position + 1] == '/')
            {
                while (position < text.Length && text[position] != '\n')
                    position++;
                continue;
            }

            if (c == '/' && position + 1 < text.Length && text[position + 1] == '*')
            {
                int startLine = line;
                position += 2;
                while (position + 1 < text.Length && !(text[position] == '*' && text[position + 1] == '/'))
                {
                    if (text[position] == '\n')
                        line++;
                    position++;
                }

                if (position + 1 >= text.Length)
                    throw new ShaderLabParseException(sourcePath, startLine, "A block comment has no end.");

                position += 2;
                continue;
            }

            return;
        }
    }

    private static bool IsNumberStart(string text, int position)
    {
        char c = text[position];
        if (char.IsAsciiDigit(c))
            return true;
        if (c is '-' or '+' or '.')
            return position + 1 < text.Length && (char.IsAsciiDigit(text[position + 1]) || (text[position + 1] == '.' && c != '.'));
        return false;
    }

    private static int ScanNumber(string text, int position)
    {
        if (text[position] is '-' or '+')
            position++;
        while (position < text.Length && (char.IsAsciiDigit(text[position]) || text[position] == '.'))
            position++;

        // Accept an exponent only when digits follow, so "2D" stays an identifier.
        if (position + 1 < text.Length && text[position] is 'e' or 'E')
        {
            int exponent = position + 1;
            if (text[exponent] is '-' or '+')
                exponent++;
            if (exponent < text.Length && char.IsAsciiDigit(text[exponent]))
            {
                position = exponent;
                while (position < text.Length && char.IsAsciiDigit(text[position]))
                    position++;
            }
        }

        return position;
    }

    private static bool IsIdentifierStart(char c)
        => char.IsAsciiLetter(c) || c == '_';

    private static bool IsIdentifierPart(char c)
        => char.IsAsciiLetterOrDigit(c) || c == '_';
}
