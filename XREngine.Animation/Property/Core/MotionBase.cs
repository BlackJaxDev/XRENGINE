using System.Numerics;
using System.Diagnostics;
using MemoryPack;
using XREngine.Core.Files;
using XREngine.Data;

namespace XREngine.Animation
{
    [MemoryPackable]
    [MemoryPackUnion(0, typeof(AnimationClip))]
    [MemoryPackUnion(1, typeof(BlendTree1D))]
    [MemoryPackUnion(2, typeof(BlendTree2D))]
    [MemoryPackUnion(3, typeof(BlendTreeDirect))]
    public abstract partial class MotionBase : XRAsset
    {
        // Track logged messages to avoid spamming the same warnings
        private static readonly HashSet<string> _loggedWarnings = [];
        
        private static void LogWarningOnce(string message)
        {
            if (_loggedWarnings.Add(message))
                Debug.WriteLine(message);
        }

        [MemoryPackIgnore]
        protected readonly Dictionary<string, AnimationMember> _animatedCurves = [];

        /// <summary>
        /// Legacy dictionary store — kept as a compatibility bridge for callers that still use string paths.
        /// New code should use <see cref="ValueStore"/> directly via slot indices.
        /// </summary>
        [MemoryPackIgnore]
        protected internal Dictionary<string, object?> _animationValues = [];
        [MemoryPackIgnore]
        private readonly object _animationValuesLock = new();

        /// <summary>
        /// Typed struct-of-arrays value store. Eliminates boxing and string hashing on hot paths.
        /// Sized during <see cref="AnimStateMachine.Initialize"/> after slot assignment.
        /// </summary>
        [MemoryPackIgnore]
        internal AnimationValueStore ValueStore { get; } = new();

        /// <summary>
        /// Shared slot layout from the owning state machine. Null when used outside a state machine context.
        /// </summary>
        [MemoryPackIgnore]
        internal AnimationSlotLayout? SlotLayout { get; set; }

        /// <summary>
        /// Dense array of all animated members for this motion, built during slot assignment.
        /// Enables O(1) iteration without dictionary enumeration.
        /// </summary>
        [MemoryPackIgnore]
        internal AnimationMember[] AnimatedMembersArray { get; set; } = [];

        [MemoryPackIgnore]
        public Dictionary<string, object?> AnimationValues => _animationValues;
        [MemoryPackIgnore]
        public Dictionary<string, AnimationMember> AnimatedCurves => _animatedCurves;

        public void Initialize(AnimLayer layer, AnimStateMachine owner, object? rootObject)
        {
            //Register all animated curves in the motion
            InitializeMotion(this, layer, owner, rootObject, []);
        }

        public void Deinitialize()
        {
            //Unregister all animated curves in the motion
            _animatedCurves.Clear();
            AnimatedMembersArray = [];
        }

        public virtual void SetDefaults()
        {
            // Write defaults to typed store if available
            if (SlotLayout is not null)
            {
                foreach (var member in AnimatedMembersArray)
                    member.WriteDefaultToStore(ValueStore);
                return;
            }
            // Legacy path
            foreach (var kv in _animatedCurves)
                SetAnimValue(kv.Key, kv.Value.DefaultValue);
        }

        /// <summary>
        /// Registers all child motions in the motion, or the clip if the motion is a clip.
        /// </summary>
        /// <param name="motion"></param>
        public static void InitializeMotion(MotionBase motion, AnimLayer layer, AnimStateMachine owner, object? rootObject, Stack<MotionBase> parentMotions)
        {
            parentMotions.Push(motion);
            switch (motion)
            {
                case BlendTree1D blendTree1D:
                    foreach (var child in blendTree1D.Children)
                        if (child.Motion is not null)
                            InitializeMotion(child.Motion, layer, owner, rootObject, parentMotions);
                    break;
                case BlendTree2D blendTree2D:
                    foreach (var child in blendTree2D.Children)
                        if (child.Motion is not null)
                            InitializeMotion(child.Motion, layer, owner, rootObject, parentMotions);
                    break;
                case BlendTreeDirect blendTreeDirect:
                    foreach (var child in blendTreeDirect.Children)
                        if (child.Motion is not null)
                            InitializeMotion(child.Motion, layer, owner, rootObject, parentMotions);
                    break;
                case AnimationClip animationClip:
                    if (animationClip is not null)
                        InitializeClip(animationClip, layer, owner, rootObject, parentMotions);
                    break;
            }
            parentMotions.Pop();
        }

