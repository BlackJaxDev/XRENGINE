using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using YamlDotNet.Serialization;

namespace XREngine.Data.Core
{
    /// <summary>
    /// A persistent call to a method on a target object.
    /// </summary>
    [Serializable]
    public sealed class XRPersistentCall
    {
        private Guid _nodeId = Guid.Empty;
        private Guid _targetObjectId = Guid.Empty;
        private string? _methodName;
        private string[]? _parameterTypeNames;
        private bool _useTupleExpansion;

        /// <summary>
        /// The scene node the user selected in the editor when choosing the callback.
        /// Used for UI grouping and human-readable display.
        /// </summary>
        public Guid NodeId
        {
            get => _nodeId;
            set => _nodeId = value;
        }

        /// <summary>
        /// The specific target object to invoke the method on.
        /// This will be either the selected <see cref="XREngine.Scene.SceneNode"/> or one of its components.
        /// </summary>
        public Guid TargetObjectId
        {
            get => _targetObjectId;
            set
            {
                if (_targetObjectId != value)
                {
                    _targetObjectId = value;
                    InvalidateCachedBinding();
                }
            }
        }

        public string? MethodName
        {
            get => _methodName;
            set
            {
                if (!string.Equals(_methodName, value, StringComparison.Ordinal))
                {
                    _methodName = value;
                    InvalidateCachedBinding();
                }
            }
        }

        /// <summary>
        /// Assembly-qualified parameter type names used to disambiguate overloads.
        /// </summary>
        public string[]? ParameterTypeNames
        {
            get => _parameterTypeNames;
            set
            {
                _parameterTypeNames = value;
                InvalidateCachedBinding();
            }
        }

        /// <summary>
        /// If true, and the event payload is a ValueTuple, the tuple elements are expanded into individual parameters.
        /// </summary>
        public bool UseTupleExpansion
        {
            get => _useTupleExpansion;
            set
            {
                if (_useTupleExpansion != value)
                {
                    _useTupleExpansion = value;
                    InvalidateCachedBinding();
                }
            }
        }

        [YamlIgnore]
        private MethodInfo? _cachedMethod;
        [YamlIgnore]
        private Guid _cachedTargetId;
        [YamlIgnore]
        private Type[]? _cachedParameterTypes;
        [YamlIgnore]
        private bool _parameterTypesResolved;
        [YamlIgnore]
        private object? _cachedDelegateTarget;
        [YamlIgnore]
        private Delegate? _cachedDelegate;
        [YamlIgnore]
        private bool _delegateBindAttempted;

        public bool IsConfigured
            => TargetObjectId != Guid.Empty
            && !string.IsNullOrWhiteSpace(MethodName);

        internal bool TryInvokeZeroArg(object? target)
        {
            if (target is null || string.IsNullOrWhiteSpace(MethodName))
                return false;

            if (ReferenceEquals(_cachedDelegateTarget, target) && _cachedDelegate is Action cachedAction)
            {
                try
                {
                    cachedAction();
                    return true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[XRPersistentCall] Failed to invoke '{MethodName}' on '{target.GetType().Name}': {ex}");
                    return false;
                }
            }

            try
            {
                if (TryGetResolvedParameterTypes(out Type[]? resolvedParameterTypes)
                    && RuntimeMemberAccessorRegistry.TryInvoke(target, MethodName, resolvedParameterTypes, Array.Empty<object?>()))
                {
                    return true;
                }

                if (!_delegateBindAttempted || !ReferenceEquals(_cachedDelegateTarget, target))
                {
                    _cachedDelegateTarget = target;
                    _delegateBindAttempted = true;
                    _cachedDelegate = null;
#pragma warning disable IL2072
                    MethodInfo? method = ResolveMethod(target.GetType());
#pragma warning restore IL2072
                    if (method is not null && method.GetParameters().Length == 0)
                        _cachedDelegate = Delegate.CreateDelegate(typeof(Action), target, method, throwOnBindFailure: false);
                }

                if (_cachedDelegate is Action action)
                {
                    action();
                    return true;
                }

#pragma warning disable IL2072
                MethodInfo? fallbackMethod = ResolveMethod(target.GetType());
#pragma warning restore IL2072
                if (fallbackMethod is null)
                    return false;

                fallbackMethod.Invoke(target, Array.Empty<object?>());
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[XRPersistentCall] Failed to invoke '{MethodName}' on '{target.GetType().Name}': {ex}");
                return false;
            }
        }

