using XREngine.Extensions;
using System.Globalization;
using System.Reflection;

namespace XREngine.Core.Tools
{
    public static class ExpressionParser
    {
        /// <summary>
        /// Parses an expression and returns the resulting primitive type.
        /// </summary>
        /// <param name="expression">The expression to evaluate, as a string.</param>
        /// <param name="context">The object to provide variable values.</param>
        public static T Evaluate<T>(string expression, object context)
            where T : struct, IComparable, IConvertible, IComparable<T>, IEquatable<T>
        {
            Queue<string> queue = new();
            Stack<string> stack = new();
            try
            {
                ConvertToPostFix(expression, queue, stack);
                string result = GetAnswer(context, queue, stack);
                object? value = GetValue(result, context);
                return value is T tVal
                    ? tVal
                    : Type.GetTypeCode(typeof(T)) switch
                    {
                        TypeCode.Boolean => (T)(object)Convert.ToBoolean(value),
                        TypeCode.Char => (T)(object)Convert.ToChar(value),
                        TypeCode.SByte => (T)(object)Convert.ToSByte(value),
                        TypeCode.Byte => (T)(object)Convert.ToByte(value),
                        TypeCode.Int16 => (T)(object)Convert.ToInt16(value),
                        TypeCode.UInt16 => (T)(object)Convert.ToUInt16(value),
                        TypeCode.Int32 => (T)(object)Convert.ToInt32(value),
                        TypeCode.UInt32 => (T)(object)Convert.ToUInt32(value),
                        TypeCode.Int64 => (T)(object)Convert.ToInt64(value),
                        TypeCode.UInt64 => (T)(object)Convert.ToUInt64(value),
                        TypeCode.Single => (T)(object)Convert.ToSingle(value),
                        TypeCode.Double => (T)(object)Convert.ToDouble(value),
                        TypeCode.Decimal => (T)(object)Convert.ToDecimal(value),
                        _ => throw new InvalidCastException(),
                    };
                throw new InvalidCastException();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                return default;
            }
        }

        private static readonly char[] separator = [' '];

        private static string[] SplitInFix(string inFix)
        {
            inFix = inFix.ReplaceWhitespace("").Replace(")", " ) ").Replace("(", " ( ");
            var ops = _precedence.SelectMany(x => x).ToList();
            foreach (var o in ops)
                inFix = inFix.Replace(o, $" {o} ");
            return inFix.Split(separator, StringSplitOptions.RemoveEmptyEntries);
        }

        private static void ConvertToPostFix(string inFix, Queue<string> queue, Stack<string> stack)
        {
            //Split up the expression by the spaces between each token
            string[] tokens = SplitInFix(inFix);
            //Loop through each token
            for (int i = 0; i < tokens.Length; ++i)
            {
                //Get current token from the array
                string token = tokens[i];
                //Ignore blank tokens
                if (token is null || token.Length == 0)
                    continue;
                //Push open parentheses to the stack
                if (token.Equals("("))
                    stack.Push(token);
                else if (token.Equals(")"))
                    //Enqueue stack until the next open parentheses in the stack
                    while (!stack.Peek().Equals("("))
                        queue.Enqueue(stack.Pop());
                else if (IsOperator(token))
                {
                    //enqueue stack as long as it is an operator
                    //and the token operator does not take precedence over it
                    while (stack.Count > 0 &&
                        !stack.Peek().Equals("(") &&
                        !ComparePrecedence(token, stack.Peek()))
                        queue.Enqueue(stack.Pop());

                    stack.Push(token);
                }
                else //is a number
                    queue.Enqueue(token);
            }
            //Queue everything left in the stack
            while (stack.Count > 0)
                queue.Enqueue(stack.Pop());
        }

        private static bool DigitsOnly(string str)
        {
            foreach (char c in str)
                if (c < '0' || c > '9')
                    return false;
            return true;
        }

        private static bool HexDigitsOnly(string str)
        {
            foreach (char c in str)
                if (c < '0' || c > 'F' || c == '@')
                    return false;
            return true;
        }

