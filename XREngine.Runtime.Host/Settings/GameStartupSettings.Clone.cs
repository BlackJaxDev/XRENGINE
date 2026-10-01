using System.Reflection;
using XREngine.Core.Files;

namespace XREngine;

public partial class GameStartupSettings
{
    /// <summary>
    /// Creates a detached runtime-session projection of this settings root.
    /// </summary>
    public GameStartupSettings DeepClone()
    {
        Type runtimeType = GetType();
        var clone = Activator.CreateInstance(runtimeType) as GameStartupSettings ??
            throw new InvalidOperationException(
                $"Settings type '{runtimeType.FullName}' requires a public parameterless constructor for session projection.");

        CopyBaseSettingsProperties(this, clone);
        CopyDerivedLaunchProperties(this, clone, runtimeType);
        DetachClonedAsset(clone.BuildSettings);
        DetachClonedAsset(clone.DefaultUserSettings);
        DetachClonedAsset(clone);
        return clone;
    }

    /// <summary>
    /// Finalizes the owned settings assets after session overrides have been applied.
    /// </summary>
    internal void FinalizeSessionProjection()
    {
        DetachClonedAsset(BuildSettings);
        DetachClonedAsset(DefaultUserSettings);
        DetachClonedAsset(this);

        BuildSettings.EmbeddedAssets.Clear();
        DefaultUserSettings.EmbeddedAssets.Clear();
        EmbeddedAssets.Clear();

        BuildSettings.MarkAsTransientProjection();
        DefaultUserSettings.MarkAsTransientProjection();
        MarkAsTransientProjection();
        ClearDirty();
    }

    /// <summary>
    /// Removes authored graph ownership from a cloned settings asset before it
    /// becomes a process-local projection.
    /// </summary>
    private static void DetachClonedAsset(XRAsset asset)
    {
        asset.SourceAsset = asset;
        asset.FilePath = null;
        asset.ClearDirty();
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
