using System.Linq.Expressions;
using System.Reflection;
using XREngine.Core.Files;

namespace XREngine.Data.Core;

/// <summary>
/// Stores process-local setting values as replayable property-path operations.
/// </summary>
/// <remarks>
/// The overlay never mutates or owns a persisted settings object. Callers compose a
/// detached effective settings object from the current persistent state and then
/// call <see cref="Apply{TSettings}(TSettings)"/> before publishing that object.
/// </remarks>
public sealed class SessionSettingsOverlay
{
    private readonly Lock _sync = new();
    private readonly Dictionary<Type, Dictionary<string, (long Sequence, Action<object> Apply)>> _operations = [];
    private long _nextSequence;

    /// <summary>
    /// Sets a process-local value and rolls the operation back if publishing the
    /// updated effective settings root fails.
    /// </summary>
    public void SetAndPublish<TSettings, TValue>(
        Expression<Func<TSettings, TValue>> propertySelector,
        TValue value,
        Action publish)
        where TSettings : class
    {
        ArgumentNullException.ThrowIfNull(propertySelector);
        ArgumentNullException.ThrowIfNull(publish);

        PropertyInfo[] propertyPath = ResolvePropertyPath(propertySelector);
        StoreOperation(typeof(TSettings), propertyPath, value, publish);
    }

    /// <summary>
    /// Sets a process-local value by dotted path and rolls the operation back if
    /// publishing the updated effective settings root fails.
    /// </summary>
    public string SetAndPublish<TSettings>(string propertyPath, object? value, Action publish)
        where TSettings : class
    {
        ArgumentNullException.ThrowIfNull(publish);

        PropertyInfo[] properties = ResolvePropertyPath(typeof(TSettings), propertyPath);
        return StoreOperation(typeof(TSettings), properties, value, publish);
    }

    private string StoreOperation(
        Type settingsType,
        PropertyInfo[] propertyPath,
        object? value,
        Action? publish = null)
    {
        Type valueType = propertyPath[^1].PropertyType;
        if (value is null && valueType.IsValueType && Nullable.GetUnderlyingType(valueType) is null)
            throw new ArgumentException($"Session setting '{propertyPath[^1].Name}' does not accept null.", nameof(value));

        if (value is not null && !valueType.IsInstanceOfType(value))
        {
            throw new ArgumentException(
                $"Session setting '{propertyPath[^1].Name}' requires '{valueType.FullName}', not '{value.GetType().FullName}'.",
                nameof(value));
        }

        string path = string.Join('.', propertyPath.Select(static property => property.Name));
        object? snapshot = XRBase.ClonePropertyValue(value, valueType);

        Action<object> operation = target => ApplyValue(
            target,
            propertyPath,
            pathIndex: 0,
            XRBase.ClonePropertyValue(snapshot, valueType));

        long sequence;
        bool hadPreviousOperation;
        (long Sequence, Action<object> Apply) previousOperation = default;
        lock (_sync)
        {
            if (!_operations.TryGetValue(
                settingsType,
                out Dictionary<string, (long Sequence, Action<object> Apply)>? settingsOperations))
            {
                settingsOperations = new Dictionary<string, (long, Action<object>)>(StringComparer.OrdinalIgnoreCase);
                _operations.Add(settingsType, settingsOperations);
            }

            hadPreviousOperation = settingsOperations.TryGetValue(path, out previousOperation);
            sequence = ++_nextSequence;
            settingsOperations[path] = (sequence, operation);
        }

        if (publish is not null)
            PublishOrRollback(settingsType, path, sequence, hadPreviousOperation, previousOperation, publish);

        return path;
    }