        private const BindingFlags AllInstanceVars = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static object? GetValue(string token, object provider)
        {
            token = token.Trim();

            if (token == "null")
                return null;
            
            //Inversions will be found first
            int invertCount = 0;
            while (token.StartsWith('!'))
            {
                ++invertCount;
                token = token[1..];
            }

            //Don't invert if even, invert if odd
            bool shouldInvert = (invertCount & 1) != 0;

            //Handle strings FIRST, since they could contain anything
            if (token.StartsWith('\"'))
            {
                if (token.EndsWith('\"'))
                {
                    string str = token[1..^1];
                    for (int i = 0; i < str.Length; ++i)
                    {
                        char c = str[i];
                        if (c == '\\')
                            str = str.Remove(i, 1);
                    }
                    return str;
                }
                else
                    throw new Exception("Invalid string: " + token);
            }

            //Only methods can contain parentheses
            int paramStart = token.IndexOf('(');
            if (paramStart > 0)
            {
                int paramEnd = token.LastIndexOf(')');
                if (paramEnd > paramStart)
                {
                    string methodName = token[..paramStart];
                    MethodInfo[] methods = provider is null ? [] : provider.GetType().GetMethods(AllInstanceVars).Where(x => string.Equals(x.Name, methodName)).ToArray();
                    var parameters = token[(paramStart + 1)..paramEnd].Split(',');
                    
                    //TODO: invoke matching method, return value
                }
                else
                    throw new Exception("Invalid method: " + token);
            }
            
            //Handle boolean literals.
            //Only valid if lowercase
            if (token.Equals("true", StringComparison.InvariantCulture) || 
                token.Equals("false", StringComparison.InvariantCulture))
            {
                bool value = bool.Parse(token);
                if (shouldInvert)
                    return !value;
                return value;
            }

            //Handle numeric literals
            object? numericValue = TryParseNumeric(token);
            if (numericValue != null)
            {
                if (invertCount > 0)
                    throw new Exception("Cannot invert numeric types.");
                return numericValue;
            }

            //Handle field/property names
            List<FieldInfo> fields = [];
            List<PropertyInfo> properties = [];
            if (provider != null)
            {
                Type? t = provider.GetType();
                while (t != null && t != typeof(object))
                {
                    fields.AddRange(t.GetFields(AllInstanceVars));
                    properties.AddRange(t.GetProperties(AllInstanceVars));
                    t = t.BaseType;
                }
            }

            FieldInfo? field = fields.FirstOrDefault(x => x.Name.Equals(token));
            if (field != null)
                return InvertBool(invertCount, shouldInvert, field.GetValue(provider), field.FieldType);
            
            PropertyInfo? property = properties.FirstOrDefault(x => x.Name.Equals(token));
            if (property != null)
                return InvertBool(invertCount, shouldInvert, property.GetValue(provider), property.PropertyType);
            
            //throw new InvalidOperationException("Token not recognized: " + token);
            return null;
        }

        private static object? InvertBool(int invertCount, bool shouldInvert, object? value, Type type)
        {
            if (type != typeof(bool) && invertCount > 0)
                throw new Exception("Cannot invert non-bool.");
            return shouldInvert && value is bool b ? !b : value;
        }

        private static object? TryParseNumeric(string token)
        {
            token = token.ToLowerInvariant();

            //Negations will be found first
            int invertCount = 0;
            bool negate;
            while ((negate = token.StartsWith('-')) || token.StartsWith('+'))
            {
                if (negate)
                    ++invertCount;
                token = token[1..];
            }            
            //Don't negate if even, negate if odd
            bool shouldInvert = (invertCount & 1) != 0;

