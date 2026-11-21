using System.Collections.Concurrent;
using System.ComponentModel;
using System.Numerics;
using System.Reflection;

namespace XREngine.Rendering;

/// <summary>
/// Finds the pipeline settings that the <c>settings</c> block of a script can set. A setting is a public property with
/// a public setter, declared on a pipeline type below <see cref="RenderPipeline"/>, of a simple value type: bool, a
/// number, an enum, a string, or a vector. Properties marked <c>[Browsable(false)]</c> are not settings.
/// </summary>
public static class RenderPipelineScriptSettings
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> s_cache = new();

    /// <summary>Gets the setting properties of <paramref name="pipelineType"/>, in declaration order.</summary>
    public static IReadOnlyList<PropertyInfo> GetSettingProperties(Type pipelineType)
    {
        ArgumentNullException.ThrowIfNull(pipelineType);
        return s_cache.GetOrAdd(pipelineType, static type =>
        {
            List<PropertyInfo> settings = [];
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (IsSetting(property))
                    settings.Add(property);
            }

            return [.. settings];
        });
    }

    /// <summary>Finds a setting by name. The match ignores case.</summary>
    public static PropertyInfo? FindSetting(Type pipelineType, string name)
    {
        IReadOnlyList<PropertyInfo> settings = GetSettingProperties(pipelineType);
        for (int i = 0; i < settings.Count; i++)
        {
            if (string.Equals(settings[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return settings[i];
        }

        return null;
    }

    private static bool IsSetting(PropertyInfo property)
    {
        Type? declaringType = property.DeclaringType;
        if (declaringType is null || declaringType == typeof(RenderPipeline) || !typeof(RenderPipeline).IsAssignableFrom(declaringType))
            return false;
        if (!property.CanRead || property.SetMethod?.IsPublic != true || property.GetIndexParameters().Length > 0)
            return false;
        if (property.GetCustomAttribute<BrowsableAttribute>() is { Browsable: false })
            return false;

        return IsSettingType(property.PropertyType);
    }

    private static bool IsSettingType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsEnum ||
            type == typeof(bool) ||
            type == typeof(int) ||
            type == typeof(uint) ||
            type == typeof(float) ||
            type == typeof(double) ||
            type == typeof(string) ||
            type == typeof(Vector2) ||
            type == typeof(Vector3) ||
            type == typeof(Vector4);
    }
}
