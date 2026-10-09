using System.Collections;
using System.Reflection;
using XREngine.Data.Core;

namespace XREngine.Editor.Mcp;

public sealed partial class EditorMcpActions
{
    internal static bool TryResolveInspectionTarget(RuntimeWorld? world, string objectId, string? path, out XRBase? target, out string? error)
    {
        target = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            bool found = TryResolveXRObject(world, objectId, out var root, out error);
            target = root;
            return found;
        }
        if (world is null || !Guid.TryParse(objectId, out Guid id) || FindObjectInWorld(world, id) is not { IsDestroyed: false } liveRoot)
        {
            error = "object_path requires an existing node, transform or component in the active world.";
            return false;
        }
        return TryResolveOwnedObject(liveRoot, path, out target, out error);
    }

    /// <summary>Resolves an explicitly selected owned instance without revisiting global ID caches.</summary>
    internal static bool TryResolveOwnedObject(XRBase root, string? path, out XRBase? target, out string? error)
    {
        target = null;
        if (!TryResolveOwnedReference(root, path, out object? reference, out error))
            return false;
        target = reference as XRBase;
        if (target is not null)
            return true;
        error = $"object_path '{path}' must end at a live XRBase instance.";
        return false;
    }

    /// <summary>Reads an existing reference through a live engine object's member path.</summary>
    private static bool TryResolveOwnedReference(XRBase root, string? path, out object? target, out string? error)
    {
        target = root;
        error = null;
        if (string.IsNullOrWhiteSpace(path))
            return true;
        if (path.Length > 1024)
        {
            error = "object_path exceeds 1024 characters.";
            return false;
        }

        string[] segments = path.Split('.');
        if (segments.Length > 16)
        {
            error = "object_path exceeds 16 member steps.";
            return false;
        }

        object? current = root;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
        foreach (string rawSegment in segments)
        {
            string segment = rawSegment.Trim();
            if (current is null || current is XRObjectBase { IsDestroyed: true })
            {
                error = $"object_path '{path}' encounters a null or destroyed instance before '{segment}'.";
                target = null;
                return false;
            }

            int bracket = segment.IndexOf('[');
            string memberName = bracket < 0 ? segment : segment[..bracket];
            int index = -1;
            if (memberName.Length == 0 || (bracket >= 0 &&
                (!segment.EndsWith(']') || !int.TryParse(segment.AsSpan(bracket + 1, segment.Length - bracket - 2), out index) || index < 0)))
            {
                error = $"Invalid object_path member '{segment}'. Use member names and nonnegative list indices.";
                target = null;
                return false;
            }

            try
            {
                Type type = current.GetType();
                PropertyInfo? property = type.GetProperty(memberName, flags);
                if (property is { CanRead: true } && property.GetIndexParameters().Length == 0)
                    current = property.GetValue(current);
                else if (type.GetField(memberName, flags) is { } field)
                    current = field.GetValue(current);
                else
                    throw new InvalidOperationException($"Instance member '{memberName}' was not found on '{type.Name}'.");

                if (bracket >= 0)
                {
                    if (current is not IList list || index >= list.Count)
                        throw new InvalidOperationException($"Member '{memberName}' has no list item at index {index}.");
                    current = list[index];
                }
            }
            catch (Exception ex)
            {
                target = null;
                error = $"Unable to resolve object_path '{path}': {ex.InnerException?.Message ?? ex.Message}";
                return false;
            }
        }

        target = current;
        if (target is null || target.GetType().IsValueType || target is XRObjectBase { IsDestroyed: true })
        {
            target = null;
            error = $"object_path '{path}' must end at a live reference instance.";
            return false;
        }
        return true;
    }
}