            bool isIntegral = !token.Contains('.');
            if (isIntegral)
            {
                bool isHex = token.StartsWith("0x");

                if (isHex)
                    token = token[2..];

                bool forceLong = false, forceUnsigned = false;
                if (token.EndsWith('l'))
                {
                    forceLong = true;
                    token = token[..^1];
                    if (token.EndsWith('u'))
                    {
                        forceUnsigned = true;
                        token = token[..^1];
                    }
                }
                else if (token.EndsWith('u'))
                {
                    forceUnsigned = true;
                    token = token[..^1];
                    if (token.EndsWith('l'))
                    {
                        forceLong = true;
                        token = token[..^1];
                    }
                }
                if (isHex)
                {
                    if (HexDigitsOnly(token))
                    {
                        if (forceUnsigned)
                        {
                            if (shouldInvert)
                                throw new InvalidOperationException("Cannot negate an unsigned integer.");

                            if (forceLong)
                                return ulong.Parse(token, NumberStyles.HexNumber);
                            else
                                return uint.Parse(token, NumberStyles.HexNumber);
                        }
                        else
                        {
                            if (forceLong)
                            {
                                long value = long.Parse(token, NumberStyles.HexNumber);
                                return shouldInvert ? -value : value;
                            }
                            else
                            {
                                int value = int.Parse(token, NumberStyles.HexNumber);
                                return shouldInvert ? -value : value;
                            }
                        }
                    }
                }
                else if (DigitsOnly(token))
                {
                    if (forceUnsigned)
                    {
                        if (shouldInvert)
                            throw new InvalidOperationException("Cannot negate an unsigned integer.");

                        if (forceLong)
                            return ulong.Parse(token);
                        else
                            return uint.Parse(token);
                    }
                    else if (forceLong)
                    {
                        long value = long.Parse(token);
                        return shouldInvert ? -value : value;
                    }
                    else
                    {
                        int value = int.Parse(token);
                        return shouldInvert ? -value : value;
                    }
                }
            }
            else
            {
                bool
                    forceSingle = token.EndsWith('f'),
                    forceDouble = token.EndsWith('d'),
                    forceDecimal = token.EndsWith('m');

                if (forceSingle || forceDouble || forceDecimal)
                    token = token[..^1];

                string[] parts = token.Split('.');
                if (DigitsOnly(parts[0]) && DigitsOnly(parts[1]))
                {
                    if (forceSingle)
                        return float.Parse(token);

                    if (forceDecimal)
                        return decimal.Parse(token);

                    //if (forceDouble)
                    return double.Parse(token);
                }
            }
            return null;
        }

        private static readonly Dictionary<string, string[]> _implicitConversions = new()
        {
            { "SByte",  new string[] { "Int16", "Int32", "Int64", "Single", "Double", "Decimal" } },
            { "Byte",   new string[] { "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64", "Single", "Double", "Decimal" } },
            { "Int16",  new string[] { "Int32", "Int64", "Single", "Double", "Decimal" } },
            { "UInt16", new string[] { "Int32", "UInt32", "Int64", "UInt64", "Single", "Double", "Decimal" } },
            { "Int32",  new string[] { "Int64", "Single", "Double", "Decimal" } },
            { "UInt32", new string[] { "Int64", "UInt64", "Single", "Double", "Decimal" } },
            { "Int64",  new string[] { "Single", "Double", "Decimal" } },
            { "UInt64", new string[] { "Single", "Double", "Decimal" } },
            { "Char",   new string[] { "UInt16", "Int32", "UInt32", "Int64", "UInt64", "Single", "Double", "Decimal" } },
            { "Single", new string[] { "Single", "Double" } },
        };

