using System.Collections;
using System.Reflection;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.Resources.Authoring;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Resource declaration edits on a <see cref="RenderPipelineDraftState"/>. Command properties that hold resource
/// names carry <see cref="RenderResourceNameAttribute"/>, so a rename can update every reference.
/// </summary>
public static class RenderPipelineDraftResourceEdits
{
    /// <summary>Returns a name that starts with <paramref name="baseName"/> and that no declaration uses.</summary>
    public static string MakeUniqueName(RenderPipelineDraftState state, string baseName)
    {
        string name = string.IsNullOrWhiteSpace(baseName) ? "Resource" : baseName.Trim();
        if (FindResource(state, name) is null)
            return name;

        for (int suffix = 2; ; suffix++)
        {
            string candidate = string.Concat(name, suffix.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (FindResource(state, candidate) is null)
                return candidate;
        }
    }

    /// <summary>Adds a declaration of <paramref name="kind"/> with a unique name and usable defaults.</summary>
    public static AuthoredRenderResource Add(RenderPipelineDraftState state, EAuthoredRenderResourceKind kind)
    {
        AuthoredRenderResource resource = new()
        {
            Name = MakeUniqueName(state, string.Concat("New", kind.ToString())),
            Kind = kind,
        };

        state.Resources.Add(resource);
        return resource;
    }

    /// <summary>Adds a copy of <paramref name="name"/> with a unique name.</summary>
    /// <returns>The copy, or null when no declaration has the name.</returns>
    public static AuthoredRenderResource? Duplicate(RenderPipelineDraftState state, string name)
    {
        AuthoredRenderResource? source = FindResource(state, name);
        if (source is null)
            return null;

        AuthoredRenderResource copy = source.Clone();
        copy.Name = MakeUniqueName(state, string.Concat(source.Name, "Copy"));
        state.Resources.Insert(state.Resources.IndexOf(source) + 1, copy);
        return copy;
    }

    /// <summary>
    /// Renames a declaration and updates every reference: command properties with
    /// <see cref="RenderResourceNameAttribute"/>, framebuffer attachments, view sources, and quad material inputs.
    /// </summary>
    /// <returns>The number of references that changed, or -1 when the rename is not valid.</returns>
    public static int Rename(RenderPipelineDraftState state, string oldName, string newName)
    {
        newName = newName?.Trim() ?? string.Empty;
        AuthoredRenderResource? resource = FindResource(state, oldName);
        if (resource is null || newName.Length == 0)
            return -1;
        if (!string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase) && FindResource(state, newName) is not null)
            return -1;

        resource.Name = newName;
        int changed = 0;
        for (int i = 0; i < state.Resources.Count; i++)
            changed += RenameInResource(state.Resources[i], oldName, newName);

        RenderPipelineDraftEdits.ForEachCommand(state.Commands, command => changed += RenameInCommand(command, oldName, newName));
        return changed;
    }

    /// <summary>
    /// Removes a declaration when nothing references it.
    /// </summary>
    /// <param name="state">The draft state.</param>
    /// <param name="name">The declaration to remove.</param>
    /// <param name="references">The references that block the delete.</param>
    /// <returns>True when the declaration was removed.</returns>
    public static bool TryDelete(RenderPipelineDraftState state, string name, List<string> references)
    {
        references.Clear();
        AuthoredRenderResource? resource = FindResource(state, name);
        if (resource is null)
            return false;

        CollectReferences(state, name, references);
        if (references.Count > 0)
            return false;

        state.Resources.Remove(resource);
        return true;
    }

