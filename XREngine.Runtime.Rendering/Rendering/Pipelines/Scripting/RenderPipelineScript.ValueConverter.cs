using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering;

// Converts script values to the types of command properties.
public sealed partial class RenderPipelineScript
{
    private static class ScriptValueConverter
    {
        public static object? ConvertTo(IValueNode value, Type targetType)
        {
            Type destinationType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            object? raw = value.GetConstantValue(destinationType);
            if (raw is null)
                return null;

            if (destinationType.IsInstanceOfType(raw))
                return raw;

            if (raw is IReadOnlyList<IValueNode> listValues)
                return ConvertList(listValues, destinationType);

            if (destinationType.IsEnum)
            {
                if (raw is string enumName)
                    return Enum.Parse(destinationType, enumName, ignoreCase: true);
                return Enum.ToObject(destinationType, System.Convert.ToInt64(raw, CultureInfo.InvariantCulture));
            }

            if (destinationType == typeof(string))
                return raw.ToString();
            if (destinationType == typeof(bool))
                return System.Convert.ToBoolean(raw, CultureInfo.InvariantCulture);
            if (destinationType == typeof(int))
                return System.Convert.ToInt32(raw, CultureInfo.InvariantCulture);
            if (destinationType == typeof(uint))
                return System.Convert.ToUInt32(raw, CultureInfo.InvariantCulture);
            if (destinationType == typeof(float))
                return System.Convert.ToSingle(raw, CultureInfo.InvariantCulture);
            if (destinationType == typeof(double))
                return System.Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            if (destinationType == typeof(Vector2) || destinationType == typeof(Vector3) || destinationType == typeof(Vector4) || destinationType == typeof(Matrix4x4))
                return raw;

            TypeConverter converter = TypeDescriptor.GetConverter(destinationType);
            if (converter.CanConvertFrom(raw.GetType()))
                return converter.ConvertFrom(null, CultureInfo.InvariantCulture, raw);
            if (raw is string rawString && converter.CanConvertFrom(typeof(string)))
                return converter.ConvertFromInvariantString(rawString);

            return System.Convert.ChangeType(raw, destinationType, CultureInfo.InvariantCulture);
        }

        private static object ConvertList(IReadOnlyList<IValueNode> items, Type destinationType)
        {
            if (destinationType.IsArray)
            {
                Type elementType = destinationType.GetElementType() ?? typeof(object);
                Array array = Array.CreateInstance(elementType, items.Count);
                for (int i = 0; i < items.Count; i++)
                    array.SetValue(ConvertTo(items[i], elementType), i);
                return array;
            }

            Type? element = destinationType.IsGenericType ? destinationType.GetGenericArguments()[0] : null;
            if (element is null)
                throw new InvalidOperationException($"Unsupported list destination type '{destinationType.Name}'.");

            Type listType = typeof(List<>).MakeGenericType(element);
            if (XRRuntimeEnvironment.IsAotRuntimeBuild)
                throw new InvalidOperationException($"Render pipeline scripts cannot construct list type '{listType.Name}' in published AOT builds; use arrays or a registered code-authored factory path.");

            var list = (System.Collections.IList)(Activator.CreateInstance(listType)
                ?? throw new InvalidOperationException($"Unable to create list type '{listType.Name}'."));
            for (int i = 0; i < items.Count; i++)
                list.Add(ConvertTo(items[i], element));
            return list;
        }
    }
}