        private static string GetAnswer(object provider, Queue<string> queue, Stack<string> stack)
        {
            //Dequeue sorted operators and numbers from the queue until the queue is empty
            while (queue.Count > 0)
            {
                string token = queue.Dequeue();
                if (IsOperator(token))
                {
                    //Get the second operand first
                    string stackToken2 = stack.Pop();
                    object? value2 = GetValue(stackToken2, provider);

                    //First operand comes second
                    object? value1;
                    if (stack.Count == 0)
                    {
                        if (token == "-" || token == "+")
                            value1 = 0;
                        else
                            throw new InvalidOperationException($"Operator {token} needs operands on both sides.");
                    }
                    else
                    {
                        string stackToken1 = stack.Pop();
                        value1 = GetValue(stackToken1, provider);
                    }

                    if (value1 is null)
                    {
                        bool isNull = value2 is null;
                        if (token == "==")
                            stack.Push(isNull ? "true" : "false");
                        else if (token == "!=")
                            stack.Push(isNull ? "false" : "true");
                        else
                            throw new Exception();
                        continue;
                    }
                    else if (value2 is null)
                    {
                        if (token == "==")
                            stack.Push("false");
                        else if (token == "!=")
                            stack.Push("true");
                        else
                            throw new Exception();
                        continue;
                    }

                    Type t1 = value1.GetType();
                    Type t2 = value2.GetType();
                    if (t1.BaseType == typeof(Enum))
                        t1 = t1.BaseType;
                    if (t2.BaseType == typeof(Enum))
                        t2 = t2.BaseType;

                    string result;
                    switch (token)
                    {
                        case "&&":
                            if (t1.BaseType != typeof(bool) || t2.BaseType != typeof(bool))
                                throw new Exception("Cannot logical-and non-boolean types.");
                            result = ((bool)value1 && (bool)value2).ToString();
                            break;
                        case "||":
                            if (t1.BaseType != typeof(bool) || t2.BaseType != typeof(bool))
                                throw new Exception("Cannot logical-or non-boolean types.");
                            result = ((bool)value1 || (bool)value2).ToString();
                            break;
                        case "*":
                        case "/":
                        case "+":
                        case "-":
                        case "<<":
                        case ">>":
                        case "<":
                        case ">":
                        case "<=":
                        case ">=":
                        case "&":
                        case "^":
                        case "|":
                        case "==":
                        case "!=":
                            result = EvaluateOperation(t1, t2, value1, value2, token);
                            break;
                        default:
                            throw new Exception(string.Format("Token \"{0}\" not supported.", token));
                    }
                    //Push the result back onto the stack
                    stack.Push(result);
                }
                else if (!token.Equals("("))
                {
                    //Push numbers directly to the stack; ignore '('
                    //object value = GetValue(token, provider);
                    //stack.Push(value is null ? "null" : value.ToString());
                    stack.Push(token);
                }
            }
            //The result of the expression is now the only number in the stack
            return stack.Pop();
        }

        private static string EvaluateOperation(
            Type t1, Type t2,
            object value1, object value2,
            string token)
        {
            if (t1.BaseType != typeof(ValueType) || t2.BaseType != typeof(ValueType))
                throw new Exception($"Cannot evaluate {t1.Name} {token} {t2.Name}");

            //types are the same?
            if (string.Equals(t1.Name, t2.Name))
                return EvalToken(value1, value2, token, t1.Name);

            //t1 can be converted to t2's type?
            else if (_implicitConversions.TryGetValue(t1.Name, out string[]? t1Value) && t1Value.FirstOrDefault(x => string.Equals(x, t2.Name)) != null)
                return EvalToken(value1, value2, token, t2.Name);

            //t2 can be converted to t1's type?
            else if (_implicitConversions.TryGetValue(t2.Name, out string[]? t2Value) && t2Value.FirstOrDefault(x => string.Equals(x, t1.Name)) != null)
                return EvalToken(value1, value2, token, t1.Name);

            //both can be converted to a next largest common type?
            else if (_implicitConversions.TryGetValue(t1.Name, out string[]? value) && _implicitConversions.ContainsKey(t2.Name))
            {
                string[] commonImplicitTypes = value.Intersect(value).ToArray();
                if (commonImplicitTypes.Length > 0)
                    return EvalToken(value1, value2, token, commonImplicitTypes[0]);
                else
                    throw new Exception($"Cannot evaluate {t1.Name} {token} {t2.Name}");
            }
            else
                throw new Exception($"Cannot evaluate {t1.Name} {token} {t2.Name}"); 
        }

        private static bool IsOperator(string s)
            => _precedence.Any(x => x.Contains(s));