    /// <summary>
    /// Adds a text for each place that names <paramref name="name"/>, such as "VPRC_Tonemap.SourceTexture" or
    /// "HDRFBO attachment".
    /// </summary>
    public static void CollectReferences(RenderPipelineDraftState state, string name, List<string> references)
    {
        for (int i = 0; i < state.Resources.Count; i++)
        {
            AuthoredRenderResource resource = state.Resources[i];
            if (string.Equals(resource.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;

            if (NameEquals(resource.SourceTexture, name))
                references.Add(string.Concat(resource.Name, " view source"));
            for (int a = 0; a < resource.Attachments.Count; a++)
            {
                if (NameEquals(resource.Attachments[a].ResourceName, name))
                    references.Add(string.Concat(resource.Name, " attachment"));
            }

            for (int t = 0; t < resource.InputTextures.Count; t++)
            {
                if (NameEquals(resource.InputTextures[t], name))
                    references.Add(string.Concat(resource.Name, " input"));
            }
        }

        RenderPipelineDraftEdits.ForEachCommand(state.Commands, command =>
        {
            PropertyInfo[] properties = GetResourceNameProperties(command.GetType());
            for (int i = 0; i < properties.Length; i++)
            {
                if (ValueNames(properties[i].GetValue(command), name))
                    references.Add(string.Concat(command.GetType().Name, ".", properties[i].Name));
            }
        });
    }

    /// <summary>Returns the declaration named <paramref name="name"/>, or null. Names compare without case.</summary>
    public static AuthoredRenderResource? FindResource(RenderPipelineDraftState state, string name)
    {
        for (int i = 0; i < state.Resources.Count; i++)
        {
            if (string.Equals(state.Resources[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return state.Resources[i];
        }

        return null;
    }

    /// <summary>Returns the properties of <paramref name="commandType"/> that hold resource names.</summary>
    public static PropertyInfo[] GetResourceNameProperties(Type commandType)
        => s_resourceNameProperties.GetOrAdd(commandType, static type =>
        {
            PropertyInfo[] copied = RenderCommandGraphCloner.GetCopiedProperties(type);
            List<PropertyInfo> named = [];
            for (int i = 0; i < copied.Length; i++)
            {
                if (copied[i].GetCustomAttribute<RenderResourceNameAttribute>(inherit: true) is not null)
                    named.Add(copied[i]);
            }

            return [.. named];
        });

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, PropertyInfo[]> s_resourceNameProperties = new();

    private static int RenameInResource(AuthoredRenderResource resource, string oldName, string newName)
    {
        int changed = 0;
        if (NameEquals(resource.SourceTexture, oldName))
        {
            resource.SourceTexture = newName;
            changed++;
        }

        for (int i = 0; i < resource.Attachments.Count; i++)
        {
            if (!NameEquals(resource.Attachments[i].ResourceName, oldName))
                continue;

            resource.Attachments[i].ResourceName = newName;
            changed++;
        }

        for (int i = 0; i < resource.InputTextures.Count; i++)
        {
            if (!NameEquals(resource.InputTextures[i], oldName))
                continue;

            resource.InputTextures[i] = newName;
            changed++;
        }

        return changed;
    }

    private static int RenameInCommand(ViewportRenderCommand command, string oldName, string newName)
    {
        int changed = 0;
        PropertyInfo[] properties = GetResourceNameProperties(command.GetType());
        for (int i = 0; i < properties.Length; i++)
        {
            PropertyInfo property = properties[i];
            object? value = property.GetValue(command);
            switch (value)
            {
                case string text when NameEquals(text, oldName):
                    property.SetValue(command, newName);
                    changed++;
                    break;
                case string[] array:
                    for (int a = 0; a < array.Length; a++)
                    {
                        if (!NameEquals(array[a], oldName))
                            continue;

                        array[a] = newName;
                        changed++;
                    }
                    break;
                case IList list when value is not string:
                    for (int a = 0; a < list.Count; a++)
                    {
                        if (list[a] is not string item || !NameEquals(item, oldName))
                            continue;

                        list[a] = newName;
                        changed++;
                    }
                    break;
            }
        }

        return changed;
    }

    private static bool ValueNames(object? value, string name)
    {
        switch (value)
        {
            case string text:
                return NameEquals(text, name);
            case IEnumerable items:
                foreach (object? item in items)
                {
                    if (item is string text && NameEquals(text, name))
                        return true;
                }
                return false;
            default:
                return false;
        }
    }

    private static bool NameEquals(string? value, string name)
        => !string.IsNullOrEmpty(value) && string.Equals(value, name, StringComparison.OrdinalIgnoreCase);
}
