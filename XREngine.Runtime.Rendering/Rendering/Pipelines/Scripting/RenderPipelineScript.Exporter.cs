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
            nameof(VPRC_IfElse.Condition),
            nameof(VPRC_IfElse.Label),
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
            AppendContainer(builder, container, 0, null);
            return builder.ToString();
        }

        /// <summary>
        /// Appends the commands of <paramref name="container"/>. When <paramref name="unconverted"/> is not null, each
        /// code item that the text cannot hold gets an <c>// unconverted:</c> line before its command and a line in the
        /// list.
        /// </summary>
        public static void AppendContainer(System.Text.StringBuilder builder, ViewportRenderCommandContainer container, int depth, List<string>? unconverted)
        {
            for (int i = 0; i < container.Count; i++)
                AppendCommand(builder, container[i], depth, unconverted);
        }

        private static void AppendUnconverted(System.Text.StringBuilder builder, string indent, List<string>? unconverted, string note)
        {
            if (unconverted is null)
                return;

            unconverted.Add(note);
            builder.Append(indent).Append("// ").Append(UnconvertedCommentPrefix).Append(' ').AppendLine(note);
        }

        private static void AppendCondition(System.Text.StringBuilder builder, RenderBranchCondition? condition, string? label)
        {
            builder.Append('(').Append(condition is null ? CodeConditionText : FormatCondition(condition)).Append(')');
            if (!string.IsNullOrEmpty(label))
                builder.Append(" as ").Append(Quote(label));
        }

        /// <summary>Writes a data condition in the form that the parser reads.</summary>
        public static string FormatCondition(RenderBranchCondition condition)
        {
            if (condition.Source == ERenderBranchConditionSource.Constant)
                return condition.Value != 0.0 ? "true" : "false";

            string source = condition.Source switch
            {
                ERenderBranchConditionSource.PipelineVariable => string.Concat("var ", condition.Key),
                ERenderBranchConditionSource.PipelineSetting => string.Concat("setting ", condition.Key),
                ERenderBranchConditionSource.StageEnabled => string.Concat("stage ", FormatName(condition.Key), " enabled"),
                ERenderBranchConditionSource.Capability => string.Concat("capability ", condition.Key),
                _ => CodeConditionText,
            };

            string value = condition.Value.ToString(CultureInfo.InvariantCulture);
            return condition.Operator switch
            {
                ERenderBranchConditionOperator.IsTrue => source,
                ERenderBranchConditionOperator.IsFalse => string.Concat("!", source),
                ERenderBranchConditionOperator.Equal => string.Concat(source, " == ", value),
                ERenderBranchConditionOperator.NotEqual => string.Concat(source, " != ", value),
                ERenderBranchConditionOperator.Greater => string.Concat(source, " > ", value),
                ERenderBranchConditionOperator.GreaterOrEqual => string.Concat(source, " >= ", value),
                ERenderBranchConditionOperator.Less => string.Concat(source, " < ", value),
                ERenderBranchConditionOperator.LessOrEqual => string.Concat(source, " <= ", value),
                _ => source,
            };
        }

        /// <summary>Writes a name as an identifier, or as a quoted string when it is not a plain identifier.</summary>
        public static string FormatName(string name)
        {
            if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_') || IsKeywordText(name))
                return Quote(name);

            for (int i = 1; i < name.Length; i++)
            {
                if (!char.IsLetterOrDigit(name[i]) && name[i] != '_')
                    return Quote(name);
            }

            return name;
        }

        private static void AppendCommand(System.Text.StringBuilder builder, ViewportRenderCommand command, int depth, List<string>? unconverted)
        {
            string indent = new(' ', depth * 4);
            switch (command)
            {
                case VPRC_IfElse ifElse:
                    if (ifElse.Condition is null && ifElse.ConditionEvaluator is not null)
                        AppendUnconverted(builder, indent, unconverted, string.Concat("code condition of if '", ifElse.Label ?? "unlabelled", "'"));
                    builder.Append(indent).Append("if ");
                    AppendCondition(builder, ifElse.Condition, ifElse.Label);
                    builder.AppendLine(" {");
                    if (ifElse.TrueCommands is not null)
                        AppendContainer(builder, ifElse.TrueCommands, depth + 1, unconverted);
                    builder.Append(indent).Append('}');
                    if (ifElse.FalseCommands is not null)
                    {
                        builder.AppendLine(" else {");
                        AppendContainer(builder, ifElse.FalseCommands, depth + 1, unconverted);
                        builder.Append(indent).AppendLine("}");
                    }
                    else
                    {
                        builder.AppendLine();
                    }
                    return;

                case VPRC_Switch switchCommand:
                    if (switchCommand.Condition is null && switchCommand.SwitchEvaluator is not null)
                        AppendUnconverted(builder, indent, unconverted, string.Concat("code selector of switch '", switchCommand.Label ?? "unlabelled", "'"));
                    builder.Append(indent).Append("switch ");
                    AppendCondition(builder, switchCommand.Condition, switchCommand.Label);
                    builder.AppendLine(" {");
                    if (switchCommand.Cases is not null)
                    {
                        foreach (KeyValuePair<int, ViewportRenderCommandContainer> pair in switchCommand.Cases.OrderBy(static x => x.Key))
                        {
                            builder.Append(indent).Append("    case ").Append(pair.Key.ToString(CultureInfo.InvariantCulture)).AppendLine(": {");
                            AppendContainer(builder, pair.Value, depth + 2, unconverted);
                            builder.Append(indent).AppendLine("    }");
                        }
                    }
                    if (switchCommand.DefaultCase is not null)
                    {
                        builder.Append(indent).AppendLine("    default: {");
                        AppendContainer(builder, switchCommand.DefaultCase, depth + 2, unconverted);
                        builder.Append(indent).AppendLine("    }");
                    }
                    builder.Append(indent).AppendLine("}");
                    return;

                case VPRC_Repeat repeat:
                    if (repeat.CountProvider is not null || repeat.IterationConfigurator is not null)
                        AppendUnconverted(builder, indent, unconverted, "code count or iteration setup of repeat");
                    builder.Append(indent).Append("repeat(")
                        .Append(repeat.CountProvider is null
                            ? repeat.Count.ToString(CultureInfo.InvariantCulture)
                            : "1")
                        .AppendLine(") {");
                    if (repeat.Body is not null)
                        AppendContainer(builder, repeat.Body, depth + 1, unconverted);
                    builder.Append(indent).AppendLine("}");
                    return;

                case VPRC_ConditionalRender conditionalRender:
                    AppendCodeProperties(builder, indent, conditionalRender, unconverted);
                    builder.Append(indent).Append("when(");
                    AppendProperties(builder, conditionalRender);
                    builder.AppendLine(") {");
                    if (conditionalRender.Body is not null)
                        AppendContainer(builder, conditionalRender.Body, depth + 1, unconverted);
                    builder.Append(indent).AppendLine("}");
                    return;

                case VPRC_ForEachCascade forEachCascade:
                    AppendCodeProperties(builder, indent, forEachCascade, unconverted);
                    builder.Append(indent).Append("foreach_cascade(");
                    AppendProperties(builder, forEachCascade);
                    builder.AppendLine(") {");
                    if (forEachCascade.Body is not null)
                        AppendContainer(builder, forEachCascade.Body, depth + 1, unconverted);
                    builder.Append(indent).AppendLine("}");
                    return;
            }

            AppendCodeProperties(builder, indent, command, unconverted);
            builder.Append(indent).Append(GetScriptCommandMethodName(command.GetType())).Append('(');
            AppendProperties(builder, command);
            builder.AppendLine(");");
        }

        /// <summary>Adds an unconverted note for each code delegate on <paramref name="command"/> that is not the type default.</summary>
        private static void AppendCodeProperties(System.Text.StringBuilder builder, string indent, ViewportRenderCommand command, List<string>? unconverted)
        {
            if (unconverted is null)
                return;

            object? defaultCommand = CreateDefaultCommand(command);
            foreach (PropertyInfo property in command.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!property.CanRead || property.GetIndexParameters().Length > 0 || !typeof(Delegate).IsAssignableFrom(property.PropertyType))
                    continue;
                if (property.Name is nameof(VPRC_IfElse.ConditionEvaluator) or nameof(VPRC_Switch.SwitchEvaluator) or nameof(VPRC_Repeat.CountProvider) or nameof(VPRC_Repeat.IterationConfigurator))
                    continue;

                object? value = property.GetValue(command);
                if (value is null || (defaultCommand is not null && Equals(value, property.GetValue(defaultCommand))))
                    continue;

                AppendUnconverted(builder, indent, unconverted, string.Concat("code delegate ", GetScriptCommandMethodName(command.GetType()), ".", property.Name));
            }
        }

        private static object? CreateDefaultCommand(ViewportRenderCommand command)
        {
            try
            {
                if (ViewportRenderCommandContainer.TryCreateRegisteredCommand(command.GetType(), out ViewportRenderCommand? registeredDefault) && registeredDefault is not null)
                    return registeredDefault;

                return XRRuntimeEnvironment.IsAotRuntimeBuild ? null : Activator.CreateInstance(command.GetType());
            }
            catch (Exception ex) when (ex is MissingMethodException or TargetInvocationException or InvalidOperationException or ArgumentException)
            {
                return null;
            }
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

        /// <summary>
        /// Appends <c>Name=value</c> for each public settable property of <paramref name="value"/> whose text differs
        /// from the same property on <paramref name="defaults"/>. Properties in <paramref name="skip"/> are not written.
        /// </summary>
        /// <returns>The number of properties written.</returns>
        public static int AppendObjectProperties(System.Text.StringBuilder builder, object value, object defaults, ICollection<string>? skip)
        {
            int written = 0;
            foreach (PropertyInfo property in value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!property.CanRead || property.SetMethod?.IsPublic != true || property.GetIndexParameters().Length > 0)
                    continue;
                if (skip is not null && skip.Contains(property.Name))
                    continue;
                if (!TrySerializeValue(property.GetValue(value), out string? text))
                    continue;
                if (TrySerializeValue(property.GetValue(defaults), out string? defaultText) && string.Equals(text, defaultText, StringComparison.Ordinal))
                    continue;

                if (written > 0)
                    builder.Append(", ");
                builder.Append(property.Name).Append('=').Append(text);
                written++;
            }

            return written;
        }

        public static bool TrySerializeValue(object? value, out string? serialized)
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

            if (value is not null && ScriptValueConverter.GetObjectConstructorName(value.GetType()) is { } constructorName)
            {
                System.Text.StringBuilder objectText = new();
                objectText.Append(constructorName).Append('(');
                AppendObjectProperties(objectText, value, ScriptValueConverter.CreateDefaultObject(constructorName), null);
                serialized = objectText.Append(')').ToString();
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

        public static string Quote(string value)
            => $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

        private static string FormatNumber(float value)
            => value.ToString("0.0###", CultureInfo.InvariantCulture);
    }
}
