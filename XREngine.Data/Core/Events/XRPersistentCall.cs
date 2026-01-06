using System.Diagnostics;
using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using YamlDotNet.Serialization;

namespace XREngine.Data.Core
{
    /// <summary>
    /// A persistent call to a method on a target object.
    /// </summary>
    [Serializable]
    public sealed class XRPersistentCall
    {
        /// <summary>
        /// The scene node the user selected in the editor when choosing the callback.
        /// Used for UI grouping and human-readable display.
        /// </summary>
        public Guid NodeId { get; set; } = Guid.Empty;

        /// <summary>
        /// The specific target object to invoke the method on.
        /// This will be either the selected <see cref="XREngine.Scene.SceneNode"/> or one of its components.
        /// </summary>
        public Guid TargetObjectId { get; set; } = Guid.Empty;

        public string? MethodName { get; set; }

        /// <summary>
        /// Assembly-qualified parameter type names used to disambiguate overloads.
        /// </summary>
        public string[]? ParameterTypeNames { get; set; }

        /// <summary>
        /// If true, and the event payload is a ValueTuple, the tuple elements are expanded into individual parameters.
        /// </summary>
        public bool UseTupleExpansion { get; set; }

        [YamlIgnore]
        private MethodInfo? _cachedMethod;
        [YamlIgnore]
        private Guid _cachedTargetId;

        public bool IsConfigured
            => TargetObjectId != Guid.Empty
            && !string.IsNullOrWhiteSpace(MethodName);

        internal bool TryInvoke(object? target, object?[] args)
        {
            if (target is null || string.IsNullOrWhiteSpace(MethodName))
                return false;

            try
            {
#pragma warning disable IL2072
                var method = ResolveMethod(target.GetType());
#pragma warning restore IL2072
                if (method is null)
                    return false;

                method.Invoke(target, args);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[XRPersistentCall] Failed to invoke '{MethodName}' on '{target.GetType().Name}': {ex}");
                return false;
            }
        }

        private MethodInfo? ResolveMethod([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type targetType)
        {
            if (_cachedMethod is not null && _cachedTargetId == TargetObjectId)
                return _cachedMethod;

            _cachedTargetId = TargetObjectId;
            _cachedMethod = null;

            var name = MethodName;
            if (string.IsNullOrWhiteSpace(name))
                return null;

            Type[]? desiredParamTypes = null;
            if (ParameterTypeNames is { Length: > 0 } typeNames)
            {
                desiredParamTypes = new Type[typeNames.Length];
                for (int i = 0; i < typeNames.Length; i++)
                {
#pragma warning disable IL2057
                    var t = Type.GetType(typeNames[i], throwOnError: false);
#pragma warning restore IL2057
                    if (t is null)
                        return null;
                    desiredParamTypes[i] = t;
                }
            }

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
            var candidates = targetType.GetMethods(flags)
                .Where(m => !m.IsSpecialName && m.Name == name && !m.ContainsGenericParameters)
                .ToArray();

            if (desiredParamTypes is null)
            {
                // Best-effort fallback: if we don't have a signature, take the first public instance void method with the right name.
                _cachedMethod = candidates.FirstOrDefault(m => m.ReturnType == typeof(void));
                return _cachedMethod;
            }

            foreach (var method in candidates)
            {
                if (method.ReturnType != typeof(void))
                    continue;

                var ps = method.GetParameters();
                if (ps.Length != desiredParamTypes.Length)
                    continue;

                bool match = true;
                for (int i = 0; i < ps.Length; i++)
                {
                    if (ps[i].ParameterType != desiredParamTypes[i])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    _cachedMethod = method;
                    return _cachedMethod;
                }
            }

            return null;
        }
    }
}