        /// <summary>
        /// Registers all paths and their curves in the animation clip.
        /// </summary>
        /// <param name="animationClip"></param>
        private static void InitializeClip(AnimationClip animationClip, AnimLayer layer, AnimStateMachine owner, object? rootObject, IEnumerable<MotionBase> parentMotions)
            => InitializeMember(animationClip.RootMember, string.Empty, layer, owner, rootObject, parentMotions);

        /// <summary>
        /// Recursively registers all animation members and their children if they have an animation.
        /// </summary>
        /// <param name="member"></param>
        /// <param name="path"></param>
        private static void InitializeMember(AnimationMember? member, string path, AnimLayer layer, AnimStateMachine owner, object? parentObject, IEnumerable<MotionBase> parentMotions)
        {
            if (member is null)
                return;

            object? prevObject = parentObject;
            
            // Fallback: If Initialize delegate is null but we have a valid MemberType, call the appropriate method directly
            if (member.Initialize is null && member.MemberType != EAnimationMemberType.Group)
            {
                LogWarningOnce($"[Animation] Initialize delegate was null for member '{member.MemberName}' (type={member.MemberType}). Calling initialization directly.");
                parentObject = member.MemberType switch
                {
                    EAnimationMemberType.Field => member.InitializeField(parentObject),
                    EAnimationMemberType.Property => member.InitializeProperty(parentObject),
                    EAnimationMemberType.Method => member.InitializeMethod(parentObject),
                    _ => parentObject
                };
            }
            else
            {
                parentObject = member.Initialize?.Invoke(parentObject);
            }

            // If we had a parent object and initialization returns null, the animation path is broken at this segment.
            // This commonly happens when scene node names don't match (e.g., FindDescendantByName("Body") returns null).
            // Only log for humanoid/important paths, not blendshapes which commonly have mismatches
            if (prevObject is not null && member.MemberType != EAnimationMemberType.Group && parentObject is null)
            {
                string brokenAt = string.IsNullOrEmpty(path) ? member.MemberName : $"{path}{member.MemberName}";
                // Log GetComponent/GetComponentInHierarchy failures (HumanoidComponent, etc), not FindDescendantByName (blendshape mesh paths)
                if (member.MemberName == "GetComponent" || member.MemberName == "GetComponentInHierarchy")
                    LogWarningOnce($"[Animation] Broken animation path at '{brokenAt}' (segment '{member.MemberName}') on '{prevObject.GetType().Name}'. Humanoid animation will not work.");
            }

            if (member.MemberNotFound)
            {
                string brokenAt = string.IsNullOrEmpty(path) ? member.MemberName : $"{path}{member.MemberName}";
                LogWarningOnce($"[Animation] Animation member not found at '{brokenAt}' (segment '{member.MemberName}').");
            }

            if (member.MemberType != EAnimationMemberType.Group)
                path += $"{member.MemberName}";

            if (member.MemberType == EAnimationMemberType.Method)
            {
                for (int i = 0; i < member.MethodArguments.Length; i++)
                {
                    path += ":";
                    object? arg = member.MethodArguments[i];
                    if (member.AnimatedMethodArgumentIndex == i)
                    {
                        //if (member.Animation is not null)
                            path += $"<AnimatedValue>";
                        //else
                        //    path += member.MethodArguments[i] is null 
                        //        ? "<null>" 
                        //        : $"{member.MethodArguments[i]}";
                    }
                    else if (arg is null)
                        path += "<null>";
                    else
                        path += $"{arg}";
                }
            }

            if (member.Animation is not null || member.MemberType == EAnimationMemberType.Method)
            {
                foreach (var motion in parentMotions)
                {
                    if (!motion._animatedCurves.TryAdd(path, member))
                    {
                        //Debug.WriteLine($"Animation member {path} already registered in motion.");
                    }
                }
                if (!owner._animatedCurves.TryAdd(path, member))
                {
                    //Debug.WriteLine($"Animation member {path} already registered in state machine.");
                }
            }

            if (member.Children.Count == 0)
                return;

            if (member.MemberType != EAnimationMemberType.Group)
                path += "/";

            foreach (AnimationMember child in member.Children)
                InitializeMember(child, path, layer, owner, parentObject, parentMotions);
        }

        protected internal void SetAnimValue(string path, object? animValue)
        {
            // Write to typed store if slot is available
            if (SlotLayout is not null && _animatedCurves.TryGetValue(path, out var member) && member.Slot.IsValid)
            {
                ValueStore.SetValue(member.Slot, animValue);
                return;
            }
            // Legacy fallback
            lock (_animationValuesLock)
            {
                if (!_animationValues.TryAdd(path, animValue))
                    _animationValues[path] = animValue;
            }
        }