        private static readonly string[][] _precedence =
        [
            ["*", "/"],
            ["+", "-"],
            ["<<", ">>"],
            ["<", ">", "<=", ">="],
            ["==", "!="],
            ["&"],
            ["^"],
            ["|"],
            ["&&"],
            ["||"],
        ];

        /// <summary>
        /// True if op1 >= op2 in precedence.
        /// </summary>
        /// <param name="op1">First operator to compare.</param>
        /// <param name="op2">Second operator to compare.</param>
        private static bool ComparePrecedence(string op1, string op2)
        {
            int op1p = -1, op2p = -1;
            for (int i = 0; i < _precedence.Length; ++i)
            {
                string[] ops = _precedence[i];
                foreach (string s in ops)
                {
                    if (op1.Equals(s))
                        op1p = i;
                    if (op2.Equals(s))
                        op2p = i;
                    if (op1p >= 0 && op2p >= 0)
                        return op1p >= op2p;
                }
            }
            return false;
        }

        private static string EvalToken(object value1, object value2, string token, string commonType) 
            => token switch
            {
                "*" => EvalMult(value1, value2, commonType),
                "/" => EvalDiv(value1, value2, commonType),
                "+" => EvalAdd(value1, value2, commonType),
                "-" => EvalSub(value1, value2, commonType),
                "<<" => EvalLeftShift(value1, value2, commonType),
                ">>" => EvalRightShift(value1, value2, commonType),
                "<" => EvalLess(value1, value2, commonType),
                ">" => EvalGreater(value1, value2, commonType),
                "<=" => EvalLessEqual(value1, value2, commonType),
                ">=" => EvalGreaterEqual(value1, value2, commonType),
                "&" => EvalAnd(value1, value2, commonType),
                "^" => EvalXor(value1, value2, commonType),
                "|" => EvalOr(value1, value2, commonType),
                "==" => EvalEquals(value1, value2, commonType),
                "!=" => EvalNotEquals(value1, value2, commonType),
                _ => throw new Exception("Not a numeric primitive type"),
            };

        private static string EvalMult(object value1, object value2, string commonType)
            => commonType switch
            {
                "SByte" => ((sbyte)value1 * (sbyte)value2).ToString(),
                "Byte" => ((byte)value1 * (byte)value2).ToString(),
                "Int16" => ((short)value1 * (short)value2).ToString(),
                "UInt16" => ((ushort)value1 * (ushort)value2).ToString(),
                "Int32" => ((int)value1 * (int)value2).ToString(),
                "UInt32" => ((uint)value1 * (uint)value2).ToString(),
                "Int64" => ((long)value1 * (long)value2).ToString(),
                "UInt64" => ((ulong)value1 * (ulong)value2).ToString(),
                "Single" => ((float)value1 * (float)value2).ToString(),
                "Double" => ((double)value1 * (double)value2).ToString(),
                "Decimal" => ((decimal)value1 * (decimal)value2).ToString(),
                _ => throw new Exception("Not a numeric primitive type"),
            };

        private static string EvalDiv(object value1, object value2, string commonType)
            => commonType switch
            {
                "SByte" => ((sbyte)value1 / (sbyte)value2).ToString(),
                "Byte" => ((byte)value1 / (byte)value2).ToString(),
                "Int16" => ((short)value1 / (short)value2).ToString(),
                "UInt16" => ((ushort)value1 / (ushort)value2).ToString(),
                "Int32" => ((int)value1 / (int)value2).ToString(),
                "UInt32" => ((uint)value1 / (uint)value2).ToString(),
                "Int64" => ((long)value1 / (long)value2).ToString(),
                "UInt64" => ((ulong)value1 / (ulong)value2).ToString(),
                "Single" => ((float)value1 / (float)value2).ToString(),
                "Double" => ((double)value1 / (double)value2).ToString(),
                "Decimal" => ((decimal)value1 / (decimal)value2).ToString(),
                _ => throw new Exception("Not a numeric primitive type"),
            };

