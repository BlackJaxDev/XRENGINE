using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.Editor.Mcp
{
    public sealed partial class EditorMcpActions
    {
        // ═══════════════════════════════════════════════════════════════════
        // Phase 4 — Live Instance Inspection
        // ═══════════════════════════════════════════════════════════════════

        // ───────────────────────────────────────────────────────────────────
        // P4.1 — Generic Object Reflection
        // ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Reads all property values from any <see cref="XRBase"/>-derived instance by GUID.
        /// </summary>
        [XRMcp(Name = "get_object_properties", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Read all property values from any XRBase-derived instance by GUID.")]
        public static Task<McpToolResponse> GetObjectPropertiesAsync(
            McpToolContext context,
            [McpName("object_id"), Description("GUID of the XRBase-derived object to inspect.")]
            string objectId,
            [McpName("include_non_public"), Description("Include non-public instance members.")]
            bool includeNonPublic = false,
            [McpName("max_depth"), Description("Maximum nesting depth for nested object serialization (default 1).")]
            int maxDepth = 1)
        {
            if (!TryResolveXRObject(objectId, out var obj, out var error))
                return Task.FromResult(new McpToolResponse(error!, isError: true));

            var objType = obj!.GetType();
            var flags = BindingFlags.Instance | BindingFlags.Public;
            if (includeNonPublic) flags |= BindingFlags.NonPublic;

            var properties = objType
                .GetProperties(flags)
                .Where(IsInvocableProperty)
                .Select(p =>
                {
                    object? rawValue = null;
                    string? readError = null;
                    if (p.CanRead)
                    {
                        try { rawValue = p.GetValue(obj); }
                        catch (Exception ex) { readError = ex.InnerException?.Message ?? ex.Message; }
                    }

                    return new
                    {
                        name = p.Name,
                        type = FormatTypeName(p.PropertyType),
                        kind = "property",
                        canRead = p.CanRead,
                        canWrite = p.CanWrite,
                        isStatic = p.GetMethod?.IsStatic ?? p.SetMethod?.IsStatic ?? false,
                        value = p.CanRead && readError is null ? SerializeValue(rawValue, 0, maxDepth) : null,
                        valuePreview = p.CanRead && readError is null ? rawValue?.ToString() : null,
                        readError
                    };
                })
                .ToArray();

            var fields = objType
                .GetFields(flags)
                .Where(f => !f.IsStatic)
                .Select(f =>
                {
                    object? rawValue = null;
                    string? readError = null;
                    try { rawValue = f.GetValue(obj); }
                    catch (Exception ex) { readError = ex.InnerException?.Message ?? ex.Message; }

                    return new
                    {
                        name = f.Name,
                        type = FormatTypeName(f.FieldType),
                        kind = "field",
                        isReadOnly = f.IsInitOnly,
                        value = readError is null ? SerializeValue(rawValue, 0, maxDepth) : null,
                        valuePreview = readError is null ? rawValue?.ToString() : null,
                        readError
                    };
                })
                .ToArray();

            return Task.FromResult(new McpToolResponse(
                $"Retrieved {properties.Length} properties and {fields.Length} fields from '{objType.Name}' ({objectId}).",
                new
                {
                    objectId = obj.ID,
                    objectName = obj.Name,
                    objectType = objType.FullName ?? objType.Name,
                    properties,
                    fields
                }));
        }

        /// <summary>
        /// Sets a property on any <see cref="XRBase"/> instance by GUID using the <c>SetField</c> pipeline.
        /// </summary>
        [XRMcp(Name = "set_object_property", Permission = McpPermissionLevel.Mutate, PermissionReason = "Modifies a live object property.")]
        [Description("Set a property on any XRBase instance by GUID (uses SetField pipeline).")]
        public static Task<McpToolResponse> SetObjectPropertyAsync(
            McpToolContext context,
            [McpName("object_id"), Description("GUID of the XRBase-derived object.")]
            string objectId,
            [McpName("property_name"), Description("Property name to set (case-insensitive).")]
            string propertyName,
            [McpName("value"), Description("JSON value to assign. For simple types use the literal (e.g. 42, true, \"hello\"). For colors pass an object like {\"R\":1,\"G\":0,\"B\":0,\"A\":1} or a hex string \"#FF0000\". For vectors pass {\"X\":1,\"Y\":2,\"Z\":3}. For enums pass the string name.")]
            object value)
        {
            if (!TryResolveXRObject(objectId, out var obj, out var error))
                return Task.FromResult(new McpToolResponse(error!, isError: true));

            var objType = obj!.GetType();
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;

            // Try property first
            var property = objType.GetProperty(propertyName, flags);
            if (property is not null && property.CanWrite)
            {
                if (!McpToolRegistry.TryConvertValue(value, property.PropertyType, out var converted, out var convError))
                    return Task.FromResult(new McpToolResponse(convError ?? $"Unable to convert value for '{property.Name}'.", isError: true));

                using var _ = Undo.TrackChange($"MCP Set {property.Name}", obj);
                property.SetValue(obj, converted);
                return Task.FromResult(new McpToolResponse(
                    $"Set property '{property.Name}' on '{objType.Name}' ({objectId}).",
                    new
                    {
                        objectId = obj.ID,
                        objectType = objType.FullName ?? objType.Name,
                        property = property.Name,
                        propertyType = FormatTypeName(property.PropertyType)
                    }));
            }

            return Task.FromResult(new McpToolResponse(
                $"Writable property '{propertyName}' not found on '{objType.Name}'.", isError: true));
        }

        /// <summary>
        /// Invokes a method on an <see cref="XRBase"/> instance or a static method on any type.
        /// </summary>
        [XRMcp(Name = "invoke_method", Permission = McpPermissionLevel.Arbitrary, PermissionReason = "Executes arbitrary code via reflection.")]
        [Description("Invoke a method on an XRBase instance (by GUID) or a static method on any type.")]
        public static Task<McpToolResponse> InvokeMethodAsync(
            McpToolContext context,
            [McpName("method_name"), Description("Name of the method to invoke.")]
            string methodName,
            [McpName("object_id"), Description("GUID of the target XRBase instance (null for static methods).")]
            string? objectId = null,
            [McpName("type_name"), Description("Fully-qualified or short type name (required for static methods).")]
            string? typeName = null,
            [McpName("arguments"), Description("JSON array of positional arguments.")]
            object[]? arguments = null)
        {
            object? target = null;
            Type targetType;

            if (!string.IsNullOrWhiteSpace(objectId))
            {
                // Instance method
                if (!TryResolveXRObject(objectId!, out var obj, out var error))
                    return Task.FromResult(new McpToolResponse(error!, isError: true));

                target = obj;
                targetType = obj!.GetType();
            }
            else if (!string.IsNullOrWhiteSpace(typeName))
            {
                // Static method
                if (!TryResolveAnyType(typeName!, out targetType))
                    return Task.FromResult(new McpToolResponse($"Type '{typeName}' not found.", isError: true));
            }
            else
            {
                return Task.FromResult(new McpToolResponse(
                    "Provide object_id for an instance method or type_name for a static method.", isError: true));
            }

            var bindingFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            bindingFlags |= target is not null ? BindingFlags.Instance : BindingFlags.Static;

            // Find matching methods
            var candidates = targetType.GetMethods(bindingFlags)
                .Where(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (candidates.Length == 0)
                return Task.FromResult(new McpToolResponse(
                    $"Method '{methodName}' not found on '{targetType.Name}'.", isError: true));

            var args = arguments ?? [];

            // Try to find a matching overload
            MethodInfo? bestMatch = null;
            object?[]? convertedArgs = null;

            foreach (var candidate in candidates)
            {
                var parameters = candidate.GetParameters();

                // Skip if too few args for required params
                int requiredCount = parameters.Count(p => !p.IsOptional && !p.HasDefaultValue);
                if (args.Length < requiredCount)
                    continue;
                if (args.Length > parameters.Length)
                    continue;

                // Try converting all provided args
                var tempArgs = new object?[parameters.Length];
                bool allConverted = true;

                for (int i = 0; i < parameters.Length; i++)
                {
                    if (i < args.Length)
                    {
                        if (!McpToolRegistry.TryConvertValue(args[i], parameters[i].ParameterType, out var conv, out _))
                        {
                            allConverted = false;
                            break;
                        }
                        tempArgs[i] = conv;
                    }
                    else if (parameters[i].HasDefaultValue)
                    {
                        tempArgs[i] = parameters[i].DefaultValue;
                    }
                    else
                    {
                        allConverted = false;
                        break;
                    }
                }

                if (allConverted)
                {
                    bestMatch = candidate;
                    convertedArgs = tempArgs;
                    break;
                }
            }

            if (bestMatch is null)
            {
                var signatures = candidates.Select(m =>
                    $"  {m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{FormatTypeName(p.ParameterType)} {p.Name}"))})");
                return Task.FromResult(new McpToolResponse(
                    $"No overload of '{methodName}' on '{targetType.Name}' matches the provided {args.Length} argument(s).\nAvailable overloads:\n{string.Join("\n", signatures)}",
                    isError: true));
            }

            try
            {
                object? result = bestMatch.Invoke(target, convertedArgs);

                // Handle Task/Task<T>
                if (result is Task task)
                {
                    task.GetAwaiter().GetResult(); // synchronous wait inside MCP handler
                    var taskType = task.GetType();
                    if (taskType.IsGenericType && taskType.GetProperty("Result") is PropertyInfo resultProp)
                        result = resultProp.GetValue(task);
                    else
                        result = null; // void Task
                }

                return Task.FromResult(new McpToolResponse(
                    $"Invoked '{bestMatch.Name}' on '{targetType.Name}'.",
                    new
                    {
                        objectId = (target as XRObjectBase)?.ID,
                        objectType = targetType.FullName ?? targetType.Name,
                        methodName = bestMatch.Name,
                        returnType = FormatTypeName(bestMatch.ReturnType),
                        result = SerializeValue(result, 0, 2)
                    }));
            }
            catch (TargetInvocationException ex)
            {
                var inner = ex.InnerException ?? ex;
                return Task.FromResult(new McpToolResponse(
                    $"Method '{bestMatch.Name}' threw: {inner.GetType().Name}: {inner.Message}", isError: true));
            }
            catch (Exception ex)
            {
                return Task.FromResult(new McpToolResponse(
                    $"Error invoking '{bestMatch.Name}': {ex.Message}", isError: true));
            }
        }

        /// <summary>
        /// Evaluates a simple property-chain expression on a scene node or any XRBase object.
        /// For example: <c>Transform.WorldMatrix.Translation.X</c>
        /// </summary>
        [XRMcp(Name = "evaluate_expression", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Evaluate a dot-separated property chain expression on an XRBase object (e.g. 'Transform.WorldMatrix.Translation.X').")]
        public static Task<McpToolResponse> EvaluateExpressionAsync(
            McpToolContext context,
            [McpName("object_id"), Description("GUID of the XRBase-derived object to evaluate on (or scene node ID).")]
            string objectId,
            [McpName("expression"), Description("Dot-separated property chain, e.g. 'Transform.WorldMatrix.Translation.X'.")]
            string expression)
        {
            if (string.IsNullOrWhiteSpace(expression))
                return Task.FromResult(new McpToolResponse("Expression must not be empty.", isError: true));

            if (!TryResolveXRObject(objectId, out var obj, out var error))
                return Task.FromResult(new McpToolResponse(error!, isError: true));

            string[] segments = expression.Split('.');
            object? current = obj;
            Type currentType = obj!.GetType();
            string resolvedPath = obj.GetType().Name;

            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i].Trim();
                if (string.IsNullOrEmpty(segment))
                    return Task.FromResult(new McpToolResponse(
                        $"Empty segment at position {i} in expression '{expression}'.", isError: true));

                if (current is null)
                    return Task.FromResult(new McpToolResponse(
                        $"Null reference at '{resolvedPath}' while evaluating segment '{segment}'.", isError: true));

                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;

                // Try property
                var prop = currentType.GetProperty(segment, flags);
                if (prop is not null && prop.CanRead && prop.GetIndexParameters().Length == 0)
                {
                    try
                    {
                        current = prop.GetValue(current);
                        currentType = prop.PropertyType;
                        resolvedPath += $".{prop.Name}";
                        continue;
                    }
                    catch (Exception ex)
                    {
                        return Task.FromResult(new McpToolResponse(
                            $"Error reading '{resolvedPath}.{prop.Name}': {ex.InnerException?.Message ?? ex.Message}", isError: true));
                    }
                }

                // Try field
                var field = currentType.GetField(segment, flags);
                if (field is not null)
                {
                    try
                    {
                        current = field.GetValue(current);
                        currentType = field.FieldType;
                        resolvedPath += $".{field.Name}";
                        continue;
                    }
                    catch (Exception ex)
                    {
                        return Task.FromResult(new McpToolResponse(
                            $"Error reading '{resolvedPath}.{field.Name}': {ex.InnerException?.Message ?? ex.Message}", isError: true));
                    }
                }

                return Task.FromResult(new McpToolResponse(
                    $"Member '{segment}' not found on type '{FormatTypeName(currentType)}' at '{resolvedPath}'.", isError: true));
            }

            return Task.FromResult(new McpToolResponse(
                $"Evaluated '{expression}' on '{obj.GetType().Name}' ({objectId}).",
                new
                {
                    objectId = obj.ID,
                    objectType = obj.GetType().FullName ?? obj.GetType().Name,
                    expression,
                    resolvedPath,
                    resultType = current is not null ? FormatTypeName(current.GetType()) : (currentType.FullName ?? currentType.Name),
                    value = SerializeValue(current, 0, 2),
                    valuePreview = current?.ToString()
                }));
        }

        // ───────────────────────────────────────────────────────────────────
        // P4.2 — Event & Change Tracking
        // ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Lists events on a component and their current subscriber count.
        /// </summary>
        [XRMcp(Name = "get_component_events", Permission = McpPermissionLevel.ReadOnly)]
        [Description("List events on a component with subscriber counts.")]
        public static Task<McpToolResponse> GetComponentEventsAsync(
            McpToolContext context,
            [McpName("node_id"), Description("Scene node ID that owns the component.")]
            string nodeId,
            [McpName("component_id"), Description("Optional component ID to target.")]
            string? componentId = null,
            [McpName("component_name"), Description("Optional component instance name to target.")]
            string? componentName = null,
            [McpName("component_type"), Description("Optional component type name to target.")]
            string? componentTypeName = null)
        {
            if (string.IsNullOrWhiteSpace(componentId) && string.IsNullOrWhiteSpace(componentName) && string.IsNullOrWhiteSpace(componentTypeName))
                return Task.FromResult(new McpToolResponse("Provide component_id, component_name, or component_type.", isError: true));

            if (!TryGetNodeById(context.WorldInstance, nodeId, out var node, out var nodeError))
                return Task.FromResult(new McpToolResponse(nodeError ?? "Scene node not found.", isError: true));

            XRComponent? component = FindComponent(node!, componentId, componentName, componentTypeName, out var compError);
            if (component is null)
                return Task.FromResult(new McpToolResponse(compError ?? "Component not found on the specified node.", isError: true));

            var componentType = component.GetType();
            var events = new List<object>();

            // Collect C# events (declared via 'event' keyword)
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var eventInfo in componentType.GetEvents(flags))
            {
                int subscriberCount = GetEventSubscriberCount(component, componentType, eventInfo);
                events.Add(new
                {
                    name = eventInfo.Name,
                    kind = "clr_event",
                    handlerType = FormatTypeName(eventInfo.EventHandlerType!),
                    subscriberCount,
                    declaringType = eventInfo.DeclaringType != componentType
                        ? FormatTypeName(eventInfo.DeclaringType!)
                        : null
                });
            }

            // Collect XREvent / XREvent<T> fields (the engine's custom event system)
            foreach (var field in componentType.GetFields(flags))
            {
                if (!typeof(IEnumerable).IsAssignableFrom(field.FieldType))
                    continue;
                if (!IsXREventType(field.FieldType))
                    continue;

                var eventObj = field.GetValue(component);
                int count = 0;
                if (eventObj is not null)
                {
                    var countProp = field.FieldType.GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
                    if (countProp is not null)
                    {
                        try { count = (int)(countProp.GetValue(eventObj) ?? 0); }
                        catch { /* non-critical */ }
                    }
                }

                events.Add(new
                {
                    name = field.Name,
                    kind = "xr_event",
                    handlerType = FormatTypeName(field.FieldType),
                    subscriberCount = count,
                    declaringType = field.DeclaringType != componentType
                        ? FormatTypeName(field.DeclaringType!)
                        : null
                });
            }

            return Task.FromResult(new McpToolResponse(
                $"Found {events.Count} event(s) on '{componentType.Name}'.",
                new
                {
                    componentId = component.ID,
                    componentType = componentType.FullName ?? componentType.Name,
                    nodeId = node!.ID,
                    events
                }));
        }

        /// <summary>
        /// Subscribes to change notifications for a property on an XRBase object.
        /// Stores change records that can be queried via polling with the same watch ID.
        /// </summary>
        [XRMcp(Name = "watch_property", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Watch a property for changes on an XRBase object. Returns a watch_id for polling, or retrieves accumulated changes when watch_id is provided.")]
        public static Task<McpToolResponse> WatchPropertyAsync(
            McpToolContext context,
            [McpName("object_id"), Description("GUID of the XRBase-derived object.")]
            string objectId,
            [McpName("property_name"), Description("Property name to monitor for changes.")]
            string propertyName,
            [McpName("watch_id"), Description("If provided, polls for accumulated changes and returns them. If omitted, creates a new watch.")]
            string? watchId = null,
            [McpName("stop"), Description("If true and watch_id is provided, stops the watch and removes it.")]
            bool stop = false)
        {
            // Poll / stop an existing watch
            if (!string.IsNullOrWhiteSpace(watchId))
            {
                if (!ActiveWatches.TryGetValue(watchId!, out var existingWatch))
                    return Task.FromResult(new McpToolResponse($"Watch '{watchId}' not found.", isError: true));

                if (stop)
                {
                    existingWatch.Dispose();
                    ActiveWatches.TryRemove(watchId!, out _);
                    return Task.FromResult(new McpToolResponse($"Stopped watch '{watchId}'."));
                }

                // Drain accumulated changes
                var changes = existingWatch.DrainChanges();
                return Task.FromResult(new McpToolResponse(
                    $"Polled watch '{watchId}': {changes.Count} change(s).",
                    new
                    {
                        watchId,
                        objectId = existingWatch.ObjectId,
                        propertyName = existingWatch.PropertyName,
                        changeCount = changes.Count,
                        changes
                    }));
            }

            // Create a new watch
            if (!TryResolveXRObject(objectId, out var obj, out var error))
                return Task.FromResult(new McpToolResponse(error!, isError: true));

            // Verify property exists
            var objType = obj!.GetType();
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            var prop = objType.GetProperty(propertyName, flags);
            if (prop is null || !prop.CanRead)
            {
                var field = objType.GetField(propertyName, flags);
                if (field is null)
                {
                    return Task.FromResult(new McpToolResponse(
                        $"Property or field '{propertyName}' not found on '{objType.Name}'.", isError: true));
                }
            }

            string newWatchId = Guid.NewGuid().ToString("N")[..12];
            var watch = new PropertyWatch(obj, propertyName, newWatchId);
            ActiveWatches[newWatchId] = watch;

            return Task.FromResult(new McpToolResponse(
                $"Started watching '{propertyName}' on '{objType.Name}' ({objectId}).",
                new
                {
                    watchId = newWatchId,
                    objectId = obj.ID,
                    objectType = objType.FullName ?? objType.Name,
                    propertyName
                }));
        }

        // ───────────────────────────────────────────────────────────────────
        // Helpers — Live Inspection
        // ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Resolves any XRObjectBase from the global ObjectsCache by GUID string.
        /// </summary>
        private static bool TryResolveXRObject(string objectId, out XRObjectBase? obj, out string? error)
        {
            obj = null;
            error = null;

            if (string.IsNullOrWhiteSpace(objectId))
            {
                error = "object_id must not be empty.";
                return false;
            }

            if (!Guid.TryParse(objectId, out var guid))
            {
                error = $"Invalid object_id '{objectId}'.";
                return false;
            }

            if (!XRObjectBase.ObjectsCache.TryGetValue(guid, out var resolved))
            {
                error = $"Object '{objectId}' not found in the runtime cache.";
                return false;
            }

            if (resolved.IsDestroyed)
            {
                error = $"Object '{objectId}' has been destroyed.";
                return false;
            }

            obj = resolved;
            return true;
        }

        /// <summary>
        /// Recursively serializes a value to an MCP-safe representation with depth limiting.
        /// </summary>
        private static object? SerializeValue(object? value, int currentDepth, int maxDepth)
        {
            if (value is null)
                return null;

            Type type = value.GetType();

            // Primitives, strings, enums, common value types
            if (type.IsPrimitive || value is string || value is decimal || value is Guid || value is DateTime || type.IsEnum)
                return value;

            // IntPtr / UIntPtr / nint / nuint — not supported by System.Text.Json,
            // convert to a serializable representation.
            if (value is IntPtr ip)
                return ip.ToString();
            if (value is UIntPtr uip)
                return uip.ToString();

            // Pointer types and ByRef-like types cannot be serialized.
            if (type.IsPointer || type.IsByRef || type.IsByRefLike)
                return $"<{FormatTypeName(type)}>";

            // XRObjectBase — return a summary reference
            if (value is XRObjectBase xrObj)
            {
                if (currentDepth >= maxDepth)
                {
                    return new
                    {
                        objectId = xrObj.ID,
                        objectName = xrObj.Name,
                        objectType = xrObj.GetType().FullName ?? xrObj.GetType().Name
                    };
                }

                // One level deeper: include a few key properties
                var props = xrObj.GetType()
                    .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Where(IsInvocableProperty)
                    .Take(20)
                    .Select(p =>
                    {
                        object? v = null;
                        try { v = p.GetValue(xrObj); }
                        catch { /* skip */ }
                        return new { name = p.Name, value = SerializeValue(v, currentDepth + 1, maxDepth) };
                    })
                    .ToArray();

                return new
                {
                    objectId = xrObj.ID,
                    objectName = xrObj.Name,
                    objectType = xrObj.GetType().FullName ?? xrObj.GetType().Name,
                    properties = props
                };
            }

            // Collections
            if (value is IEnumerable enumerable && value is not string)
            {
                if (currentDepth >= maxDepth)
                {
                    int count = 0;
                    foreach (var _ in enumerable) count++;
                    return new { type = FormatTypeName(type), count, truncated = true };
                }

                var items = new List<object?>();
                int total = 0;
                foreach (object? item in enumerable)
                {
                    total++;
                    if (items.Count < 16)
                        items.Add(SerializeValue(item, currentDepth + 1, maxDepth));
                }

                return new { type = FormatTypeName(type), count = total, items, truncated = total > items.Count };
            }

            // Depth limit reached for complex types
            if (currentDepth >= maxDepth)
                return value.ToString();

            // Structs / other objects — serialize top-level properties.
            // Filter out ref-returning, ByRefLike, and otherwise non-invocable properties
            // to avoid NotSupportedException from the reflection invoke path.
            var structProps = type
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(IsInvocableProperty)
                .Take(20)
                .ToDictionary(
                    p => p.Name,
                    p =>
                    {
                        try { return SerializeValue(p.GetValue(value), currentDepth + 1, maxDepth); }
                        catch { return null; }
                    });

            return new { type = FormatTypeName(type), properties = structProps };
        }

        /// <summary>
        /// Returns true if a property can be safely invoked via <see cref="PropertyInfo.GetValue(object)"/>
        /// without throwing <see cref="NotSupportedException"/> (e.g. ref-returning or ByRefLike properties).
        /// </summary>
        private static bool IsInvocableProperty(PropertyInfo p)
        {
            if (p.GetIndexParameters().Length != 0 || !p.CanRead)
                return false;
            if (p.PropertyType.IsByRef || p.PropertyType.IsByRefLike || p.PropertyType.IsPointer)
                return false;
            var getter = p.GetGetMethod(nonPublic: true);
            if (getter is null)
                return false;
            var retType = getter.ReturnType;
            return !retType.IsByRef && !retType.IsByRefLike && !retType.IsPointer;
        }

        /// <summary>
        /// Gets the subscriber count for a CLR event by accessing its backing delegate field.
        /// </summary>
        private static int GetEventSubscriberCount(object target, Type type, EventInfo eventInfo)
        {
            // Try to find the backing field for the event
            var backingField = type.GetField(eventInfo.Name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                ?? type.GetField("_" + char.ToLowerInvariant(eventInfo.Name[0]) + eventInfo.Name[1..], BindingFlags.Instance | BindingFlags.NonPublic)
                ?? type.GetField(char.ToLowerInvariant(eventInfo.Name[0]) + eventInfo.Name[1..], BindingFlags.Instance | BindingFlags.NonPublic);

            if (backingField is null)
            {
                // Walk up the type hierarchy for auto-implemented events
                Type? current = type.BaseType;
                while (current is not null && backingField is null)
                {
                    backingField = current.GetField(eventInfo.Name,
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    current = current.BaseType;
                }
            }

            if (backingField is null)
                return -1; // cannot determine

            var fieldValue = backingField.GetValue(target);
            if (fieldValue is null)
                return 0;

            if (fieldValue is Delegate del)
                return del.GetInvocationList().Length;

            // XREventBase<T> has a Count property
            var countProp = fieldValue.GetType().GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
            if (countProp is not null)
            {
                try { return (int)(countProp.GetValue(fieldValue) ?? 0); }
                catch { return -1; }
            }

            return -1;
        }

        /// <summary>
        /// Checks whether a type is or derives from XREventBase.
        /// </summary>
        private static bool IsXREventType(Type type)
        {
            Type? current = type;
            while (current is not null)
            {
                if (current.IsGenericType)
                {
                    string name = current.GetGenericTypeDefinition().Name;
                    if (name.StartsWith("XREventBase", StringComparison.Ordinal) ||
                        name.StartsWith("XREvent", StringComparison.Ordinal) ||
                        name.StartsWith("XRBoolEvent", StringComparison.Ordinal))
                        return true;
                }
                else
                {
                    string name = current.Name;
                    if (name == "XREvent" || name == "XRBoolEvent")
                        return true;
                }
                current = current.BaseType;
            }
            return false;
        }

        // ───────────────────────────────────────────────────────────────────
        // Property Watch Infrastructure
        // ───────────────────────────────────────────────────────────────────

        /// <summary>
        /// Active property watches keyed by watch ID.
        /// </summary>
        private static readonly ConcurrentDictionary<string, PropertyWatch> ActiveWatches = new();

        /// <summary>
        /// Tracks property changes on an XRBase instance via the PropertyChanged event.
        /// </summary>
        private sealed class PropertyWatch : IDisposable
        {
            private readonly XRObjectBase _target;
            private readonly string _propertyName;
            private readonly string _watchId;
            private readonly ConcurrentQueue<object> _changes = new();
            private bool _disposed;

            public string ObjectId => _target.ID.ToString();
            public string PropertyName => _propertyName;

            public PropertyWatch(XRObjectBase target, string propertyName, string watchId)
            {
                _target = target;
                _propertyName = propertyName;
                _watchId = watchId;

                if (target is XRBase xrBase)
                    xrBase.PropertyChanged += OnPropertyChanged;
            }

            private void OnPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
            {
                if (!string.Equals(e.PropertyName, _propertyName, StringComparison.OrdinalIgnoreCase))
                    return;

                _changes.Enqueue(new
                {
                    timestamp = DateTime.UtcNow.ToString("o"),
                    propertyName = e.PropertyName,
                    oldValue = e.PreviousValue?.ToString(),
                    newValue = e.NewValue?.ToString()
                });
            }

            public List<object> DrainChanges()
            {
                var result = new List<object>();
                while (_changes.TryDequeue(out var change))
                    result.Add(change);
                return result;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;

                if (_target is XRBase xrBase)
                    xrBase.PropertyChanged -= OnPropertyChanged;
            }
        }
    }
}
