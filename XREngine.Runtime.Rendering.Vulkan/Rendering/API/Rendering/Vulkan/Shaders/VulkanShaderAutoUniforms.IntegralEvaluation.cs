using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Numerics;
using Silk.NET.Core.Native;
using Silk.NET.Shaderc;
using Silk.NET.Vulkan;
using XREngine.Rendering.Models.Materials;
using XREngine.Data.Rendering;
using XREngine.Diagnostics;
using XREngine.Rendering;
using XREngine.Rendering.Shaders;

namespace XREngine.Rendering.Vulkan;

internal static partial class VulkanShaderAutoUniforms
{
    private static Dictionary<string, uint> ParseIntegralConstants(string source)
    {
        var constants = new Dictionary<string, uint>(StringComparer.Ordinal);
        var candidates = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in ConstIntegralRegex.Matches(source))
        {
            if (!match.Success)
                continue;

            string name = match.Groups["name"].Value;
            string valueText = match.Groups["value"].Value;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(valueText))
                continue;

            candidates[name] = valueText;
        }

        foreach (Match match in DefineIntegralRegex.Matches(source))
        {
            if (!match.Success)
                continue;

            string name = match.Groups["name"].Value;
            string valueText = match.Groups["value"].Value;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(valueText))
                continue;

            candidates[name] = valueText;
        }

        for (int pass = 0; pass < candidates.Count; pass++)
        {
            bool resolvedAny = false;
            foreach ((string name, string expression) in candidates)
            {
                if (constants.ContainsKey(name))
                    continue;

                if (!TryEvaluateIntegralExpression(expression, constants, out uint value))
                    continue;

                constants[name] = value;
                resolvedAny = true;
            }

            if (!resolvedAny)
                break;
        }

        return constants;
    }

    private static bool TryEvaluateIntegralExpression(
        string expression,
        IReadOnlyDictionary<string, uint> constants,
        out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(expression))
            return false;

        int lineComment = expression.IndexOf("//", StringComparison.Ordinal);
        if (lineComment >= 0)
            expression = expression[..lineComment];

        IntegralExpressionParser parser = new(expression, constants);
        if (!parser.TryParse(out long parsed) || parsed < 0 || parsed > uint.MaxValue)
            return false;

        value = (uint)parsed;
        return true;
    }

    private sealed class IntegralExpressionParser
    {
        private readonly string _expression;
        private readonly IReadOnlyDictionary<string, uint> _constants;
        private int _index;

        public IntegralExpressionParser(string expression, IReadOnlyDictionary<string, uint> constants)
        {
            _expression = expression;
            _constants = constants;
        }

        public bool TryParse(out long value)
        {
            _index = 0;
            if (!TryParseAdditive(out value))
                return false;

            SkipWhitespace();
            return _index == _expression.Length;
        }

        private bool TryParseAdditive(out long value)
        {
            if (!TryParseMultiplicative(out value))
                return false;

            while (true)
            {
                SkipWhitespace();
                char op = Peek();
                if (op is not ('+' or '-'))
                    return true;

                _index++;

                if (!TryParseMultiplicative(out long rhs))
                    return false;

                value = op == '-' ? value - rhs : value + rhs;
            }
        }

        private bool TryParseMultiplicative(out long value)
        {
            if (!TryParseUnary(out value))
                return false;

            while (true)
            {
                SkipWhitespace();
                char op = Peek();
                if (op is not ('*' or '/' or '%'))
                    return true;

                _index++;
                if (!TryParseUnary(out long rhs))
                    return false;

                switch (op)
                {
                    case '*':
                        value *= rhs;
                        break;
                    case '/':
                        if (rhs == 0)
                            return false;
                        value /= rhs;
                        break;
                    case '%':
                        if (rhs == 0)
                            return false;
                        value %= rhs;
                        break;
                }
            }
        }

        private bool TryParseUnary(out long value)
        {
            SkipWhitespace();
            if (TryConsume('+'))
                return TryParseUnary(out value);

            if (TryConsume('-'))
            {
                if (!TryParseUnary(out value))
                    return false;

                value = -value;
                return true;
            }

            return TryParsePrimary(out value);
        }

        private bool TryParsePrimary(out long value)
        {
            SkipWhitespace();
            value = 0;

            if (TryConsume('('))
            {
                if (!TryParseAdditive(out value))
                    return false;

                SkipWhitespace();
                return TryConsume(')');
            }

            char current = Peek();
            if (char.IsDigit(current))
                return TryParseNumber(out value);

            if (current == '_' || char.IsLetter(current))
                return TryParseIdentifier(out value);

            return false;
        }

        private bool TryParseNumber(out long value)
        {
            int start = _index;
            while (_index < _expression.Length && char.IsDigit(_expression[_index]))
                _index++;

            if (_index < _expression.Length && (_expression[_index] == 'u' || _expression[_index] == 'U'))
                _index++;

            string token = _expression[start.._index].TrimEnd('u', 'U');
            return long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private bool TryParseIdentifier(out long value)
        {
            int start = _index;
            _index++;
            while (_index < _expression.Length && (_expression[_index] == '_' || char.IsLetterOrDigit(_expression[_index])))
                _index++;

            string name = _expression[start.._index];
            if (!_constants.TryGetValue(name, out uint constant))
            {
                value = 0;
                return false;
            }

            value = constant;
            return true;
        }

        private char Peek()
            => _index < _expression.Length ? _expression[_index] : '\0';

        private bool TryConsume(char value)
        {
            SkipWhitespace();
            if (Peek() != value)
                return false;

            _index++;
            return true;
        }

        private void SkipWhitespace()
        {
            while (_index < _expression.Length && char.IsWhiteSpace(_expression[_index]))
                _index++;
        }
    }

}
