using System.Reflection;

namespace XREngine;

public partial class GameStartupSettings
{
    /// <summary>
    /// Creates a detached runtime projection of this settings root.
    /// </summary>
    public GameStartupSettings DeepClone()
    {
        Type runtimeType = GetType();
        var clone = Activator.CreateInstance(runtimeType) as GameStartupSettings ??
            throw new InvalidOperationException(
                $"Settings type '{runtimeType.FullName}' requires a public parameterless constructor for session projection.");

        CopyBaseSettingsProperties(this, clone);
        CopyDerivedLaunchProperties(this, clone, runtimeType);
        clone.SourceAsset = clone;
        clone.FilePath = null;
        clone.ClearDirty();
        return clone;
    }

    private static void CopyBaseSettingsProperties(
        GameStartupSettings source,
        GameStartupSettings target)
    {
        const BindingFlags flags =
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.DeclaredOnly;

        foreach (PropertyInfo property in typeof(GameStartupSettings).GetProperties(flags))
        {
            if (!property.CanRead ||
                property.SetMethod?.IsPublic != true ||
                property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            object? value = property.GetValue(source);
            object? cloneValue = property.Name == nameof(StartupWindows) &&
                value is List<GameWindowStartupSettings> startupWindows
                    ? new List<GameWindowStartupSettings>(startupWindows)
                    : ClonePropertyValue(value, property.PropertyType);
            property.SetValue(target, cloneValue);
        }
    }

    private static void CopyDerivedLaunchProperties(
        GameStartupSettings source,
        GameStartupSettings target,
        Type runtimeType)
    {
        const BindingFlags flags =
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.DeclaredOnly;

        for (Type? type = runtimeType;
             type is not null && type != typeof(GameStartupSettings);
             type = type.BaseType)
        {
            foreach (PropertyInfo property in type.GetProperties(flags))
            {
                if (!property.CanRead ||
                    property.SetMethod?.IsPublic != true ||
                    property.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                // Derived startup contracts can contain external manifest objects that
                // are not engine-serializable. Preserve those process inputs by reference;
                // session paths registered for GameStartupSettings address the deeply
                // cloned base settings declared above.
                property.SetValue(target, property.GetValue(source));
            }
        }
    }
}
