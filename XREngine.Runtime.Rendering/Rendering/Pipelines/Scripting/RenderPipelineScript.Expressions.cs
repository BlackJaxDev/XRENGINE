using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering;

// Value and expression nodes, and the runtime context that evaluates them.
public sealed partial class RenderPipelineScript
{
    private interface IValueNode
    {
        object? GetConstantValue(Type? targetType = null);
    }

    private interface IExpressionNode : IValueNode
    {
        object? Evaluate(ScriptRuntimeContext context);
    }

    private sealed class LiteralNode(object? value) : IExpressionNode
    {
        private readonly object? _value = value;

        public object? Evaluate(ScriptRuntimeContext context)
            => _value;

        public object? GetConstantValue(Type? targetType = null)
            => _value;
    }

    private sealed class IdentifierNode(string name) : IExpressionNode
    {
        private readonly string _name = name;

        public object? Evaluate(ScriptRuntimeContext context)
            => context.GetVariable(_name);

        public object? GetConstantValue(Type? targetType = null)
        {
            if (targetType is not null && targetType.IsEnum)
                return Enum.Parse(targetType, _name, ignoreCase: true);
            if (targetType == typeof(string))
                return _name;
            throw new InvalidOperationException($"Identifier '{_name}' requires runtime evaluation or an enum/string target type.");
        }
    }

    private sealed class ArrayNode(IReadOnlyList<IValueNode> items) : IValueNode
    {
        private readonly IReadOnlyList<IValueNode> _items = items;

        public object? GetConstantValue(Type? targetType = null)
            => _items;

        public IReadOnlyList<IValueNode> Items => _items;
    }

    private sealed class ConstructorNode(string name, IReadOnlyList<IValueNode> args) : IValueNode
    {
        private readonly string _name = name;
        private readonly IReadOnlyList<IValueNode> _args = args;

        public object? GetConstantValue(Type? targetType = null)
        {
            float GetFloat(int index) => Convert.ToSingle(ScriptValueConverter.ConvertTo(_args[index], typeof(float)), CultureInfo.InvariantCulture);

            return _name.ToLowerInvariant() switch
            {
                "vec2" when _args.Count == 2 => new Vector2(GetFloat(0), GetFloat(1)),
                "vec3" when _args.Count == 3 => new Vector3(GetFloat(0), GetFloat(1), GetFloat(2)),
                "vec4" when _args.Count == 4 => new Vector4(GetFloat(0), GetFloat(1), GetFloat(2), GetFloat(3)),
                "mat4" when _args.Count == 16 => new Matrix4x4(
                    GetFloat(0), GetFloat(1), GetFloat(2), GetFloat(3),
                    GetFloat(4), GetFloat(5), GetFloat(6), GetFloat(7),
                    GetFloat(8), GetFloat(9), GetFloat(10), GetFloat(11),
                    GetFloat(12), GetFloat(13), GetFloat(14), GetFloat(15)),
                _ => throw new InvalidOperationException($"Unknown script constructor '{_name}'.")
            };
        }
    }

    private sealed class UnaryExpressionNode(string op, IExpressionNode operand) : IExpressionNode
    {
        private readonly string _op = op;
        private readonly IExpressionNode _operand = operand;

        public object? Evaluate(ScriptRuntimeContext context)
        {
            object? value = _operand.Evaluate(context);
            return _op switch
            {
                "!" => !ScriptRuntimeContext.ToBoolean(value),
                "-" => -Convert.ToDouble(value, CultureInfo.InvariantCulture),
                "+" => Convert.ToDouble(value, CultureInfo.InvariantCulture),
                _ => throw new InvalidOperationException($"Unsupported unary operator '{_op}'.")
            };
        }

        public object? GetConstantValue(Type? targetType = null)
            => Evaluate(ScriptRuntimeContext.Empty);
    }

    private sealed class BinaryExpressionNode(string op, IExpressionNode left, IExpressionNode right) : IExpressionNode
    {
        private readonly string _op = op;
        private readonly IExpressionNode _left = left;
        private readonly IExpressionNode _right = right;

        public object? Evaluate(ScriptRuntimeContext context)
        {
            object? leftValue = _left.Evaluate(context);
            object? rightValue = _right.Evaluate(context);

            return _op switch
            {
                "&&" => ScriptRuntimeContext.ToBoolean(leftValue) && ScriptRuntimeContext.ToBoolean(rightValue),
                "||" => ScriptRuntimeContext.ToBoolean(leftValue) || ScriptRuntimeContext.ToBoolean(rightValue),
                "==" => Equals(leftValue, rightValue),
                "!=" => !Equals(leftValue, rightValue),
                "<" => Compare(leftValue, rightValue) < 0,
                ">" => Compare(leftValue, rightValue) > 0,
                "<=" => Compare(leftValue, rightValue) <= 0,
                ">=" => Compare(leftValue, rightValue) >= 0,
                "+" => Add(leftValue, rightValue),
                "-" => Convert.ToDouble(leftValue, CultureInfo.InvariantCulture) - Convert.ToDouble(rightValue, CultureInfo.InvariantCulture),
                "*" => Convert.ToDouble(leftValue, CultureInfo.InvariantCulture) * Convert.ToDouble(rightValue, CultureInfo.InvariantCulture),
                "/" => Convert.ToDouble(leftValue, CultureInfo.InvariantCulture) / Convert.ToDouble(rightValue, CultureInfo.InvariantCulture),
                _ => throw new InvalidOperationException($"Unsupported binary operator '{_op}'.")
            };
        }

