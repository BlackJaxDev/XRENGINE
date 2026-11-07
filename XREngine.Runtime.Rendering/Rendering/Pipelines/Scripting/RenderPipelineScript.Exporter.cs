using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering;

// Writes a command container as script text.
public sealed partial class RenderPipelineScript
{
    private static class ScriptExporter
    {
        private static readonly HashSet<string> s_ignoredProperties =
        [
            nameof(ViewportRenderCommand.CommandContainer),
            nameof(ViewportRenderCommand.ParentPipeline),
            nameof(VPRC_IfElse.TrueCommands),
            nameof(VPRC_IfElse.FalseCommands),
            nameof(VPRC_IfElse.ConditionEvaluator),
            nameof(VPRC_Switch.Cases),
            nameof(VPRC_Switch.DefaultCase),
            nameof(VPRC_Switch.SwitchEvaluator),
            nameof(VPRC_Repeat.Body),
            nameof(VPRC_Repeat.CountProvider),
            nameof(VPRC_Repeat.IterationConfigurator),
            nameof(VPRC_ConditionalRender.Body),
            nameof(VPRC_ForEachCascade.Body),
            nameof(VPRC_SetVariable.ValueEvaluator),
        ];

        public static string Export(ViewportRenderCommandContainer container)
        {
            System.Text.StringBuilder builder = new();
            AppendContainer(builder, container, 0);
            return builder.ToString();
        }

        private static void AppendContainer(System.Text.StringBuilder builder, ViewportRenderCommandContainer container, int depth)
        {
            for (int i = 0; i < container.Count; i++)
                AppendCommand(builder, container[i], depth);
        }

        private static void AppendCommand(System.Text.StringBuilder builder, ViewportRenderCommand command, int depth)
        {
            string indent = new(' ', depth * 4);
            switch (command)
            {
                case VPRC_IfElse ifElse:
                    builder.Append(indent).AppendLine("if (false) {");
                    if (ifElse.TrueCommands is not null)
                        AppendContainer(builder, ifElse.TrueCommands, depth + 1);
                    builder.Append(indent).Append('}');
                    if (ifElse.FalseCommands is not null)
                    {
                        builder.AppendLine(" else {");
                        AppendContainer(builder, ifElse.FalseCommands, depth + 1);
                        builder.Append(indent).AppendLine("}");
                    }
                    else
                    {
                        builder.AppendLine();
                    }
                    return;

                case VPRC_Switch switchCommand:
                    builder.Append(indent).AppendLine("switch (0) {");
                    if (switchCommand.Cases is not null)
                    {
                        foreach (KeyValuePair<int, ViewportRenderCommandContainer> pair in switchCommand.Cases.OrderBy(static x => x.Key))
                        {
                            builder.Append(indent).Append("    case ").Append(pair.Key.ToString(CultureInfo.InvariantCulture)).AppendLine(": {");
                            AppendContainer(builder, pair.Value, depth + 2);
                            builder.Append(indent).AppendLine("    }");
                        }
                    }
                    if (switchCommand.DefaultCase is not null)
                    {
                        builder.Append(indent).AppendLine("    default: {");
                        AppendContainer(builder, switchCommand.DefaultCase, depth + 2);
                        builder.Append(indent).AppendLine("    }");
                    }
                    builder.Append(indent).AppendLine("}");
                    return;

                case VPRC_Repeat repeat:
                    builder.Append(indent).Append("repeat(")
                        .Append(repeat.CountProvider is null
                            ? repeat.Count.ToString(CultureInfo.InvariantCulture)
                            : "1")
                        .AppendLine(") {");
                    if (repeat.Body is not null)
                        AppendContainer(builder, repeat.Body, depth + 1);
                    builder.Append(indent).AppendLine("}");
                    return;

                case VPRC_ConditionalRender conditionalRender:
                    builder.Append(indent).Append("when(");
                    AppendProperties(builder, conditionalRender);
                    builder.AppendLine(") {");
                    if (conditionalRender.Body is not null)
                        AppendContainer(builder, conditionalRender.Body, depth + 1);
                    builder.Append(indent).AppendLine("}");
                    return;

                case VPRC_ForEachCascade forEachCascade:
                    builder.Append(indent).Append("foreach_cascade(");
                    AppendProperties(builder, forEachCascade);
                    builder.AppendLine(") {");
                    if (forEachCascade.Body is not null)
                        AppendContainer(builder, forEachCascade.Body, depth + 1);
                    builder.Append(indent).AppendLine("}");
                    return;
            }

            builder.Append(indent).Append(GetScriptCommandMethodName(command.GetType())).Append('(');
            AppendProperties(builder, command);
            builder.AppendLine(");");
        }

