using System.Numerics;
using XREngine.Core.Files;
using XREngine.Data;

namespace XREngine.Animation
{
    public abstract class MotionBase : XRAsset
    {
        protected readonly Dictionary<string, AnimationMember> _animatedCurves = [];
        protected internal Dictionary<string, object?> _animationValues = [];

        public Dictionary<string, object?> AnimationValues => _animationValues;
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

            parentObject = member.Initialize?.Invoke(parentObject);

            path += $"{member.MemberName}";

            if (member.MemberType == EAnimationMemberType.Method)
            {
                for (int i = 0; i < member.MethodArguments.Length; i++)
                {
                    path += ":";
                    object? arg = member.MethodArguments[i];
                    if (member.MethodValueArgumentIndex == i)
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

            path += "/";
            foreach (AnimationMember child in member.Children)
                InitializeMember(child, path, layer, owner, parentObject, parentMotions);
        }

        private void SetAnimValue(string path, object? animValue)
        {
            if (!_animationValues.TryAdd(path, animValue))
                _animationValues[path] = animValue;
        }

        /// <summary>
        /// Copies the animation values from the given dictionary to the current motion.
        /// </summary>
        /// <param name="v"></param>
        private void CopyAnimValues(Dictionary<string, object?>? v)
        {
            if (v is null)
                return;

            foreach (var kvp in v)
                SetAnimValue(kvp.Key, kvp.Value);
        }

        /// <summary>
        /// Lerps between two dictionaries of animation values and sets the result in the current motion.
        /// </summary>
        /// <param name="v1"></param>
        /// <param name="v2"></param>
        /// <param name="t"></param>
        private void GetLerpedValues(
            Dictionary<string, object?>? v1,
            Dictionary<string, object?>? v2,
            float t)
        {
            IEnumerable<string> keys =
                (v1?.Keys ?? Enumerable.Empty<string>()).Union
                (v2?.Keys ?? Enumerable.Empty<string>()).Distinct();

            foreach (string key in keys)
            {
                //Leave values that don't match alone
                if (!(v1?.TryGetValue(key, out object? v1Value) ?? false) ||
                    !(v2?.TryGetValue(key, out object? v2Value) ?? false))
                    continue;

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
                    default: //Pick the discrete value with the higher weight
                        SetAnimValue(key, t > 0.5f ? v2Value : v1Value);
                        break;
                }
            }
        }

        /// <summary>
        /// Lerps between three dictionaries of animation values and sets the result in the current motion.
        /// </summary>
        /// <param name="values1"></param>
        /// <param name="values2"></param>
        /// <param name="values3"></param>
        /// <param name="w1"></param>
        /// <param name="w2"></param>
        /// <param name="w3"></param>
        private void GetTriLerpedValues(
            Dictionary<string, object?>? values1,
            Dictionary<string, object?>? values2,
            Dictionary<string, object?>? values3,
            float w1,
            float w2,
            float w3)
        {
            IEnumerable<string> keys =
                (values1?.Keys ?? Enumerable.Empty<string>()).Union
                (values2?.Keys ?? Enumerable.Empty<string>()).Union
                (values3?.Keys ?? Enumerable.Empty<string>()).Distinct();

            foreach (string key in keys)
            {
                //Leave values that don't match alone
                if (!(values1?.TryGetValue(key, out object? v1Value) ?? false) ||
                    !(values2?.TryGetValue(key, out object? v2Value) ?? false) ||
                    !(values3?.TryGetValue(key, out object? v3Value) ?? false))
                    continue;

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
                    default: //Pick the discrete value with the highest weight
                        SetAnimValue(key, 
                            Math.Max(Math.Max(w1, w2), w3) == w1 ? v1Value : 
                            Math.Max(w2, w3) == w2 ? v2Value : 
                            v3Value);
                        break;
                }
            }
        }