    private void PublishOrRollback(
        Type settingsType,
        string propertyPath,
        long sequence,
        bool hadPreviousOperation,
        (long Sequence, Action<object> Apply) previousOperation,
        Action publish)
    {
        try
        {
            publish();
        }
        catch
        {
            bool rolledBack = false;
            lock (_sync)
            {
                if (_operations.TryGetValue(
                        settingsType,
                        out Dictionary<string, (long Sequence, Action<object> Apply)>? settingsOperations) &&
                    settingsOperations.TryGetValue(propertyPath, out var currentOperation) &&
                    currentOperation.Sequence == sequence)
                {
                    if (hadPreviousOperation)
                    {
                        settingsOperations[propertyPath] = previousOperation;
                    }
                    else
                    {
                        settingsOperations.Remove(propertyPath);
                        if (settingsOperations.Count == 0)
                            _operations.Remove(settingsType);
                    }

                    rolledBack = true;
                }
            }

            if (rolledBack)
            {
                try
                {
                    publish();
                }
                catch
                {
                    // Preserve the original publish exception. Previously committed
                    // operations were already valid; this is only best-effort state repair.
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Removes the process-local value for a property.
    /// </summary>
    public bool Clear<TSettings, TValue>(Expression<Func<TSettings, TValue>> propertySelector)
        where TSettings : class
    {
        ArgumentNullException.ThrowIfNull(propertySelector);

        string path = GetPropertyPath(propertySelector);
        lock (_sync)
        {
            if (!_operations.TryGetValue(
                    typeof(TSettings),
                    out Dictionary<string, (long Sequence, Action<object> Apply)>? settingsOperations) ||
                !settingsOperations.Remove(path))
            {
                return false;
            }

            if (settingsOperations.Count == 0)
                _operations.Remove(typeof(TSettings));

            return true;
        }
    }

    /// <summary>
    /// Removes a process-local value using a case-insensitive dotted property path.
    /// </summary>
    public bool Clear<TSettings>(string propertyPath)
        where TSettings : class
    {
        string path = NormalizePropertyPath(propertyPath);
        lock (_sync)
        {
            if (!_operations.TryGetValue(
                    typeof(TSettings),
                    out Dictionary<string, (long Sequence, Action<object> Apply)>? settingsOperations) ||
                !settingsOperations.Remove(path))
            {
                return false;
            }

            if (settingsOperations.Count == 0)
                _operations.Remove(typeof(TSettings));

            return true;
        }
    }

    /// <summary>
    /// Removes every process-local value for a settings root type.
    /// </summary>
    public bool Clear<TSettings>()
        where TSettings : class
    {
        lock (_sync)
            return _operations.Remove(typeof(TSettings));
    }

    /// <summary>
    /// Removes all process-local setting values and returns the affected root types.
    /// </summary>
    public Type[] ClearAll()
    {
        lock (_sync)
        {
            Type[] affectedTypes = [.. _operations.Keys];
            _operations.Clear();
            return affectedTypes;
        }
    }

    /// <summary>
    /// Returns whether the settings root has at least one process-local value.
    /// </summary>
    public bool HasAny<TSettings>()
        where TSettings : class
    {
        lock (_sync)
            return _operations.TryGetValue(
                    typeof(TSettings),
                    out Dictionary<string, (long Sequence, Action<object> Apply)>? operations) &&
                operations.Count > 0;
    }

    /// <summary>
    /// Returns whether an exact property path has a process-local value.
    /// </summary>
    public bool Contains<TSettings>(string propertyPath)
        where TSettings : class
    {
        if (string.IsNullOrWhiteSpace(propertyPath))
            return false;

        string path = NormalizePropertyPath(propertyPath);
        lock (_sync)
            return _operations.TryGetValue(
                    typeof(TSettings),
                    out Dictionary<string, (long Sequence, Action<object> Apply)>? operations) &&
                operations.ContainsKey(path);
    }

    /// <summary>
    /// Returns whether a property path or one of its descendants has a process-local value.
    /// </summary>
    public bool ContainsAtOrBelow<TSettings>(string propertyPath)
        where TSettings : class
    {
        if (string.IsNullOrWhiteSpace(propertyPath))
            return false;

        string path = NormalizePropertyPath(propertyPath);
        string descendantPrefix = path + '.';
        lock (_sync)
        {
            if (!_operations.TryGetValue(
                typeof(TSettings),
                out Dictionary<string, (long Sequence, Action<object> Apply)>? operations))
            {
                return false;
            }

            foreach (string candidate in operations.Keys)
            {
                if (string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase) ||
                    candidate.StartsWith(descendantPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Applies all current values for the supplied settings root.
    /// </summary>
    public void Apply<TSettings>(TSettings target)
        where TSettings : class
    {
        ArgumentNullException.ThrowIfNull(target);

        Action<object>[] operations;
        lock (_sync)
        {
            if (!_operations.TryGetValue(
                    typeof(TSettings),
                    out Dictionary<string, (long Sequence, Action<object> Apply)>? settingsOperations) ||
                settingsOperations.Count == 0)
            {
                return;
            }

            operations =
            [
                .. settingsOperations.Values
                    .OrderBy(static operation => operation.Sequence)
                    .Select(static operation => operation.Apply)
            ];
        }

        foreach (Action<object> operation in operations)
            operation(target);
    }

    /// <summary>
    /// Returns the normalized dotted path represented by a property selector.
    /// </summary>
    public static string GetPropertyPath<TSettings, TValue>(Expression<Func<TSettings, TValue>> propertySelector)
        where TSettings : class
        => string.Join('.', ResolvePropertyPath(propertySelector).Select(static property => property.Name));

    private static PropertyInfo[] ResolvePropertyPath<TSettings, TValue>(Expression<Func<TSettings, TValue>> propertySelector)
        where TSettings : class
    {
        Expression? expression = propertySelector.Body;
        if (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } conversion)
            expression = conversion.Operand;

        var properties = new Stack<PropertyInfo>();
        while (expression is MemberExpression memberExpression)
        {
            if (memberExpression.Member is not PropertyInfo property || property.GetIndexParameters().Length != 0)
                throw new ArgumentException("Session setting selectors must contain only non-indexed properties.", nameof(propertySelector));

            properties.Push(property);
            expression = memberExpression.Expression;
        }

        if (expression != propertySelector.Parameters[0] || properties.Count == 0)
            throw new ArgumentException("Session setting selectors must be rooted at the settings parameter.", nameof(propertySelector));

        PropertyInfo[] result = [.. properties];
        ValidateWritablePath(result, nameof(propertySelector));
        ValidateSettingsPath(result, nameof(propertySelector));

        return result;
    }

    private static PropertyInfo[] ResolvePropertyPath(Type rootType, string propertyPath)
    {
        if (string.IsNullOrWhiteSpace(propertyPath))
            throw new ArgumentException("Session setting property paths cannot be empty.", nameof(propertyPath));

        string[] segments = propertyPath.Split(
            '.',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
            throw new ArgumentException("Session setting property paths cannot be empty.", nameof(propertyPath));

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase;
        var properties = new PropertyInfo[segments.Length];
        Type ownerType = rootType;
        for (int i = 0; i < segments.Length; i++)
        {
            PropertyInfo? property = ownerType.GetProperty(segments[i], flags);
            if (property is null || property.GetIndexParameters().Length != 0 || !property.CanRead)
            {
                throw new ArgumentException(
                    $"Readable property '{segments[i]}' was not found on '{ownerType.FullName}'.",
                    nameof(propertyPath));
            }

            properties[i] = property;
            ownerType = property.PropertyType;
        }

        ValidateWritablePath(properties, nameof(propertyPath));
        ValidateSettingsPath(properties, nameof(propertyPath));

        return properties;
    }

    private static void ValidateSettingsPath(PropertyInfo[] properties, string argumentName)
    {
        foreach (PropertyInfo property in properties)
        {
            if (property.DeclaringType is not Type declaringType ||
                declaringType != typeof(XRAsset) && declaringType != typeof(XRObjectBase))
            {
                continue;
            }

            throw new ArgumentException(
                $"'{property.Name}' is asset infrastructure, not a session setting.",
                argumentName);
        }
    }

    private static void ValidateWritablePath(PropertyInfo[] properties, string argumentName)
    {
        PropertyInfo leaf = properties[^1];
        if (!leaf.CanWrite || leaf.SetMethod?.IsPublic != true)
        {
            throw new ArgumentException(
                $"Session setting property '{leaf.Name}' is not publicly writable.",
                argumentName);
        }

        for (int i = 0; i < properties.Length - 1; i++)
        {
            PropertyInfo property = properties[i];
            if (property.PropertyType.IsValueType && (!property.CanWrite || property.SetMethod?.IsPublic != true))
            {
                throw new ArgumentException(
                    $"Value-type path segment '{property.Name}' must be publicly writable.",
                    argumentName);
            }
        }
    }

    private static void ApplyValue(
        object owner,
        PropertyInfo[] propertyPath,
        int pathIndex,
        object? value)
    {
        PropertyInfo property = propertyPath[pathIndex];
        if (pathIndex == propertyPath.Length - 1)
        {
            property.SetValue(owner, value);
            return;
        }

        object? child = property.GetValue(owner);
        if (child is null)
        {
            if (!property.CanWrite || property.SetMethod?.IsPublic != true)
            {
                throw new InvalidOperationException(
                    $"Cannot create null session setting path segment '{property.Name}' because it is not publicly writable.");
            }

            Type childType = property.PropertyType;
            if (childType.IsAbstract || childType.IsInterface)
            {
                throw new InvalidOperationException(
                    $"Cannot create null session setting path segment '{property.Name}' of type '{childType.FullName}'.");
            }

            child = Activator.CreateInstance(childType) ??
                throw new InvalidOperationException(
                    $"Cannot create null session setting path segment '{property.Name}' of type '{childType.FullName}'.");
            property.SetValue(owner, child);
        }

        ApplyValue(child, propertyPath, pathIndex + 1, value);

        if (property.PropertyType.IsValueType)
            property.SetValue(owner, child);
    }

    private static string NormalizePropertyPath(string propertyPath)
    {
        if (string.IsNullOrWhiteSpace(propertyPath))
            throw new ArgumentException("Session setting property paths cannot be empty.", nameof(propertyPath));

        string[] segments = propertyPath.Split(
            '.',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
            throw new ArgumentException("Session setting property paths cannot be empty.", nameof(propertyPath));

        return string.Join('.', segments);
    }
}