        /// <summary>
        /// Sets a typed float value directly into the store by slot index. No boxing.
        /// </summary>
        protected internal void SetAnimFloat(int slotIndex, float value)
            => ValueStore.SetFloat(slotIndex, value);

        /// <summary>
        /// Sets a typed Vector3 value directly into the store by slot index. No boxing.
        /// </summary>
        protected internal void SetAnimVector3(int slotIndex, Vector3 value)
            => ValueStore.SetVector3(slotIndex, value);

        /// <summary>
        /// Sets a typed Quaternion value directly into the store by slot index. No boxing.
        /// </summary>
        protected internal void SetAnimQuaternion(int slotIndex, Quaternion value)
            => ValueStore.SetQuaternion(slotIndex, value);

        [Obsolete("Use ValueStore directly. This allocates a snapshot array every call.")]
        public KeyValuePair<string, object?>[] GetAnimationValuesSnapshot()
        {
            lock (_animationValuesLock)
                return _animationValues.ToArray();
        }

        [Obsolete("Use ValueStore directly. This allocates a snapshot array every call.")]
        public string[] GetAnimationValueKeysSnapshot()
        {
            lock (_animationValuesLock)
                return [.. _animationValues.Keys];
        }

        /// <summary>
        /// Copies animation values from another motion's typed store (zero-alloc).
        /// Falls back to legacy dictionary copy if stores aren't initialized.
        /// </summary>
        public void CopyAnimationValuesFrom(MotionBase? motion)
        {
            if (motion is null)
                return;

            if (SlotLayout is not null && motion.SlotLayout is not null)
            {
                ValueStore.CopyFrom(motion.ValueStore);
                return;
            }

            // Legacy fallback
            lock (motion._animationValuesLock)
            {
                foreach (var kvp in motion._animationValues)
                    SetAnimValue(kvp.Key, kvp.Value);
            }
        }

        /// <summary>
        /// Lerps between two child motions' typed stores into this store (zero-alloc, no LINQ, no boxing).
        /// Falls back to legacy dictionary lerp when stores aren't available.
        /// </summary>
        private void GetLerpedValues(MotionBase? m1, MotionBase? m2, float t)
        {
            if (SlotLayout is not null && m1?.SlotLayout is not null && m2?.SlotLayout is not null)
            {
                AnimationValueStore.Lerp(m1.ValueStore, m2.ValueStore, t, ValueStore);
                return;
            }

            // Legacy path
            GetLerpedValuesLegacy(m1?._animationValues, m2?._animationValues, t);
        }

        private void GetLerpedValuesLegacy(
            Dictionary<string, object?>? v1,
            Dictionary<string, object?>? v2,
            float t)
        {
            if (v1 is null || v2 is null)
                return;

            foreach (var kvp in v1)
            {
                string key = kvp.Key;
                if (!v2.TryGetValue(key, out object? v2Value))
                    continue;

                object? v1Value = kvp.Value;
                switch (v1Value)
                {
                    case float f1 when v2Value is float f2:
                        SetAnimValue(key, Interp.Lerp(f1, f2, t));
                        break;
                    case Vector2 vector21 when v2Value is Vector2 vector22:
                        SetAnimValue(key, Vector2.Lerp(vector21, vector22, t));
                        break;
                    case Vector3 vector31 when v2Value is Vector3 vector32:
                        SetAnimValue(key, Vector3.Lerp(vector31, vector32, t));
                        break;
                    case Vector4 vector41 when v2Value is Vector4 vector42:
                        SetAnimValue(key, Vector4.Lerp(vector41, vector42, t));
                        break;
                    case Quaternion quaternion1 when v2Value is Quaternion quaternion2:
                        SetAnimValue(key, Quaternion.Slerp(quaternion1, quaternion2, t));
                        break;
                    default:
                        SetAnimValue(key, t > 0.5f ? v2Value : v1Value);
                        break;
                }
            }
        }

        private void GetTriLerpedValues(MotionBase? m1, MotionBase? m2, MotionBase? m3, float w1, float w2, float w3)
        {
            if (SlotLayout is not null && m1?.SlotLayout is not null && m2?.SlotLayout is not null && m3?.SlotLayout is not null)
            {
                AnimationValueStore.TriLerp(m1.ValueStore, m2.ValueStore, m3.ValueStore, w1, w2, w3, ValueStore);
                return;
            }

            // Legacy fallback
            GetTriLerpedValuesLegacy(m1?._animationValues, m2?._animationValues, m3?._animationValues, w1, w2, w3);
        }

