using System.Reflection;
using XREngine.Data.Animation;
using XREngine.Data.Core;

namespace XREngine.Animation
{
    public class AnimationMember : XRBase
    {
        public AnimationMember()
        {
            _fieldCache = null;
            _propertyCache = null;
            _methodCache = null;
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
        }

        private const BindingFlags BindingFlag = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        //Cached at runtime
        private PropertyInfo? _propertyCache;
        private MethodInfo? _methodCache;
        private FieldInfo? _fieldCache;
        internal Action<object?, float>? _tick = null;

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

        private readonly EventList<AnimationMember> _children = [];
        public EventList<AnimationMember> Children => _children;

        private string _memberName = string.Empty;
        public string MemberName
        {
            get => _memberName;
            set => SetField(ref _memberName, value);
        }

        //For now, methods can only have one animated argument (but can still have multiple non-animated arguments)
        public object[] MethodArguments { get; set; } = new object[1];
        public int MethodValueArgumentIndex { get; set; } = 0;

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
                            _tick = FieldTick;
                            break;
                        case EAnimationMemberType.Property:
                            _propertyCache = null;
                            _cacheAttempted = false;
                            _tick = PropertyTick;
                            break;
                        case EAnimationMemberType.Method:
                            _methodCache = null;
                            _cacheAttempted = false;
                            _tick = MethodTick;
                            break;
                    }
                    break;
            }
        }

        public void CollectAnimations(string? path, Dictionary<string, BasePropAnim> animations)
        {
            if (!string.IsNullOrEmpty(path))
                path += $".{_memberName}";
            else
                path = _memberName ?? string.Empty;

            if (Animation != null)
                animations.Add(path, Animation);

            foreach (AnimationMember member in _children)
                member.CollectAnimations(path, animations);
        }

        private object? _cachedReturnValue = null;
        private bool _cacheAttempted = false;

        internal void MethodTick(object? obj, float delta)
        {
            if (obj is null || MemberNotFound)
                return;

            if (_methodCache is null)
            {
                Type? type = obj.GetType();
                while (type != null)
                {
                    if ((_methodCache = type.GetMethod(_memberName, BindingFlag, MethodArguments.Select(x => x.GetType()).ToArray())) is null)
                        type = type.BaseType;
                    else
                        break;
                }

                if (MemberNotFound = _methodCache is null)
                    return;
            }

            if (_methodCache is null)
                return;

            object? methodReturn;
            //Set the value of the method argument and call the method
            var animation = Animation;
            if (animation is not null)
            {
                if (animation.State == EAnimationState.Playing)
                {
                    methodReturn = animation.SetValue(obj, _methodCache, MethodValueArgumentIndex, MethodArguments);
                    animation.Tick(delta);
                }
                else
                    methodReturn = GetMethodReturnValue(obj);
            }
            else
                methodReturn = GetMethodReturnValue(obj);
            
            //Tick children on method return value
            if (_children.Count > 0)
            {
                foreach (AnimationMember f in _children)
                    f._tick?.Invoke(methodReturn, delta);
            }

            object? GetMethodReturnValue(object obj)
            {
                object? methodReturn;
                if (CacheReturnValue)
                {
                    if (!_cacheAttempted)
                    {
                        _cacheAttempted = true;
                        _cachedReturnValue = _methodCache.Invoke(obj, MethodArguments);
                    }
                    methodReturn = _cachedReturnValue;
                }
                else
                    methodReturn = _methodCache.Invoke(obj, MethodArguments);
                return methodReturn;
            }
        }
        internal void PropertyTick(object? obj, float delta)
        {
            if (obj is null || MemberNotFound)
                return;

            if (_propertyCache is null)
            {
                Type? type = obj.GetType();
                while (type != null)
                {
                    if ((_propertyCache = type.GetProperty(_memberName, BindingFlag)) is null)
                        type = type.BaseType;
                    else
                        break;
                }

                if (MemberNotFound = _propertyCache is null)
                    return;
            }

            if (_propertyCache is null)
                return;

            object? propertyReturn;
            //Set the value of the property and tick the animation
            var animation = Animation;
            if (animation is not null)
            {
                if (animation.State == EAnimationState.Playing)
                {
                    propertyReturn = animation.SetValue(obj, _propertyCache);
                    animation.Tick(delta);
                }
                else
                    propertyReturn = GetPropertyReturnValue(obj);
            }
            else
            {
                propertyReturn = GetPropertyReturnValue(obj);
            }

            //Tick children on property value
            if (_children.Count > 0)
            {
                foreach (AnimationMember f in _children)
                    f._tick?.Invoke(propertyReturn, delta);
            }

            object? GetPropertyReturnValue(object obj)
            {
                object? propertyReturn;
                if (CacheReturnValue)
                {
                    if (!_cacheAttempted)
                    {
                        _cacheAttempted = true;
                        _cachedReturnValue = _propertyCache.GetValue(obj);
                    }
                    propertyReturn = _cachedReturnValue;
                }
                else
                    propertyReturn = _propertyCache.GetValue(obj);
                return propertyReturn;
            }
        }
        internal void FieldTick(object? obj, float delta)
        {
            if (obj is null || MemberNotFound)
                return;

            if (_fieldCache is null)
            {
                Type? type = obj.GetType();
                while (type != null)
                {
                    if ((_fieldCache = type.GetField(_memberName, BindingFlag)) is null)
                        type = type.BaseType;
                    else
                        break;
                }
                if (MemberNotFound = _fieldCache is null)
                    return;
            }

            if (_fieldCache is null)
                return;
            
            object? fieldReturn;
            var animation = Animation;
            if (animation is not null)
            {
                if (animation.State == EAnimationState.Playing)
                {
                    fieldReturn = animation.SetValue(obj, _fieldCache);
                    animation.Tick(delta);
                }
                else
                    fieldReturn = GetFieldReturnValue(obj);
            }
            else
                fieldReturn = GetFieldReturnValue(obj);
            
            if (_children.Count > 0)
            {
                foreach (AnimationMember f in _children)
                    f._tick?.Invoke(fieldReturn, delta);
            }

            object? GetFieldReturnValue(object obj)
            {
                object? fieldReturn;
                if (CacheReturnValue)
                {
                    if (!_cacheAttempted)
                    {
                        _cacheAttempted = true;
                        _cachedReturnValue = _fieldCache.GetValue(obj);
                    }
                    fieldReturn = _cachedReturnValue;
                }
                else
                    fieldReturn = _fieldCache.GetValue(obj);
                return fieldReturn;
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
            MemberNotFound = false;
            _fieldCache = null;
            _propertyCache = null;
            _methodCache = null;

            Animation?.Start();
        }

        internal void StopAnimations()
        {
            MemberNotFound = false;
            _fieldCache = null;
            _propertyCache = null;
            _methodCache = null;

            Animation?.Stop();
            foreach (AnimationMember folder in _children)
                folder.StopAnimations();
        }
    }
}