        private static string EvalAdd(object value1, object value2, string commonType)
            => commonType switch
            {
                "SByte" => ((sbyte)value1 + (sbyte)value2).ToString(),
                "Byte" => ((byte)value1 + (byte)value2).ToString(),
                "Int16" => ((short)value1 + (short)value2).ToString(),
                "UInt16" => ((ushort)value1 + (ushort)value2).ToString(),
                "Int32" => ((int)value1 + (int)value2).ToString(),
                "UInt32" => ((uint)value1 + (uint)value2).ToString(),
                "Int64" => ((long)value1 + (long)value2).ToString(),
                "UInt64" => ((ulong)value1 + (ulong)value2).ToString(),
                "Single" => ((float)value1 + (float)value2).ToString(),
                "Double" => ((double)value1 + (double)value2).ToString(),
                "Decimal" => ((decimal)value1 + (decimal)value2).ToString(),
                _ => throw new Exception("Not a numeric primitive type"),
            };

        private static string EvalSub(object value1, object value2, string commonType)
            => commonType switch
            {
                "SByte" => ((sbyte)value1 - (sbyte)value2).ToString(),
                "Byte" => ((byte)value1 - (byte)value2).ToString(),
                "Int16" => ((short)value1 - (short)value2).ToString(),
                "UInt16" => ((ushort)value1 - (ushort)value2).ToString(),
                "Int32" => ((int)value1 - (int)value2).ToString(),
                "UInt32" => ((uint)value1 - (uint)value2).ToString(),
                "Int64" => ((long)value1 - (long)value2).ToString(),
                "UInt64" => ((ulong)value1 - (ulong)value2).ToString(),
                "Single" => ((float)value1 - (float)value2).ToString(),
                "Double" => ((double)value1 - (double)value2).ToString(),
                "Decimal" => ((decimal)value1 - (decimal)value2).ToString(),
                _ => throw new Exception("Not a numeric primitive type"),
            };

        private static string EvalLeftShift(object value1, object value2, string commonType)
            => commonType switch
            {
                "SByte" => ((sbyte)value1 << (sbyte)value2).ToString(),
                "Byte" => ((byte)value1 << (byte)value2).ToString(),
                "Int16" => ((short)value1 << (short)value2).ToString(),
                "UInt16" => ((ushort)value1 << (ushort)value2).ToString(),
                "Int32" => ((int)value1 << (int)value2).ToString(),
                "UInt32" => ((uint)value1 << (int)value2).ToString(),
                "Int64" => ((long)value1 << (int)value2).ToString(),
                "UInt64" => ((ulong)value1 << (int)value2).ToString(),
                "Single" or "Double" or "Decimal" => throw new Exception("Cannot left shift " + commonType),
                _ => throw new Exception("Not a numeric primitive type"),
            };

        private static string EvalRightShift(object value1, object value2, string commonType)
            => commonType switch
            {
                "SByte" => ((sbyte)value1 >> (sbyte)value2).ToString(),
                "Byte" => ((byte)value1 >> (byte)value2).ToString(),
                "Int16" => ((short)value1 >> (short)value2).ToString(),
                "UInt16" => ((ushort)value1 >> (ushort)value2).ToString(),
                "Int32" => ((int)value1 >> (int)value2).ToString(),
                "UInt32" => ((uint)value1 >> (int)value2).ToString(),
                "Int64" => ((long)value1 >> (int)value2).ToString(),
                "UInt64" => ((ulong)value1 >> (int)value2).ToString(),
                "Single" or "Double" or "Decimal" => throw new Exception("Cannot right shift " + commonType),
                _ => throw new Exception("Not a numeric primitive type"),
            };

        private static string EvalAnd(object value1, object value2, string commonType)
            => commonType switch
            {
                "SByte" => ((sbyte)value1 & (sbyte)value2).ToString(),
                "Byte" => ((byte)value1 & (byte)value2).ToString(),
                "Int16" => ((short)value1 & (short)value2).ToString(),
                "UInt16" => ((ushort)value1 & (ushort)value2).ToString(),
                "Int32" => ((int)value1 & (int)value2).ToString(),
                "UInt32" => ((uint)value1 & (uint)value2).ToString(),
                "Int64" => ((long)value1 & (long)value2).ToString(),
                "UInt64" => ((ulong)value1 & (ulong)value2).ToString(),
                "Single" or "Double" or "Decimal" => throw new Exception("Cannot bitwise-and " + commonType),
                _ => throw new Exception("Not a numeric primitive type"),
            };

