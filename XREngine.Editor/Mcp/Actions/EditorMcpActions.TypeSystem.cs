using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using XREngine.Data.Core;

namespace XREngine.Editor.Mcp
{
    public sealed partial class EditorMcpActions
    {
        // ═══════════════════════════════════════════════════════════════════
        // P1.1 — Core Type Inspection
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Gets full type metadata serialized as JSON for any type in any loaded assembly.
        /// </summary>
        [XRMcp(Name = "get_type_info", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Get full type metadata (name, namespace, base type, interfaces, flags) for any loaded type.")]
        public static Task<McpToolResponse> GetTypeInfoAsync(
            McpToolContext context,
            [McpName("type_name"), Description("Short or fully-qualified type name to look up.")]
            string typeName,
            [McpName("include_members"), Description("Include public properties, fields, and methods in the result.")]
            bool includeMembers = false)
        {
            if (!TryResolveAnyType(typeName, out var type))
                return Task.FromResult(new McpToolResponse($"Type '{typeName}' not found in any loaded assembly.", isError: true));

            var info = BuildTypeInfo(type, includeMembers);
            return Task.FromResult(new McpToolResponse($"Retrieved type info for '{type.FullName}'.", info));
        }

        /// <summary>
        /// Gets public properties, fields, methods, events, and constructors from any type.
        /// </summary>
        [XRMcp(Name = "get_type_members", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Get properties, fields, methods, events, and constructors from any loaded type.")]
        public static Task<McpToolResponse> GetTypeMembersAsync(
            McpToolContext context,
            [McpName("type_name"), Description("Short or fully-qualified type name.")]
            string typeName,
            [McpName("include_properties"), Description("Include properties.")]
            bool includeProperties = true,
            [McpName("include_fields"), Description("Include fields.")]
            bool includeFields = true,
            [McpName("include_methods"), Description("Include methods.")]
            bool includeMethods = true,
            [McpName("include_events"), Description("Include events.")]
            bool includeEvents = false,
            [McpName("include_constructors"), Description("Include constructors.")]
            bool includeConstructors = false,
            [McpName("include_non_public"), Description("Include non-public members.")]
            bool includeNonPublic = false,
            [McpName("include_static"), Description("Include static members.")]
            bool includeStatic = false,
            [McpName("include_inherited"), Description("Include inherited members.")]
            bool includeInherited = false)
        {
            if (!TryResolveAnyType(typeName, out var type))
                return Task.FromResult(new McpToolResponse($"Type '{typeName}' not found.", isError: true));

            var bindingFlags = BindingFlags.Instance | BindingFlags.Public;
            if (includeNonPublic) bindingFlags |= BindingFlags.NonPublic;
            if (includeStatic) bindingFlags |= BindingFlags.Static;
            if (!includeInherited) bindingFlags |= BindingFlags.DeclaredOnly;

            object? properties = null;
            object? fields = null;
            object? methods = null;
            object? events = null;
            object? constructors = null;

            if (includeProperties)
            {
                properties = type.GetProperties(bindingFlags)
                    .Where(p => p.GetIndexParameters().Length == 0)
                    .Select(p => new
                    {
                        name = p.Name,
                        type = FormatTypeName(p.PropertyType),
                        canRead = p.CanRead,
                        canWrite = p.CanWrite,
                        isStatic = p.GetMethod?.IsStatic ?? p.SetMethod?.IsStatic ?? false,
                        description = p.GetCustomAttribute<DescriptionAttribute>()?.Description,
                        category = p.GetCustomAttribute<CategoryAttribute>()?.Category,
                        declaringType = p.DeclaringType != type ? FormatTypeName(p.DeclaringType!) : null
                    })
                    .OrderBy(p => p.name)
                    .ToArray();
            }

            if (includeFields)
            {
                fields = type.GetFields(bindingFlags)
                    .Select(f => new
                    {
                        name = f.Name,
                        type = FormatTypeName(f.FieldType),
                        isReadOnly = f.IsInitOnly,
                        isStatic = f.IsStatic,
                        isLiteral = f.IsLiteral,
                        description = f.GetCustomAttribute<DescriptionAttribute>()?.Description,
                        declaringType = f.DeclaringType != type ? FormatTypeName(f.DeclaringType!) : null
                    })
                    .OrderBy(f => f.name)
                    .ToArray();
            }

            if (includeMethods)
            {
                methods = type.GetMethods(bindingFlags)
                    .Where(m => !m.IsSpecialName) // skip property accessors, event add/remove
                    .Select(m => new
                    {
                        name = m.Name,
                        returnType = FormatTypeName(m.ReturnType),
                        parameters = m.GetParameters().Select(p => new
                        {
                            name = p.Name,
                            type = FormatTypeName(p.ParameterType),
                            hasDefault = p.HasDefaultValue,
                            defaultValue = p.HasDefaultValue ? p.DefaultValue?.ToString() : null
                        }).ToArray(),
                        isStatic = m.IsStatic,
                        isVirtual = m.IsVirtual,
                        isAbstract = m.IsAbstract,
                        genericArguments = m.IsGenericMethod
                            ? m.GetGenericArguments().Select(FormatTypeName).ToArray()
                            : null,
                        declaringType = m.DeclaringType != type ? FormatTypeName(m.DeclaringType!) : null
                    })
                    .OrderBy(m => m.name)
                    .ToArray();
            }

            if (includeEvents)
            {
                events = type.GetEvents(bindingFlags)
                    .Select(e => new
                    {
                        name = e.Name,
                        handlerType = FormatTypeName(e.EventHandlerType!),
                        declaringType = e.DeclaringType != type ? FormatTypeName(e.DeclaringType!) : null
                    })
                    .OrderBy(e => e.name)
                    .ToArray();
            }

            if (includeConstructors)
            {
                constructors = type.GetConstructors(bindingFlags)
                    .Select(c => new
                    {
                        parameters = c.GetParameters().Select(p => new
                        {
                            name = p.Name,
                            type = FormatTypeName(p.ParameterType),
                            hasDefault = p.HasDefaultValue,
                            defaultValue = p.HasDefaultValue ? p.DefaultValue?.ToString() : null
                        }).ToArray(),
                        isPublic = c.IsPublic
                    })
                    .ToArray();
            }

            var result = new
            {
                typeName = FormatTypeName(type),
                fullName = type.FullName,
                properties,
                fields,
                methods,
                events,
                constructors
            };

            return Task.FromResult(new McpToolResponse($"Retrieved members for '{type.FullName}'.", result));
        }

        /// <summary>
        /// Gets detailed signature for a specific method, including overload resolution.
        /// </summary>
        [XRMcp(Name = "get_method_info", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Get detailed method signature including parameters, return type, generic constraints, and attributes.")]
        public static Task<McpToolResponse> GetMethodInfoAsync(
            McpToolContext context,
            [McpName("type_name"), Description("Short or fully-qualified type name.")]
            string typeName,
            [McpName("method_name"), Description("Method name to look up.")]
            string methodName,
            [McpName("parameter_types"), Description("Optional array of parameter type names for overload resolution.")]
            string[]? parameterTypes = null)
        {
            if (!TryResolveAnyType(typeName, out var type))
                return Task.FromResult(new McpToolResponse($"Type '{typeName}' not found.", isError: true));

            var allMethods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (allMethods.Length == 0)
                return Task.FromResult(new McpToolResponse($"Method '{methodName}' not found on type '{type.FullName}'.", isError: true));

            MethodInfo? method = null;
            if (parameterTypes is not null && parameterTypes.Length > 0)
            {
                method = allMethods.FirstOrDefault(m =>
                {
                    var parameters = m.GetParameters();
                    if (parameters.Length != parameterTypes.Length) return false;
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        if (!MatchesTypeName(parameters[i].ParameterType, parameterTypes[i]))
                            return false;
                    }
                    return true;
                });

                if (method is null)
                    return Task.FromResult(new McpToolResponse(
                        $"No overload of '{methodName}' matches the specified parameter types.", isError: true));
            }
            else if (allMethods.Length == 1)
            {
                method = allMethods[0];
            }
            else
            {
                // Multiple overloads — return all of them as summaries
                var overloads = allMethods.Select(BuildMethodDetail).ToArray();
                return Task.FromResult(new McpToolResponse(
                    $"Found {overloads.Length} overloads of '{methodName}'. Specify parameter_types to disambiguate.",
                    new { overloadCount = overloads.Length, overloads }));
            }

            var detail = BuildMethodDetail(method);
            return Task.FromResult(new McpToolResponse($"Retrieved method info for '{type.Name}.{method.Name}'.", detail));
        }

        // ═══════════════════════════════════════════════════════════════════
        // P1.2 — Inheritance & Relationship Queries
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Finds all types that derive from a given type across all loaded assemblies.
        /// </summary>
        [XRMcp(Name = "get_derived_types", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Find all types that derive from a given type across all loaded assemblies.")]
        public static Task<McpToolResponse> GetDerivedTypesAsync(
            McpToolContext context,
            [McpName("type_name"), Description("Base type name to search for derived types.")]
            string typeName,
            [McpName("direct_only"), Description("Only return direct subclasses (not transitive).")]
            bool directOnly = false,
            [McpName("include_abstract"), Description("Include abstract types in the result.")]
            bool includeAbstract = true)
        {
            if (!TryResolveAnyType(typeName, out var baseType))
                return Task.FromResult(new McpToolResponse($"Type '{typeName}' not found.", isError: true));

            var derived = new List<object>();
            foreach (var type in EnumerateAllTypes())
            {
                if (type == baseType) continue;
                if (!includeAbstract && type.IsAbstract) continue;

                bool isDerived = directOnly
                    ? type.BaseType == baseType
                    : baseType.IsAssignableFrom(type);

                if (isDerived)
                {
                    derived.Add(new
                    {
                        name = type.Name,
                        fullName = type.FullName,
                        @namespace = type.Namespace,
                        assembly = type.Assembly.GetName().Name,
                        isAbstract = type.IsAbstract,
                        isSealed = type.IsSealed,
                        baseType = type.BaseType is not null ? FormatTypeName(type.BaseType) : null
                    });
                }
            }

            derived.Sort((a, b) => string.Compare(
                ((dynamic)a).fullName ?? "",
                ((dynamic)b).fullName ?? "",
                StringComparison.OrdinalIgnoreCase));

            return Task.FromResult(new McpToolResponse(
                $"Found {derived.Count} derived types for '{baseType.FullName}'.",
                new { baseTypeName = FormatTypeName(baseType), count = derived.Count, types = derived }));
        }

        /// <summary>
        /// Walks the inheritance chain upward from a type, including all implemented interfaces.
        /// </summary>
        [XRMcp(Name = "get_parent_types", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Walk the inheritance chain upward from a type, including interfaces.")]
        public static Task<McpToolResponse> GetParentTypesAsync(
            McpToolContext context,
            [McpName("type_name"), Description("Type name to inspect.")]
            string typeName,
            [McpName("include_interfaces"), Description("Include implemented interfaces.")]
            bool includeInterfaces = true)
        {
            if (!TryResolveAnyType(typeName, out var type))
                return Task.FromResult(new McpToolResponse($"Type '{typeName}' not found.", isError: true));

            // Build the base type chain
            var chain = new List<object>();
            Type? current = type.BaseType;
            while (current is not null)
            {
                chain.Add(new
                {
                    name = current.Name,
                    fullName = current.FullName,
                    @namespace = current.Namespace,
                    assembly = current.Assembly.GetName().Name,
                    isAbstract = current.IsAbstract,
                    isSealed = current.IsSealed
                });
                current = current.BaseType;
            }

            object[]? interfaces = null;
            if (includeInterfaces)
            {
                interfaces = type.GetInterfaces()
                    .Select(iface => new
                    {
                        name = iface.Name,
                        fullName = iface.FullName,
                        @namespace = iface.Namespace,
                        assembly = iface.Assembly.GetName().Name,
                        isGeneric = iface.IsGenericType,
                        genericArguments = iface.IsGenericType
                            ? iface.GetGenericArguments().Select(FormatTypeName).ToArray()
                            : null
                    })
                    .OrderBy(i => i.fullName)
                    .ToArray();
            }

            return Task.FromResult(new McpToolResponse(
                $"Retrieved parent types for '{type.FullName}'.",
                new
                {
                    typeName = FormatTypeName(type),
                    baseTypeChain = chain,
                    interfaces
                }));
        }

        /// <summary>
        /// Returns a full inheritance tree rooted at a type as nested JSON.
        /// </summary>
        [XRMcp(Name = "get_type_hierarchy_tree", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Get a full inheritance tree rooted at a type as nested JSON. Supports up/down/both direction.")]
        public static Task<McpToolResponse> GetTypeHierarchyTreeAsync(
            McpToolContext context,
            [McpName("type_name"), Description("Root type name for the hierarchy tree.")]
            string typeName,
            [McpName("max_depth"), Description("Maximum depth to traverse (default: 10).")]
            int maxDepth = 10,
            [McpName("direction"), Description("Direction: 'up' (ancestors), 'down' (descendants), or 'both'.")]
            string direction = "down")
        {
            if (!TryResolveAnyType(typeName, out var type))
                return Task.FromResult(new McpToolResponse($"Type '{typeName}' not found.", isError: true));

            object? upTree = null;
            object? downTree = null;

            if (direction is "up" or "both")
                upTree = BuildAncestorTree(type);

            if (direction is "down" or "both")
                downTree = BuildDescendantTree(type, 0, maxDepth);

            return Task.FromResult(new McpToolResponse(
                $"Retrieved hierarchy tree for '{type.FullName}' (direction: {direction}).",
                new { typeName = FormatTypeName(type), direction, ancestors = upTree, descendants = downTree }));
        }

        // ═══════════════════════════════════════════════════════════════════
        // P1.3 — Discovery & Search
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Fuzzy or regex search across all loaded types.
        /// </summary>
        [XRMcp(Name = "search_types", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Search across all loaded types by name pattern (contains, regex, or exact match).")]
        public static Task<McpToolResponse> SearchTypesAsync(
            McpToolContext context,
            [McpName("pattern"), Description("Search pattern string.")]
            string pattern,
            [McpName("match_mode"), Description("Match mode: 'contains' (default), 'regex', or 'exact'.")]
            string matchMode = "contains",
            [McpName("base_type"), Description("Optional base type filter — only return types assignable to this type.")]
            string? baseTypeName = null,
            [McpName("assembly_filter"), Description("Optional assembly name filter (contains match).")]
            string? assemblyFilter = null,
            [McpName("max_results"), Description("Maximum number of results to return (default: 100).")]
            int maxResults = 100)
        {
            if (string.IsNullOrWhiteSpace(pattern))
                return Task.FromResult(new McpToolResponse("Pattern must not be empty.", isError: true));

            Type? baseTypeFilter = null;
            if (!string.IsNullOrWhiteSpace(baseTypeName))
            {
                if (!TryResolveAnyType(baseTypeName, out baseTypeFilter))
                    return Task.FromResult(new McpToolResponse($"Base type filter '{baseTypeName}' not found.", isError: true));
            }

            Regex? regex = null;
            if (matchMode == "regex")
            {
                try
                {
                    regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(2));
                }
                catch (RegexParseException ex)
                {
                    return Task.FromResult(new McpToolResponse($"Invalid regex pattern: {ex.Message}", isError: true));
                }
            }

            var results = new List<object>();
            foreach (var type in EnumerateAllTypes())
            {
                if (results.Count >= maxResults) break;

                // Assembly filter
                if (!string.IsNullOrWhiteSpace(assemblyFilter))
                {
                    string asmName = type.Assembly.GetName().Name ?? "";
                    if (!asmName.Contains(assemblyFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                // Base type filter
                if (baseTypeFilter is not null && !baseTypeFilter.IsAssignableFrom(type))
                    continue;

                // Name matching
                string fullName = type.FullName ?? type.Name;
                bool matches = matchMode switch
                {
                    "exact" => string.Equals(type.Name, pattern, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(type.FullName, pattern, StringComparison.OrdinalIgnoreCase),
                    "regex" => regex!.IsMatch(fullName),
                    _ => fullName.Contains(pattern, StringComparison.OrdinalIgnoreCase)
                         || type.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase)
                };

                if (matches)
                {
                    results.Add(new
                    {
                        name = type.Name,
                        fullName = type.FullName,
                        @namespace = type.Namespace,
                        assembly = type.Assembly.GetName().Name,
                        isAbstract = type.IsAbstract,
                        isSealed = type.IsSealed,
                        isEnum = type.IsEnum,
                        isInterface = type.IsInterface,
                        isValueType = type.IsValueType
                    });
                }
            }

            return Task.FromResult(new McpToolResponse(
                $"Found {results.Count} types matching '{pattern}' (mode: {matchMode}).",
                new { count = results.Count, maxResults, types = results }));
        }

        /// <summary>
        /// Lists all loaded assemblies in the current AppDomain.
        /// </summary>
        [XRMcp(Name = "list_assemblies", Permission = McpPermissionLevel.ReadOnly)]
        [Description("List all loaded assemblies in the current AppDomain.")]
        public static Task<McpToolResponse> ListAssembliesAsync(McpToolContext context)
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Select(asm =>
                {
                    var asmName = asm.GetName();
                    string category = ClassifyAssembly(asm);
                    int typeCount;
                    try { typeCount = asm.GetTypes().Length; }
                    catch { typeCount = -1; }

                    return new
                    {
                        name = asmName.Name,
                        version = asmName.Version?.ToString(),
                        location = string.IsNullOrWhiteSpace(asm.Location) ? null : asm.Location,
                        category,
                        isDynamic = asm.IsDynamic,
                        typeCount
                    };
                })
                .OrderBy(a => a.category)
                .ThenBy(a => a.name)
                .ToArray();

            return Task.FromResult(new McpToolResponse(
                $"Listed {assemblies.Length} loaded assemblies.",
                new { count = assemblies.Length, assemblies }));
        }

        /// <summary>
        /// Lists all types in a specific assembly.
        /// </summary>
        [XRMcp(Name = "get_assembly_types", Permission = McpPermissionLevel.ReadOnly)]
        [Description("List all types in a specific loaded assembly.")]
        public static Task<McpToolResponse> GetAssemblyTypesAsync(
            McpToolContext context,
            [McpName("assembly_name"), Description("Assembly name (short name, e.g. 'XREngine').")]
            string assemblyName,
            [McpName("include_internal"), Description("Include internal/private types.")]
            bool includeInternal = false,
            [McpName("namespace_filter"), Description("Optional namespace prefix filter.")]
            string? namespaceFilter = null,
            [McpName("max_results"), Description("Maximum number of results to return (default: 500).")]
            int maxResults = 500)
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(asm => string.Equals(asm.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));

            if (assembly is null)
                return Task.FromResult(new McpToolResponse($"Assembly '{assemblyName}' not found.", isError: true));

            Type[] allTypes;
            try
            {
                allTypes = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                allTypes = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
            }

            var types = allTypes
                .Where(t =>
                {
                    if (!includeInternal && !t.IsPublic && !t.IsNestedPublic)
                        return false;
                    if (!string.IsNullOrWhiteSpace(namespaceFilter) &&
                        !(t.Namespace?.StartsWith(namespaceFilter, StringComparison.OrdinalIgnoreCase) ?? false))
                        return false;
                    return true;
                })
                .Take(maxResults)
                .Select(t => new
                {
                    name = t.Name,
                    fullName = t.FullName,
                    @namespace = t.Namespace,
                    isAbstract = t.IsAbstract,
                    isSealed = t.IsSealed,
                    isEnum = t.IsEnum,
                    isInterface = t.IsInterface,
                    isValueType = t.IsValueType,
                    isStatic = t.IsAbstract && t.IsSealed,
                    baseType = t.BaseType is not null ? FormatTypeName(t.BaseType) : null
                })
                .OrderBy(t => t.fullName)
                .ToArray();

            return Task.FromResult(new McpToolResponse(
                $"Listed {types.Length} types from assembly '{assemblyName}'.",
                new { assemblyName = assembly.GetName().Name, count = types.Length, maxResults, types }));
        }

        /// <summary>
        /// Lists enum types with their values from loaded assemblies.
        /// </summary>
        [XRMcp(Name = "list_enums", Permission = McpPermissionLevel.ReadOnly)]
        [Description("List enum types from loaded assemblies, optionally filtered by namespace or assembly.")]
        public static Task<McpToolResponse> ListEnumsAsync(
            McpToolContext context,
            [McpName("namespace_filter"), Description("Optional namespace prefix filter.")]
            string? namespaceFilter = null,
            [McpName("assembly_filter"), Description("Optional assembly name filter (contains match).")]
            string? assemblyFilter = null,
            [McpName("max_results"), Description("Maximum number of results (default: 200).")]
            int maxResults = 200)
        {
            var enums = new List<object>();
            foreach (var type in EnumerateAllTypes())
            {
                if (enums.Count >= maxResults) break;
                if (!type.IsEnum) continue;

                if (!string.IsNullOrWhiteSpace(namespaceFilter) &&
                    !(type.Namespace?.StartsWith(namespaceFilter, StringComparison.OrdinalIgnoreCase) ?? false))
                    continue;

                if (!string.IsNullOrWhiteSpace(assemblyFilter))
                {
                    string asmName = type.Assembly.GetName().Name ?? "";
                    if (!asmName.Contains(assemblyFilter, StringComparison.OrdinalIgnoreCase))
                        continue;
                }

                var names = Enum.GetNames(type);
                enums.Add(new
                {
                    name = type.Name,
                    fullName = type.FullName,
                    @namespace = type.Namespace,
                    assembly = type.Assembly.GetName().Name,
                    underlyingType = Enum.GetUnderlyingType(type).Name,
                    valueCount = names.Length,
                    isFlags = type.GetCustomAttribute<FlagsAttribute>() is not null
                });
            }

            return Task.FromResult(new McpToolResponse(
                $"Found {enums.Count} enum types.",
                new { count = enums.Count, maxResults, enums }));
        }

        /// <summary>
        /// Gets all named values for an enum type.
        /// </summary>
        [XRMcp(Name = "get_enum_values", Permission = McpPermissionLevel.ReadOnly)]
        [Description("Get all named values for an enum type.")]
        public static Task<McpToolResponse> GetEnumValuesAsync(
            McpToolContext context,
            [McpName("type_name"), Description("Enum type name (short or fully-qualified).")]
            string typeName)
        {
            if (!TryResolveAnyType(typeName, out var type))
                return Task.FromResult(new McpToolResponse($"Type '{typeName}' not found.", isError: true));

            if (!type.IsEnum)
                return Task.FromResult(new McpToolResponse($"Type '{type.FullName}' is not an enum.", isError: true));

            var underlyingType = Enum.GetUnderlyingType(type);
            var names = Enum.GetNames(type);
            var values = Enum.GetValues(type);

            var entries = new List<object>();
            for (int i = 0; i < names.Length; i++)
            {
                var raw = values.GetValue(i)!;
                entries.Add(new
                {
                    name = names[i],
                    value = Convert.ChangeType(raw, underlyingType),
                    description = type.GetField(names[i])?.GetCustomAttribute<DescriptionAttribute>()?.Description
                });
            }

            return Task.FromResult(new McpToolResponse(
                $"Retrieved {entries.Count} values for enum '{type.FullName}'.",
                new
                {
                    enumName = FormatTypeName(type),
                    fullName = type.FullName,
                    underlyingType = underlyingType.Name,
                    isFlags = type.GetCustomAttribute<FlagsAttribute>() is not null,
                    values = entries
                }));
        }

        // ═══════════════════════════════════════════════════════════════════
        // Helper methods for Type System tools
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Resolves any type by short name or fully-qualified name across all loaded assemblies.
        /// </summary>
        private static bool TryResolveAnyType(string name, out Type type)
        {
            type = null!;
            if (string.IsNullOrWhiteSpace(name))
                return false;

            // Try exact match via Type.GetType first (handles assembly-qualified names)
            var exact = Type.GetType(name, throwOnError: false, ignoreCase: true);
            if (exact is not null)
            {
                type = exact;
                return true;
            }

            // Search all loaded assemblies
            Type? shortNameMatch = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[]? types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray(); }
                catch { continue; }

                foreach (var candidate in types)
                {
                    // Full name match (highest priority)
                    if (string.Equals(candidate.FullName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        type = candidate;
                        return true;
                    }

                    // Short name match (remember first match, keep looking for full name match)
                    if (shortNameMatch is null && string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
                        shortNameMatch = candidate;
                }
            }

            if (shortNameMatch is not null)
            {
                type = shortNameMatch;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Enumerates all types across all loaded assemblies, handling load failures gracefully.
        /// </summary>
        private static IEnumerable<Type> EnumerateAllTypes()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[]? types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray(); }
                catch { continue; }

                foreach (var type in types)
                    yield return type;
            }
        }

        /// <summary>
        /// Formats a type name in a readable way, handling generics.
        /// </summary>
        private static string FormatTypeName(Type type)
        {
            if (type.IsGenericType)
            {
                string baseName = type.Name;
                int backtickIndex = baseName.IndexOf('`');
                if (backtickIndex > 0)
                    baseName = baseName[..backtickIndex];

                var args = type.GetGenericArguments().Select(FormatTypeName);
                return $"{type.Namespace}.{baseName}<{string.Join(", ", args)}>";
            }

            return type.FullName ?? type.Name;
        }

        /// <summary>
        /// Checks whether a type matches a given name (short or full).
        /// </summary>
        private static bool MatchesTypeName(Type type, string name)
        {
            return string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(type.FullName, name, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Classifies an assembly as "engine", "game", "system", or "third-party".
        /// </summary>
        private static string ClassifyAssembly(Assembly assembly)
        {
            string name = assembly.GetName().Name ?? "";
            if (name.StartsWith("XREngine", StringComparison.OrdinalIgnoreCase))
                return "engine";
            if (name.StartsWith("System", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase) ||
                name == "mscorlib")
                return "system";
            if (assembly.IsDynamic || (assembly.GetName().Name?.Contains("Game", StringComparison.OrdinalIgnoreCase) ?? false))
                return "game";
            return "third-party";
        }

        /// <summary>
        /// Builds a complete type info object for a given type.
        /// </summary>
        private static object BuildTypeInfo(Type type, bool includeMembers)
        {
            object? members = null;
            if (includeMembers)
            {
                var bindingFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly;

                members = new
                {
                    properties = type.GetProperties(bindingFlags)
                        .Where(p => p.GetIndexParameters().Length == 0)
                        .Select(p => new
                        {
                            name = p.Name,
                            type = FormatTypeName(p.PropertyType),
                            canRead = p.CanRead,
                            canWrite = p.CanWrite
                        })
                        .OrderBy(p => p.name)
                        .ToArray(),
                    fields = type.GetFields(bindingFlags)
                        .Select(f => new
                        {
                            name = f.Name,
                            type = FormatTypeName(f.FieldType),
                            isReadOnly = f.IsInitOnly,
                            isStatic = f.IsStatic
                        })
                        .OrderBy(f => f.name)
                        .ToArray(),
                    methods = type.GetMethods(bindingFlags)
                        .Where(m => !m.IsSpecialName)
                        .Select(m => new
                        {
                            name = m.Name,
                            returnType = FormatTypeName(m.ReturnType),
                            parameterCount = m.GetParameters().Length,
                            isStatic = m.IsStatic
                        })
                        .OrderBy(m => m.name)
                        .ToArray()
                };
            }

            return new
            {
                name = type.Name,
                fullName = type.FullName,
                @namespace = type.Namespace,
                assembly = type.Assembly.GetName().Name,
                baseType = type.BaseType is not null ? FormatTypeName(type.BaseType) : null,
                interfaces = type.GetInterfaces().Select(FormatTypeName).OrderBy(n => n).ToArray(),
                genericParameters = type.IsGenericTypeDefinition
                    ? type.GetGenericArguments().Select(g => new
                    {
                        name = g.Name,
                        constraints = g.GetGenericParameterConstraints().Select(FormatTypeName).ToArray()
                    }).ToArray()
                    : null,
                attributes = type.GetCustomAttributes(false)
                    .Select(a => a.GetType().Name)
                    .Distinct()
                    .OrderBy(n => n)
                    .ToArray(),
                isPublic = type.IsPublic || type.IsNestedPublic,
                isAbstract = type.IsAbstract,
                isSealed = type.IsSealed,
                isStatic = type.IsAbstract && type.IsSealed,
                isEnum = type.IsEnum,
                isInterface = type.IsInterface,
                isValueType = type.IsValueType && !type.IsEnum,
                isGenericType = type.IsGenericType,
                isGenericTypeDefinition = type.IsGenericTypeDefinition,
                description = type.GetCustomAttribute<DescriptionAttribute>()?.Description,
                displayName = type.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName,
                category = type.GetCustomAttribute<CategoryAttribute>()?.Category,
                members
            };
        }

        /// <summary>
        /// Builds detailed method information, including parameter defaults and generic constraints.
        /// </summary>
        private static object BuildMethodDetail(MethodInfo method)
        {
            return new
            {
                name = method.Name,
                declaringType = method.DeclaringType is not null ? FormatTypeName(method.DeclaringType) : null,
                returnType = FormatTypeName(method.ReturnType),
                parameters = method.GetParameters().Select(p => new
                {
                    name = p.Name,
                    type = FormatTypeName(p.ParameterType),
                    position = p.Position,
                    hasDefault = p.HasDefaultValue,
                    defaultValue = p.HasDefaultValue ? p.DefaultValue?.ToString() : null,
                    isOut = p.IsOut,
                    isRef = p.ParameterType.IsByRef && !p.IsOut,
                    isParams = p.GetCustomAttribute<ParamArrayAttribute>() is not null,
                    description = p.GetCustomAttribute<DescriptionAttribute>()?.Description
                }).ToArray(),
                isStatic = method.IsStatic,
                isVirtual = method.IsVirtual,
                isAbstract = method.IsAbstract,
                isOverride = method.IsVirtual && method.GetBaseDefinition().DeclaringType != method.DeclaringType,
                genericArguments = method.IsGenericMethod
                    ? method.GetGenericArguments().Select(g => new
                    {
                        name = g.Name,
                        constraints = g.GetGenericParameterConstraints().Select(FormatTypeName).ToArray()
                    }).ToArray()
                    : null,
                attributes = method.GetCustomAttributes(false)
                    .Select(a => a.GetType().Name)
                    .Distinct()
                    .OrderBy(n => n)
                    .ToArray()
            };
        }

        /// <summary>
        /// Builds an ancestor tree (walking upward from a type).
        /// </summary>
        private static object? BuildAncestorTree(Type type)
        {
            if (type.BaseType is null)
                return null;

            return new
            {
                name = type.BaseType.Name,
                fullName = type.BaseType.FullName,
                parent = BuildAncestorTree(type.BaseType)
            };
        }

        /// <summary>
        /// Builds a descendant tree (walking downward from a type, breadth-limited).
        /// </summary>
        private static object BuildDescendantTree(Type rootType, int currentDepth, int maxDepth)
        {
            object[]? children = null;
            if (currentDepth < maxDepth)
            {
                var directDerived = EnumerateAllTypes()
                    .Where(t => t.BaseType == rootType)
                    .OrderBy(t => t.FullName)
                    .ToArray();

                if (directDerived.Length > 0)
                {
                    children = directDerived
                        .Select(child => BuildDescendantTree(child, currentDepth + 1, maxDepth))
                        .ToArray();
                }
            }

            return new
            {
                name = rootType.Name,
                fullName = rootType.FullName,
                isAbstract = rootType.IsAbstract,
                isSealed = rootType.IsSealed,
                childCount = children?.Length ?? 0,
                children
            };
        }
    }
}