        /// <summary>
        /// Lerps between four dictionaries of animation values and sets the result in the current motion.
        /// </summary>
        /// <param name="values1"></param>
        /// <param name="values2"></param>
        /// <param name="values3"></param>
        /// <param name="values4"></param>
        /// <param name="w1"></param>
        /// <param name="w2"></param>
        /// <param name="w3"></param>
        /// <param name="w4"></param>
        private void GetQuadLerpedValues(
            Dictionary<string, object?>? values1,
            Dictionary<string, object?>? values2,
            Dictionary<string, object?>? values3,
            Dictionary<string, object?>? values4,
            float w1,
            float w2,
            float w3,
            float w4)
        {
            IEnumerable<string> keys = 
                (values1?.Keys ?? Enumerable.Empty<string>()).Union
                (values2?.Keys ?? Enumerable.Empty<string>()).Union
                (values3?.Keys ?? Enumerable.Empty<string>()).Union
                (values4?.Keys ?? Enumerable.Empty<string>()).Distinct();

            foreach (string key in keys)
            {
                //Leave values that don't match alone
                if (!(values1?.TryGetValue(key, out object? v1Value) ?? false) ||
                    !(values2?.TryGetValue(key, out object? v2Value) ?? false) ||
                    !(values3?.TryGetValue(key, out object? v3Value) ?? false) ||
                    !(values4?.TryGetValue(key, out object? v4Value) ?? false))
                    continue;

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
                    default: //Pick the discrete value with the highest weight
                        SetAnimValue(key,
                            Math.Max(Math.Max(Math.Max(w1, w2), w3), w4) == w1 ? v1Value :
                            Math.Max(Math.Max(w2, w3), w2) == w2 ? v2Value :
                            Math.Max(Math.Max(w3, w4), w3) == w3 ? v3Value : 
                            v4Value);
                        break;
                }
            }
        }

        public virtual void GetAnimationValues()
        {
            foreach (var kvp in _animatedCurves)
            {
                if (kvp.Value.Animation is null && kvp.Value.MemberType != EAnimationMemberType.Method)
                    continue;

                var value = kvp.Value.GetAnimationValue();
                if (!_animationValues.TryAdd(kvp.Key, value))
                    _animationValues[kvp.Key] = value;
            }
        }

        /// <summary>
        /// Blends the animation values of the given child motions into this motion.
        /// </summary>
        /// <param name="parentMotion"></param>
        /// <param name="motion"></param>
        public void Blend(
            MotionBase? motion)
            => CopyAnimValues(motion?._animationValues);

        /// <summary>
        /// Blends the animation values of the given child motions into this motion.
        /// </summary>
        /// <param name="parentMotion"></param>
        /// <param name="m1"></param>
        /// <param name="m2"></param>
        /// <param name="t"></param>
        public void Blend(
            MotionBase? m1,
            MotionBase? m2,
            float t)
            => GetLerpedValues(m1?._animationValues, m2?._animationValues, t);

        /// <summary>
        /// Blends the animation values of the given child motions into this motion.
        /// </summary>
        /// <param name="parentMotion"></param>
        /// <param name="m1"></param>
        /// <param name="w1"></param>
        /// <param name="m2"></param>
        /// <param name="w2"></param>
        /// <param name="m3"></param>
        /// <param name="w3"></param>
        public void Blend(
            MotionBase? m1,
            float w1,
            MotionBase? m2,
            float w2,
            MotionBase? m3,
            float w3)
            => GetTriLerpedValues(m1?._animationValues, m2?._animationValues, m3?._animationValues, w1, w2, w3);

        /// <summary>
        /// Blends the animation values of the given child motions into this motion.
        /// </summary>
        /// <param name="parentMotion"></param>
        /// <param name="m1"></param>
        /// <param name="w1"></param>
        /// <param name="m2"></param>
        /// <param name="w2"></param>
        /// <param name="m3"></param>
        /// <param name="w3"></param>
        /// <param name="m4"></param>
        /// <param name="w4"></param>
        public void Blend(
            MotionBase? m1,
            float w1,
            MotionBase? m2,
            float w2,
            MotionBase? m3,
            float w3,
            MotionBase? m4,
            float w4)
            => GetQuadLerpedValues(m1?._animationValues, m2?._animationValues, m3?._animationValues, m4?._animationValues, w1, w2, w3, w4);

        public void EvaluateMotion(IDictionary<string, AnimVar> variables)
        {
            GetAnimationValues();
            BlendAnimationValues(variables);
        }

        public abstract void BlendAnimationValues(IDictionary<string, AnimVar> variables);

        /// <summary>
        /// Advances all property animations in the motion by the given delta time.
        /// </summary>
        /// <param name="delta"></param>
        public void TickPropertyAnimations(float delta)
        {
            foreach (AnimationMember curve in _animatedCurves.Values)
                curve.Animation?.Tick(delta);
        }

        public abstract void Tick(float delta);
    }
}