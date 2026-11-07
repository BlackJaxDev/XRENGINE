using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Resources.Authoring;

namespace XREngine.Rendering;

// Converts script values to the types of command properties.
public sealed partial class RenderPipelineScript
{
    private static class ScriptValueConverter
    {
        /// <summary>
        /// The object constructors that scripts can write, by name. Each entry has an explicit factory, so an object
        /// value does not need <see cref="Activator"/> for these types.
        /// </summary>
        private static readonly Dictionary<string, (Type Type, Func<object> Factory)> s_objectConstructors = new(StringComparer.OrdinalIgnoreCase)
        {
            ["size"] = (typeof(AuthoredSizePolicy), static () => new AuthoredSizePolicy()),
            ["attachment"] = (typeof(AuthoredAttachment), static () => new AuthoredAttachment()),
        };

        /// <summary>Returns the script constructor name for <paramref name="type"/>, or null when scripts cannot write it.</summary>
        public static string? GetObjectConstructorName(Type type)
        {
            foreach (KeyValuePair<string, (Type Type, Func<object> Factory)> pair in s_objectConstructors)
            {
                if (pair.Value.Type == type)
                    return pair.Key;
            }

            return null;
        }

        /// <summary>Creates an object with default values for the object constructor <paramref name="name"/>.</summary>
        public static object CreateDefaultObject(string name)
            => s_objectConstructors[name].Factory();

        /// <summary>Returns true when <paramref name="name"/> is an object constructor such as <c>size</c>.</summary>
        public static bool IsObjectConstructor(string name)
            => s_objectConstructors.ContainsKey(name);

        /// <summary>
        /// Creates an object for a constructor with named arguments, for example
        /// <c>size(SizeClass=WindowResolution, ScaleX=0.5)</c>. Each argument sets the property with the same name.
        /// </summary>
        public static object CreateObject(string name, IReadOnlyList<KeyValuePair<string, IValueNode>> namedArguments, Type? targetType)
        {
            object instance;
            if (s_objectConstructors.TryGetValue(name, out (Type Type, Func<object> Factory) entry))
            {
                if (targetType is not null && targetType != typeof(object) && !targetType.IsAssignableFrom(entry.Type))
                    throw new InvalidOperationException($"Constructor '{name}' makes a {entry.Type.Name}, not a {targetType.Name}.");

                instance = entry.Factory();
            }
            else
            {
                throw new InvalidOperationException($"Unknown script constructor '{name}'.");
            }

            for (int i = 0; i < namedArguments.Count; i++)
                SetObjectProperty(instance, namedArguments[i].Key, namedArguments[i].Value);

            return instance;
        }

        /// <summary>Sets the public property <paramref name="propertyName"/> of <paramref name="target"/> from a script value.</summary>
        public static void SetObjectProperty(object target, string propertyName, IValueNode value)
        {
            PropertyInfo? property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (property is null || !property.CanWrite || property.SetMethod?.IsPublic != true)
                throw new InvalidOperationException($"'{target.GetType().Name}' has no settable property '{propertyName}'.");

            property.SetValue(target, ConvertTo(value, property.PropertyType));
        }

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
