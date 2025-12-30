using System.Numerics;
using MemoryPack;
using XREngine.Animation.IK;
using XREngine.Data;
using XREngine.Data.MMD;

namespace XREngine.Animation
{

    /// <summary>
    /// Represents a single animation clip that can be played with an AnimationClipComponent or an AnimStateMachineComponent.
    /// </summary>
    [XR3rdPartyExtensions(typeof(XREngine.Data.XRDefault3rdPartyImportOptions), "vmd")]
    [MemoryPackable]
    public partial class AnimationClip : MotionBase
    {
        public override string ToString()
            => $"AnimationClip: {Name}";

        private EAnimTreeTraversalMethod _traversalMethod = EAnimTreeTraversalMethod.Parallel;
        public EAnimTreeTraversalMethod TraversalMethod
        {
            get => _traversalMethod;
            set => SetField(ref _traversalMethod, value);
        }

        [MemoryPackConstructor]
        public AnimationClip()
            : base() { }
        public AnimationClip(AnimationMember rootFolder)
            : this() => RootMember = rootFolder;
        public AnimationClip(string animationName, string memberPath, BasePropAnim anim) : this()
        {
            Name = animationName;

            string[] memberPathParts = memberPath.Split('.');
            AnimationMember? last = null;

            foreach (string childMemberName in memberPathParts)
            {
                AnimationMember member = new(childMemberName);

                if (last is null)
                    RootMember = member;
                else
                    last.Children.Add(member);

                last = member;
            }

            LengthInSeconds = anim.LengthInSeconds;
            Looped = anim.Looped;
            if (last != null)
                last.Animation = anim;
        }

        private float _lengthInSeconds = 0.0f;
        /// <summary>
        /// The length of the longest included sub-animation in seconds.
        /// </summary>
        public float LengthInSeconds
        {
            get => _lengthInSeconds;
            set => SetField(ref _lengthInSeconds, value);
        }

        private bool _looped = false;
        /// <summary>
        /// Whether or not the animation should loop when the longest sub-animation reaches the end.
        /// </summary>
        public bool Looped
        {
            get => _looped;
            set => SetField(ref _looped, value);
        }

        private int _totalAnimCount = 0;
        public int TotalAnimCount
        {
            get => _totalAnimCount;
            set => SetField(ref _totalAnimCount, value);
        }

        private int _endedAnimations = 0;

        [MemoryPackIgnore]
        private AnimationMember? _rootMember;

