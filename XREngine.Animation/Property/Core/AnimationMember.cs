using ImmediateReflection;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using XREngine.Data.Core;

namespace XREngine.Animation
{
    public class AnimationMember : XRBase
    {
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
                            Initialize = InitializeField;
                            break;
                        case EAnimationMemberType.Property:
                            _propertyCache = null;
                            _cacheAttempted = false;
                            Initialize = InitializeProperty;
                            break;
                        case EAnimationMemberType.Method:
                            _methodCache = null;
                            _cacheAttempted = false;
                            Initialize = InitializeMethod;
                            break;
                        case EAnimationMemberType.Group:
                            _fieldCache = null;
                            _propertyCache = null;
                            _methodCache = null;
                            _cacheAttempted = false;
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

            if (Animation != null)
                animations.Add(path, Animation);

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

            if (MemberNotFound)
            {
                string argTypes = string.Join(", ", argumentTypes.Select(t => t.Name));
                LogWarningOnce($"[Animation] Method '{_memberName}({argTypes})' not found on type '{parentObj.GetType().Name}'.");
            }

            DefaultValue = AnimatedMethodArgumentIndex >= 0 && AnimatedMethodArgumentIndex < MethodArguments.Length
                ? MethodArguments[AnimatedMethodArgumentIndex]
                : null;
            
            object? result = Cache(_methodCache?.Invoke(parentObj, MethodArguments));
            
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

            object? value = _propertyCache?.GetValue(parentObj);
            DefaultValue = value;
            return Cache(value);
        }

        public object? InitializeField(object? parentObj)
        {
            if (parentObj is null || MemberNotFound)
                return null;

            _fieldCache ??= parentObj.GetImmediateType().GetField(_memberName);
            
            MemberNotFound = _fieldCache is null;

            object? value = _fieldCache?.GetValue(parentObj);
            DefaultValue = value;
            return Cache(value);
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
