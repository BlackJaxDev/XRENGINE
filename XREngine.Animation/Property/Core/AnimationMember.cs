using ImmediateReflection;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Numerics;
using System.Reflection;
using XREngine.Data.Core;

namespace XREngine.Animation
{
    public class AnimationMember : XRBase
    {
        private delegate void DelTypedMethodInvoker<T>(object? target, AnimationMember member, T value);

        // Track logged messages to avoid spamming the same warnings every frame
        private static readonly HashSet<string> _loggedWarnings = [];
        
        private static void LogWarningOnce(string message)
        {
            if (_loggedWarnings.Add(message))
                Debug.WriteLine(message);
        }

        public AnimationMember()
        {
            _fieldCache = null;
            _propertyCache = null;
            _methodCache = null;
            // Default MemberType is Property, so set the Initialize delegate accordingly
            _initialize = InitializeProperty;
        }
        /// <summary>
        /// Constructor to create a subtree without an animation at this level.
        /// </summary>
        /// <param name="memberName">The name of this property and optionally sub-properties separated by a period.</param>
        /// <param name="children">Any sub-properties this property owns and you want to animate.</param>
        public AnimationMember(string memberName, EAnimationMemberType memberType, params AnimationMember[] children) : this()
        {
            int splitIndex = memberName.IndexOf('.');
            if (splitIndex >= 0)
            {
                string remainingPath = memberName[(splitIndex + 1)..];
                _children.Add(new AnimationMember(remainingPath));
                memberName = memberName[..splitIndex];
            }
            if (children != null)
                _children.AddRange(children);
            _memberName = memberName;
            Animation = null;
            MemberType = memberType;
            // Explicitly set Initialize delegate in case OnPropertyChanged didn't fire
            _initialize = memberType switch
            {
                EAnimationMemberType.Field => InitializeField,
                EAnimationMemberType.Property => InitializeProperty,
                EAnimationMemberType.Method => InitializeMethod,
                _ => null
            };
        }
        /// <summary>
        /// Constructor to create a subtree with an animation attached at this level.
        /// </summary>
        /// <param name="memberName">The name of the field, property or method to animate.</param>
        /// <param name="memberType"></param>
        /// <param name="animation"></param>
        public AnimationMember(string memberName, EAnimationMemberType memberType = EAnimationMemberType.Property, BasePropAnim? animation = null) : this()
        {
            if (memberType != EAnimationMemberType.Property)
            {
                int splitIndex = memberName.IndexOf('.');
                if (splitIndex >= 0)
                {
                    string remainingPath = memberName[(splitIndex + 1)..];
                    _children.Add(new AnimationMember(remainingPath));
                    memberName = memberName[..splitIndex];
                }
            }
            _memberName = memberName;
            Animation = animation;
            MemberType = memberType;
            // Explicitly set Initialize delegate in case OnPropertyChanged didn't fire
            _initialize = memberType switch
            {
                EAnimationMemberType.Field => InitializeField,
                EAnimationMemberType.Property => InitializeProperty,
                EAnimationMemberType.Method => InitializeMethod,
                _ => null
            };
        }

        private const BindingFlags BindingFlag = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        //Cached at runtime
        private ImmediateProperty? _propertyCache;
        private MethodInfo? _methodCache;
        private Action<object?, object?[]?>? _methodInvoker;
        private ImmediateField? _fieldCache;
        private Action<object?, bool>? _boolValueApplier;
        private Action<object?, float>? _floatValueApplier;
        private Action<object?, Vector2>? _vector2ValueApplier;
        private Action<object?, Vector3>? _vector3ValueApplier;
        private Action<object?, Vector4>? _vector4ValueApplier;
        private Action<object?, Quaternion>? _quaternionValueApplier;

        /// <summary>
        /// Dense slot assigned during state machine initialization for O(1) typed value store access.
        /// </summary>
        internal AnimSlot Slot = AnimSlot.Invalid;

        public delegate object? DelInitialize(object? parentObject);

        private DelInitialize? _initialize = null;
        public DelInitialize? Initialize
        {
            get => _initialize;
            private set => SetField(ref _initialize, value);
        }

        private BasePropAnim? _animation = null;
        public BasePropAnim? Animation
        {
            get => _animation;
            set => SetField(ref _animation, value);
        }

        private AnimationClip? _parentClip = null;
        public AnimationClip? ParentClip
        {
            get => _parentClip;
            internal set => SetField(ref _parentClip, value);
        }

        private EventList<AnimationMember> _children = [];
        public EventList<AnimationMember> Children
        {
            get => _children;
            set => SetField(ref _children, value);
        }

        private string _memberName = string.Empty;
        public string MemberName
        {
            get => _memberName;
            set => SetField(ref _memberName, value);
        }