        [MemoryPackIgnore]
        public AnimationMember? RootMember
        {
            get => _rootMember;
            set => SetField(ref _rootMember, value);
        }

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(RootMember):
                        //_rootMember?.Unregister(this);
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
                case nameof(RootMember):
                    //TotalAnimCount = _rootMember?.Register(this) ?? 0;
                    break;
            }
        }

        internal void AnimationHasEnded(BaseAnimation obj)
        {
            //if (Interlocked.Increment(ref _endedAnimations) >= _totalAnimCount)
                //AllAnimationsEnded();
            //else
            //    Debug.WriteLine($"Animation {obj.Name} ended, {TotalAnimCount - _endedAnimations} remaining.");
        }

        private void AllAnimationsEnded()
        {
            if (Looped)
            {
                _rootMember?.StartAnimations();
                _endedAnimations = 0;
            }
            else
                OnAnimationEnded();
        }

        private void OnAnimationEnded()
        {
            _rootMember?.Unregister(this);
            IsPlaying = false;
            AnimationEnded?.Invoke(this);
        }

        public event Action<AnimationClip>? AnimationStarted;
        public event Action<AnimationClip>? AnimationEnded;

        public bool IsPlaying { get; private set; } = false;

        public Dictionary<string, BasePropAnim> GetAllAnimations()
        {
            Dictionary<string, BasePropAnim> anims = [];
            _rootMember?.CollectAnimations(null, anims);
            return anims;
        }
        public void Start(object? rootObject)
        {
            IsPlaying = true;
            TotalAnimCount = _rootMember?.Register(this, true) ?? 0;
            AnimationStarted?.Invoke(this);
        }
        public void Stop()
        {
            if (_endedAnimations < _totalAnimCount)
                _rootMember?.StopAnimations();
        }

        public override void GetAnimationValues(MotionBase? parentMotion, IDictionary<string, AnimVar> variables, float weight)
        {
            foreach (var kvp in _animatedCurves)
            {
                if (kvp.Value.Animation is null && kvp.Value.MemberType != EAnimationMemberType.Method)
                {
                    SetAnimValue(kvp.Key, kvp.Value.DefaultValue);
                    continue;
                }
                
                //if (weight < float.Epsilon)
                //    SetAnimValue(kvp.Key, kvp.Value.DefaultValue);
                //else if (weight > 1.0f - float.Epsilon)
                //    SetAnimValue(kvp.Key, kvp.Value.GetAnimationValue());
                //else
                //{
                    object? defaultvalue = kvp.Value.DefaultValue;
                    object? animatedValue = kvp.Value.GetAnimationValue();
                    object? weightedValue = Lerp(defaultvalue, animatedValue, weight);
                    SetAnimValue(kvp.Key, weightedValue);
                //}
            }
            parentMotion?.CopyAnimationValuesFrom(this);
        }

        private static object? Lerp(object? defaultvalue, object? animatedValue, float weight) => defaultvalue switch
        {
            float df when animatedValue is float af => Interp.Lerp(df, af, weight),
            Vector2 df2 when animatedValue is Vector2 af2 => Vector2.Lerp(df2, af2, weight),
            Vector3 df3 when animatedValue is Vector3 af3 => Vector3.Lerp(df3, af3, weight),
            Vector4 df4 when animatedValue is Vector4 af4 => Vector4.Lerp(df4, af4, weight),
            Quaternion dfq when animatedValue is Quaternion afq => Quaternion.Slerp(dfq, afq, weight),
            _ => weight > 0.5f ? animatedValue : defaultvalue, //Discrete; choose closest value
        };

        public override void Tick(float delta)
        {
            TickPropertyAnimations(delta);
        }

        public override bool Load3rdParty(string filePath)
        {
            if (filePath.EndsWith(".vmd"))
            {
                VMDFile vmd = new();
                vmd.Load(filePath);
                LoadFromVMD(vmd);
                return true;
            }
            return false;
        }

        public void LoadFromVMD(VMDFile vmd)
        {
            const float fps = 30.0f;
            LengthInSeconds = vmd.MaxFrameCount / fps;
            AssembleVMDTree(vmd, fps);
        }

        /// <summary>
        /// Creates 4 quaternion control points for a cubic Bézier curve between start and end,
        /// using cp1 and cp2 (from a 2D easing curve defined on [0,1]²) to control the tangents.
        /// </summary>
        public static void BezierCurveToControlPoints(Quaternion start, Quaternion end, Vector2 cp1, Vector2 cp2, out Quaternion startCP, out Quaternion endCP)
        {
            Quaternion q0 = start;
            Quaternion q3 = end;

            // Compute the relative rotation from start to end
            Quaternion delta = Quaternion.Inverse(q0) * q3;
            Vector3 logDelta = QuaternionLog(delta);

            // Use the y-values of the 2D control points as easing factors:
            // Q1 rotates from q0 by a fraction cp1.y of the total rotation.
            startCP = q0 * QuaternionExp(logDelta * cp1.Y);
            // Q2 rotates backwards from q3 by a fraction (1 - cp2.y) of the total rotation.
            endCP = q3 * QuaternionExp(-logDelta * (1 - cp2.Y));
        }

        public static void BezierCurveToControlPoints(float start, float end, Vector2 cp1, Vector2 cp2, out float startCP, out float endCP)
        {
            float p0 = start;
            float p3 = end;
            // Use the y-values of the 2D control points as easing factors:
            // P1 moves from p0 by a fraction cp1.y of the total distance.
            startCP = p0 + (p3 - p0) * cp1.Y;
            // P2 moves backwards from p3 by a fraction (1 - cp2.y) of the total distance.
            endCP = p3 + (p0 - p3) * (1 - cp2.Y);
        }
        public static void BezierCurveToControlPoints(Vector3 start, Vector3 end, Vector2 cp1, Vector2 cp2, out Vector3 startCP, out Vector3 endCP)
        {
            Vector3 p0 = start;
            Vector3 p3 = end;
            // Use the y-values of the 2D control points as easing factors:
            // P1 moves from p0 by a fraction cp1.y of the total distance.
            startCP = p0 + (p3 - p0) * cp1.Y;
            // P2 moves backwards from p3 by a fraction (1 - cp2.y) of the total distance.
            endCP = p3 + (p0 - p3) * (1 - cp2.Y);
        }

        /// <summary>
        /// Computes the logarithm of a unit quaternion.
        /// The result is a vector representing the "angle-axis" (with angle = |v|) in the tangent space.
        /// </summary>
        public static Vector3 QuaternionLog(Quaternion q)
        {
            // Ensure the quaternion is normalized.
            if (q.W > 1f)
                q = Quaternion.Normalize(q);

            float angle = MathF.Acos(q.W);
            float sinAngle = MathF.Sin(angle);
            if (MathF.Abs(sinAngle) > 0.0001f)
                return new Vector3(q.X, q.Y, q.Z) * (angle / sinAngle);
            else
                return new Vector3(q.X, q.Y, q.Z); // small-angle approximation
        }

        /// <summary>
        /// Computes the exponential of a pure imaginary quaternion (represented as a Vector3).
        /// This returns a unit quaternion.
        /// </summary>
        public static Quaternion QuaternionExp(Vector3 v)
        {
            float angle = v.Length();
            float sinAngle = MathF.Sin(angle);
            Quaternion result;
            if (MathF.Abs(angle) > 0.0001f)
            {
                result = new Quaternion(
                    v.X * (sinAngle / angle),
                    v.Y * (sinAngle / angle),
                    v.Z * (sinAngle / angle),
                    MathF.Cos(angle));
            }
            else
            {
                result = new Quaternion(v.X, v.Y, v.Z, 1.0f); // small-angle approximation
            }
            return Quaternion.Normalize(result);
        }
        private void AssembleVMDTree(VMDFile vmd, float fps)
        {
            RootMember = new("SceneNode", EAnimationMemberType.Property);
            AnimationMember? ikRoot = null;
            if (vmd.BoneAnimation is not null)
            {
                foreach (var bone in vmd.BoneAnimation)
                {
                    PropAnimFloat xAnim = new((int)vmd.MaxFrameCount, fps, false, true);
                    PropAnimFloat yAnim = new((int)vmd.MaxFrameCount, fps, false, true);
                    PropAnimFloat zAnim = new((int)vmd.MaxFrameCount, fps, false, true);
                    PropAnimQuaternion rotAnim = new((int)vmd.MaxFrameCount, fps, false, true);
                    PopulateVMDAnimation(fps, bone, xAnim, yAnim, zAnim, rotAnim);

                    //ConstrainAndLerpFloat(fps, xAnim);
                    //ConstrainAndLerpFloat(fps, yAnim);
                    //ConstrainAndLerpFloat(fps, zAnim);
                    //ConstrainAndLerpQuat(fps, rotAnim);

                    if (bone.Key.Contains("IK"))
                    {
                        if (ikRoot is null)
                        {
                            ikRoot = new AnimationMember("GetComponent", EAnimationMemberType.Method)
                            {
                                MethodArguments = ["HumanoidIKSolverComponent"],
                                CacheReturnValue = true, //Cache this method call so we don't have to search for the humanoid every frame
                            };
                            RootMember.Children.Add(ikRoot);
                        }

                        if (bone.Key.Contains("Foot"))
                        {
                            bool left = bone.Key.Contains('L');
                            ELimbEndEffector eff = left ? ELimbEndEffector.LeftFoot : ELimbEndEffector.RightFoot;
                            AnimationMember boneX = new("SetIKPositionX", EAnimationMemberType.Method, xAnim)
                            {
                                MethodArguments = [eff, 0.0f],
                                AnimatedMethodArgumentIndex = 1,
                            };
                            ikRoot.Children.Add(boneX);

                            AnimationMember boneY = new("SetIKPositionY", EAnimationMemberType.Method, yAnim)
                            {
                                MethodArguments = [eff, 0.0f],
                                AnimatedMethodArgumentIndex = 1,
                            };
                            ikRoot.Children.Add(boneY);

                            AnimationMember boneZ = new("SetIKPositionZ", EAnimationMemberType.Method, zAnim)
                            {
                                MethodArguments = [eff, 0.0f],
                                AnimatedMethodArgumentIndex = 1,
                            };
                            ikRoot.Children.Add(boneZ);

                            AnimationMember boneRot = new("SetIKRotation", EAnimationMemberType.Method, rotAnim)
                            {
                                MethodArguments = [eff, Quaternion.Identity],
                                AnimatedMethodArgumentIndex = 1,
                            };
                            ikRoot.Children.Add(boneRot);
                        }
                    }
                    else
                    {
                        //Trace.WriteLine($"Bone: {bone.Key}");
                        var getBone = new AnimationMember("FindDescendantByName", EAnimationMemberType.Method)
                        {
                            MethodArguments = [bone.Key, StringComparison.InvariantCultureIgnoreCase],
                            CacheReturnValue = true, //Cache this method call so we don't have to search for the bone every frame
                        };
                        RootMember.Children.Add(getBone);

                        var transform = new AnimationMember("Transform", EAnimationMemberType.Property);
                        getBone.Children.Add(transform);

                        AnimationMember boneX = new("SetBindRelativeX", EAnimationMemberType.Method, xAnim) { MethodArguments = [0.0f] };
                        transform.Children.Add(boneX);

                        AnimationMember boneY = new("SetBindRelativeY", EAnimationMemberType.Method, yAnim) { MethodArguments = [0.0f] };
                        transform.Children.Add(boneY);

                        AnimationMember boneZ = new("SetBindRelativeZ", EAnimationMemberType.Method, zAnim) { MethodArguments = [0.0f] };
                        transform.Children.Add(boneZ);

                        AnimationMember boneRot = new("SetBindRelativeRotation", EAnimationMemberType.Method, rotAnim) { MethodArguments = [Quaternion.Identity] };
                        transform.Children.Add(boneRot);
                    }
                }
            }
            if (vmd.ShapeKeyAnimation is not null)
            {
                //foreach (var morph in vmd.ShapeKeyAnimation)
                //{
                //    var getMorph = new AnimationMember("SetBlendshapeValue", EAnimationMemberType.Method)
                //    {
                //        MethodArguments = [morph.Key, StringComparison.InvariantCultureIgnoreCase],
                //        CacheReturnValue = true, //Cache this method call so we don't have to search for the meshes every frame
                //    };
                //    morphMember.Children.Add(morphAnim);
                //    PropAnimFloat morphAnimFloat = new((int)vmd.MaxFrameCount, fps, false, true);
                //    AnimationMember morphFloat = new("SetMorph");
                //    morphAnim.Children.Add(morphFloat);
                //    foreach (var frame in morph.Value)
                //        morphAnimFloat.Keyframes.Add(new FloatKeyframe((int)frame.Key, fps, frame.Value.Weight, 0.0f, EVectorInterpType.Step));
                //}
            }

            static void ConstrainAndLerpFloat(float fps, PropAnimFloat anim)
            {
                anim.ConstrainKeyframedFPS = true;
                anim.BakedFramesPerSecond = fps;
                anim.LerpConstrainedFPS = true;
            }
            static void ConstrainAndLerpQuat(float fps, PropAnimQuaternion anim)
            {
                anim.ConstrainKeyframedFPS = true;
                anim.BakedFramesPerSecond = fps;
                anim.LerpConstrainedFPS = true;
            }
        }

        private static void PopulateVMDAnimation(
            float fps,
            KeyValuePair<string, FrameDictionary<BoneFrameKey>> bone,
            PropAnimFloat xAnim,
            PropAnimFloat yAnim,
            PropAnimFloat zAnim,
            PropAnimQuaternion rotAnim)
        {
            var frames = bone.Value.ToArray();
            for (int i = 0; i < frames.Length; i++)
            {
                KeyValuePair<uint, BoneFrameKey> frame = frames[i];
                bool firstFrame = i == 0;
                bool lastFrame = i == frames.Length - 1;
                var data = frame.Value;
                var lastData = lastFrame ? data : frames[i + 1].Value;
                var nextData = firstFrame ? data : frames[i - 1].Value;

                BezierCurveToControlPoints(
                    lastData.Translation.X,
                    data.Translation.X,
                    lastData.TranslationXBezier!.StartControlPoint,
                    data.TranslationXBezier!.EndControlPoint,
                    out _,
                    out float xOutTan);
                BezierCurveToControlPoints(
                    data.Translation.X,
                    nextData.Translation.X,
                    lastData.TranslationXBezier!.StartControlPoint,
                    data.TranslationXBezier!.EndControlPoint,
                    out float xInTan,
                    out _);
                xAnim.Keyframes.Add(new FloatKeyframe((int)frame.Key, fps, data.Translation.X, xOutTan, xInTan, EVectorInterpType.Smooth));

                BezierCurveToControlPoints(
                    lastData.Translation.Y,
                    data.Translation.Y,
                    lastData.TranslationYBezier!.StartControlPoint,
                    data.TranslationYBezier!.EndControlPoint,
                    out _,
                    out float yOutTan);
                BezierCurveToControlPoints(
                    data.Translation.Y,
                    nextData.Translation.Y,
                    lastData.TranslationYBezier!.StartControlPoint,
                    data.TranslationYBezier!.EndControlPoint,
                    out float yInTan,
                    out _);
                yAnim.Keyframes.Add(new FloatKeyframe((int)frame.Key, fps, data.Translation.Y, yOutTan, yInTan, EVectorInterpType.Smooth));

                BezierCurveToControlPoints(
                    lastData.Translation.Z,
                    data.Translation.Z,
                    lastData.TranslationZBezier!.StartControlPoint,
                    data.TranslationZBezier!.EndControlPoint,
                    out _,
                    out float zOutTan);
                BezierCurveToControlPoints(
                    data.Translation.Z,
                    nextData.Translation.Z,
                    lastData.TranslationZBezier!.StartControlPoint,
                    data.TranslationZBezier!.EndControlPoint,
                    out float zInTan,
                    out _);
                zAnim.Keyframes.Add(new FloatKeyframe((int)frame.Key, fps, data.Translation.Z, zOutTan, zInTan, EVectorInterpType.Smooth));

                BezierCurveToControlPoints(
                    lastData.Rotation,
                    data.Rotation,
                    lastData.RotationBezier!.StartControlPoint,
                    data.RotationBezier!.EndControlPoint,
                    out _,
                    out Quaternion outRotTan);
                BezierCurveToControlPoints(
                    data.Rotation,
                    nextData.Rotation,
                    lastData.RotationBezier!.StartControlPoint,
                    data.RotationBezier!.EndControlPoint,
                    out Quaternion inRotTan,
                    out _);
                rotAnim.Keyframes.Add(new QuaternionKeyframe((int)frame.Key, fps, data.Rotation, outRotTan, inRotTan, ERadialInterpType.Smooth));
            }
        }
    }
}