        private static string EvalXor(object value1, object value2, string commonType)
            => commonType switch
            {
                "SByte" => ((sbyte)value1 ^ (sbyte)value2).ToString(),
                "Byte" => ((byte)value1 ^ (byte)value2).ToString(),
                "Int16" => ((short)value1 ^ (short)value2).ToString(),
                "UInt16" => ((ushort)value1 ^ (ushort)value2).ToString(),
                "Int32" => ((int)value1 ^ (int)value2).ToString(),
                "UInt32" => ((uint)value1 ^ (uint)value2).ToString(),
                "Int64" => ((long)value1 ^ (long)value2).ToString(),
                "UInt64" => ((ulong)value1 ^ (ulong)value2).ToString(),
                "Single" or "Double" or "Decimal" => throw new Exception("Cannot bitwise-xor " + commonType),
                _ => throw new Exception("Not a numeric primitive type"),
            };

        private static string EvalOr(object value1, object value2, string commonType)
            => commonType switch
            {
                "SByte" => ((sbyte)value1 | (sbyte)value2).ToString(),
                "Byte" => ((byte)value1 | (byte)value2).ToString(),
                "Int16" => ((short)value1 | (short)value2).ToString(),
                "UInt16" => ((ushort)value1 | (ushort)value2).ToString(),
                "Int32" => ((int)value1 | (int)value2).ToString(),
                "UInt32" => ((uint)value1 | (uint)value2).ToString(),
                "Int64" => ((long)value1 | (long)value2).ToString(),
                "UInt64" => ((ulong)value1 | (ulong)value2).ToString(),
                "Single" or "Double" or "Decimal" => throw new Exception("Cannot bitwise-or " + commonType),
                _ => throw new Exception("Not a numeric primitive type"),
            };

        private static string EvalLess(object value1, object value2, string commonType)
            => commonType switch
            {
                "SByte" => ((sbyte)value1 < (sbyte)value2).ToString(),
                "Byte" => ((byte)value1 < (byte)value2).ToString(),
                "Int16" => ((short)value1 < (short)value2).ToString(),
                "UInt16" => ((ushort)value1 < (ushort)value2).ToString(),
                "Int32" => ((int)value1 < (int)value2).ToString(),
                "UInt32" => ((uint)value1 < (uint)value2).ToString(),
                "Int64" => ((long)value1 < (long)value2).ToString(),
                "UInt64" => ((ulong)value1 < (ulong)value2).ToString(),
                "Single" => ((float)value1 < (float)value2).ToString(),
                "Double" => ((double)value1 < (double)value2).ToString(),
                "Decimal" => ((decimal)value1 < (decimal)value2).ToString(),
                _ => throw new Exception("Not a numeric primitive type"),
            };

        private static string EvalGreater(object value1, object value2, string commonType)
            => commonType switch
            {
                "SByte" => ((sbyte)value1 > (sbyte)value2).ToString(),
                "Byte" => ((byte)value1 > (byte)value2).ToString(),
                "Int16" => ((short)value1 > (short)value2).ToString(),
                "UInt16" => ((ushort)value1 > (ushort)value2).ToString(),
                "Int32" => ((int)value1 > (int)value2).ToString(),
                "UInt32" => ((uint)value1 > (uint)value2).ToString(),
                "Int64" => ((long)value1 > (long)value2).ToString(),
                "UInt64" => ((ulong)value1 > (ulong)value2).ToString(),
                "Single" => ((float)value1 > (float)value2).ToString(),
                "Double" => ((double)value1 > (double)value2).ToString(),
                "Decimal" => ((decimal)value1 > (decimal)value2).ToString(),
                _ => throw new Exception("Not a numeric primitive type"),
            };