        internal bool TryInvokeTyped<T>(object? target, T arg)
        {
            if (target is null || string.IsNullOrWhiteSpace(MethodName))
                return false;

            // Allow XREvent<T> to bind parameterless void methods without boxing T.
            if (ParameterTypeNames is { Length: 0 })
                return TryInvokeZeroArg(target);

            if (ReferenceEquals(_cachedDelegateTarget, target))
            {
                if (_cachedDelegate is Action<T> cachedTyped)
                {
                    try
                    {
                        cachedTyped(arg);
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[XRPersistentCall] Failed to invoke '{MethodName}' on '{target.GetType().Name}': {ex}");
                        return false;
                    }
                }

                if (_cachedDelegate is Action cachedZeroArg)
                {
                    try
                    {
                        cachedZeroArg();
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[XRPersistentCall] Failed to invoke '{MethodName}' on '{target.GetType().Name}': {ex}");
                        return false;
                    }
                }
            }

            try
            {
                if (!_delegateBindAttempted || !ReferenceEquals(_cachedDelegateTarget, target))
                {
                    _cachedDelegateTarget = target;
                    _delegateBindAttempted = true;
                    _cachedDelegate = null;
#pragma warning disable IL2072
                    MethodInfo? method = ResolveMethod(target.GetType());
#pragma warning restore IL2072
                    if (method is not null)
                    {
                        ParameterInfo[] parameters = method.GetParameters();
                        if (parameters.Length == 1)
                            _cachedDelegate = Delegate.CreateDelegate(typeof(Action<T>), target, method, throwOnBindFailure: false);
                        else if (parameters.Length == 0)
                            _cachedDelegate = Delegate.CreateDelegate(typeof(Action), target, method, throwOnBindFailure: false);
                    }
                }

                if (_cachedDelegate is Action<T> typedAction)
                {
                    typedAction(arg);
                    return true;
                }

                if (_cachedDelegate is Action zeroArgAction)
                {
                    zeroArgAction();
                    return true;
                }

                return TryInvoke(target, [arg]);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[XRPersistentCall] Failed to invoke '{MethodName}' on '{target.GetType().Name}': {ex}");
                return false;
            }
        }

        internal bool TryInvoke(object? target, object?[] args)
        {
            if (target is null || string.IsNullOrWhiteSpace(MethodName))
                return false;

            try
            {
                if (!TryGetResolvedParameterTypes(out Type[]? generatedParameterTypes))
                    return false;

                if (RuntimeMemberAccessorRegistry.TryInvoke(target, MethodName, generatedParameterTypes, args))
                    return true;
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

        private void InvalidateCachedBinding()
        {
            _cachedMethod = null;
            _cachedTargetId = Guid.Empty;
            _cachedParameterTypes = null;
            _parameterTypesResolved = false;
            _cachedDelegateTarget = null;
            _cachedDelegate = null;
            _delegateBindAttempted = false;
        }

        private bool TryGetResolvedParameterTypes(out Type[]? resolvedTypes)
        {
            if (_parameterTypesResolved)
            {
                resolvedTypes = _cachedParameterTypes;
                return true;
            }

            if (ParameterTypeNames is not { Length: > 0 } names)
            {
                _cachedParameterTypes = null;
                _parameterTypesResolved = true;
                resolvedTypes = null;
                return true;
            }

            Type[] types = new Type[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                Type? resolved = AotRuntimeMetadataStore.ResolveType(names[i]);
                if (resolved is null)
                {
                    resolvedTypes = null;
                    return false;
                }

                types[i] = resolved;
            }

            _cachedParameterTypes = types;
            _parameterTypesResolved = true;
            resolvedTypes = types;
            return true;
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

            if (!TryGetResolvedParameterTypes(out Type[]? desiredParamTypes))
                return null;

            Runtime.AotParity.AotParityDiagnostics.Report(
                targetType,
                Runtime.AotParity.EAotParityCategory.ReflectiveMemberBinding,
                $"{nameof(XRPersistentCall)}.{nameof(ResolveMethod)}",
                $"Generate a typed invoker for '{targetType.FullName}.{name}' so persistent calls bind without reflecting over methods.");

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
            var candidates = targetType.GetMethods(flags);

            if (ParameterTypeNames is null)
            {
                // Best-effort fallback: if we don't have a signature, take the first public instance void method with the right name.
                for (int i = 0; i < candidates.Length; i++)
                {
                    MethodInfo method = candidates[i];
                    if (!method.IsSpecialName
                        && method.Name == name
                        && !method.ContainsGenericParameters
                        && method.ReturnType == typeof(void))
                    {
                        _cachedMethod = method;
                        return _cachedMethod;
                    }
                }

                return null;
            }

            int desiredLength = desiredParamTypes?.Length ?? 0;
            for (int i = 0; i < candidates.Length; i++)
            {
                MethodInfo method = candidates[i];
                if (method.IsSpecialName
                    || method.Name != name
                    || method.ContainsGenericParameters
                    || method.ReturnType != typeof(void))
                {
                    continue;
                }

                ParameterInfo[] ps = method.GetParameters();
                if (ps.Length != desiredLength)
                    continue;

                bool match = true;
                for (int j = 0; j < desiredLength; j++)
                {
                    if (ps[j].ParameterType != desiredParamTypes![j])
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