        private static void AppendProperties(System.Text.StringBuilder builder, ViewportRenderCommand command)
        {
            object? defaultCommand = null;
            try
            {
                if (!ViewportRenderCommandContainer.TryCreateRegisteredCommand(command.GetType(), out ViewportRenderCommand? registeredDefault) || registeredDefault is null)
                {
                    if (XRRuntimeEnvironment.IsAotRuntimeBuild)
                        return;

                    defaultCommand = Activator.CreateInstance(command.GetType());
                }
                else
                {
                    defaultCommand = registeredDefault;
                }
            }
            catch
            {
            }

            bool wroteAny = false;
            foreach (PropertyInfo property in command.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length > 0 || s_ignoredProperties.Contains(property.Name))
                    continue;

                object? value = property.GetValue(command);
                object? defaultValue = defaultCommand is null ? null : property.GetValue(defaultCommand);
                if (EqualsSerializedValue(value, defaultValue))
                    continue;

                if (!TrySerializeValue(value, out string? serialized))
                    continue;

                if (wroteAny)
                    builder.Append(", ");
                builder.Append(property.Name).Append('=').Append(serialized);
                wroteAny = true;
            }
        }

        private static bool EqualsSerializedValue(object? left, object? right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left is null || right is null)
                return false;

            if (left is System.Collections.IEnumerable leftEnumerable && right is System.Collections.IEnumerable rightEnumerable && left is not string && right is not string)
            {
                List<object?> leftItems = [];
                List<object?> rightItems = [];
                foreach (object? item in leftEnumerable)
                    leftItems.Add(item);
                foreach (object? item in rightEnumerable)
                    rightItems.Add(item);
                return leftItems.SequenceEqual(rightItems);
            }

            return Equals(left, right);
        }

        private static bool TrySerializeValue(object? value, out string? serialized)
        {
            switch (value)
            {
                case null:
                    serialized = "null";
                    return true;
                case bool boolValue:
                    serialized = boolValue ? "true" : "false";
                    return true;
                case string stringValue:
                    serialized = Quote(stringValue);
                    return true;
                case Enum enumValue:
                    serialized = enumValue.ToString();
                    return true;
                case sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal:
                    serialized = Convert.ToString(value, CultureInfo.InvariantCulture);
                    return true;
                case Vector2 vector2:
                    serialized = $"vec2({FormatNumber(vector2.X)}, {FormatNumber(vector2.Y)})";
                    return true;
                case Vector3 vector3:
                    serialized = $"vec3({FormatNumber(vector3.X)}, {FormatNumber(vector3.Y)}, {FormatNumber(vector3.Z)})";
                    return true;
                case Vector4 vector4:
                    serialized = $"vec4({FormatNumber(vector4.X)}, {FormatNumber(vector4.Y)}, {FormatNumber(vector4.Z)}, {FormatNumber(vector4.W)})";
                    return true;
                case Matrix4x4 matrix:
                    serialized = $"mat4({FormatNumber(matrix.M11)}, {FormatNumber(matrix.M12)}, {FormatNumber(matrix.M13)}, {FormatNumber(matrix.M14)}, {FormatNumber(matrix.M21)}, {FormatNumber(matrix.M22)}, {FormatNumber(matrix.M23)}, {FormatNumber(matrix.M24)}, {FormatNumber(matrix.M31)}, {FormatNumber(matrix.M32)}, {FormatNumber(matrix.M33)}, {FormatNumber(matrix.M34)}, {FormatNumber(matrix.M41)}, {FormatNumber(matrix.M42)}, {FormatNumber(matrix.M43)}, {FormatNumber(matrix.M44)})";
                    return true;
            }

            if (value is System.Collections.IEnumerable enumerable && value is not string)
            {
                List<string> items = [];
                foreach (object? item in enumerable)
                {
                    if (!TrySerializeValue(item, out string? itemText))
                    {
                        serialized = null;
                        return false;
                    }
                    items.Add(itemText!);
                }
                serialized = $"[{string.Join(", ", items)}]";
                return true;
            }

            serialized = null;
            return false;
        }

        private static string Quote(string value)
            => $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

        private static string FormatNumber(float value)
            => value.ToString("0.0###", CultureInfo.InvariantCulture);
    }
}