        private static string EvalLessEqual(object value1, object value2, string commonType)
            => commonType switch
            {
                "SByte" => ((sbyte)value1 <= (sbyte)value2).ToString(),
                "Byte" => ((byte)value1 <= (byte)value2).ToString(),
                "Int16" => ((short)value1 <= (short)value2).ToString(),
                "UInt16" => ((ushort)value1 <= (ushort)value2).ToString(),
                "Int32" => ((int)value1 <= (int)value2).ToString(),
                "UInt32" => ((uint)value1 <= (uint)value2).ToString(),
                "Int64" => ((long)value1 <= (long)value2).ToString(),
                "UInt64" => ((ulong)value1 <= (ulong)value2).ToString(),
                "Single" => ((float)value1 <= (float)value2).ToString(),
                "Double" => ((double)value1 <= (double)value2).ToString(),
                "Decimal" => ((decimal)value1 <= (decimal)value2).ToString(),
                _ => throw new Exception("Not a numeric primitive type"),
            };

        private static string EvalGreaterEqual(object value1, object value2, string commonType)
            => commonType switch
            {
                "SByte" => ((sbyte)value1 >= (sbyte)value2).ToString(),
                "Byte" => ((byte)value1 >= (byte)value2).ToString(),
                "Int16" => ((short)value1 >= (short)value2).ToString(),
                "UInt16" => ((ushort)value1 >= (ushort)value2).ToString(),
                "Int32" => ((int)value1 >= (int)value2).ToString(),
                "UInt32" => ((uint)value1 >= (uint)value2).ToString(),
                "Int64" => ((long)value1 >= (long)value2).ToString(),
                "UInt64" => ((ulong)value1 >= (ulong)value2).ToString(),
                "Single" => ((float)value1 >= (float)value2).ToString(),
                "Double" => ((double)value1 >= (double)value2).ToString(),
                "Decimal" => ((decimal)value1 >= (decimal)value2).ToString(),
                _ => throw new Exception("Not a numeric primitive type"),
            };

        private static string EvalEquals(object value1, object value2, string commonType)
            => commonType switch
            {
                "Boolean" => ((bool)value1 == (bool)value2).ToString(),
                "SByte" => ((sbyte)value1 == (sbyte)value2).ToString(),
                "Byte" => ((byte)value1 == (byte)value2).ToString(),
                "Int16" => ((short)value1 == (short)value2).ToString(),
                "UInt16" => ((ushort)value1 == (ushort)value2).ToString(),
                "Int32" => ((int)value1 == (int)value2).ToString(),
                "UInt32" => ((uint)value1 == (uint)value2).ToString(),
                "Int64" => ((long)value1 == (long)value2).ToString(),
                "UInt64" => ((ulong)value1 == (ulong)value2).ToString(),
                "Single" => ((float)value1 == (float)value2).ToString(),
                "Double" => ((double)value1 == (double)value2).ToString(),
                "Decimal" => ((decimal)value1 == (decimal)value2).ToString(),
                _ => throw new Exception("Not a primitive type"),
            };

        private static string EvalNotEquals(object value1, object value2, string commonType)
            => commonType switch
            {
                "Boolean" => ((bool)value1 != (bool)value2).ToString(),
                "SByte" => ((sbyte)value1 != (sbyte)value2).ToString(),
                "Byte" => ((byte)value1 != (byte)value2).ToString(),
                "Int16" => ((short)value1 != (short)value2).ToString(),
                "UInt16" => ((ushort)value1 != (ushort)value2).ToString(),
                "Int32" => ((int)value1 != (int)value2).ToString(),
                "UInt32" => ((uint)value1 != (uint)value2).ToString(),
                "Int64" => ((long)value1 != (long)value2).ToString(),
                "UInt64" => ((ulong)value1 != (ulong)value2).ToString(),
                "Single" => ((float)value1 != (float)value2).ToString(),
                "Double" => ((double)value1 != (double)value2).ToString(),
                "Decimal" => ((decimal)value1 != (decimal)value2).ToString(),
                _ => throw new Exception("Not a primitive type"),
            };
    }
}