        //For now, methods can only have one animated argument (but can still have multiple non-animated arguments)
        /// <summary>
        /// These are the arguments passed into the method call.
        /// </summary>
        public object?[] MethodArguments
        {
            get => _methodArguments;
            set => SetField(ref _methodArguments, value);
        }
        /// <summary>
        /// This is the index of the argument that should be set by the animation.
        /// </summary>
        public int AnimatedMethodArgumentIndex
        {
            get => _methodValueArgumentIndex;
            set => SetField(ref _methodValueArgumentIndex, value);
        }

        //TODO: resolve _memberType as a new object animated
        private EAnimationMemberType _memberType = EAnimationMemberType.Property;
        public EAnimationMemberType MemberType
        {
            get => _memberType;
            set => SetField(ref _memberType, value);
        }

        private bool _memberNotFound = false;
        public bool MemberNotFound
        {
            get => _memberNotFound;
            private set => SetField(ref _memberNotFound, value);
        }

        private bool _cacheReturnValue = false;
        public bool CacheReturnValue
        {
            get => _cacheReturnValue;
            set => SetField(ref _cacheReturnValue, value);
        }

        private bool _registeredWithClip = false;
        public bool RegisteredWithClip
        {
            get => _registeredWithClip;
            private set => SetField(ref _registeredWithClip, value);
        }

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(ParentClip):
                        if (ParentClip != null && Animation != null)
                            Animation.AnimationEnded -= ParentClip.AnimationHasEnded;
                        break;
                }
            }
            return change;
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(CacheReturnValue):
                    _cacheAttempted = false;
                    break;
                case nameof(Animation):
                    if (ParentClip != null && Animation != null && !_registeredWithClip)
                    {
                        Animation.AnimationEnded += ParentClip.AnimationHasEnded;
                        _registeredWithClip = true;
                    }
                    break;
                case nameof(ParentClip):
                    if (ParentClip != null && Animation != null && !_registeredWithClip)
                    {
                        Animation.AnimationEnded += ParentClip.AnimationHasEnded;
                        _registeredWithClip = true;
                    }
                    break;
                case nameof(MemberType):
                    MemberNotFound = false;
                    switch (_memberType)
                    {
                        case EAnimationMemberType.Field:
                            _fieldCache = null;
                            _cacheAttempted = false;
                            ResetTypedValueAppliers();
                            Initialize = InitializeField;
                            break;
                        case EAnimationMemberType.Property:
                            _propertyCache = null;
                            _cacheAttempted = false;
                            ResetTypedValueAppliers();
                            Initialize = InitializeProperty;
                            break;
                        case EAnimationMemberType.Method:
                            _methodCache = null;
                            _cacheAttempted = false;
                            ResetTypedValueAppliers();
                            Initialize = InitializeMethod;
                            break;
                        case EAnimationMemberType.Group:
                            _fieldCache = null;
                            _propertyCache = null;
                            _methodCache = null;
                            _cacheAttempted = false;
                            ResetTypedValueAppliers();
                            Initialize = null;
                            break;
                    }
                    break;
                case nameof(MemberNotFound):
                    if (MemberNotFound)
                        Debug.WriteLine($"Animation member '{_memberName}' not found. Animation will not be applied.");
                    break;
            }
        }

        public void CollectAnimations(string? path, Dictionary<string, BasePropAnim> animations)
        {
            if (MemberType != EAnimationMemberType.Group)
            {
                if (!string.IsNullOrEmpty(path))
                    path += $".{_memberName}";
                else
                    path = _memberName ?? string.Empty;

                if (MemberType == EAnimationMemberType.Method)
                {
                    for (int i = 0; i < _methodArguments.Length; i++)
                    {
                        path += ":";
                        object? arg = _methodArguments[i];
                        if (AnimatedMethodArgumentIndex == i)
                            path += "<AnimatedValue>";
                        else if (arg is null)
                            path += "<null>";
                        else
                            path += $"{arg}";
                    }
                }
            }
            else
            {
                path ??= string.Empty;
            }

            if (Animation != null)
                animations.TryAdd(path, Animation);

            foreach (AnimationMember member in _children)
                member.CollectAnimations(path, animations);
        }

        private object? _parentObject;
        private object? _cachedReturnValue = null;
        private bool _cacheAttempted = false;
        private int _methodValueArgumentIndex = 0;
        private object?[] _methodArguments = new object?[1];

        private object? _defaultValue = null;
        public object? DefaultValue
        {
            get => _defaultValue;
            set => SetField(ref _defaultValue, value);
        }

        /// <summary>
        /// Returns the current value of the animation.
        /// </summary>
        /// <returns></returns>
        public object? GetAnimationValue()
        {
            var a = Animation;
            if (a is not null)
                return a.GetCurrentValueGeneric();

            // For methods without an animation, return the default argument value if valid
            if (MemberType == EAnimationMemberType.Method &&
                AnimatedMethodArgumentIndex >= 0 &&
                AnimatedMethodArgumentIndex < _methodArguments.Length)
            {
                return _methodArguments[AnimatedMethodArgumentIndex];
            }

            return null;
        }

        /// <summary>
        /// Applies a potentially modified (weighted, blended, scaled) animation value to the parent object.
        /// </summary>
        /// <param name="value"></param>
        public void ApplyAnimationValue(object? value)
        {
            if (value is null || MemberNotFound)
                return;

            if (TryApplyKnownTypedValue(value))
                return;

            switch (MemberType)
            {
                case EAnimationMemberType.Field:
                    _fieldCache?.SetValue(_parentObject, value);
                    break;
                case EAnimationMemberType.Property:
                    _propertyCache?.SetValue(_parentObject, value);
                    break;
                case EAnimationMemberType.Method:
                    if (AnimatedMethodArgumentIndex < 0 || AnimatedMethodArgumentIndex >= _methodArguments.Length)
                        return;

                    if (_methodCache is null)
                    {
                        LogWarningOnce($"[Animation] Cannot apply value for method '{_memberName}': method cache is null (was the method resolved during initialization?)");
                        return;
                    }

                    if (_parentObject is null)
                    {
                        LogWarningOnce($"[Animation] Cannot apply value for method '{_memberName}': parent object is null (lookup may have failed earlier in the chain)");
                        return;
                    }

                    object? lastArg = _methodArguments[AnimatedMethodArgumentIndex];
                    _methodArguments[AnimatedMethodArgumentIndex] = value;
                    try
                    {
                        if (_methodInvoker is not null)
                            _methodInvoker(_parentObject, _methodArguments);
                        else
                            _methodCache.Invoke(_parentObject, _methodArguments);
                    }
                    catch (Exception ex)
                    {
                        LogWarningOnce($"[Animation] Failed to invoke method '{_memberName}' on '{_parentObject.GetType().Name}': {ex.Message}");
                    }
                    _methodArguments[AnimatedMethodArgumentIndex] = lastArg;

                    break;
            }
        }

        private bool TryApplyKnownTypedValue(object value)
            => value switch
            {
                bool boolValue => TryApplyBool(boolValue),
                float floatValue => TryApplyFloat(floatValue),
                Vector2 vector2Value => TryApplyVector2(vector2Value),
                Vector3 vector3Value => TryApplyVector3(vector3Value),
                Vector4 vector4Value => TryApplyVector4(vector4Value),
                Quaternion quaternionValue => TryApplyQuaternion(quaternionValue),
                _ => false,
            };

        public bool TryApplyBool(bool value)
            => TryApplyTypedValue(_boolValueApplier, value);

        public bool TryApplyFloat(float value)
            => TryApplyTypedValue(_floatValueApplier, value);

        public bool TryApplyVector2(Vector2 value)
            => TryApplyTypedValue(_vector2ValueApplier, value);

        public bool TryApplyVector3(Vector3 value)
            => TryApplyTypedValue(_vector3ValueApplier, value);

        public bool TryApplyVector4(Vector4 value)
            => TryApplyTypedValue(_vector4ValueApplier, value);

        public bool TryApplyQuaternion(Quaternion value)
            => TryApplyTypedValue(_quaternionValueApplier, value);

        /// <summary>
        /// Applies the value at this member's <see cref="Slot"/> from the given store,
        /// using the cached typed delegate (no boxing).
        /// Falls back to generic <see cref="ApplyAnimationValue(object?)"/> for discrete/unknown types.
        /// </summary>
        public void ApplyFromStore(AnimationValueStore store)
        {
            if (MemberNotFound || !Slot.IsValid)
                return;

            switch (Slot.Type)
            {
                case EAnimValueType.Float:
                    TryApplyFloat(store.GetFloat(Slot.TypeIndex));
                    break;
                case EAnimValueType.Vector2:
                    TryApplyVector2(store.GetVector2(Slot.TypeIndex));
                    break;
                case EAnimValueType.Vector3:
                    TryApplyVector3(store.GetVector3(Slot.TypeIndex));
                    break;
                case EAnimValueType.Vector4:
                    TryApplyVector4(store.GetVector4(Slot.TypeIndex));
                    break;
                case EAnimValueType.Quaternion:
                    TryApplyQuaternion(store.GetQuaternion(Slot.TypeIndex));
                    break;
                case EAnimValueType.Bool:
                    TryApplyBool(store.GetBool(Slot.TypeIndex));
                    break;
                default:
                    ApplyAnimationValue(store.GetDiscrete(Slot.TypeIndex));
                    break;
            }
        }

        /// <summary>
        /// Writes the current animation value (from <see cref="Animation"/>) into the given store
        /// at this member's <see cref="Slot"/>. No boxing for known typed animations.
        /// </summary>
        public void WriteCurrentValueToStore(AnimationValueStore store)
        {
            if (!Slot.IsValid)
                return;

            switch (Slot.Type)
            {
                case EAnimValueType.Float:
                    store.SetFloat(Slot.TypeIndex, GetCurrentFloat());
                    break;
                case EAnimValueType.Vector2:
                    store.SetVector2(Slot.TypeIndex, GetCurrentVector2());
                    break;
                case EAnimValueType.Vector3:
                    store.SetVector3(Slot.TypeIndex, GetCurrentVector3());
                    break;
                case EAnimValueType.Vector4:
                    store.SetVector4(Slot.TypeIndex, GetCurrentVector4());
                    break;
                case EAnimValueType.Quaternion:
                    store.SetQuaternion(Slot.TypeIndex, GetCurrentQuaternion());
                    break;
                case EAnimValueType.Bool:
                    store.SetBool(Slot.TypeIndex, GetCurrentBool());
                    break;
                default:
                    store.SetDiscrete(Slot.TypeIndex, GetAnimationValue());
                    break;
            }
        }

        /// <summary>
        /// Writes the default value into the given store at this member's <see cref="Slot"/>.
        /// </summary>
        public void WriteDefaultToStore(AnimationValueStore store)
        {
            if (!Slot.IsValid)
                return;

            store.SetValue(Slot, DefaultValue);
        }

        private float GetCurrentFloat()
        {
            if (Animation is PropAnimFloat f) return f.CurrentPosition;
            if (Animation is PropAnimMethod<float> mf) return (float)(mf.GetCurrentValueGeneric() ?? 0f);
            return DefaultValue is float df ? df : 0f;
        }

        private Vector2 GetCurrentVector2()
        {
            if (Animation is PropAnimVector2 v) return v.CurrentPosition;
            return DefaultValue is Vector2 dv ? dv : Vector2.Zero;
        }

        private Vector3 GetCurrentVector3()
        {
            if (Animation is PropAnimVector3 v) return v.CurrentPosition;
            return DefaultValue is Vector3 dv ? dv : Vector3.Zero;
        }

        private Vector4 GetCurrentVector4()
        {
            if (Animation is PropAnimVector4 v) return v.CurrentPosition;
            return DefaultValue is Vector4 dv ? dv : Vector4.Zero;
        }

        private Quaternion GetCurrentQuaternion()
        {
            if (Animation is PropAnimQuaternion q) return q.CurrentValue;
            return DefaultValue is Quaternion dq ? dq : Quaternion.Identity;
        }

        private bool GetCurrentBool()
        {
            if (Animation is PropAnimBool b) return (bool)b.GetCurrentValueGeneric();
            return DefaultValue is bool db && db;
        }

        /// <summary>
        /// Determines the <see cref="EAnimValueType"/> for this member based on its animation or property/field type.
        /// </summary>
        public EAnimValueType DetermineValueType()
        {
            // Check animation type first
            return Animation switch
            {
                PropAnimFloat or PropAnimMethod<float> => EAnimValueType.Float,
                PropAnimVector2 => EAnimValueType.Vector2,
                PropAnimVector3 => EAnimValueType.Vector3,
                PropAnimVector4 => EAnimValueType.Vector4,
                PropAnimQuaternion => EAnimValueType.Quaternion,
                PropAnimBool => EAnimValueType.Bool,
                _ => DetermineValueTypeFromMemberInfo(),
            };
        }

        private EAnimValueType DetermineValueTypeFromMemberInfo()
        {
            Type? valueType = MemberType switch
            {
                EAnimationMemberType.Property => _propertyCache?.PropertyType,
                EAnimationMemberType.Field => _fieldCache?.FieldType,
                EAnimationMemberType.Method => GetMethodAnimatedParameterType(),
                _ => null,
            };

            if (valueType is null)
                return EAnimValueType.Discrete;

            if (valueType == typeof(float)) return EAnimValueType.Float;
            if (valueType == typeof(Vector2)) return EAnimValueType.Vector2;
            if (valueType == typeof(Vector3)) return EAnimValueType.Vector3;
            if (valueType == typeof(Vector4)) return EAnimValueType.Vector4;
            if (valueType == typeof(Quaternion)) return EAnimValueType.Quaternion;
            if (valueType == typeof(bool)) return EAnimValueType.Bool;

            return EAnimValueType.Discrete;
        }

        private Type? GetMethodAnimatedParameterType()
        {
            if (_methodCache is null || AnimatedMethodArgumentIndex < 0)
                return null;

            var parameters = _methodCache.GetParameters();
            if (AnimatedMethodArgumentIndex >= parameters.Length)
                return null;

            Type t = parameters[AnimatedMethodArgumentIndex].ParameterType;
            return t.IsByRef ? t.GetElementType() : t;
        }

        private object? Cache(object? memberValue)
        {
            if (!CacheReturnValue)
                return memberValue;

            if (_cacheAttempted)
                return _cachedReturnValue;

            _cacheAttempted = true;
            return _cachedReturnValue = memberValue;
        }

        public object? InitializeMethod(object? parentObj)
        {
            _parentObject = parentObj;

            if (parentObj is null || MemberNotFound)
                return null;
            
            var argumentTypes = MethodArguments.Select(x => x?.GetType() ?? typeof(object)).ToArray();
            _methodCache ??= parentObj.GetType()?.GetMethod(_memberName, BindingFlag, argumentTypes);

            if (_methodCache is not null && _methodInvoker is null)
                _methodInvoker = BuildMethodInvoker(_methodCache, parentObj.GetType(), argumentTypes.Length);

            MemberNotFound = _methodCache is null;
            ConfigureTypedValueAppliers(parentObj);

            if (MemberNotFound)
            {
                string argTypes = string.Join(", ", argumentTypes.Select(t => t.Name));
                LogWarningOnce($"[Animation] Method '{_memberName}({argTypes})' not found on type '{parentObj.GetType().Name}'.");
            }

            DefaultValue = AnimatedMethodArgumentIndex >= 0 && AnimatedMethodArgumentIndex < MethodArguments.Length
                ? MethodArguments[AnimatedMethodArgumentIndex]
                : null;

            object? result = null;
            if (CacheReturnValue)
                result = Cache(_methodCache?.Invoke(parentObj, MethodArguments));
            
            // Log if a lookup method (like GetComponent) returns null - this breaks the animation chain
            if (CacheReturnValue && result is null && !MemberNotFound)
            {
                string args = string.Join(", ", MethodArguments.Select(a => a?.ToString() ?? "null"));
                LogWarningOnce($"[Animation] Method '{_memberName}({args})' on '{parentObj.GetType().Name}' returned null. Downstream animation members will not function.");
            }
            
            return result;
        }
        public object? InitializeProperty(object? parentObj)
        {
            _parentObject = parentObj;

            if (parentObj is null || MemberNotFound)
                return null;

            _propertyCache ??= parentObj.GetImmediateType().GetProperty(_memberName);

            MemberNotFound = _propertyCache is null;
            ConfigureTypedValueAppliers(parentObj);

            object? value = _propertyCache?.GetValue(parentObj);
            DefaultValue = value;
            return Cache(value);
        }

        public object? InitializeField(object? parentObj)
        {
            _parentObject = parentObj;

            if (parentObj is null || MemberNotFound)
                return null;

            _fieldCache ??= parentObj.GetImmediateType().GetField(_memberName);
            
            MemberNotFound = _fieldCache is null;
            ConfigureTypedValueAppliers(parentObj);

            object? value = _fieldCache?.GetValue(parentObj);
            DefaultValue = value;
            return Cache(value);
        }

        private bool TryApplyTypedValue<T>(Action<object?, T>? applier, T value)
        {
            if (applier is null || MemberNotFound)
                return false;

            try
            {
                applier(_parentObject, value);
                return true;
            }
            catch (Exception ex)
            {
                LogWarningOnce($"[Animation] Failed to apply typed value for '{_memberName}' on '{_parentObject?.GetType().Name ?? "null"}': {ex.Message}");
                return false;
            }
        }

        private void ResetTypedValueAppliers()
        {
            _boolValueApplier = null;
            _floatValueApplier = null;
            _vector2ValueApplier = null;
            _vector3ValueApplier = null;
            _vector4ValueApplier = null;
            _quaternionValueApplier = null;
        }

        private void ConfigureTypedValueAppliers(object? parentObj)
        {
            ResetTypedValueAppliers();

            if (parentObj is null || MemberNotFound)
                return;

            Type targetType = parentObj.GetType();
            switch (MemberType)
            {
                case EAnimationMemberType.Field:
                    if (ResolveFieldInfo(targetType) is FieldInfo fieldInfo)
                        AssignFieldValueAppliers(fieldInfo, fieldInfo.DeclaringType ?? targetType);
                    break;
                case EAnimationMemberType.Property:
                    if (ResolvePropertyInfo(targetType) is PropertyInfo propertyInfo)
                        AssignPropertyValueAppliers(propertyInfo, propertyInfo.DeclaringType ?? targetType);
                    break;
                case EAnimationMemberType.Method:
                    AssignMethodValueAppliers(targetType);
                    break;
            }
        }

        private FieldInfo? ResolveFieldInfo(Type targetType)
        {
            for (Type? type = targetType; type is not null; type = type.BaseType)
            {
                FieldInfo? fieldInfo = type.GetField(_memberName, BindingFlag);
                if (fieldInfo is not null)
                    return fieldInfo;
            }

            return null;
        }

        private PropertyInfo? ResolvePropertyInfo(Type targetType)
        {
            for (Type? type = targetType; type is not null; type = type.BaseType)
            {
                PropertyInfo? propertyInfo = type.GetProperty(_memberName, BindingFlag);
                if (propertyInfo is not null)
                    return propertyInfo;
            }

            return null;
        }

        private void AssignFieldValueAppliers(FieldInfo fieldInfo, Type targetType)
        {
            Type valueType = fieldInfo.FieldType;

            if (valueType == typeof(bool))
                _boolValueApplier = BuildTypedFieldSetter<bool>(fieldInfo, targetType);
            else if (valueType == typeof(float))
                _floatValueApplier = BuildTypedFieldSetter<float>(fieldInfo, targetType);
            else if (valueType == typeof(Vector2))
                _vector2ValueApplier = BuildTypedFieldSetter<Vector2>(fieldInfo, targetType);
            else if (valueType == typeof(Vector3))
                _vector3ValueApplier = BuildTypedFieldSetter<Vector3>(fieldInfo, targetType);
            else if (valueType == typeof(Vector4))
                _vector4ValueApplier = BuildTypedFieldSetter<Vector4>(fieldInfo, targetType);
            else if (valueType == typeof(Quaternion))
                _quaternionValueApplier = BuildTypedFieldSetter<Quaternion>(fieldInfo, targetType);
        }

        private void AssignPropertyValueAppliers(PropertyInfo propertyInfo, Type targetType)
        {
            if (propertyInfo.SetMethod is null && propertyInfo.GetSetMethod(true) is null)
                return;

            Type valueType = propertyInfo.PropertyType;

            if (valueType == typeof(bool))
                _boolValueApplier = BuildTypedPropertySetter<bool>(propertyInfo, targetType);
            else if (valueType == typeof(float))
                _floatValueApplier = BuildTypedPropertySetter<float>(propertyInfo, targetType);
            else if (valueType == typeof(Vector2))
                _vector2ValueApplier = BuildTypedPropertySetter<Vector2>(propertyInfo, targetType);
            else if (valueType == typeof(Vector3))
                _vector3ValueApplier = BuildTypedPropertySetter<Vector3>(propertyInfo, targetType);
            else if (valueType == typeof(Vector4))
                _vector4ValueApplier = BuildTypedPropertySetter<Vector4>(propertyInfo, targetType);
            else if (valueType == typeof(Quaternion))
                _quaternionValueApplier = BuildTypedPropertySetter<Quaternion>(propertyInfo, targetType);
        }

        private void AssignMethodValueAppliers(Type targetType)
        {
            MethodInfo? methodInfo = _methodCache;
            if (methodInfo is null)
                return;

            ParameterInfo[] parameters = methodInfo.GetParameters();
            if (AnimatedMethodArgumentIndex < 0 || AnimatedMethodArgumentIndex >= parameters.Length)
                return;

            Type valueType = parameters[AnimatedMethodArgumentIndex].ParameterType;
            if (valueType.IsByRef)
                valueType = valueType.GetElementType() ?? valueType;

            if (valueType == typeof(bool))
            {
                var invoker = BuildTypedMethodInvoker<bool>(methodInfo, targetType, AnimatedMethodArgumentIndex);
                if (invoker is not null)
                    _boolValueApplier = (target, value) => invoker(target, this, value);
            }
            else if (valueType == typeof(float))
            {
                var invoker = BuildTypedMethodInvoker<float>(methodInfo, targetType, AnimatedMethodArgumentIndex);
                if (invoker is not null)
                    _floatValueApplier = (target, value) => invoker(target, this, value);
            }
            else if (valueType == typeof(Vector2))
            {
                var invoker = BuildTypedMethodInvoker<Vector2>(methodInfo, targetType, AnimatedMethodArgumentIndex);
                if (invoker is not null)
                    _vector2ValueApplier = (target, value) => invoker(target, this, value);
            }
            else if (valueType == typeof(Vector3))
            {
                var invoker = BuildTypedMethodInvoker<Vector3>(methodInfo, targetType, AnimatedMethodArgumentIndex);
                if (invoker is not null)
                    _vector3ValueApplier = (target, value) => invoker(target, this, value);
            }
            else if (valueType == typeof(Vector4))
            {
                var invoker = BuildTypedMethodInvoker<Vector4>(methodInfo, targetType, AnimatedMethodArgumentIndex);
                if (invoker is not null)
                    _vector4ValueApplier = (target, value) => invoker(target, this, value);
            }
            else if (valueType == typeof(Quaternion))
            {
                var invoker = BuildTypedMethodInvoker<Quaternion>(methodInfo, targetType, AnimatedMethodArgumentIndex);
                if (invoker is not null)
                    _quaternionValueApplier = (target, value) => invoker(target, this, value);
            }
        }

        private static Action<object?, T>? BuildTypedFieldSetter<T>(FieldInfo fieldInfo, Type targetType)
        {
            try
            {
                var targetParam = Expression.Parameter(typeof(object), "target");
                var valueParam = Expression.Parameter(typeof(T), "value");

                Expression targetExpr = fieldInfo.IsStatic
                    ? null!
                    : Expression.Convert(targetParam, targetType);
                Expression valueExpr = fieldInfo.FieldType == typeof(T)
                    ? valueParam
                    : Expression.Convert(valueParam, fieldInfo.FieldType);

                Expression assignExpr = fieldInfo.IsStatic
                    ? Expression.Assign(Expression.Field(null, fieldInfo), valueExpr)
                    : Expression.Assign(Expression.Field(targetExpr, fieldInfo), valueExpr);

                return Expression.Lambda<Action<object?, T>>(assignExpr, targetParam, valueParam).Compile();
            }
            catch
            {
                return null;
            }
        }

        private static Action<object?, T>? BuildTypedPropertySetter<T>(PropertyInfo propertyInfo, Type targetType)
        {
            try
            {
                MethodInfo? setMethod = propertyInfo.SetMethod ?? propertyInfo.GetSetMethod(true);
                if (setMethod is null)
                    return null;

                var targetParam = Expression.Parameter(typeof(object), "target");
                var valueParam = Expression.Parameter(typeof(T), "value");

                Expression targetExpr = setMethod.IsStatic
                    ? null!
                    : Expression.Convert(targetParam, targetType);
                Expression valueExpr = propertyInfo.PropertyType == typeof(T)
                    ? valueParam
                    : Expression.Convert(valueParam, propertyInfo.PropertyType);

                Expression callExpr = setMethod.IsStatic
                    ? Expression.Call(setMethod, valueExpr)
                    : Expression.Call(targetExpr, setMethod, valueExpr);

                return Expression.Lambda<Action<object?, T>>(callExpr, targetParam, valueParam).Compile();
            }
            catch
            {
                return null;
            }
        }

        private static DelTypedMethodInvoker<T>? BuildTypedMethodInvoker<T>(MethodInfo methodInfo, Type targetType, int animatedArgumentIndex)
        {
            try
            {
                var targetParam = Expression.Parameter(typeof(object), "target");
                var memberParam = Expression.Parameter(typeof(AnimationMember), "member");
                var valueParam = Expression.Parameter(typeof(T), "value");
                var methodArgumentsField = typeof(AnimationMember).GetField(nameof(_methodArguments), BindingFlags.Instance | BindingFlags.NonPublic)!;
                var methodArgumentsExpr = Expression.Field(memberParam, methodArgumentsField);

                Expression instance = methodInfo.IsStatic
                    ? null!
                    : Expression.Convert(targetParam, targetType);

                var parameters = methodInfo.GetParameters();
                var callArgs = new Expression[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    Type parameterType = parameters[i].ParameterType;
                    if (parameterType.IsByRef)
                        parameterType = parameterType.GetElementType() ?? parameterType;

                    if (i == animatedArgumentIndex)
                    {
                        callArgs[i] = parameterType == typeof(T)
                            ? valueParam
                            : Expression.Convert(valueParam, parameterType);
                    }
                    else
                    {
                        var indexExpr = Expression.Constant(i);
                        var argAccess = Expression.ArrayIndex(methodArgumentsExpr, indexExpr);
                        callArgs[i] = Expression.Convert(argAccess, parameterType);
                    }
                }

                Expression call = methodInfo.IsStatic
                    ? Expression.Call(methodInfo, callArgs)
                    : Expression.Call(instance, methodInfo, callArgs);

                var body = methodInfo.ReturnType == typeof(void)
                    ? (Expression)call
                    : Expression.Block(call, Expression.Empty());

                return Expression.Lambda<DelTypedMethodInvoker<T>>(body, targetParam, memberParam, valueParam).Compile();
            }
            catch
            {
                return null;
            }
        }

        private static Action<object?, object?[]?>? BuildMethodInvoker(MethodInfo methodInfo, Type targetType, int argumentCount)
        {
            try
            {
                var targetParam = Expression.Parameter(typeof(object), "target");
                var argsParam = Expression.Parameter(typeof(object[]), "args");

                Expression instance = methodInfo.IsStatic
                    ? null!
                    : Expression.Convert(targetParam, targetType);

                var parameters = methodInfo.GetParameters();
                var callArgs = new Expression[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    var indexExpr = Expression.Constant(i);
                    var argAccess = Expression.ArrayIndex(argsParam, indexExpr);
                    callArgs[i] = Expression.Convert(argAccess, parameters[i].ParameterType);
                }

                Expression call = methodInfo.IsStatic
                    ? Expression.Call(methodInfo, callArgs)
                    : Expression.Call(instance, methodInfo, callArgs);

                var body = methodInfo.ReturnType == typeof(void)
                    ? (Expression)call
                    : Expression.Block(call, Expression.Empty());

                var lambda = Expression.Lambda<Action<object?, object?[]?>>(body, targetParam, argsParam);
                return lambda.Compile();
            }
            catch
            {
                return null;
            }
        }
        /// <summary>
        /// Registers to the AnimationHasEnded method in the animation tree
        /// and returns the total amount of animations this member and its child members contain.
        /// </summary>
        /// <param name="tree">The animation tree that owns this member.</param>
        /// <returns>The total amount of animations this member and its child members contain.</returns>
        internal int Register(AnimationClip tree, bool startNow = true)
        {
            ParentClip = tree;

            int count = 0;

            if (Animation != null)
            {
                count++;

                if (startNow)
                    StartAnimation();
            }

            foreach (AnimationMember folder in _children)
                count += folder.Register(tree, startNow);

            return count;
        }
        /// <summary>
        /// Registers to the AnimationHasEnded method in the animation tree
        /// and returns the total amount of animations this member and its child members contain.
        /// </summary>
        /// <param name="tree">The animation tree that owns this member.</param>
        /// <returns>The total amount of animations this member and its child members contain.</returns>
        internal int Unregister(AnimationClip tree)
        {
            ParentClip = null;

            int count = Animation != null ? 1 : 0;

            foreach (AnimationMember folder in _children)
                count += folder.Unregister(tree);

            return count;
        }
        internal void StartAnimations()
        {
            StartAnimation();
            foreach (AnimationMember folder in _children)
                folder.StartAnimations();
        }

        private void StartAnimation()
        {
            // Note: Do NOT clear _fieldCache, _propertyCache, _methodCache here.
            // These are populated during Initialize() and must persist for ApplyAnimationValue() to work.
            if (Animation is null)
                return;
            //Debug.WriteLine($"Starting animation {Animation.Name} on {MemberName}");
            Animation?.Start();
        }

        internal void StopAnimations()
        {
            // Note: Do NOT clear _fieldCache, _propertyCache, _methodCache here.
            // These are populated during Initialize() and must persist for subsequent playback.
            Animation?.Stop();
            foreach (AnimationMember folder in _children)
                folder.StopAnimations();
        }

        public static AnimationMember SetBlendshapePercent(string name, float percent)
            => new("SetBlendShapeWeight", EAnimationMemberType.Method)
            {
                MethodArguments = [name, percent, StringComparison.InvariantCultureIgnoreCase],
                AnimatedMethodArgumentIndex = 1
            };

        public static AnimationMember SetBlendshapeNormalized(string name, float normalizedValue)
            => new("SetBlendShapeWeightNormalized", EAnimationMemberType.Method) 
            {
                MethodArguments = [name, normalizedValue, StringComparison.InvariantCultureIgnoreCase],
                AnimatedMethodArgumentIndex = 1
            };

        public static AnimationMember SetNormalizedBlendshapeValuesByModelNodeName(string sceneNodeName, params (string blendshapeName, float normalizedValue)[] blendshapeValues)
            => new("SceneNode", EAnimationMemberType.Property)
            {
                Children =
                [
                    new AnimationMember("FindDescendantByName", EAnimationMemberType.Method)
                    {
                        MethodArguments = [sceneNodeName, StringComparison.InvariantCultureIgnoreCase],
                        AnimatedMethodArgumentIndex = 0,
                        CacheReturnValue = true,
                        Children =
                        [
                            new AnimationMember("GetComponent", EAnimationMemberType.Method)
                            {
                                MethodArguments = ["ModelComponent"],
                                AnimatedMethodArgumentIndex = 0,
                                CacheReturnValue = true,
                                Children = [.. blendshapeValues.Select(x => SetBlendshapeNormalized(x.blendshapeName, x.normalizedValue))]
                            }
                        ]
                    }
                ]
            };

    }
}