        public object? GetConstantValue(Type? targetType = null)
            => Evaluate(ScriptRuntimeContext.Empty);

        private static int Compare(object? leftValue, object? rightValue)
        {
            double left = Convert.ToDouble(leftValue, CultureInfo.InvariantCulture);
            double right = Convert.ToDouble(rightValue, CultureInfo.InvariantCulture);
            return left.CompareTo(right);
        }

        private static object Add(object? leftValue, object? rightValue)
        {
            if (leftValue is string ls && rightValue is string rs)
                return string.Concat(ls, rs);
            if (leftValue is string ls2)
                return string.Concat(ls2, rightValue?.ToString());
            if (rightValue is string rs2)
                return string.Concat(leftValue?.ToString(), rs2);

            double result = Convert.ToDouble(leftValue, CultureInfo.InvariantCulture) + Convert.ToDouble(rightValue, CultureInfo.InvariantCulture);
            if (leftValue is int && rightValue is int)
                return (int)result;
            if (leftValue is uint && rightValue is uint)
                return (uint)result;
            if (leftValue is float || rightValue is float)
                return (float)result;
            return result;
        }
    }

    private sealed class IndexExpressionNode(IExpressionNode target, IExpressionNode index) : IExpressionNode
    {
        private readonly IExpressionNode _target = target;
        private readonly IExpressionNode _index = index;

        public object? Evaluate(ScriptRuntimeContext context)
        {
            object? target = _target.Evaluate(context);
            int index = Convert.ToInt32(_index.Evaluate(context), CultureInfo.InvariantCulture);

            return target switch
            {
                null => null,
                Array array => index >= 0 && index < array.Length ? array.GetValue(index) : null,
                System.Collections.IList list => index >= 0 && index < list.Count ? list[index] : null,
                IReadOnlyList<object?> readOnlyObjects => index >= 0 && index < readOnlyObjects.Count ? readOnlyObjects[index] : null,
                string text => index >= 0 && index < text.Length ? text[index].ToString() : null,
                _ => throw new InvalidOperationException($"Type '{target.GetType().Name}' does not support script indexing.")
            };
        }

        public object? GetConstantValue(Type? targetType = null)
            => Evaluate(ScriptRuntimeContext.Empty);
    }

    private sealed class ScriptRuntimeContext(XRRenderPipelineInstance? instance)
    {
        public static ScriptRuntimeContext Empty { get; } = new(null);

        public static ScriptRuntimeContext CreateActive()
            => new(ViewportRenderCommand.ActivePipelineInstance);

        public object? GetVariable(string name)
        {
            if (instance is null)
                return null;

            XRRenderPipelineInstance.PipelineVariableStore variables = instance.Variables;
            if (variables.TryGet(name, out bool boolValue))
                return boolValue;
            if (variables.TryGet(name, out int intValue))
                return intValue;
            if (variables.TryGet(name, out uint uintValue))
                return uintValue;
            if (variables.TryGet(name, out float floatValue))
                return floatValue;
            if (variables.TryGet(name, out Vector2 vector2Value))
                return vector2Value;
            if (variables.TryGet(name, out Vector3 vector3Value))
                return vector3Value;
            if (variables.TryGet(name, out Vector4 vector4Value))
                return vector4Value;
            if (variables.TryGet(name, out Matrix4x4 matrixValue))
                return matrixValue;
            if (variables.TryGet(name, out string? stringValue))
                return stringValue;
            if (variables.TryResolveTexture(instance.Resources, name, out XRTexture? texture))
                return texture;
            if (variables.TryResolveFrameBuffer(instance.Resources, name, out XRFrameBuffer? frameBuffer))
                return frameBuffer;
            if (variables.TryResolveBuffer(instance.Resources, name, out XRDataBuffer? buffer))
                return buffer;
            if (variables.TryResolveRenderBuffer(instance.Resources, name, out XRRenderBuffer? renderBuffer))
                return renderBuffer;

            return null;
        }

        public static bool ToBoolean(object? value)
            => value switch
            {
                null => false,
                bool boolValue => boolValue,
                int intValue => intValue != 0,
                uint uintValue => uintValue != 0,
                float floatValue => Math.Abs(floatValue) > float.Epsilon,
                double doubleValue => Math.Abs(doubleValue) > double.Epsilon,
                string stringValue when bool.TryParse(stringValue, out bool parsed) => parsed,
                string stringValue => !string.IsNullOrWhiteSpace(stringValue),
                _ => Convert.ToBoolean(value, CultureInfo.InvariantCulture)
            };
    }
}
