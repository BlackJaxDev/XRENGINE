using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using XREngine.Core.Attributes;

namespace XREngine.Core
{
    /// <summary>
    /// Central registry that rewrites legacy type names to their current equivalents.
    /// Types opt in by annotating themselves with <see cref="XRTypeRedirectAttribute"/>.
    /// </summary>
    public static class XRTypeRedirectRegistry
    {
        private sealed record RedirectTarget(string FullName, string? AssemblyQualifiedName);

        private static readonly ConcurrentDictionary<string, RedirectTarget> Redirects = new(StringComparer.Ordinal);
        private static volatile bool _initialized;

        public static string RewriteTypeName(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
                return typeName;

            EnsureInitialized();

            // Fast path: exact match on legacy full name.
            if (Redirects.TryGetValue(typeName, out var direct))
                return direct.FullName;

            // Handle assembly-qualified names (and other comma-suffixed forms) by prefix matching.
            int commaIndex = typeName.IndexOf(',');
            if (commaIndex > 0)
            {
                string prefix = typeName.Substring(0, commaIndex);
                if (Redirects.TryGetValue(prefix, out var target))
                    return target.AssemblyQualifiedName ?? target.FullName;
            }

            return typeName;
        }

        private static void EnsureInitialized()
        {
            if (_initialized)
                return;

            lock (Redirects)
            {
                if (_initialized)
                    return;

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types;
                    try
                    {
                        types = asm.GetTypes();
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var type in types)
                    {
                        XRTypeRedirectAttribute[] attrs;
                        try
                        {
                            attrs = (XRTypeRedirectAttribute[])type.GetCustomAttributes(typeof(XRTypeRedirectAttribute), inherit: false);
                        }
                        catch
                        {
                            continue;
                        }

                        if (attrs.Length == 0)
                            continue;

                        var target = new RedirectTarget(
                            type.FullName ?? type.Name,
                            type.AssemblyQualifiedName);

                        foreach (var attr in attrs)
                        {
                            foreach (var legacy in attr.LegacyTypeNames ?? Array.Empty<string>())
                            {
                                if (string.IsNullOrWhiteSpace(legacy))
                                    continue;

                                // Use legacy full name as the key.
                                Redirects.TryAdd(legacy.Trim(), target);
                            }
                        }
                    }
                }

                _initialized = true;
            }
        }
    }
}