        private void GetTriLerpedValuesLegacy(
            Dictionary<string, object?>? values1,
            Dictionary<string, object?>? values2,
            Dictionary<string, object?>? values3,
            float w1, float w2, float w3)
        {
            if (values1 is null || values2 is null || values3 is null)
                return;

            foreach (var kvp in values1)
            {
                string key = kvp.Key;
                if (!values2.TryGetValue(key, out object? v2Value) ||
                    !values3.TryGetValue(key, out object? v3Value))
                    continue;

                object? v1Value = kvp.Value;
                switch (v1Value)
                {
                    case float f1 when v2Value is float f2 && v3Value is float f3:
                        SetAnimValue(key, Interp.WeightedLerp(f1, f2, f3, w1, w2, w3));
                        break;
                    case Vector2 vector21 when v2Value is Vector2 vector22 && v3Value is Vector2 vector23:
                        SetAnimValue(key, Interp.WeightedLerp(vector21, vector22, vector23, w1, w2, w3));
                        break;
                    case Vector3 vector31 when v2Value is Vector3 vector32 && v3Value is Vector3 vector33:
                        SetAnimValue(key, Interp.WeightedLerp(vector31, vector32, vector33, w1, w2, w3));
                        break;
                    case Vector4 vector41 when v2Value is Vector4 vector42 && v3Value is Vector4 vector43:
                        SetAnimValue(key, Interp.WeightedLerp(vector41, vector42, vector43, w1, w2, w3));
                        break;
                    case Quaternion quaternion1 when v2Value is Quaternion quaternion2 && v3Value is Quaternion quaternion3:
                        SetAnimValue(key, Interp.WeightedSlerp(quaternion1, quaternion2, quaternion3, w1, w2, w3));
                        break;
                    default:
                        float maxW = Math.Max(Math.Max(w1, w2), w3);
                        SetAnimValue(key, maxW == w1 ? v1Value : maxW == w2 ? v2Value : v3Value);
                        break;
                }
            }
        }

        private void GetQuadLerpedValues(MotionBase? m1, MotionBase? m2, MotionBase? m3, MotionBase? m4, float w1, float w2, float w3, float w4)
        {
            if (SlotLayout is not null && m1?.SlotLayout is not null && m2?.SlotLayout is not null && m3?.SlotLayout is not null && m4?.SlotLayout is not null)
            {
                AnimationValueStore.QuadLerp(m1.ValueStore, m2.ValueStore, m3.ValueStore, m4.ValueStore, w1, w2, w3, w4, ValueStore);
                return;
            }

            // Legacy fallback
            GetQuadLerpedValuesLegacy(m1?._animationValues, m2?._animationValues, m3?._animationValues, m4?._animationValues, w1, w2, w3, w4);
        }

        private void GetQuadLerpedValuesLegacy(
            Dictionary<string, object?>? values1,
            Dictionary<string, object?>? values2,
            Dictionary<string, object?>? values3,
            Dictionary<string, object?>? values4,
            float w1, float w2, float w3, float w4)
        {
            if (values1 is null || values2 is null || values3 is null || values4 is null)
                return;

            foreach (var kvp in values1)
            {
                string key = kvp.Key;
                if (!values2.TryGetValue(key, out object? v2Value) ||
                    !values3.TryGetValue(key, out object? v3Value) ||
                    !values4.TryGetValue(key, out object? v4Value))
                    continue;

                object? v1Value = kvp.Value;
                switch (v1Value)
                {
                    case float f1 when v2Value is float f2 && v3Value is float f3 && v4Value is float f4:
                        SetAnimValue(key, Interp.WeightedLerp(f1, f2, f3, f4, w1, w2, w3, w4));
                        break;
                    case Vector2 vector21 when v2Value is Vector2 vector22 && v3Value is Vector2 vector23 && v4Value is Vector2 vector24:
                        SetAnimValue(key, Interp.WeightedLerp(vector21, vector22, vector23, vector24, w1, w2, w3, w4));
                        break;
                    case Vector3 vector31 when v2Value is Vector3 vector32 && v3Value is Vector3 vector33 && v4Value is Vector3 vector34:
                        SetAnimValue(key, Interp.WeightedLerp(vector31, vector32, vector33, vector34, w1, w2, w3, w4));
                        break;
                    case Vector4 vector41 when v2Value is Vector4 vector42 && v3Value is Vector4 vector43 && v4Value is Vector4 vector44:
                        SetAnimValue(key, Interp.WeightedLerp(vector41, vector42, vector43, vector44, w1, w2, w3, w4));
                        break;
                    case Quaternion quaternion1 when v2Value is Quaternion quaternion2 && v3Value is Quaternion quaternion3 && v4Value is Quaternion quaternion4:
                        SetAnimValue(key, Interp.WeightedSlerp(quaternion1, quaternion2, quaternion3, quaternion4, w1, w2, w3, w4));
                        break;
                    default:
                        float maxW = Math.Max(Math.Max(Math.Max(w1, w2), w3), w4);
                        SetAnimValue(key, maxW == w1 ? v1Value : maxW == w2 ? v2Value : maxW == w3 ? v3Value : v4Value);
                        break;
                }
            }
        }

        /// <summary>
        /// Retrieves all animation values and copies them into parentMotion.
        /// 'weight' can be used to lerp between default values and animated values.
        /// </summary>
        /// <param name="parentMotion"></param>
        /// <param name="variables"></param>
        /// <param name="weight"></param>
        public abstract void GetAnimationValues(MotionBase? parentMotion, IDictionary<string, AnimVar> variables, float weight);

        /// <summary>
        /// Blends the animation values of the two given child motions into this motion.
        /// </summary>
        public void Blend(
            MotionBase? m1,
            MotionBase? m2,
            float t,
            IDictionary<string, AnimVar> variables,
            float parentWeight)
        {
            m1?.GetAnimationValues(null, variables, parentWeight);
            m2?.GetAnimationValues(null, variables, parentWeight);
            GetLerpedValues(m1, m2, t);
        }

        /// <summary>
        /// Blends the animation values of the three given child motions into this motion.
        /// </summary>
        public void Blend(
            MotionBase? m1,
            float w1,
            MotionBase? m2,
            float w2,
            MotionBase? m3,
            float w3,
            IDictionary<string, AnimVar> variables,
            float parentWeight)
        {
            m1?.GetAnimationValues(null, variables, parentWeight);
            m2?.GetAnimationValues(null, variables, parentWeight);
            m3?.GetAnimationValues(null, variables, parentWeight);
            GetTriLerpedValues(m1, m2, m3, w1, w2, w3);
        }

        /// <summary>
        /// Blends the animation values of the four given child motions into this motion.
        /// </summary>
        public void Blend(
            MotionBase? m1,
            float w1,
            MotionBase? m2,
            float w2,
            MotionBase? m3,
            float w3,
            MotionBase? m4,
            float w4,
            IDictionary<string, AnimVar> variables,
            float parentWeight)
        {
            m1?.GetAnimationValues(null, variables, parentWeight);
            m2?.GetAnimationValues(null, variables, parentWeight);
            m3?.GetAnimationValues(null, variables, parentWeight);
            m4?.GetAnimationValues(null, variables, parentWeight);
            GetQuadLerpedValues(m1, m2, m3, m4, w1, w2, w3, w4);
        }

        /// <summary>
        /// Retrieves current animation values, and if this is a blend tree, blends them.
        /// </summary>
        /// <param name="variables"></param>
        public void EvaluateRootMotion(IDictionary<string, AnimVar> variables)
            => GetAnimationValues(this, variables, 1.0f);

        /// <summary>
        /// Advances all property animations in the motion by the given delta time.
        /// </summary>
        /// <param name="delta"></param>
        public void TickPropertyAnimations(float delta)
        {
            foreach (AnimationMember curve in _animatedCurves.Values)
                curve.Animation?.Tick(delta);
        }

        public void TickPropertyAnimations(long deltaTicks)
        {
            foreach (AnimationMember curve in _animatedCurves.Values)
                curve.Animation?.Tick(deltaTicks);
        }

        public virtual void Tick(long deltaTicks)
            => Tick(StopwatchTicksToSeconds(deltaTicks));

        protected static long ScaleStopwatchTicks(long deltaTicks, float speed)
            => deltaTicks == 0L || !float.IsFinite(speed) || speed == 0.0f
                ? 0L
                : (long)Math.Round(deltaTicks * (double)speed);

        protected static float StopwatchTicksToSeconds(long deltaTicks)
            => (float)(deltaTicks / (double)Stopwatch.Frequency);

        public abstract void Tick(float delta);
    }
}