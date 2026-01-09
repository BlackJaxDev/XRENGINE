# C# `new` allocations report

Generated: 2026-01-08 17:00:24

Scan roots:
- XRENGINE
- XREngine.Animation
- XREngine.Audio
- XREngine.Data
- XREngine.Editor
- XREngine.Extensions
- XREngine.Input
- XREngine.Modeling
- XREngine.Server
- XREngine.UnitTests
- XREngine.VRClient

Search patterns:
- \bnew\s+[A-Za-z_@][\w\.@]*
- \bnew\s*\(\)
- \bnew\s*\[\]

Excluded paths (regex):
- \\Submodules\\|\\Build\\Submodules\\|\\bin\\|\\obj\\

Notes:
- Comment-only lines (//, ///, /*, *, */) are skipped to reduce false positives.
- Matches inside string literals are skipped to reduce false positives (e.g., "Default ... new ...").


## XREngine.Animation/AnimationCurve.cs
- L17 C22: new() :: Linear = new();
- L18 C22: new() :: Smooth = new();
- L19 C20: new() :: Step = new();
- L20 C23: new() :: EaseOut = new();
- L21 C22: new() :: EaseIn = new();
- L24 C17: new FloatKeyframe :: new FloatKeyframe(0.0f, 0.0f, 0.0f, EVectorInterpType.Linear),
- L25 C17: new FloatKeyframe :: new FloatKeyframe(1.0f, 1.0f, 0.0f, EVectorInterpType.Linear));
- L27 C17: new FloatKeyframe :: new FloatKeyframe(0.0f, 0.0f, 0.0f, EVectorInterpType.Smooth),
- L28 C17: new FloatKeyframe :: new FloatKeyframe(1.0f, 1.0f, 0.0f, EVectorInterpType.Smooth));
- L30 C17: new FloatKeyframe :: new FloatKeyframe(0.0f, 0.0f, 0.0f, EVectorInterpType.Step),
- L31 C17: new FloatKeyframe :: new FloatKeyframe(1.0f, 1.0f, 0.0f, EVectorInterpType.Step));
- L33 C17: new FloatKeyframe :: new FloatKeyframe(0.0f, 0.0f, 0.0f, EVectorInterpType.Smooth),
- L34 C17: new FloatKeyframe :: new FloatKeyframe(1.0f, 1.0f, 0.0f, EVectorInterpType.Smooth));
- L36 C17: new FloatKeyframe :: new FloatKeyframe(0.0f, 0.0f, 0.0f, EVectorInterpType.Smooth),
- L37 C17: new FloatKeyframe :: new FloatKeyframe(1.0f, 1.0f, 0.0f, EVectorInterpType.Smooth));


## XREngine.Animation/Interfaces/ICartesianKeyframe.cs
- L5 C9: new T :: new T InValue { get; set; }
- L6 C9: new T :: new T OutValue { get; set; }
- L7 C9: new T :: new T InTangent { get; set; }
- L8 C9: new T :: new T OutTangent { get; set; }


## XREngine.Animation/Interfaces/IPlanarKeyframeT.cs
- L5 C9: new T :: new T InValue { get; set; }
- L6 C9: new T :: new T OutValue { get; set; }
- L7 C9: new T :: new T InTangent { get; set; }
- L8 C9: new T :: new T OutTangent { get; set; }


## XREngine.Animation/Keyframes/Keyframe.cs
- L139 C27: new Exception :: throw new Exception();
- L161 C23: new Exception :: throw new Exception();


## XREngine.Animation/Keyframes/KeyframeTrack.cs
- L11 C108: new() :: public class KeyframeTrack<T> : BaseKeyframeTrack, IList, IList<T>, IEnumerable<T> where T : Keyframe, new()
- L57 C43: new object :: public object SyncRoot { get; } = new object();
- L76 C23: new IndexOutOfRangeException :: throw new IndexOutOfRangeException();
- L107 C23: new IndexOutOfRangeException :: throw new IndexOutOfRangeException();
- L267 C19: new System.NotImplementedException :: throw new System.NotImplementedException();
- L272 C19: new System.NotImplementedException :: throw new System.NotImplementedException();
- L277 C19: new System.NotImplementedException :: throw new System.NotImplementedException();
- L282 C19: new System.NotImplementedException :: throw new System.NotImplementedException();
- L287 C19: new System.NotImplementedException :: throw new System.NotImplementedException();
- L292 C19: new System.NotImplementedException :: throw new System.NotImplementedException();
- L297 C19: new System.NotImplementedException :: throw new System.NotImplementedException();
- L302 C19: new System.NotImplementedException :: throw new System.NotImplementedException();


## XREngine.Animation/Keyframes/VectorKeyframe.cs
- L17 C26: new T :: : this(0.0f, new T(), new T(), EVectorInterpType.Smooth) { }
- L17 C35: new T :: : this(0.0f, new T(), new T(), EVectorInterpType.Smooth) { }
- L169 C16: new VectorKeyframe :: public new VectorKeyframe<T>? Next
- L174 C16: new VectorKeyframe :: public new VectorKeyframe<T>? Prev
- L397 C79: new T :: return type == EVectorValueType.Position ? OutValue : new T();
- L425 C74: new T :: return type == EVectorValueType.Position ? InValue : new T();
- L463 C79: new T :: return type == EVectorValueType.Position ? OutValue : new T();
- L496 C74: new T :: return type == EVectorValueType.Position ? InValue : new T();
- L539 C36: new T :: velocity = new T();
- L540 C40: new T :: acceleration = new T();
- L594 C36: new T :: velocity = new T();
- L595 C40: new T :: acceleration = new T();
- L608 C69: new () :: => (diff / span) < 1.0f ? OutValue : (next?.OutValue ?? new ());
- L610 C16: new() :: => new();
- L612 C16: new() :: => new();
- L617 C16: new() :: => new();
- L624 C67: new() :: => (diff / span) < 1.0f ? InValue : (prev?.InValue ?? new());
- L626 C16: new() :: => new();
- L628 C16: new() :: => new();
- L633 C16: new() :: => new();


## XREngine.Animation/Property/BoolKeyframe.cs
- L17 C16: new BoolKeyframe :: public new BoolKeyframe? Next
- L22 C16: new BoolKeyframe :: public new BoolKeyframe? Prev


## XREngine.Animation/Property/Core/AnimationClip.cs
- L215 C31: new() :: VMDFile vmd = new();
- L284 C24: new Vector3 :: return new Vector3(q.X, q.Y, q.Z) * (angle / sinAngle);
- L286 C24: new Vector3 :: return new Vector3(q.X, q.Y, q.Z); // small-angle approximation
- L300 C26: new Quaternion :: result = new Quaternion(
- L308 C26: new Quaternion :: result = new Quaternion(v.X, v.Y, v.Z, 1.0f); // small-angle approximation
- L335 C38: new AnimationMember :: ikRoot = new AnimationMember("GetComponent", EAnimationMemberType.Method)
- L379 C39: new AnimationMember :: var getBone = new AnimationMember("FindDescendantByName", EAnimationMemberType.Method)
- L386 C41: new AnimationMember :: var transform = new AnimationMember("Transform", EAnimationMemberType.Property);
- L467 C37: new FloatKeyframe :: xAnim.Keyframes.Add(new FloatKeyframe((int)frame.Key, fps, data.Translation.X, xOutTan, xInTan, EVectorInterpType.Smooth));
- L483 C37: new FloatKeyframe :: yAnim.Keyframes.Add(new FloatKeyframe((int)frame.Key, fps, data.Translation.Y, yOutTan, yInTan, EVectorInterpType.Smooth));
- L499 C37: new FloatKeyframe :: zAnim.Keyframes.Add(new FloatKeyframe((int)frame.Key, fps, data.Translation.Z, zOutTan, zInTan, EVectorInterpType.Smooth));
- L515 C39: new QuaternionKeyframe :: rotAnim.Keyframes.Add(new QuaternionKeyframe((int)frame.Key, fps, data.Rotation, outRotTan, inRotTan, ERadialInterpType.Smooth));


## XREngine.Animation/Property/Core/AnimationMember.cs
- L27 C31: new AnimationMember :: _children.Add(new AnimationMember(remainingPath));
- L50 C35: new AnimationMember :: _children.Add(new AnimationMember(remainingPath));
- L241 C46: new object :: private object?[] _methodArguments = new object?[1];
- L439 C21: new AnimationMember :: new AnimationMember("FindDescendantByName", EAnimationMemberType.Method)
- L446 C29: new AnimationMember :: new AnimationMember("GetComponent", EAnimationMemberType.Method)


## XREngine.Animation/Property/Core/BlendTree2D.cs
- L84 C55: new Child :: private readonly Child?[] _boundingChildren = new Child?[4];
- L95 C56: new ChildWeight :: private readonly ChildWeight[] _childWeights = new ChildWeight[4];
- L158 C30: new Child :: _sortedByX = new Child[_children.Count];
- L159 C30: new Child :: _sortedByY = new Child[_children.Count];
- L292 C49: new ChildWeight :: _childWeights[_weightCount++] = new ChildWeight { Child = a, Weight = 1.0f };
- L303 C45: new ChildWeight :: _childWeights[_weightCount++] = new ChildWeight { Child = a, Weight = 1.0f - t };
- L304 C45: new ChildWeight :: _childWeights[_weightCount++] = new ChildWeight { Child = b, Weight = t };
- L324 C49: new ChildWeight :: _childWeights[_weightCount++] = new ChildWeight { Child = a, Weight = 1.0f };
- L329 C49: new ChildWeight :: _childWeights[_weightCount++] = new ChildWeight { Child = b, Weight = 1.0f };
- L342 C45: new ChildWeight :: _childWeights[_weightCount++] = new ChildWeight { Child = a, Weight = weightA };
- L343 C45: new ChildWeight :: _childWeights[_weightCount++] = new ChildWeight { Child = b, Weight = weightB };
- L358 C49: new ChildWeight :: _childWeights[_weightCount++] = new ChildWeight { Child = nearest, Weight = 1.0f };
- L367 C85: new() :: List<(Child child, float angleDist, float dotProduct)> childDistances = new();
- L399 C49: new ChildWeight :: _childWeights[_weightCount++] = new ChildWeight { Child = nearest, Weight = 1.0f };
- L408 C31: new float :: float[] weights = new float[count];
- L415 C49: new ChildWeight :: _childWeights[_weightCount++] = new ChildWeight
- L437 C53: new ChildWeight :: _childWeights[_weightCount++] = new ChildWeight
- L447 C49: new ChildWeight :: _childWeights[_weightCount++] = new ChildWeight
- L507 C58: new Child :: int leftIdx = Array.BinarySearch(_sortedByX, new Child { PositionX = x }, _xComp);
- L581 C53: new ChildWeight :: _childWeights[_weightCount++] = new ChildWeight { Child = _boundingChildren[0]!, Weight = 1.0f };
- L620 C33: new Child :: Child[] remaining = new Child[2];
- L656 C49: new ChildWeight :: _childWeights[_weightCount++] = new ChildWeight { Child = nearest, Weight = 1.0f };
- L667 C45: new ChildWeight :: _childWeights[_weightCount++] = new ChildWeight
- L672 C45: new ChildWeight :: _childWeights[_weightCount++] = new ChildWeight
- L677 C45: new ChildWeight :: _childWeights[_weightCount++] = new ChildWeight
- L682 C45: new ChildWeight :: _childWeights[_weightCount++] = new ChildWeight
- L726 C53: new ChildWeight :: _childWeights[_weightCount++] = new ChildWeight { Child = _boundingChildren[0]!, Weight = 1.0f };
- L769 C49: new ChildWeight :: _childWeights[_weightCount++] = new ChildWeight { Child = nearest, Weight = 1.0f };
- L790 C49: new ChildWeight :: _childWeights[_weightCount++] = new ChildWeight { Child = a, Weight = 1.0f };
- L805 C45: new ChildWeight :: _childWeights[_weightCount++] = new ChildWeight { Child = a, Weight = 1.0f - t };
- L806 C45: new ChildWeight :: _childWeights[_weightCount++] = new ChildWeight { Child = b, Weight = t };
- L839 C45: new ChildWeight :: _childWeights[_weightCount++] = new ChildWeight { Child = a, Weight = normalizedAlpha };
- L840 C45: new ChildWeight :: _childWeights[_weightCount++] = new ChildWeight { Child = b, Weight = normalizedBeta };
- L841 C45: new ChildWeight :: _childWeights[_weightCount++] = new ChildWeight { Child = c, Weight = normalizedGamma };


## XREngine.Animation/Property/Core/PropAnimKeyframed.cs
- L6 C108: new() :: public abstract class PropAnimKeyframed<T> : BasePropAnimKeyframed, IEnumerable<T> where T : Keyframe, new()


## XREngine.Animation/Property/Core/PropAnimLerpable.cs
- L9 C53: new() :: where TValueKey : LerpableKeyframe<TValue>, new()
- L33 C40: new() :: private TValue _defaultValue = new();
- L142 C19: new TValue :: : new TValue();
- L166 C55: new TValue :: => _baked is null || _baked.Length == 0 ? new TValue() :
- L270 C22: new TValue :: _baked = new TValue[BakedFrameCount];
- L279 C26: new T :: : this(0.0f, new T(), new T()) { }
- L279 C35: new T :: : this(0.0f, new T(), new T()) { }
- L322 C16: new LerpableKeyframe :: public new LerpableKeyframe<T> Next
- L328 C16: new LerpableKeyframe :: public new LerpableKeyframe<T> Prev


## XREngine.Animation/Property/Core/PropAnimVector.cs
- L11 C51: new() :: where TValueKey : VectorKeyframe<TValue>, new()
- L39 C40: new() :: private TValue _defaultValue = new();
- L138 C23: new InvalidOperationException :: throw new InvalidOperationException("Cannot get baked value when not baked.");
- L187 C23: new InvalidOperationException :: throw new InvalidOperationException("Cannot get baked value when not baked.");
- L190 C24: new TValue :: return new TValue();
- L216 C55: new TValue :: => _baked is null || _baked.Length == 0 ? new TValue() :
- L277 C35: new TValue :: CurrentVelocity = new TValue();
- L278 C39: new TValue :: CurrentAcceleration = new TValue();
- L287 C35: new TValue :: CurrentVelocity = new TValue();
- L288 C39: new TValue :: CurrentAcceleration = new TValue();
- L337 C23: new() :: vel = new(),
- L338 C23: new() :: acc = new();
- L373 C22: new TValue :: _baked = new TValue[BakedFrameCount];
- L387 C56: new TValue :: float[] inComps = GetComponents(velocity ? new TValue() : DefaultValue);
- L405 C32: new float :: float[] outComps = new float[compCount];
- L406 C34: new float :: float[] inTanComps = new float[compCount];
- L407 C35: new float :: float[] outTanComps = new float[compCount];
- L511 C32: new float :: float[] outComps = new float[compCount];


## XREngine.Animation/Property/ObjectKeyframe.cs
- L7 C16: new ObjectKeyframe :: public new ObjectKeyframe? Next
- L12 C16: new ObjectKeyframe :: public new ObjectKeyframe? Prev


## XREngine.Animation/Property/PropAnimBool.cs
- L44 C22: new bool :: _baked = new bool[BakedFrameCount];


## XREngine.Animation/Property/PropAnimMatrix.cs
- L39 C19: new NotImplementedException :: throw new NotImplementedException();
- L55 C22: new Matrix4x4 :: _baked = new Matrix4x4[BakedFrameCount];
- L62 C19: new NotImplementedException :: throw new NotImplementedException();
- L81 C16: new MatrixKeyframe :: public new MatrixKeyframe Next
- L87 C16: new MatrixKeyframe :: public new MatrixKeyframe Prev
- L112 C21: new Matrix4x4 :: Value = new Matrix4x4();
- L124 C20: new Matrix4x4 :: return new Matrix4x4(


## XREngine.Animation/Property/PropAnimMethod.cs
- L62 C22: new T :: _baked = new T[BakedFrameCount];


## XREngine.Animation/Property/PropAnimObject.cs
- L73 C22: new string :: _baked = new string[BakedFrameCount];


## XREngine.Animation/Property/PropAnimQuaternion.cs
- L84 C23: new InvalidOperationException :: throw new InvalidOperationException("Cannot get baked value when not baked.");
- L128 C23: new InvalidOperationException :: throw new InvalidOperationException("Cannot get baked value when not baked.");
- L178 C22: new Quaternion :: _baked = new Quaternion[BakedFrameCount];


## XREngine.Animation/Property/PropAnimString.cs
- L55 C22: new string :: _baked = new string[BakedFrameCount];


## XREngine.Animation/Property/PropAnimVector4.cs
- L35 C51: new() :: public static readonly Vector5 Zero = new();
- L100 C23: new Vector4 :: InValue = new Vector4(float.Parse(parts[1]), float.Parse(parts[2]), float.Parse(parts[3]), float.Parse(parts[4]));
- L101 C24: new Vector4 :: OutValue = new Vector4(float.Parse(parts[5]), float.Parse(parts[6]), float.Parse(parts[7]), float.Parse(parts[8]));
- L102 C25: new Vector4 :: InTangent = new Vector4(float.Parse(parts[9]), float.Parse(parts[10]), float.Parse(parts[11]), float.Parse(parts[12]));
- L103 C26: new Vector4 :: OutTangent = new Vector4(float.Parse(parts[13]), float.Parse(parts[14]), float.Parse(parts[15]), float.Parse(parts[16]));
- L244 C19: new NotImplementedException :: throw new NotImplementedException();
- L249 C19: new NotImplementedException :: throw new NotImplementedException();
- L254 C19: new NotImplementedException :: throw new NotImplementedException();
- L259 C19: new NotImplementedException :: throw new NotImplementedException();
- L264 C19: new NotImplementedException :: throw new NotImplementedException();
- L269 C19: new NotImplementedException :: throw new NotImplementedException();


## XREngine.Animation/Property/QuaternionKeyframe.cs
- L63 C16: new QuaternionKeyframe :: public new QuaternionKeyframe? Next
- L68 C16: new QuaternionKeyframe :: public new QuaternionKeyframe? Prev
- L236 C23: new Quaternion :: InValue = new Quaternion(float.Parse(parts[1]), float.Parse(parts[2]), float.Parse(parts[3]), float.Parse(parts[4]));
- L237 C24: new Quaternion :: OutValue = new Quaternion(float.Parse(parts[5]), float.Parse(parts[6]), float.Parse(parts[7]), float.Parse(parts[8]));
- L238 C25: new Quaternion :: InTangent = new Quaternion(float.Parse(parts[9]), float.Parse(parts[10]), float.Parse(parts[11]), float.Parse(parts[12]));
- L239 C26: new Quaternion :: OutTangent = new Quaternion(float.Parse(parts[13]), float.Parse(parts[14]), float.Parse(parts[15]), float.Parse(parts[16]));


## XREngine.Animation/Property/StringKeyframe.cs
- L7 C16: new StringKeyframe :: public new StringKeyframe? Next
- L12 C16: new StringKeyframe :: public new StringKeyframe? Prev


## XREngine.Animation/Property/Vector2Keyframe.cs
- L42 C23: new Vector2 :: InValue = new Vector2(float.Parse(parts[1]), float.Parse(parts[2]));
- L43 C24: new Vector2 :: OutValue = new Vector2(float.Parse(parts[3]), float.Parse(parts[4]));
- L44 C25: new Vector2 :: InTangent = new Vector2(float.Parse(parts[5]), float.Parse(parts[6]));
- L45 C26: new Vector2 :: OutTangent = new Vector2(float.Parse(parts[7]), float.Parse(parts[8]));
- L269 C19: new NotImplementedException :: throw new NotImplementedException();
- L274 C19: new NotImplementedException :: throw new NotImplementedException();
- L279 C19: new NotImplementedException :: throw new NotImplementedException();
- L284 C19: new NotImplementedException :: throw new NotImplementedException();
- L289 C19: new NotImplementedException :: throw new NotImplementedException();
- L294 C19: new NotImplementedException :: throw new NotImplementedException();


## XREngine.Animation/Property/Vector3Keyframe.cs
- L44 C23: new Vector3 :: InValue = new Vector3(float.Parse(parts[1]), float.Parse(parts[2]), float.Parse(parts[3]));
- L45 C24: new Vector3 :: OutValue = new Vector3(float.Parse(parts[4]), float.Parse(parts[5]), float.Parse(parts[6]));
- L46 C25: new Vector3 :: InTangent = new Vector3(float.Parse(parts[7]), float.Parse(parts[8]), float.Parse(parts[9]));
- L47 C26: new Vector3 :: OutTangent = new Vector3(float.Parse(parts[10]), float.Parse(parts[11]), float.Parse(parts[12]));
- L188 C19: new NotImplementedException :: throw new NotImplementedException();
- L193 C19: new NotImplementedException :: throw new NotImplementedException();
- L198 C19: new NotImplementedException :: throw new NotImplementedException();
- L203 C19: new NotImplementedException :: throw new NotImplementedException();
- L208 C19: new NotImplementedException :: throw new NotImplementedException();
- L213 C19: new NotImplementedException :: throw new NotImplementedException();


## XREngine.Animation/State Machine/AnimStateMachine.cs
- L117 C32: new EventDictionary :: set => Variables = new EventDictionary<string, AnimVar>(value ?? []);
- L217 C37: new AnimFloat :: Variables.Add(name, new AnimFloat(name, defaultValue));
- L225 C37: new AnimInt :: Variables.Add(name, new AnimInt(name, defaultValue));
- L233 C37: new AnimBool :: Variables.Add(name, new AnimBool(name, defaultValue));


## XREngine.Animation/State Machine/Layers/AnimLayer.cs
- L28 C45: new AnyState :: public AnyState AnyState { get; } = new AnyState();
- L37 C55: new() :: private readonly BlendManager _blendManager = new();


## XREngine.Animation/State Machine/Layers/States/AnimState.cs
- L27 C66: new() :: public T AddComponent<T>() where T : AnimStateComponent, new()
- L29 C24: new T :: var comp = new T();


## XREngine.Animation/State Machine/Layers/States/AnimStateBase.cs
- L83 C46: new() :: AnimStateTransition transition = new()


## XREngine.Animation/State Machine/Layers/States/BlendManager.cs
- L60 C31: new Dictionary :: _animatedCurves = new Dictionary<string, AnimationMember>(uniquePaths.Count());


## XREngine.Animation/TransformKeyCollection.cs
- L18 C54: new PropAnimFloat :: public PropAnimFloat TranslationX { get; } = new PropAnimFloat() { DefaultValue = 0.0f };
- L20 C54: new PropAnimFloat :: public PropAnimFloat TranslationY { get; } = new PropAnimFloat() { DefaultValue = 0.0f };
- L22 C54: new PropAnimFloat :: public PropAnimFloat TranslationZ { get; } = new PropAnimFloat() { DefaultValue = 0.0f };
- L24 C48: new PropAnimFloat :: public PropAnimFloat ScaleX { get; } = new PropAnimFloat() { DefaultValue = 0.0f };
- L26 C48: new PropAnimFloat :: public PropAnimFloat ScaleY { get; } = new PropAnimFloat() { DefaultValue = 0.0f };
- L28 C48: new PropAnimFloat :: public PropAnimFloat ScaleZ { get; } = new PropAnimFloat() { DefaultValue = 0.0f };
- L30 C55: new PropAnimQuaternion :: public PropAnimQuaternion Rotation { get; } = new PropAnimQuaternion() { DefaultValue = Quaternion.Identity };
- L114 C27: new Vector3 :: translation = new Vector3(
- L121 C21: new Vector3 :: scale = new Vector3(
- L128 C27: new Vector3 :: translation = new Vector3(
- L135 C21: new Vector3 :: scale = new Vector3(


## XREngine.Audio/AudioBuffer.cs
- L116 C63: new Complex :: Complex[] complexBuffer = [.. samples.Select(x => new Complex(x, 0.0))];


## XREngine.Audio/AudioInputDevice.cs
- L22 C80: new AudioCapture :: public AudioCapture<BufferFormat> AudioCapture { get; private set; } = new AudioCapture<BufferFormat>(listener.Capture, deviceName, freq, format, bufferSize);
- L65 C39: new byte :: byte[] data = new byte[BufferSize];
- L80 C40: new short :: short[] data = new short[BufferSize];


## XREngine.Audio/AudioInputDeviceFloat.cs
- L21 C85: new AudioCapture :: public AudioCapture<FloatBufferFormat> AudioCapture { get; private set; } = new AudioCapture<FloatBufferFormat>(listener.Capture, deviceName, freq, format, bufferSize);
- L59 C28: new float :: float[] data = new float[BufferSize];


## XREngine.Audio/AudioManager.cs
- L55 C40: new() :: ListenerContext listener = new() { Name = name };


## XREngine.Audio/AudioSource.cs
- L179 C28: new ArgumentOutOfRangeException :: _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
- L189 C28: new ArgumentOutOfRangeException :: _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
- L198 C28: new ArgumentOutOfRangeException :: _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
- L207 C28: new ArgumentOutOfRangeException :: _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
- L763 C20: new AuxSendFilter :: return new AuxSendFilter { AuxEffectSlotID = slotID, AuxSendNumber = sendNumber, FilterID = filterID };


## XREngine.Audio/Effects/AudioEffect.cs
- L40 C53: new Vector3 :: => Api.SetEffectProperty(Handle, param, new Vector3(x, y, z));
- L42 C53: new Vector3 :: => Api.SetEffectProperty(Handle, param, new Vector3(value));
- L44 C53: new Vector3 :: => Api.SetEffectProperty(Handle, param, new Vector3(x, y, 0));


## XREngine.Audio/Effects/EffectContext.cs
- L45 C39: new EAXReverbEffect :: EaxReverbPool = new(() => new EAXReverbEffect(this));
- L46 C36: new ReverbEffect :: ReverbPool = new(() => new ReverbEffect(this));
- L47 C36: new ChorusEffect :: ChorusPool = new(() => new ChorusEffect(this));
- L48 C40: new DistortionEffect :: DistortionPool = new(() => new DistortionEffect(this));
- L49 C34: new EchoEffect :: EchoPool = new(() => new EchoEffect(this));
- L50 C37: new FlangerEffect :: FlangerPool = new(() => new FlangerEffect(this));
- L51 C46: new FrequencyShifterEffect :: FrequencyShifterPool = new(() => new FrequencyShifterEffect(this));
- L52 C42: new VocalMorpherEffect :: VocalMorpherPool = new(() => new VocalMorpherEffect(this));
- L53 C42: new PitchShifterEffect :: PitchShifterPool = new(() => new PitchShifterEffect(this));
- L54 C43: new RingModulatorEffect :: RingModulatorPool = new(() => new RingModulatorEffect(this));
- L55 C37: new AutowahEffect :: AutowahPool = new(() => new AutowahEffect(this));
- L56 C40: new CompressorEffect :: CompressorPool = new(() => new CompressorEffect(this));
- L57 C39: new EqualizerEffect :: EqualizerPool = new(() => new EqualizerEffect(this));


## XREngine.Audio/ListenerContext.cs
- L63 C27: new EffectContext :: Effects = new EffectContext(this, effectExtension);
- L90 C26: new ResourcePool :: SourcePool = new ResourcePool<AudioSource>(() => new AudioSource(this));
- L90 C62: new AudioSource :: SourcePool = new ResourcePool<AudioSource>(() => new AudioSource(this));
- L91 C26: new ResourcePool :: BufferPool = new ResourcePool<AudioBuffer>(() => new AudioBuffer(this));
- L91 C62: new AudioBuffer :: BufferPool = new ResourcePool<AudioBuffer>(() => new AudioBuffer(this));
- L325 C35: new float :: float[] orientation = new float[6];
- L329 C23: new Vector3 :: forward = new Vector3(orientation[0], orientation[1], orientation[2]);
- L330 C18: new Vector3 :: up = new Vector3(orientation[3], orientation[4], orientation[5]);


## XREngine.Audio/Steam/OpaqueHandles.cs
- L16 C66: new() :: public static implicit operator IPLContext(IntPtr handle) => new() { Handle = handle };
- L42 C19: new Exception :: throw new Exception(error.ToString());
- L51 C19: new Exception :: throw new Exception(error.ToString());


## XREngine.Audio/XRAudioUtil.cs
- L11 C30: new MMDeviceEnumerator :: var enumerator = new MMDeviceEnumerator();
- L18 C30: new MMDeviceEnumerator :: var enumerator = new MMDeviceEnumerator();
- L25 C30: new MMDeviceEnumerator :: var enumerator = new MMDeviceEnumerator();
- L32 C30: new MMDeviceEnumerator :: var enumerator = new MMDeviceEnumerator();
- L39 C30: new MMDeviceEnumerator :: var enumerator = new MMDeviceEnumerator();
- L46 C30: new MMDeviceEnumerator :: var enumerator = new MMDeviceEnumerator();


## XREngine.Data/ArchiveExtractor.cs
- L35 C26: new ArchiveExtractionProgress :: yield return new ArchiveExtractionProgress(0f, EArchiveExtractionPhase.Preparing, "Preparing extraction...");
- L44 C30: new ArchiveExtractionProgress :: yield return new ArchiveExtractionProgress(1f, EArchiveExtractionPhase.Completed, "Import complete.");
- L66 C36: new FileStream :: using var fileStream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
- L67 C36: new GZipInputStream :: using var gzipStream = new GZipInputStream(fileStream);
- L68 C35: new TarInputStream :: using var tarStream = new TarInputStream(gzipStream, Encoding.Default);
- L86 C40: new FileStream :: using var output = new FileStream(entryPath, FileMode.Create, FileAccess.Write, FileShare.None);
- L94 C30: new ArchiveExtractionProgress :: yield return new ArchiveExtractionProgress(
- L151 C30: new ArchiveExtractionProgress :: yield return new ArchiveExtractionProgress(


## XREngine.Data/AudioData.cs
- L43 C30: new float :: float[] result = new float[_data.Length / sizeof(float)];
- L55 C30: new short :: short[] result = new short[_data.Length / sizeof(short)];
- L67 C29: new byte :: byte[] result = new byte[_data.Length];
- L118 C24: new float :: var data = new float[reader.TotalSamples * reader.Channels];
- L142 C28: new byte :: byte[] bytes = new byte[totalBytes];
- L150 C21: new DataSource :: _data = new DataSource(bytes);
- L161 C28: new byte :: byte[] bytes = new byte[reader.Length];
- L166 C29: new DataSource :: _data = new DataSource(bytes);
- L170 C29: new DataSource :: _data = new DataSource(bytes);
- L174 C29: new DataSource :: _data = new DataSource(bytes);
- L200 C29: new DataSource :: _data = new DataSource(ConvertStereoToMono(GetByteData()));
- L219 C29: new DataSource :: _data = new DataSource(GetOneChannelFromStereo(GetByteData(), true));
- L238 C29: new DataSource :: _data = new DataSource(GetOneChannelFromStereo(GetByteData(), false));
- L254 C30: new() :: AudioData mono = new()
- L269 C30: new() :: AudioData mono = new()
- L284 C30: new() :: AudioData mono = new()
- L298 C34: new byte :: byte[] monoSamples = new byte[monoLength];
- L317 C35: new short :: short[] monoSamples = new short[monoLength];
- L335 C35: new float :: float[] monoSamples = new float[monoLength];
- L354 C34: new byte :: byte[] monoSamples = new byte[monoLength];
- L364 C35: new short :: short[] monoSamples = new short[monoLength];
- L374 C35: new float :: float[] monoSamples = new float[monoLength];
- L386 C36: new float :: float[] floatSamples = new float[sampleCount];


## XREngine.Data/BSP/BSPCube.cs
- L20 C17: new Vector3 :: new Vector3(-halfSize, -halfSize, -halfSize),
- L21 C17: new Vector3 :: new Vector3(halfSize, -halfSize, -halfSize),
- L22 C17: new Vector3 :: new Vector3(halfSize, halfSize, -halfSize),
- L23 C17: new Vector3 :: new Vector3(-halfSize, halfSize, -halfSize),
- L24 C17: new Vector3 :: new Vector3(-halfSize, -halfSize, halfSize),
- L25 C17: new Vector3 :: new Vector3(halfSize, -halfSize, halfSize),
- L26 C17: new Vector3 :: new Vector3(halfSize, halfSize, halfSize),
- L27 C17: new Vector3 :: new Vector3(-halfSize, halfSize, halfSize),
- L45 C17: new Vector3 :: new Vector3(0, 0, -1),
- L46 C17: new Vector3 :: new Vector3(0, 0, 1),
- L47 C17: new Vector3 :: new Vector3(0, -1, 0),
- L48 C17: new Vector3 :: new Vector3(0, 1, 0),
- L49 C17: new Vector3 :: new Vector3(-1, 0, 0),
- L50 C17: new Vector3 :: new Vector3(1, 0, 0)


## XREngine.Data/BSP/BSPNode.cs
- L36 C27: new BSPNode :: Front ??= new BSPNode();
- L42 C26: new BSPNode :: Back ??= new BSPNode();
- L165 C28: new Triangle :: output.Add(new Triangle(p0, polygon[i], polygon[i + 1]));
- L184 C20: new System.Numerics.Plane :: return new System.Numerics.Plane(-plane.Value.Normal, -plane.Value.D);
- L200 C24: new List :: return new List<Triangle>(triangles);
- L230 C33: new() :: BSPNode cloneNode = new();
- L233 C35: new System.Numerics.Plane :: cloneNode.Plane = new System.Numerics.Plane(Plane.Value.Normal, Plane.Value.D);
- L242 C41: new Triangle :: cloneNode.Triangles.Add(new Triangle(triangle.A, triangle.B, triangle.C));


## XREngine.Data/BSP/BSPShapeExtensions.cs
- L16 C31: new Triangle :: triangles.Add(new Triangle(a, b, c));
- L19 C28: new() :: BSPNode node = new();


## XREngine.Data/ComputerInfo.cs
- L18 C49: new() :: public static ComputerInfo Analyze() => new()


## XREngine.Data/ConcurrentHashSet.cs
- L223 C25: new object :: var locks = new object[concurrencyLevel];
- L225 C28: new object :: locks[i] = new object();
- L227 C32: new int :: var countPerLock = new int[locks.Length];
- L228 C27: new Node :: var buckets = new Node[capacity];
- L229 C23: new Tables :: _tables = new Tables(buckets, locks, countPerLock);
- L257 C33: new Tables :: var newTables = new Tables(new Node[DefaultCapacity], _tables.Locks, new int[_tables.CountPerLock.Length]);
- L257 C44: new Node :: var newTables = new Tables(new Node[DefaultCapacity], _tables.Locks, new int[_tables.CountPerLock.Length]);
- L257 C86: new int :: var newTables = new Tables(new Node[DefaultCapacity], _tables.Locks, new int[_tables.CountPerLock.Length]);
- L392 C27: new ArgumentException :: throw new ArgumentException("The index is equal to or greater than the length of the array, or the number of elements in the set is greater than the available space from index to the end of the destination array.");
- L447 C66: new Node :: Volatile.Write(ref tables.Buckets[bucketNo], new Node(item, hashcode, tables.Buckets[bucketNo]));
- L579 C32: new object :: newLocks = new object[tables.Locks.Length * 2];
- L582 C39: new object :: newLocks[i] = new object();
- L585 C34: new Node :: var newBuckets = new Node[newLength];
- L586 C39: new int :: var newCountPerLock = new int[newLocks.Length];
- L597 C51: new Node :: newBuckets[newBucketNo] = new Node(current.Item, current.Hashcode, newBuckets[newBucketNo]);
- L612 C27: new Tables :: _tables = new Tables(newBuckets, newLocks, newCountPerLock);


## XREngine.Data/Core/Assets/XRAsset.cs
- L114 C23: new InvalidOperationException :: throw new InvalidOperationException("Cannot open a file for streaming without a file path.");
- L227 C32: new StreamWriter :: using var writer = new StreamWriter(filePath, append: false, Encoding.UTF8);


## XREngine.Data/Core/Assets/XRAssetGraphUtility.cs
- L27 C101: new() :: private static readonly ConcurrentDictionary<Type, List<Func<object, object?>>> AccessorCache = new();
- L28 C78: new() :: private static readonly ConcurrentDictionary<Type, bool> LeafTypeCache = new();
- L29 C87: new() :: private static readonly ConcurrentDictionary<Type, bool> InspectMemberTypeCache = new();
- L60 C30: new HashSet :: var visitedObjects = new HashSet<object>(ReferenceEqualityComparer.Instance);
- L61 C32: new HashSet :: var discoveredAssets = new HashSet<XRAsset>(AssetReferenceComparer.Instance);
- L285 C25: new List :: var accessors = new List<Func<object, object?>>();
- L420 C66: new() :: public static readonly AssetReferenceComparer Instance = new();


## XREngine.Data/Core/Colors/ColorF3.cs
- L137 C23: new ColorF3 :: => this = new ColorF3(str);


## XREngine.Data/Core/Colors/HSVPixel.cs
- L51 C28: new ARGBPixel :: newPixel = new ARGBPixel(255, v, v, v);
- L63 C26: new ARGBPixel :: 0 => new ARGBPixel(255, v, t, p),
- L64 C26: new ARGBPixel :: 1 => new ARGBPixel(255, q, v, p),
- L65 C26: new ARGBPixel :: 2 => new ARGBPixel(255, p, v, t),
- L66 C26: new ARGBPixel :: 3 => new ARGBPixel(255, p, q, v),
- L67 C26: new ARGBPixel :: 4 => new ARGBPixel(255, t, p, v),
- L68 C26: new ARGBPixel :: _ => new ARGBPixel(255, v, p, q),


## XREngine.Data/Core/Events/XRBoolEvent.cs
- L179 C19: new() :: e ??= new();


## XREngine.Data/Core/Events/XREvent.cs
- L83 C19: new() :: e ??= new();
- L150 C38: new object :: object?[] expanded = new object?[tuple.Length];
- L186 C19: new() :: e ??= new();


## XREngine.Data/Core/Events/XRPersistentCall.cs
- L85 C37: new Type :: desiredParamTypes = new Type[typeNames.Length];


## XREngine.Data/Core/Memory/Bin16.cs
- L13 C68: new Bin16 :: public static implicit operator Bin16(ushort val) { return new Bin16(val); }
- L84 C20: new Bin16 :: return new Bin16(b);


## XREngine.Data/Core/Memory/Bin24.cs
- L14 C66: new Bin24 :: public static implicit operator Bin24(uint val) { return new Bin24((UInt24)val); }
- L16 C68: new Bin24 :: public static implicit operator Bin24(UInt24 val) { return new Bin24(val); }
- L87 C20: new Bin24 :: return new Bin24((UInt24)b);


## XREngine.Data/Core/Memory/Bin32.cs
- L12 C66: new Bin32 :: public static implicit operator Bin32(uint val) { return new Bin32(val); }
- L84 C20: new Bin32 :: return new Bin32(b);


## XREngine.Data/Core/Memory/Bin64.cs
- L12 C67: new Bin64 :: public static implicit operator Bin64(ulong val) { return new Bin64(val); }
- L84 C20: new Bin64 :: return new Bin64(b);


## XREngine.Data/Core/Memory/Bin8.cs
- L12 C65: new Bin8 :: public static implicit operator Bin8(byte val) { return new Bin8(val); }
- L84 C20: new Bin8 :: return new Bin8(b);


## XREngine.Data/Core/Memory/Compression.cs
- L32 C26: new StringBuilder :: var sb = new StringBuilder(byteStr.Length);
- L107 C23: new FormatException :: throw new FormatException(
- L121 C23: new FormatException :: throw new FormatException("Compressed byte string contains invalid hexadecimal characters.", ex);
- L128 C57: new() :: SevenZip.Compression.LZMA.Encoder encoder = new();
- L130 C44: new() :: using MemoryStream outStream = new();
- L158 C25: new() :: encoder ??= new();
- L159 C32: new() :: inStreamObject ??= new();
- L160 C33: new() :: outStreamObject ??= new();
- L179 C57: new() :: SevenZip.Compression.LZMA.Decoder decoder = new();
- L184 C33: new byte :: byte[] properties = new byte[5];
- L186 C34: new byte :: byte[] lengthBytes = new byte[4];
- L206 C25: new() :: decoder ??= new();
- L207 C32: new() :: inStreamObject ??= new();
- L208 C33: new() :: outStreamObject ??= new();
- L221 C33: new byte :: byte[] properties = new byte[5];
- L223 C34: new byte :: byte[] lengthBytes = new byte[4];
- L238 C57: new() :: SevenZip.Compression.LZMA.Decoder decoder = new();
- L240 C44: new() :: using MemoryStream outStream = new();
- L243 C33: new byte :: byte[] properties = new byte[5];
- L247 C34: new byte :: byte[] lengthBytes = new byte[sizeByteCount];
- L290 C25: new float :: var qRest = new float[3];
- L301 C41: new int :: int[] quantizedComponents = new int[3];
- L323 C40: new float :: float[] scaledComponents = new float[3];
- L342 C28: new() :: Quaternion q = new();
- L377 C32: new byte :: byte[] byteArray = new byte[totalBytes];
- L441 C41: new int :: int[] quantizedComponents = new int[3];
- L472 C28: new byte :: byte[] bytes = new byte[(bits + 7) / 8];


## XREngine.Data/Core/Memory/DataSource.cs
- L17 C50: new DataSourceFormatter :: MemoryPackFormatterProvider.Register(new DataSourceFormatter());
- L136 C28: new byte :: byte[] bytes = new byte[Length];
- L143 C30: new short :: short[] shorts = new short[Length / 2];
- L150 C30: new float :: float[] floats = new float[Length / 4];
- L158 C24: new DataSource :: return new DataSource(Address, Length, false);
- L171 C20: new Span :: s.Read(new Span<byte>(ptr, (int)source.Length));
- L242 C29: new DataSource :: value = new DataSource(0);
- L247 C25: new DataSource :: value = new DataSource(payload);


## XREngine.Data/Core/Memory/FileMap.cs
- L46 C30: new FileStream :: stream = new FileStream(path, FileMode.Open, (prot == FileMapProtect.ReadWrite) ? FileAccess.ReadWrite : FileAccess.Read, FileShare.Read, 8, options);
- L53 C26: new FileStream :: stream = new FileStream(tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 8, options | FileOptions.DeleteOnClose);
- L71 C33: new FileStream :: FileStream stream = new FileStream(path = Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 8, FileOptions.RandomAccess | FileOptions.DeleteOnClose);
- L101 C39: new WFileMap :: PlatformID.Win32NT => new WFileMap(stream.SafeFileHandle.DangerousGetHandle(), prot, offset, (uint)length) { _path = stream.Name },
- L102 C22: new CFileMap :: _ => new CFileMap(stream, prot, offset, length) { _path = stream.Name },
- L117 C39: new WFileMap :: PlatformID.Win32NT => new WFileMap(stream.SafeFileHandle.DangerousGetHandle(), prot, offset, (uint)length) { _baseStream = stream, _path = stream.Name },
- L118 C22: new CFileMap :: _ => new CFileMap(stream, prot, offset, length) { _baseStream = stream, _path = stream.Name },


## XREngine.Data/Core/Memory/FloatQuantizeHeader.cs
- L37 C27: new InvalidOperationException :: throw new InvalidOperationException("Bit count must be between 1 and 32.");
- L52 C27: new InvalidOperationException :: throw new InvalidOperationException("Component count must be 1, 2, 3 or 4.");


## XREngine.Data/Core/Memory/FloatQuantizer.cs
- L96 C49: new float :: _pData = [.. values.SelectMany(x => new float[] { x.X, x.Y, x.Z, x.W })];
- L103 C49: new float :: _pData = [.. values.SelectMany(x => new float[] { x.X, x.Y, x.Z })];
- L110 C49: new float :: _pData = [.. values.SelectMany(x => new float[] { x.X, x.Y })];
- L122 C16: new() :: => new()
- L161 C20: new Vector4 :: _min = new Vector4(float.MaxValue);
- L162 C20: new Vector4 :: _max = new Vector4(float.MinValue);
- L284 C32: new Vector4 :: Vector4[] values = new Vector4[header.ElementCount];
- L294 C32: new Vector3 :: Vector3[] values = new Vector3[header.ElementCount];
- L304 C32: new Vector2 :: Vector2[] values = new Vector2[header.ElementCount];
- L314 C30: new float :: float[] values = new float[header.ElementCount];
- L335 C36: new() :: BoolVector4 included = new() { X = hasX, Y = hasY, Z = hasZ, W = hasW };


## XREngine.Data/Core/Memory/FloatQuantizer1.cs
- L14 C23: new ArgumentException :: throw new ArgumentException("maxError must be greater than zero.");
- L71 C23: new ArgumentException :: throw new ArgumentException("Input and output spans must have the same length.");
- L88 C23: new ArgumentException :: throw new ArgumentException("Input and output spans must have the same length.");
- L104 C23: new ArgumentException :: throw new ArgumentException("values span cannot be empty.");
- L134 C36: new byte :: Span<byte> byteArray = new byte[totalBytes];
- L157 C23: new ArgumentException :: throw new ArgumentException("The byteArray is too small to hold the quantized data.");
- L185 C23: new ArgumentException :: throw new ArgumentException("The byteArray does not contain enough data.");


## XREngine.Data/Core/Memory/VoidPtr.cs
- L15 C47: new() :: public static readonly VoidPtr Zero = new() { _address = null };
- L146 C16: new() :: => new() { _address = ((byte*)p1._address + addr) };
- L148 C16: new() :: => new() { _address = ((byte*)p1._address - addr) };
- L151 C16: new() :: => new() { _address = ((byte*)p1._address + addr) };
- L153 C16: new() :: => new() { _address = ((byte*)p1._address - addr) };
- L156 C16: new() :: => new() { _address = ((byte*)p1._address + addr) };
- L158 C16: new() :: => new() { _address = ((byte*)p1._address - addr) };
- L161 C16: new() :: => new() { _address = ((byte*)p1._address + addr) };
- L163 C16: new() :: => new() { _address = ((byte*)p1._address - addr) };
- L186 C16: new() :: => new() { _address = ptr };
- L191 C16: new() :: => new() { _address = (void*)ptr };
- L195 C16: new() :: => new() { _address = (void*)ptr };
- L200 C16: new() :: => new() { _address = (void*)ptr };
- L204 C16: new() :: => new() { _address = (void*)ptr };
- L207 C16: new() :: => new() { _address = (void*)ptr };
- L408 C26: new byte :: byte[] arr = new byte[count];


## XREngine.Data/Core/Memory/Win32.cs
- L17 C81: new SafeHandle :: public static implicit operator SafeHandle(VoidPtr handle) { return new SafeHandle(handle); }
- L24 C24: new SafeHandle :: return new SafeHandle(hFile);


## XREngine.Data/Core/Objects/XRBase.cs
- L132 C46: new XRPropertyChangedEventArgs :: => PropertyChanged?.Invoke(this, new XRPropertyChangedEventArgs<T>(propName, prev, field));
- L140 C24: new XRPropertyChangingEventArgs :: var args = new XRPropertyChangingEventArgs<T>(propName, field, @new);


## XREngine.Data/Core/Objects/XRObjectBase.cs
- L55 C27: new Exception :: throw new Exception("Failed to generate a unique ID for an object."); //Highly unlikely
- L88 C83: new() :: private static readonly ConcurrentQueue<XRObjectBase> _objectsToDestroy = new();


## XREngine.Data/Core/OverrideableSetting.cs
- L160 C21: new OverrideableSetting :: value = new OverrideableSetting<T>(settingValue, hasOverride);


## XREngine.Data/Core/Type Converters/DataSourceYamlTypeConverter.cs
- L94 C34: new DataSource :: result = new DataSource(length ?? 0u, zeroMemory: true) { PreferCompressedYaml = false };
- L99 C34: new DataSource :: result = new DataSource(rawBytes) { PreferCompressedYaml = false };
- L104 C30: new DataSource :: result = new DataSource(Compression.DecompressFromString(length, byteStr)) { PreferCompressedYaml = true };
- L117 C24: new DataSource :: return new DataSource(fallbackLength, zeroMemory: true);
- L126 C26: new MappingStart :: emitter.Emit(new MappingStart(null, null, false, MappingStyle.Block));
- L128 C30: new Scalar :: emitter.Emit(new Scalar("Length"));
- L129 C30: new Scalar :: emitter.Emit(new Scalar(source.Length.ToString()));
- L133 C34: new Scalar :: emitter.Emit(new Scalar("Encoding"));
- L134 C34: new Scalar :: emitter.Emit(new Scalar("RawHex"));
- L136 C34: new Scalar :: emitter.Emit(new Scalar("Bytes"));
- L137 C34: new Scalar :: emitter.Emit(new Scalar(Convert.ToHexString(source.GetBytes())));
- L141 C34: new Scalar :: emitter.Emit(new Scalar("Bytes"));
- L142 C34: new Scalar :: emitter.Emit(new Scalar(Compression.CompressToString(source)));
- L145 C26: new MappingEnd :: emitter.Emit(new MappingEnd());
- L151 C23: new YamlException :: throw new YamlException(errorMessage);
- L183 C19: new YamlException :: throw new YamlException("Unsupported YAML node encountered while skipping a value.");
- L222 C22: new System.Text.StringBuilder :: var sb = new System.Text.StringBuilder(hex.Length);


## XREngine.Data/Core/Type Converters/Matrix4x4YamlTypeConverter.cs
- L16 C23: new YamlException :: throw new YamlException("Expected a scalar value to deserialize a Matrix4x4.");
- L19 C23: new YamlException :: throw new YamlException("Expected Matrix4x4 format 'M00 M01 M02 M03 M10 M11 M12 M13 M20 M21 M22 M23 M30 M31 M32 M33'.");
- L36 C20: new Matrix4x4 :: return new Matrix4x4(
- L45 C26: new Scalar :: emitter.Emit(new Scalar($"{m4x4.M11} {m4x4.M12} {m4x4.M13} {m4x4.M14} {m4x4.M21} {m4x4.M22} {m4x4.M23} {m4x4.M24} {m4x4.M31} {m4x4.M32} {m4x4.M33} {m4x4.M34} {m4x4.M41} {m4x4.M42} {m4x4.M43} {m4x4.M44}"));


## XREngine.Data/Core/Type Converters/QuaternionTypeConverter.cs
- L17 C28: new Quaternion :: return new Quaternion(float.Parse(parts[0]), float.Parse(parts[1]), float.Parse(parts[2]), float.Parse(parts[3]));


## XREngine.Data/Core/Type Converters/QuaternionYamlTypeConverter.cs
- L16 C23: new YamlException :: throw new YamlException("Expected a scalar value to deserialize a Vector4.");
- L19 C23: new YamlException :: throw new YamlException("Expected Vector4 format 'X Y Z W'.");
- L24 C20: new Quaternion :: return new Quaternion(x, y, z, w);
- L29 C26: new Scalar :: emitter.Emit(new Scalar($"{v4.X} {v4.Y} {v4.Z} {v4.W}"));


## XREngine.Data/Core/Type Converters/Vector2TypeConverter.cs
- L17 C28: new Vector2 :: return new Vector2(float.Parse(parts[0]), float.Parse(parts[1]));


## XREngine.Data/Core/Type Converters/Vector2YamlTypeConverter.cs
- L16 C23: new YamlException :: throw new YamlException("Expected a scalar value to deserialize a Vector2.");
- L19 C23: new YamlException :: throw new YamlException("Expected Vector2 format 'X Y'.");
- L22 C20: new Vector2 :: return new Vector2(x, y);
- L27 C26: new Scalar :: emitter.Emit(new Scalar($"{v2.X} {v2.Y}"));


## XREngine.Data/Core/Type Converters/Vector3TypeConverter.cs
- L17 C28: new Vector3 :: return new Vector3(float.Parse(parts[0]), float.Parse(parts[1]), float.Parse(parts[2]));


## XREngine.Data/Core/Type Converters/Vector3YamlTypeConverter.cs
- L17 C23: new YamlException :: throw new YamlException("Expected a scalar value to deserialize a Vector3.");
- L21 C23: new YamlException :: throw new YamlException("Expected Vector3 format 'X Y Z'.");
- L26 C20: new Vector3 :: return new Vector3(x, y, z);
- L32 C26: new Scalar :: emitter.Emit(new Scalar($"{v3.X} {v3.Y} {v3.Z}"));


## XREngine.Data/Core/Type Converters/Vector4TypeConverter.cs
- L17 C28: new Vector4 :: return new Vector4(float.Parse(parts[0]), float.Parse(parts[1]), float.Parse(parts[2]), float.Parse(parts[3]));


## XREngine.Data/Core/Type Converters/Vector4YamlTypeConverter.cs
- L16 C23: new YamlException :: throw new YamlException("Expected a scalar value to deserialize a Vector4.");
- L19 C23: new YamlException :: throw new YamlException("Expected Vector4 format 'X Y Z W'.");
- L24 C20: new Vector4 :: return new Vector4(x, y, z, w);
- L29 C26: new Scalar :: emitter.Emit(new Scalar($"{v4.X} {v4.Y} {v4.Z} {v4.W}"));


## XREngine.Data/Core/UserSettings.cs
- L108 C64: new() :: private OverrideableSetting<int> _jobWorkersOverride = new();
- L109 C66: new() :: private OverrideableSetting<int> _jobWorkerCapOverride = new();
- L110 C67: new() :: private OverrideableSetting<int> _jobQueueLimitOverride = new();
- L111 C78: new() :: private OverrideableSetting<int> _jobQueueWarningThresholdOverride = new();
- L112 C72: new() :: private OverrideableSetting<bool> _gpuRenderDispatchOverride = new();
- L113 C82: new() :: private OverrideableSetting<EOutputVerbosity> _outputVerbosityOverride = new();
- L114 C84: new() :: private OverrideableSetting<bool> _enableGpuIndirectDebugLoggingOverride = new();
- L115 C83: new() :: private OverrideableSetting<bool> _enableGpuIndirectCpuFallbackOverride = new();
- L116 C89: new() :: private OverrideableSetting<bool> _enableGpuIndirectValidationLoggingOverride = new();
- L119 C84: new() :: private OverrideableSetting<EAntiAliasingMode> _antiAliasingModeOverride = new();
- L120 C70: new() :: private OverrideableSetting<uint> _msaaSampleCountOverride = new();
- L121 C66: new() :: private OverrideableSetting<EVSyncMode> _vSyncOverride = new();
- L122 C96: new() :: private OverrideableSetting<EGlobalIlluminationMode> _globalIlluminationModeOverride = new();
- L123 C81: new() :: private OverrideableSetting<bool> _tickGroupedItemsInParallelOverride = new();
- L124 C71: new() :: private OverrideableSetting<bool> _enableNvidiaDlssOverride = new();
- L125 C78: new() :: private OverrideableSetting<EDlssQualityMode> _dlssQualityOverride = new();
- L126 C70: new() :: private OverrideableSetting<bool> _enableIntelXessOverride = new();
- L127 C78: new() :: private OverrideableSetting<EXessQualityMode> _xessQualityOverride = new();
- L130 C78: new() :: private OverrideableSetting<float> _targetUpdatesPerSecondOverride = new();
- L131 C76: new() :: private OverrideableSetting<float> _fixedFramesPerSecondOverride = new();
- L142 C63: new() :: set => SetField(ref _jobWorkersOverride, value ?? new());
- L154 C65: new() :: set => SetField(ref _jobWorkerCapOverride, value ?? new());
- L166 C66: new() :: set => SetField(ref _jobQueueLimitOverride, value ?? new());
- L178 C77: new() :: set => SetField(ref _jobQueueWarningThresholdOverride, value ?? new());
- L190 C70: new() :: set => SetField(ref _gpuRenderDispatchOverride, value ?? new());
- L202 C68: new() :: set => SetField(ref _outputVerbosityOverride, value ?? new());
- L214 C82: new() :: set => SetField(ref _enableGpuIndirectDebugLoggingOverride, value ?? new());
- L226 C81: new() :: set => SetField(ref _enableGpuIndirectCpuFallbackOverride, value ?? new());
- L238 C87: new() :: set => SetField(ref _enableGpuIndirectValidationLoggingOverride, value ?? new());
- L250 C69: new() :: set => SetField(ref _antiAliasingModeOverride, value ?? new());
- L262 C68: new() :: set => SetField(ref _msaaSampleCountOverride, value ?? new());
- L274 C58: new() :: set => SetField(ref _vSyncOverride, value ?? new());
- L286 C75: new() :: set => SetField(ref _globalIlluminationModeOverride, value ?? new());
- L298 C79: new() :: set => SetField(ref _tickGroupedItemsInParallelOverride, value ?? new());
- L310 C69: new() :: set => SetField(ref _enableNvidiaDlssOverride, value ?? new());
- L322 C64: new() :: set => SetField(ref _dlssQualityOverride, value ?? new());
- L334 C68: new() :: set => SetField(ref _enableIntelXessOverride, value ?? new());
- L346 C64: new() :: set => SetField(ref _xessQualityOverride, value ?? new());
- L358 C75: new() :: set => SetField(ref _targetUpdatesPerSecondOverride, value ?? new());
- L370 C73: new() :: set => SetField(ref _fixedFramesPerSecondOverride, value ?? new());


## XREngine.Data/Core/XRMath.cs
- L149 C20: new Vector2 :: return new Vector2(cos * radius, sin * radius);
- L154 C20: new Vector2 :: return new Vector2(cos * radius, sin * radius);
- L403 C26: new Complex :: x1 = new Complex(-b + mag, 0.0);
- L404 C26: new Complex :: x2 = new Complex(-b - mag, 0.0);
- L410 C26: new Complex :: x1 = new Complex(-b, mag);
- L411 C26: new Complex :: x2 = new Complex(-b, -mag);
- L742 C48: new Vector3 :: Vector3 result = Vector3.Transform(new Vector3(point, 0.0f),
- L743 C41: new Vector3 :: Matrix4x4.CreateTranslation(new Vector3(center, 0.0f)) *
- L744 C41: new Vector3 :: Matrix4x4.CreateTranslation(new Vector3(-center, 0.0f)) *
- L746 C20: new Vector2 :: return new Vector2(result.X, result.Y);
- L1009 C28: new int :: int[] values = new int[rowIndex + 1];
- L1141 C29: new() :: Vector3 euler = new();
- L1843 C20: new Vector3 :: return new Vector3(
- L1858 C24: new Vector3 :: return new Vector3(v.X, 0f, v.Z) * weight;
- L1869 C39: new Vector3 :: => normal == Globals.Up ? new Vector3(v.X, 0f, v.Z) : v - ProjectVector(v, normal);
- L2008 C24: new Vector3 :: return new Vector3(point.X, planePosition.Y, point.Z);


## XREngine.Data/Endian/bdouble.cs
- L15 C12: new() :: => new() { _data = Endian.SerializeBig ? val.Reverse() : val };


## XREngine.Data/Endian/bfloat.cs
- L15 C12: new() :: => new() { _data = Endian.SerializeBig ? val.Reverse() : val };


## XREngine.Data/Endian/bint.cs
- L15 C12: new() :: => new() { _data = Endian.SerializeBig ? val.Reverse() : val };


## XREngine.Data/Endian/blong.cs
- L15 C12: new() :: => new() { _data = Endian.SerializeBig ? val.Reverse() : val };


## XREngine.Data/Endian/BMatrix4.cs
- L30 C23: new() :: Matrix4x4 m = new();
- L40 C23: new() :: BMatrix4 bm = new();


## XREngine.Data/Endian/bshort.cs
- L15 C12: new() :: => new() { _data = Endian.SerializeBig ? val.Reverse() : val };


## XREngine.Data/Endian/buint.cs
- L15 C12: new() :: => new() { _data = Endian.SerializeBig ? val.Reverse() : val };


## XREngine.Data/Endian/BUInt24.cs
- L34 C64: new BUInt24 :: public static implicit operator BUInt24(uint val) { return new BUInt24(val); }
- L37 C63: new BUInt24 :: public static explicit operator BUInt24(int val) { return new BUInt24((uint)val); }
- L39 C66: new UInt24 :: public static implicit operator UInt24(BUInt24 val) { return new UInt24(val.Value); }
- L40 C66: new BUInt24 :: public static implicit operator BUInt24(UInt24 val) { return new BUInt24(val.Value); }


## XREngine.Data/Endian/bulong.cs
- L15 C12: new() :: => new() { _data = Endian.SerializeBig ? val.Reverse() : val };


## XREngine.Data/Endian/bushort.cs
- L15 C12: new() :: => new() { _data = Endian.SerializeBig ? val.Reverse() : val };


## XREngine.Data/Endian/SNormFloat4.cs
- L14 C23: new ArgumentOutOfRangeException :: throw new ArgumentOutOfRangeException(nameof(floatValue), "Value must be between -1.0 and 1.0");


## XREngine.Data/Endian/SNormFloat8.cs
- L14 C23: new ArgumentOutOfRangeException :: throw new ArgumentOutOfRangeException(nameof(floatValue), "Value must be between -1.0 and 1.0");


## XREngine.Data/Endian/UInt24.cs
- L26 C63: new UInt24 :: public static implicit operator UInt24(uint val) { return new UInt24(val); }


## XREngine.Data/Endian/UNormFloat4.cs
- L14 C23: new ArgumentOutOfRangeException :: throw new ArgumentOutOfRangeException(nameof(floatValue), "Value must be between 0.0 and 1.0");


## XREngine.Data/Endian/UNormFloat8.cs
- L14 C23: new ArgumentOutOfRangeException :: throw new ArgumentOutOfRangeException(nameof(floatValue), "Value must be between 0.0 and 1.0");


## XREngine.Data/EventDictionary.cs
- L44 C16: new TValue :: public new TValue this[TKey key]
- L63 C16: new void :: public new void Add(TKey key, TValue value)
- L70 C16: new void :: public new void Clear()
- L80 C16: new bool :: public new bool Remove(TKey key)


## XREngine.Data/Geometry/AABB.cs
- L26 C20: new AABB :: return new AABB(center - extents, center + extents);
- L159 C20: new AABB :: return new AABB(newMin, newMax);
- L222 C19: new Vector3 :: TBL = new Vector3(Left, Top, Back);
- L223 C19: new Vector3 :: TBR = new Vector3(Right, Top, Back);
- L225 C19: new Vector3 :: TFL = new Vector3(Left, Top, Front);
- L226 C19: new Vector3 :: TFR = new Vector3(Right, Top, Front);
- L228 C19: new Vector3 :: BBL = new Vector3(Left, Bottom, Back);
- L229 C19: new Vector3 :: BBR = new Vector3(Right, Bottom, Back);
- L231 C19: new Vector3 :: BFL = new Vector3(Left, Bottom, Front);
- L232 C19: new Vector3 :: BFR = new Vector3(Right, Bottom, Front);
- L279 C37: new Vector3 :: TBL = Vector3.Transform(new Vector3(Left, Top, Back), transform);
- L280 C37: new Vector3 :: TBR = Vector3.Transform(new Vector3(Right, Top, Back), transform);
- L282 C37: new Vector3 :: TFL = Vector3.Transform(new Vector3(Left, Top, Front), transform);
- L283 C37: new Vector3 :: TFR = Vector3.Transform(new Vector3(Right, Top, Front), transform);
- L285 C37: new Vector3 :: BBL = Vector3.Transform(new Vector3(Left, Bottom, Back), transform);
- L286 C37: new Vector3 :: BBR = Vector3.Transform(new Vector3(Right, Bottom, Back), transform);
- L288 C37: new Vector3 :: BFL = Vector3.Transform(new Vector3(Left, Bottom, Front), transform);
- L289 C37: new Vector3 :: BFR = Vector3.Transform(new Vector3(Right, Bottom, Front), transform);
- L314 C25: new Vector3 :: var array = new Vector3[]
- L364 C16: new() :: => new()
- L416 C19: new NotImplementedException :: throw new NotImplementedException();
- L466 C20: new AABB :: return new AABB(min, max);
- L472 C20: new AABB :: return new AABB(min, max);
- L476 C67: new Vector3 :: Vector3 min = Vector3.Min(bounds.Min, sphere.Center - new Vector3(sphere.Radius));
- L477 C67: new Vector3 :: Vector3 max = Vector3.Max(bounds.Max, sphere.Center + new Vector3(sphere.Radius));
- L478 C20: new AABB :: return new AABB(min, max);
- L484 C20: new AABB :: return new AABB(min, max);
- L536 C36: new Vector3 :: Vector3 min = center - new Vector3(radius);
- L537 C36: new Vector3 :: Vector3 max = center + new Vector3(radius);
- L538 C20: new AABB :: return new AABB(min, max);
- L545 C20: new AABB :: return new AABB(min, max);
- L552 C20: new AABB :: return new AABB(min, max);


## XREngine.Data/Geometry/BoundingRectangle.cs
- L17 C58: new() :: public static readonly BoundingRectangle Empty = new();
- L66 C20: new IVector2 :: : this(new IVector2(x, y), new IVector2(width, height), new Vector2(localOriginPercentageX, localOriginPercentageY)) { }
- L66 C40: new IVector2 :: : this(new IVector2(x, y), new IVector2(width, height), new Vector2(localOriginPercentageX, localOriginPercentageY)) { }
- L66 C69: new Vector2 :: : this(new IVector2(x, y), new IVector2(width, height), new Vector2(localOriginPercentageX, localOriginPercentageY)) { }
- L69 C20: new IVector2 :: : this(new IVector2(x, y), new IVector2(width, height)) { }
- L69 C40: new IVector2 :: : this(new IVector2(x, y), new IVector2(width, height)) { }
- L260 C20: new Rectangle :: return new Rectangle(pos.X, containerHeight - pos.Y, Width, Height);


## XREngine.Data/Geometry/BoundingRectangleF.cs
- L17 C59: new() :: public static readonly BoundingRectangleF Empty = new();
- L64 C20: new Vector2 :: : this(new Vector2(x, y), new Vector2(width, height), new Vector2(localOriginPercentageX, localOriginPercentageY)) { }
- L64 C39: new Vector2 :: : this(new Vector2(x, y), new Vector2(width, height), new Vector2(localOriginPercentageX, localOriginPercentageY)) { }
- L64 C67: new Vector2 :: : this(new Vector2(x, y), new Vector2(width, height), new Vector2(localOriginPercentageX, localOriginPercentageY)) { }
- L66 C20: new Vector2 :: : this(new Vector2(x, y), new Vector2(width, height)) { }
- L66 C39: new Vector2 :: : this(new Vector2(x, y), new Vector2(width, height)) { }
- L174 C44: new Vector2 :: readonly get => _translation + new Vector2(Width < 0 ? Width : 0, Height < 0 ? Height : 0);
- L187 C44: new Vector2 :: readonly get => _translation + new Vector2(Width > 0 ? Width : 0, Height > 0 ? Height : 0);
- L290 C20: new RectangleF :: return new RectangleF(pos.X, containerHeight - pos.Y, Width, Height);
- L357 C32: new Vector2 :: => _translation += new Vector2(x, y);
- L361 C35: new Vector2 :: => new(_translation + new Vector2(x, y), _bounds, _localOriginPercentage);


## XREngine.Data/Geometry/Box.cs
- L59 C17: new Vector3 :: new Vector3(min.X, min.Y, min.Z),
- L60 C17: new Vector3 :: new Vector3(max.X, min.Y, min.Z),
- L61 C17: new Vector3 :: new Vector3(min.X, max.Y, min.Z),
- L62 C17: new Vector3 :: new Vector3(max.X, max.Y, min.Z),
- L63 C17: new Vector3 :: new Vector3(min.X, min.Y, max.Z),
- L64 C17: new Vector3 :: new Vector3(max.X, min.Y, max.Z),
- L65 C17: new Vector3 :: new Vector3(min.X, max.Y, max.Z),
- L66 C17: new Vector3 :: new Vector3(max.X, max.Y, max.Z)
- L71 C17: new Plane :: new Plane(Vector3.UnitX, -min.X),
- L72 C17: new Plane :: new Plane(-Vector3.UnitX, max.X),
- L73 C17: new Plane :: new Plane(Vector3.UnitY, -min.Y),
- L74 C17: new Plane :: new Plane(-Vector3.UnitY, max.Y),
- L75 C17: new Plane :: new Plane(Vector3.UnitZ, -min.Z),
- L76 C17: new Plane :: new Plane(-Vector3.UnitZ, max.Z)
- L146 C26: new Vector3 :: _localSize = new Vector3(uniformSize);
- L151 C26: new Vector3 :: _localSize = new Vector3(sizeX, sizeY, sizeZ);
- L232 C19: new NotImplementedException :: throw new NotImplementedException();
- L237 C19: new NotImplementedException :: throw new NotImplementedException();
- L242 C19: new NotImplementedException :: throw new NotImplementedException();
- L247 C19: new NotImplementedException :: throw new NotImplementedException();
- L252 C19: new NotImplementedException :: throw new NotImplementedException();
- L257 C19: new NotImplementedException :: throw new NotImplementedException();


## XREngine.Data/Geometry/Capsule.cs
- L142 C28: new Exception :: _ => throw new Exception(),
- L189 C19: new NotImplementedException :: throw new NotImplementedException();
- L352 C19: new NotImplementedException :: throw new NotImplementedException();


## XREngine.Data/Geometry/CapsuleX.cs
- L37 C19: new NotImplementedException :: throw new NotImplementedException();
- L42 C19: new NotImplementedException :: throw new NotImplementedException();
- L47 C19: new NotImplementedException :: throw new NotImplementedException();
- L52 C19: new NotImplementedException :: throw new NotImplementedException();
- L57 C19: new NotImplementedException :: throw new NotImplementedException();
- L62 C19: new NotImplementedException :: throw new NotImplementedException();
- L67 C19: new NotImplementedException :: throw new NotImplementedException();
- L72 C19: new NotImplementedException :: throw new NotImplementedException();
- L77 C19: new NotImplementedException :: throw new NotImplementedException();
- L82 C19: new NotImplementedException :: throw new NotImplementedException();


## XREngine.Data/Geometry/CapsuleY.cs
- L37 C19: new NotImplementedException :: throw new NotImplementedException();
- L42 C19: new NotImplementedException :: throw new NotImplementedException();
- L47 C19: new NotImplementedException :: throw new NotImplementedException();
- L52 C19: new NotImplementedException :: throw new NotImplementedException();
- L57 C19: new NotImplementedException :: throw new NotImplementedException();
- L62 C19: new NotImplementedException :: throw new NotImplementedException();
- L67 C19: new NotImplementedException :: throw new NotImplementedException();
- L72 C19: new NotImplementedException :: throw new NotImplementedException();
- L77 C19: new NotImplementedException :: throw new NotImplementedException();
- L82 C19: new NotImplementedException :: throw new NotImplementedException();


## XREngine.Data/Geometry/CapsuleZ.cs
- L37 C19: new NotImplementedException :: throw new NotImplementedException();
- L42 C19: new NotImplementedException :: throw new NotImplementedException();
- L47 C19: new NotImplementedException :: throw new NotImplementedException();
- L52 C19: new NotImplementedException :: throw new NotImplementedException();
- L57 C19: new NotImplementedException :: throw new NotImplementedException();
- L62 C19: new NotImplementedException :: throw new NotImplementedException();
- L67 C19: new NotImplementedException :: throw new NotImplementedException();
- L72 C19: new NotImplementedException :: throw new NotImplementedException();
- L77 C19: new NotImplementedException :: throw new NotImplementedException();
- L82 C19: new NotImplementedException :: throw new NotImplementedException();


## XREngine.Data/Geometry/Circle3D.cs
- L16 C22: new Plane :: _plane = new Plane(Globals.Up, 0.0f);
- L21 C22: new Plane :: _plane = new Plane(normal, distance);
- L28 C22: new Plane :: _plane = new Plane(normal, distance);
- L34 C22: new Plane :: _plane = new Plane(normal, distance);


## XREngine.Data/Geometry/Cone.cs
- L64 C19: new NotImplementedException :: throw new NotImplementedException();
- L69 C19: new NotImplementedException :: throw new NotImplementedException();
- L74 C19: new NotImplementedException :: throw new NotImplementedException();
- L92 C19: new NotImplementedException :: throw new NotImplementedException();
- L97 C19: new NotImplementedException :: throw new NotImplementedException();
- L102 C19: new NotImplementedException :: throw new NotImplementedException();
- L107 C19: new NotImplementedException :: throw new NotImplementedException();


## XREngine.Data/Geometry/ConeX.cs
- L53 C19: new NotImplementedException :: throw new NotImplementedException();
- L58 C19: new NotImplementedException :: throw new NotImplementedException();
- L63 C19: new NotImplementedException :: throw new NotImplementedException();
- L68 C19: new NotImplementedException :: throw new NotImplementedException();
- L73 C19: new NotImplementedException :: throw new NotImplementedException();
- L78 C19: new NotImplementedException :: throw new NotImplementedException();
- L83 C19: new NotImplementedException :: throw new NotImplementedException();
- L88 C19: new NotImplementedException :: throw new NotImplementedException();
- L93 C19: new NotImplementedException :: throw new NotImplementedException();


## XREngine.Data/Geometry/ConeY.cs
- L53 C19: new NotImplementedException :: throw new NotImplementedException();
- L58 C19: new NotImplementedException :: throw new NotImplementedException();
- L63 C19: new NotImplementedException :: throw new NotImplementedException();
- L68 C19: new NotImplementedException :: throw new NotImplementedException();
- L73 C19: new NotImplementedException :: throw new NotImplementedException();
- L78 C19: new NotImplementedException :: throw new NotImplementedException();
- L83 C19: new NotImplementedException :: throw new NotImplementedException();
- L88 C19: new NotImplementedException :: throw new NotImplementedException();
- L93 C19: new NotImplementedException :: throw new NotImplementedException();


## XREngine.Data/Geometry/ConeZ.cs
- L53 C19: new NotImplementedException :: throw new NotImplementedException();
- L58 C19: new NotImplementedException :: throw new NotImplementedException();
- L63 C19: new NotImplementedException :: throw new NotImplementedException();
- L68 C19: new NotImplementedException :: throw new NotImplementedException();
- L73 C19: new NotImplementedException :: throw new NotImplementedException();
- L78 C19: new NotImplementedException :: throw new NotImplementedException();
- L83 C19: new NotImplementedException :: throw new NotImplementedException();
- L88 C19: new NotImplementedException :: throw new NotImplementedException();
- L93 C19: new NotImplementedException :: throw new NotImplementedException();


## XREngine.Data/Geometry/Frustum.cs
- L23 C47: new Vector3 :: private readonly Vector3[] _corners = new Vector3[8];
- L30 C23: new InvalidOperationException :: throw new InvalidOperationException("Cannot invert the MVP matrix.");
- L51 C31: new Vector3 :: _corners[i] = new Vector3(corner.X, corner.Y, corner.Z);
- L55 C44: new Plane :: private readonly Plane[] _planes = new Plane[6];
- L61 C20: new Plane :: Left = new Plane(
- L68 C21: new Plane :: Right = new Plane(
- L75 C22: new Plane :: Bottom = new Plane(
- L82 C19: new Plane :: Top = new Plane(
- L89 C20: new Plane :: Near = new Plane(
- L96 C19: new Plane :: Far = new Plane(
- L115 C34: new Plane :: _planes[0] = new Plane(-_planes[0].Normal, -_planes[0].D);
- L127 C34: new Plane :: _planes[1] = new Plane(-_planes[1].Normal, -_planes[1].D);
- L139 C34: new Plane :: _planes[2] = new Plane(-_planes[2].Normal, -_planes[2].D);
- L151 C34: new Plane :: _planes[3] = new Plane(-_planes[3].Normal, -_planes[3].D);
- L163 C34: new Plane :: _planes[4] = new Plane(-_planes[4].Normal, -_planes[4].D);
- L175 C34: new Plane :: _planes[5] = new Plane(-_planes[5].Normal, -_planes[5].D);
- L187 C39: new Vector3 :: DivideW(Vector4.Transform(new Vector3(-1.0f, -1.0f, 0.0f), invProj)),
- L188 C39: new Vector3 :: DivideW(Vector4.Transform(new Vector3(1.0f, -1.0f, 0.0f), invProj)),
- L189 C39: new Vector3 :: DivideW(Vector4.Transform(new Vector3(-1.0f, 1.0f, 0.0f), invProj)),
- L190 C39: new Vector3 :: DivideW(Vector4.Transform(new Vector3(1.0f, 1.0f, 0.0f), invProj)),
- L191 C39: new Vector3 :: DivideW(Vector4.Transform(new Vector3(-1.0f, -1.0f, 1.0f), invProj)),
- L192 C39: new Vector3 :: DivideW(Vector4.Transform(new Vector3(1.0f, -1.0f, 1.0f), invProj)),
- L193 C39: new Vector3 :: DivideW(Vector4.Transform(new Vector3(-1.0f, 1.0f, 1.0f), invProj)),
- L194 C39: new Vector3 :: DivideW(Vector4.Transform(new Vector3(1.0f, 1.0f, 1.0f), invProj))) { }
- L206 C17: new Vector3 :: new Vector3(-w, -h, -farPlane),
- L207 C17: new Vector3 :: new Vector3(w, h, -nearPlane),
- L445 C22: new Plane :: f.Near = new Plane(_planes[4].Normal, _planes[4].D - startDepth);
- L446 C21: new Plane :: f.Far = new Plane(_planes[5].Normal, _planes[5].D + endDepth);
- L517 C28: new NotImplementedException :: _ => throw new NotImplementedException(),
- L563 C19: new NotImplementedException :: throw new NotImplementedException();
- L576 C20: new AABB :: return new AABB(min, max);
- L581 C25: new() :: Frustum f = new();
- L594 C33: new List :: var intersections = new List<Vector3>();
- L738 C32: new Segment :: var frustumEdges = new Segment[]
- L772 C28: new Segment :: var boxEdges = new Segment[]


## XREngine.Data/Geometry/GeoUtil.cs
- L124 C39: new Plane :: PlaneIntersectsSphere(new Plane(Vector3.UnitX, XRMath.GetPlaneDistance(maximum, Vector3.UnitX)), sphere),
- L125 C39: new Plane :: PlaneIntersectsSphere(new Plane(-Vector3.UnitX, XRMath.GetPlaneDistance(minimum, -Vector3.UnitX)), sphere),
- L126 C39: new Plane :: PlaneIntersectsSphere(new Plane(Vector3.UnitY, XRMath.GetPlaneDistance(maximum, Vector3.UnitY)), sphere),
- L127 C39: new Plane :: PlaneIntersectsSphere(new Plane(-Vector3.UnitY, XRMath.GetPlaneDistance(minimum, -Vector3.UnitY)), sphere),
- L128 C39: new Plane :: PlaneIntersectsSphere(new Plane(Vector3.UnitZ, XRMath.GetPlaneDistance(maximum, Vector3.UnitZ)), sphere),
- L129 C39: new Plane :: PlaneIntersectsSphere(new Plane(-Vector3.UnitZ, XRMath.GetPlaneDistance(minimum, -Vector3.UnitZ)), sphere),
- L823 C24: new Ray :: line = new Ray();
- L830 C20: new Ray :: line = new Ray(point, point + Vector3.Normalize(direction));
- L1359 C19: new NotImplementedException :: throw new NotImplementedException();


## XREngine.Data/Geometry/Plane.cs
- L35 C20: new System.Numerics.Plane :: return new System.Numerics.Plane()
- L49 C20: new System.Numerics.Plane :: return new System.Numerics.Plane()
- L59 C16: new() :: => new()
- L134 C55: new Vector3 :: bottomLeft = position + Vector3.Transform(new Vector3(-0.5f * xExtent, -0.5f * yExtent, 0.0f), r);
- L135 C56: new Vector3 :: bottomRight = position + Vector3.Transform(new Vector3(0.5f * xExtent, -0.5f * yExtent, 0.0f), r);
- L136 C52: new Vector3 :: topLeft = position + Vector3.Transform(new Vector3(-0.5f * xExtent, 0.5f * yExtent, 0.0f), r);
- L137 C53: new Vector3 :: topRight = position + Vector3.Transform(new Vector3(0.5f * xExtent, 0.5f * yExtent, 0.0f), r);
- L174 C29: new int :: int[] indices = new int[3];
- L187 C30: new Triangle :: back.Add(new Triangle(vertices[i], vertices[j], vertices[k]));
- L192 C31: new Triangle :: front.Add(new Triangle(vertices[i], vertices[j], vertices[k]));


## XREngine.Data/Geometry/PreparedFrustum.cs
- L35 C23: new ArgumentException :: throw new ArgumentException("Frustum must have 6 planes.", nameof(planes));
- L37 C23: new ArgumentException :: throw new ArgumentException("Frustum must have 8 corners.", nameof(corners));
- L43 C18: new float :: Nx = new float[PlaneCount];
- L44 C18: new float :: Ny = new float[PlaneCount];
- L45 C18: new float :: Nz = new float[PlaneCount];
- L46 C17: new float :: D = new float[PlaneCount];
- L75 C30: new Plane :: Plane[] planes = new Plane[6];
- L76 C33: new Vector3 :: Vector3[] corners = new Vector3[8];
- L84 C20: new PreparedFrustum :: return new PreparedFrustum(planes, corners);


## XREngine.Data/Geometry/Ray.cs
- L25 C20: new Ray :: return new Ray(newStart, newEnd - newStart);
- L70 C22: new Vector3 :: result = new Vector3();
- L84 C26: new Vector3 :: result = new Vector3();


## XREngine.Data/Geometry/Sphere.cs
- L64 C19: new NotImplementedException :: throw new NotImplementedException();
- L69 C19: new NotImplementedException :: throw new NotImplementedException();
- L80 C29: new Vector3 :: => new(Center - new Vector3(Radius), Center + new Vector3(Radius));
- L80 C59: new Vector3 :: => new(Center - new Vector3(Radius), Center + new Vector3(Radius));


## XREngine.Data/Geometry/Triangle.cs
- L115 C27: new Vector3 :: barycentric = new Vector3(u, v, w);


## XREngine.Data/Half.cs
- L72 C23: new ArithmeticException :: throw new ArithmeticException("Half: Positive maximum value exceeded.");
- L75 C23: new ArithmeticException :: throw new ArithmeticException("Half: Negative minimum value exceeded.");
- L79 C23: new ArithmeticException :: throw new ArithmeticException("Half: Input is not a number (NaN).");
- L82 C23: new ArithmeticException :: throw new ArithmeticException("Half: Input is positive infinity.");
- L85 C23: new ArithmeticException :: throw new ArithmeticException("Half: Input is negative infinity.");
- L184 C27: new ArithmeticException :: throw new ArithmeticException("Half: Hardware floating-point overflow.");


## XREngine.Data/Interp.cs
- L70 C32: new Vector2 :: Vector2[] points = new Vector2[pointCount];
- L108 C23: new InvalidOperationException :: throw new InvalidOperationException();
- L110 C32: new Vector2 :: Vector2[] points = new Vector2[pointCount];
- L696 C20: new Point :: return new Point(
- L706 C20: new PointF :: return new PointF(
- L718 C31: new float :: float[] samples = new float[count];
- L729 C33: new Vector2 :: Vector2[] samples = new Vector2[count];
- L740 C33: new Vector3 :: Vector3[] samples = new Vector3[count];
- L751 C33: new Vector4 :: Vector4[] samples = new Vector4[count];


## XREngine.Data/Lists/ConsistentIndexList.cs
- L11 C42: new List :: private readonly List<T> _list = new List<T>();
- L12 C51: new List :: private readonly List<int> _nullIndices = new List<int>();
- L13 C53: new List :: private readonly List<int> _activeIndices = new List<int>();
- L14 C54: new ReaderWriterLockSlim :: private readonly ReaderWriterLockSlim _rwl = new ReaderWriterLockSlim();


## XREngine.Data/Lists/Deque/Deque.cs
- L85 C23: new ArgumentNullException :: throw new ArgumentNullException("col");
- L154 C28: new Node :: Node newNode = new Node(obj);
- L198 C28: new Node :: Node newNode = new Node(obj);
- L249 C23: new InvalidOperationException :: throw new InvalidOperationException("Deque is empty.");
- L302 C23: new InvalidOperationException :: throw new InvalidOperationException("Deque is empty.");
- L355 C23: new InvalidOperationException :: throw new InvalidOperationException("Deque is empty.");
- L378 C23: new InvalidOperationException :: throw new InvalidOperationException("Deque is empty.");
- L394 C31: new object :: object?[] array = new object[Count];
- L421 C23: new ArgumentNullException :: throw new ArgumentNullException("deque");
- L426 C20: new SynchronizedDeque :: return new SynchronizedDeque(deque);
- L549 C27: new InvalidOperationException :: throw new InvalidOperationException(
- L568 C31: new InvalidOperationException :: throw new InvalidOperationException(
- L585 C27: new InvalidOperationException :: throw new InvalidOperationException(
- L638 C27: new ArgumentNullException :: throw new ArgumentNullException("deque");
- L853 C23: new ArgumentNullException :: throw new ArgumentNullException("array");
- L857 C23: new ArgumentOutOfRangeException :: throw new ArgumentOutOfRangeException("index", index,
- L862 C23: new ArgumentException :: throw new ArgumentException("Array is multidimensional.");
- L866 C23: new ArgumentException :: throw new ArgumentException("Index is equal to or greater " +
- L871 C23: new ArgumentException :: throw new ArgumentException(
- L911 C20: new DequeEnumerator :: return new DequeEnumerator(this);
- L926 C27: new Deque :: Deque clone = new Deque(this);


## XREngine.Data/Lists/Deque/GenericDeque.cs
- L85 C23: new ArgumentNullException :: throw new ArgumentNullException("col");
- L143 C33: new Node :: Node? f = front ??= new Node(defaultValue);
- L147 C28: new Node :: f.Next ??= new Node(defaultValue) { Previous = f };
- L314 C23: new InvalidOperationException :: throw new InvalidOperationException("Deque is empty.");
- L367 C23: new InvalidOperationException :: throw new InvalidOperationException("Deque is empty.");
- L420 C23: new InvalidOperationException :: throw new InvalidOperationException("Deque is empty.");
- L443 C23: new InvalidOperationException :: throw new InvalidOperationException("Deque is empty.");
- L459 C25: new T :: T[] array = new T[Count];
- L488 C20: new SynchronizedDeque :: return new SynchronizedDeque(deque);
- L573 C23: new ArgumentNullException :: throw new ArgumentNullException(nameof(array));
- L577 C23: new ArgumentOutOfRangeException :: throw new ArgumentOutOfRangeException(nameof(index), index,
- L582 C23: new ArgumentException :: throw new ArgumentException("Array is multidimensional.");
- L586 C23: new ArgumentException :: throw new ArgumentException("Index is equal to or greater " +
- L591 C23: new ArgumentException :: throw new ArgumentException(
- L631 C20: new Enumerator :: return new Enumerator(this);
- L660 C20: new Enumerator :: return new Enumerator(this);


## XREngine.Data/Lists/Deque/GenericDeque.Enumerator.cs
- L39 C27: new ObjectDisposedException :: throw new ObjectDisposedException(this.GetType().Name);
- L43 C27: new InvalidOperationException :: throw new InvalidOperationException(
- L62 C31: new ObjectDisposedException :: throw new ObjectDisposedException(this.GetType().Name);
- L66 C31: new InvalidOperationException :: throw new InvalidOperationException(
- L83 C27: new ObjectDisposedException :: throw new ObjectDisposedException(this.GetType().Name);
- L87 C27: new InvalidOperationException :: throw new InvalidOperationException(
- L121 C31: new ObjectDisposedException :: throw new ObjectDisposedException(this.GetType().Name);
- L125 C31: new InvalidOperationException :: throw new InvalidOperationException(


## XREngine.Data/Lists/Deque/GenericDeque.Synchronized.cs
- L31 C27: new ArgumentNullException :: throw new ArgumentNullException("deque");


## XREngine.Data/Lists/Deque/GenericTester.cs
- L117 C27: new int :: int[] array = new int[deque.Count];
- L126 C21: new int :: array = new int[deque.Count * 2];
- L135 C21: new int :: array = new int[deque.Count];
- L183 C30: new int :: deque.CopyTo(new int[10, 10], deque.Count);


## XREngine.Data/Lists/Deque/Tester.cs
- L89 C27: new InvalidOperationException :: throw new InvalidOperationException("Expected integer value in deque test");
- L108 C27: new InvalidOperationException :: throw new InvalidOperationException("Expected integer value in deque test");
- L137 C27: new int :: int[] array = new int[deque.Count];
- L146 C21: new int :: array = new int[deque.Count * 2];
- L155 C21: new int :: array = new int[deque.Count];
- L203 C30: new int :: deque.CopyTo(new int[10, 10], deque.Count);


## XREngine.Data/Lists/EventArray.cs
- L46 C22: new T :: _array = new T[size];
- L49 C48: new HashSet :: private HashSet<int> _changedIndices = new HashSet<int>();
- L66 C55: new ArgumentNullException :: set => _array[index] = (T)(value ?? throw new ArgumentNullException(nameof(value)));
- L94 C20: new EventArray :: return new EventArray<T>(_array);
- L158 C57: new NotifyCollectionChangedEventArgs :: CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Replace));


## XREngine.Data/Lists/EventList.cs
- L13 C9: new int :: new int Count { get; }
- L35 C9: new T :: new T this[int index] { get; }
- L50 C36: new ReaderWriterLockSlim :: set => _lock = value ? new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion) : null;
- L180 C21: new List :: _list = new List<T>(capacity);
- L246 C53: new TCollectionChangedEventArgs :: CollectionChanged?.Invoke(this, new TCollectionChangedEventArgs<T>(ECollectionChangedAction.Add, item));
- L326 C53: new TCollectionChangedEventArgs :: CollectionChanged?.Invoke(this, new TCollectionChangedEventArgs<T>(ECollectionChangedAction.Add, collection.ToList()));
- L378 C57: new TCollectionChangedEventArgs :: CollectionChanged?.Invoke(this, new TCollectionChangedEventArgs<T>(ECollectionChangedAction.Remove, item));
- L438 C53: new TCollectionChangedEventArgs :: CollectionChanged?.Invoke(this, new TCollectionChangedEventArgs<T>(ECollectionChangedAction.Remove, range.ToList()));
- L492 C53: new TCollectionChangedEventArgs :: CollectionChanged?.Invoke(this, new TCollectionChangedEventArgs<T>(ECollectionChangedAction.Remove, item));
- L549 C53: new TCollectionChangedEventArgs :: CollectionChanged?.Invoke(this, new TCollectionChangedEventArgs<T>(ECollectionChangedAction.Clear));
- L609 C53: new TCollectionChangedEventArgs :: CollectionChanged?.Invoke(this, new TCollectionChangedEventArgs<T>(ECollectionChangedAction.Remove, matches!.ToArray()));
- L682 C53: new TCollectionChangedEventArgs :: CollectionChanged?.Invoke(this, new TCollectionChangedEventArgs<T>(ECollectionChangedAction.Add, item));
- L742 C53: new TCollectionChangedEventArgs :: CollectionChanged?.Invoke(this, new TCollectionChangedEventArgs<T>(ECollectionChangedAction.Remove, collection.ToList()));
- L938 C53: new TCollectionChangedEventArgs :: CollectionChanged?.Invoke(this, new TCollectionChangedEventArgs<T>(ECollectionChangedAction.Replace, value, index));
- L976 C29: new ThreadSafeListEnumerator :: => ThreadSafe ? new ThreadSafeListEnumerator<T>(_list, _lock) : _list.GetEnumerator();


## XREngine.Data/Lists/EventList.MemoryPack.cs
- L48 C24: new List :: var list = new List<T>(count);
- L54 C21: new EventList :: value = new EventList<T>(list, allowDuplicates, allowNull);


## XREngine.Data/Lists/HashedQueue.cs
- L5 C55: new ReaderWriterLockSlim :: private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
- L11 C21: new HashSet :: _hash = new HashSet<T>();
- L15 C21: new HashSet :: _hash = new HashSet<T>();
- L19 C21: new HashSet :: _hash = new HashSet<T>();
- L23 C16: new void :: public new void Clear()
- L37 C16: new T :: public new T Dequeue()
- L52 C16: new bool :: public new bool Enqueue(T item)


## XREngine.Data/Lists/HashedStack.cs
- L9 C21: new HashSet :: _hash = new HashSet<T>();
- L13 C21: new HashSet :: _hash = new HashSet<T>();
- L17 C21: new HashSet :: _hash = new HashSet<T>();
- L21 C16: new void :: public new void Clear()
- L26 C16: new T :: public new T Pop()
- L32 C16: new bool :: public new bool Push(T item)


## XREngine.Data/Lists/ThreadSafeEnumerator.cs
- L31 C21: new InvalidOperationException :: ? throw new InvalidOperationException()


## XREngine.Data/Lists/ThreadSafeList.cs
- L18 C16: new ThreadSafeEnumerator :: => new ThreadSafeEnumerator<T>(_inner.GetEnumerator(), _lock);
- L26 C48: new ReaderWriterLockSlim :: protected ReaderWriterLockSlim _lock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
- L47 C16: new T :: public new T this[int index]
- L79 C16: new void :: public new void Add(T item)
- L84 C16: new void :: public new void AddRange(IEnumerable<T> collection)
- L89 C16: new bool :: public new bool Remove(T item)
- L94 C16: new void :: public new void RemoveRange(int index, int count)
- L99 C16: new void :: public new void RemoveAt(int index)
- L104 C16: new void :: public new void Clear()
- L109 C16: new void :: public new void RemoveAll(Predicate<T> match)
- L114 C16: new void :: public new void Insert(int index, T item)
- L119 C16: new void :: public new void InsertRange(int index, IEnumerable<T> collection)
- L124 C16: new ReadOnlyCollection :: public new ReadOnlyCollection<T> AsReadOnly()
- L129 C16: new int :: public new int BinarySearch(int index, int count, T item, IComparer<T> comparer)
- L134 C16: new int :: public new int BinarySearch(T item)
- L139 C16: new int :: public new int BinarySearch(T item, IComparer<T> comparer)
- L144 C16: new bool :: public new bool Contains(T item)
- L149 C16: new List :: public new List<TOutput> ConvertAll<TOutput>(Converter<T, TOutput> converter)
- L154 C16: new void :: public new void CopyTo(T[] array, int arrayIndex)
- L159 C16: new void :: public new void CopyTo(int index, T[] array, int arrayIndex, int count)
- L164 C16: new void :: public new void CopyTo(T[] array)
- L169 C16: new bool :: public new bool Exists(Predicate<T> match)
- L174 C16: new T :: public new T? Find(Predicate<T> match)
- L179 C16: new List :: public new List<T> FindAll(Predicate<T> match)
- L184 C16: new int :: public new int FindIndex(Predicate<T> match)
- L189 C16: new int :: public new int FindIndex(int startIndex, Predicate<T> match)
- L194 C16: new int :: public new int FindIndex(int startIndex, int count, Predicate<T> match)
- L199 C16: new T :: public new T? FindLast(Predicate<T> match)
- L204 C16: new int :: public new int FindLastIndex(Predicate<T> match)
- L209 C16: new int :: public new int FindLastIndex(int startIndex, Predicate<T> match)
- L214 C16: new int :: public new int FindLastIndex(int startIndex, int count, Predicate<T> match)
- L219 C16: new void :: public new void ForEach(Action<T> action)
- L224 C16: new IEnumerator :: public new IEnumerator<T> GetEnumerator()
- L225 C16: new ThreadSafeListEnumerator :: => new ThreadSafeListEnumerator<T>(this, _lock);
- L226 C16: new List :: public new List<T> GetRange(int index, int count)
- L231 C16: new int :: public new int IndexOf(T item, int index, int count)
- L236 C16: new int :: public new int IndexOf(T item, int index)
- L241 C16: new int :: public new int IndexOf(T item)
- L246 C16: new int :: public new int LastIndexOf(T item)
- L251 C16: new int :: public new int LastIndexOf(T item, int index)
- L256 C16: new int :: public new int LastIndexOf(T item, int index, int count)
- L261 C16: new void :: public new void Reverse(int index, int count)
- L266 C16: new void :: public new void Reverse()
- L271 C16: new void :: public new void Sort(int index, int count, IComparer<T> comparer)
- L276 C16: new void :: public new void Sort(Comparison<T> comparison)
- L281 C16: new void :: public new void Sort()
- L286 C16: new void :: public new void Sort(IComparer<T> comparer)
- L291 C16: new T :: public new T[] ToArray()
- L296 C16: new bool :: public new bool TrueForAll(Predicate<T> match)


## XREngine.Data/Lists/Unsafe/UTF8ArrayPtr.cs
- L40 C28: new IntPtr :: var pointers = new IntPtr[strings.Count];


## XREngine.Data/Measurement.cs
- L40 C20: new FeetInches :: return new FeetInches(ift, (feet - ift) * 12.0f);


## XREngine.Data/MMD/VMD/AnimationBase.cs
- L6 C152: new() :: public abstract class AnimationBase<T> : Dictionary<string, FrameDictionary<T>>, IBinaryDataSource where T : class, IBinaryDataSource, IFramesKey, new()
- L21 C30: new() :: T frameKey = new();
- L38 C77: new byte :: byte[] nameBytes = [.. VMDUtils.ToShiftJisBytes(kv.Key), .. new byte[15 - kv.Key.Length]];
- L49 C32: new() :: StringBuilder sb = new();


## XREngine.Data/MMD/VMD/AnimationListBase.cs
- L3 C87: new() :: public abstract class AnimationListBase<T> : List<T> where T : IBinaryDataSource, new()
- L10 C30: new() :: T frameKey = new();


## XREngine.Data/MMD/VMD/BoneFrameKey.cs
- L17 C66: new Vector3 :: var pos = Interp.Lerp(Translation, next.Translation, new Vector3(
- L31 C27: new Vector3 :: Translation = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() * -1.0f) * VMDUtils.MMDUnitsToMeters;
- L32 C44: new Quaternion :: Rotation = InvertZAxisRotation(new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
- L42 C26: new float :: writer.Write(new float[] { translation.X, translation.Y, translation.Z * -1.0f }.SelectMany(BitConverter.GetBytes).ToArray());
- L43 C26: new float :: writer.Write(new float[] { rotation.X, rotation.Y, rotation.Z, rotation.W }.SelectMany(BitConverter.GetBytes).ToArray());
- L58 C34: new VMDBezier :: TranslationXBezier = new VMDBezier(new Vector2(x0, y0), new Vector2(x1, y1));
- L58 C48: new Vector2 :: TranslationXBezier = new VMDBezier(new Vector2(x0, y0), new Vector2(x1, y1));
- L58 C69: new Vector2 :: TranslationXBezier = new VMDBezier(new Vector2(x0, y0), new Vector2(x1, y1));
- L61 C34: new VMDBezier :: TranslationYBezier = new VMDBezier(new Vector2(x2, y2), new Vector2(x3, y3));
- L61 C48: new Vector2 :: TranslationYBezier = new VMDBezier(new Vector2(x2, y2), new Vector2(x3, y3));
- L61 C69: new Vector2 :: TranslationYBezier = new VMDBezier(new Vector2(x2, y2), new Vector2(x3, y3));
- L64 C34: new VMDBezier :: TranslationZBezier = new VMDBezier(new Vector2(x4, y4), new Vector2(x5, y5));
- L64 C48: new Vector2 :: TranslationZBezier = new VMDBezier(new Vector2(x4, y4), new Vector2(x5, y5));
- L64 C69: new Vector2 :: TranslationZBezier = new VMDBezier(new Vector2(x4, y4), new Vector2(x5, y5));
- L67 C30: new VMDBezier :: RotationBezier = new VMDBezier(new Vector2(x6, y6), new Vector2(x7, y7));
- L67 C44: new Vector2 :: RotationBezier = new VMDBezier(new Vector2(x6, y6), new Vector2(x7, y7));
- L67 C65: new Vector2 :: RotationBezier = new VMDBezier(new Vector2(x6, y6), new Vector2(x7, y7));
- L80 C30: new sbyte :: sbyte[] interp = new sbyte[64];
- L82 C36: new VMDBezier :: TranslationXBezier ??= new VMDBezier(Vector2.Zero, Vector2.Zero);
- L85 C36: new VMDBezier :: TranslationYBezier ??= new VMDBezier(Vector2.Zero, Vector2.Zero);
- L88 C36: new VMDBezier :: TranslationZBezier ??= new VMDBezier(Vector2.Zero, Vector2.Zero);
- L91 C32: new VMDBezier :: RotationBezier ??= new VMDBezier(Vector2.Zero, Vector2.Zero);


## XREngine.Data/MMD/VMD/CameraKeyFrameKey.cs
- L12 C55: new sbyte :: public sbyte[] Interp { get; private set; } = new sbyte[24];
- L20 C24: new Vector3 :: Location = new Vector3(reader.ReadBytes(12).SelectEvery(4, x => BitConverter.ToSingle([.. x], 0)).ToArray());
- L21 C24: new Vector3 :: Rotation = new Vector3(reader.ReadBytes(12).SelectEvery(4, x => BitConverter.ToSingle([.. x], 0)).ToArray());
- L31 C26: new float :: writer.Write(new float[] { Location.X, Location.Y, Location.Z }.SelectMany(BitConverter.GetBytes).ToArray());
- L32 C26: new float :: writer.Write(new float[] { Rotation.X, Rotation.Y, Rotation.Z }.SelectMany(BitConverter.GetBytes).ToArray());


## XREngine.Data/MMD/VMD/FrameDictionary.cs
- L7 C124: new() :: public class FrameDictionary<T> : XRBase, IReadOnlyDictionary<uint, T> where T : class, IBinaryDataSource, IFramesKey, new()


## XREngine.Data/MMD/VMD/LampKeyFrameKey.cs
- L16 C21: new Vector3 :: Color = new Vector3(reader.ReadBytes(12).SelectEvery(4, x => BitConverter.ToSingle([.. x], 0)).ToArray());
- L17 C25: new Vector3 :: Direction = new Vector3(reader.ReadBytes(12).SelectEvery(4, x => BitConverter.ToSingle([.. x], 0)).ToArray());
- L23 C26: new float :: writer.Write(new float[] { Color.R, Color.G, Color.B }.SelectMany(BitConverter.GetBytes).ToArray());
- L24 C26: new float :: writer.Write(new float[] { Direction.X, Direction.Y, Direction.Z }.SelectMany(BitConverter.GetBytes).ToArray());


## XREngine.Data/MMD/VMD/PropertyFrameKey.cs
- L31 C70: new byte :: writer.Write(VMDUtils.ToShiftJisBytes(ikName).Concat(new byte[20 - ikName.Length]).ToArray());


## XREngine.Data/MMD/VMD/SelfShadowFrameKey.cs
- L15 C23: new InvalidFileError :: throw new InvalidFileError($"Invalid self shadow mode {Mode} at frame {FrameNumber}");


## XREngine.Data/MMD/VMD/VMDFile.cs
- L19 C32: new BinaryReader :: using var reader = new BinaryReader(File.OpenRead(path));
- L22 C22: new VMDHeader :: Header = new VMDHeader();
- L48 C23: new InvalidOperationException :: throw new InvalidOperationException("Cannot save VMD file without loading it first.");
- L51 C32: new BinaryWriter :: using var writer = new BinaryWriter(File.OpenWrite(path));
- L62 C32: new() :: StringBuilder sb = new();


## XREngine.Data/MMD/VMD/VMDHeader.cs
- L16 C23: new InvalidFileError :: throw new InvalidFileError($"File signature \"{Encoding.ASCII.GetString(sig)}\" is invalid.");
- L24 C69: new byte :: writer.Write(VMDUtils.ToShiftJisBytes(ModelName).Concat(new byte[20 - ModelName.Length]).ToArray());


## XREngine.Data/MMD/VMD/VMDUtils.cs
- L320 C67: new() :: public static readonly Dictionary<string, string> JP2EN = new()


## XREngine.Data/Native/NativeStructs.cs
- L56 C16: new RECT :: => new RECT(x, y, x + width, y + height);


## XREngine.Data/Remapper.cs
- L29 C23: new InvalidOperationException :: throw new InvalidOperationException();
- L35 C50: new() :: private static readonly object NullKey = new();
- L40 C45: new() :: Dictionary<object, int> cache = new();
- L43 C27: new int :: _remapTable = new int[count];
- L44 C25: new int :: _impTable = new int[count];
- L77 C28: new int :: int[] sorted = new int[impCount];


## XREngine.Data/Rendering/Index/IndexQuad.cs
- L62 C31: new IndexTriangle :: triangles.Add(new IndexTriangle(Point0, Point1, Point2));
- L63 C31: new IndexTriangle :: triangles.Add(new IndexTriangle(Point0, Point2, Point3));
- L67 C31: new IndexTriangle :: triangles.Add(new IndexTriangle(Point0, Point1, Point3));
- L68 C31: new IndexTriangle :: triangles.Add(new IndexTriangle(Point3, Point1, Point2));


## XREngine.Data/Rendering/Index/IndexQuadStrip.cs
- L18 C31: new IndexTriangle :: triangles.Add(new IndexTriangle(_points[i], _points[i + 1], _points[i + 2]));
- L19 C31: new IndexTriangle :: triangles.Add(new IndexTriangle(_points[i + 1], _points[i + 2], _points[i + 3]));


## XREngine.Data/Rendering/Index/IndexTriangleFan.cs
- L14 C31: new IndexTriangle :: triangles.Add(new IndexTriangle(_points[0], _points[i], _points[i + 1]));


## XREngine.Data/Rendering/Index/IndexTriangleStrip.cs
- L21 C23: new Exception :: throw new Exception("A triangle strip needs 3 or more points.");
- L37 C31: new IndexTriangle :: triangles.Add(new IndexTriangle(


## XREngine.Data/Rendering/VertexWeightGroup.cs
- L52 C24: new EventDictionary :: _weights = new EventDictionary<int, float> { { boneIndex, 1.0f } };
- L58 C24: new EventDictionary :: _weights = new EventDictionary<int, float>(weights);
- L107 C34: new int :: int[] keysToRemove = new int[Weights.Count - WeightLimit];
- L160 C38: new int :: int[] keysToRemove = new int[weights.Count - maxWeightCount];


## XREngine.Data/ResourcePool.cs
- L42 C45: new ArgumentNullException :: _generator = generator ?? throw new ArgumentNullException(nameof(generator));


## XREngine.Data/ThreadSafeHashSet.cs
- L8 C57: new ReaderWriterLockSlim :: protected readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim(LockRecursionPolicy.SupportsRecursion);
- L13 C22: new HashSet :: _inner = new HashSet<T>();
- L17 C22: new HashSet :: _inner = new HashSet<T>(comparer);
- L21 C22: new HashSet :: _inner = new HashSet<T>(collection);
- L25 C22: new HashSet :: _inner = new HashSet<T>(collection, comparer);
- L71 C16: new ThreadSafeEnumerator :: => new ThreadSafeEnumerator<T>(_inner.GetEnumerator(), _lock);


## XREngine.Data/Tools/CoACD.cs
- L33 C23: new ArgumentException :: throw new ArgumentException("Triangle index buffer length must be divisible by 3.", nameof(triangleIndices));
- L36 C37: new double :: double[] vertexBuffer = new double[checked(positions.Length * 3)];
- L51 C42: new() :: NativeMesh nativeInput = new()
- L114 C26: new List :: var meshes = new List<ConvexHullMesh>(meshCount);
- L125 C39: new double :: double[] vertexData = new double[vertexCount * 3];
- L127 C38: new Vector3 :: Vector3[] vertices = new Vector3[vertexCount];
- L131 C35: new Vector3 :: vertices[v] = new Vector3(
- L138 C53: new int :: int[] indices = triangleCount > 0 ? new int[triangleCount * 3] : Array.Empty<int>();
- L142 C28: new ConvexHullMesh :: meshes.Add(new ConvexHullMesh(vertices, indices));
- L217 C54: new() :: public static CoACDParameters Default => new();


## XREngine.Data/Tools/Miniball.cs
- L26 C22: new FloatingPoint :: center = new FloatingPoint[dim];
- L27 C27: new FloatingPoint :: centerToAff = new FloatingPoint[dim];
- L28 C29: new FloatingPoint :: centerToPoint = new FloatingPoint[dim];
- L29 C23: new FloatingPoint :: lambdas = new FloatingPoint[dim + 1];
- L72 C20: new Subspan :: return new Subspan(dim, S, farthest);
- L262 C20: new Quality :: return new Quality(qr_error, min_lambda, max_overlength / radius, Math.Abs(min_underlength / radius), iteration, support.Size);
- L292 C52: new FloatingPoint :: private readonly FloatingPoint[] _values = new FloatingPoint[size * dimensions];
- L377 C41: new bool :: private readonly bool[] _bits = new bool[size];
- L409 C26: new BitSet :: membership = new BitSet(points.Size);
- L410 C23: new int :: members = new int[dim + 1];
- L414 C17: new FloatingPoint :: Q = new FloatingPoint[dim][];
- L415 C17: new FloatingPoint :: R = new FloatingPoint[dim][];
- L418 C24: new FloatingPoint :: Q[i] = new FloatingPoint[dim];
- L419 C24: new FloatingPoint :: R[i] = new FloatingPoint[dim];
- L421 C17: new FloatingPoint :: u = new FloatingPoint[dim];
- L422 C17: new FloatingPoint :: w = new FloatingPoint[dim];
- L570 C39: new FloatingPoint :: FloatingPoint[] lambdas = new FloatingPoint[Size];
- L571 C34: new FloatingPoint :: FloatingPoint[] pt = new FloatingPoint[dim];


## XREngine.Data/Tools/SimplePriorityQueue.cs
- L7 C63: new() :: private readonly List<KeyValuePair<T, float>> _heap = new();
- L15 C23: new KeyValuePair :: _heap.Add(new KeyValuePair<T, float>(item, priority));
- L33 C23: new InvalidOperationException :: throw new InvalidOperationException("The queue is empty.");
- L123 C28: new KeyValuePair :: _heap[index] = new KeyValuePair<T, float>(item, newPriority);


## XREngine.Data/Tools/SimplePriorityQueue1.cs
- L7 C67: new() :: private readonly SortedDictionary<TKey, List<T>> _queue = new();
- L17 C26: new List :: bucket = new List<T>();
- L27 C23: new InvalidOperationException :: throw new InvalidOperationException("The queue is empty.");
- L31 C23: new InvalidOperationException :: throw new InvalidOperationException("The queue is empty.");


## XREngine.Data/TraceListener.cs
- L8 C41: new() :: private readonly object _lock = new();


## XREngine.Data/Transforms/Rotations/Rotator.cs
- L211 C28: new Exception :: _ => throw new Exception("Invalid rotation order"),
- L230 C81: new() :: public static Rotator Clamp(Rotator value, Rotator min, Rotator max) => new()
- L242 C20: new Rotator :: return new Rotator(
- L258 C25: new() :: Rotator v = new()
- L495 C20: new Rotator :: return new Rotator(
- L505 C20: new Rotator :: return new Rotator(
- L539 C20: new Rotator :: return new Rotator(float.RadiansToDegrees(euler.X), float.RadiansToDegrees(euler.Y), float.RadiansToDegrees(euler.Z), ERotationOrder.YPR);


## XREngine.Data/Transforms/Rotations/Rotor.cs
- L25 C21: new Vector4 :: => _v = new Vector4(a, b01, b02, b12);
- L106 C20: new Vector3 :: return new Vector3(
- L135 C33: new Vector3 :: Vector3 xy = Rotate(new Vector3(1, 0, 0));
- L136 C33: new Vector3 :: Vector3 xz = Rotate(new Vector3(0, 1, 0));
- L137 C33: new Vector3 :: Vector3 yz = Rotate(new Vector3(0, 0, 1));
- L140 C20: new Matrix4x4 :: return new Matrix4x4(
- L165 C20: new Rotor :: return new Rotor(
- L188 C20: new Rotor :: return new Rotor(
- L203 C24: new Vector3 :: axis = new Vector3(1, 0, 0); // arbitrary
- L205 C24: new Vector3 :: axis = new Vector3(q.X / s, q.Y / s, q.Z / s);
- L210 C16: new Rotor :: => new Rotor(
- L223 C21: new Rotor :: b = new Rotor(-b.A, -b.B01, -b.B02, -b.B12);
- L238 C20: new Rotor :: return new Rotor(


## XREngine.Data/Trees/BVH/BVH.cs
- L67 C24: new List :: var hits = new List<BVHNode<GO>>();
- L97 C23: new Exception :: throw new Exception("In order to use optimize, you must set LEAF_OBJ_MAX=1");
- L137 C28: new BVHNode :: _rootBVH = new BVHNode<GO>(this, objects);
- L140 C28: new BVHNode :: _rootBVH = new BVHNode<GO>(this)


## XREngine.Data/Trees/BVH/BVHNode.cs
- L68 C27: new Exception :: throw new Exception("ssBVH Leaf has objects and left/right pointers!");
- L79 C24: new NotSupportedException :: _ => throw new NotSupportedException(),
- L91 C23: new Exception :: throw new Exception("dangling leaf!");
- L152 C23: new AABB :: box = new AABB(
- L153 C21: new Vector3 :: new Vector3(minX, minY, minZ),
- L154 C21: new Vector3 :: new Vector3(maxX, maxY, maxZ));
- L168 C19: new AABB :: box = new AABB(
- L169 C17: new Vector3 :: new Vector3(minX, minY, minZ),
- L170 C17: new Vector3 :: new Vector3(maxX, maxY, maxZ));
- L183 C23: new NotImplementedException :: throw new NotImplementedException();  // TODO: fix this... we should never get called in this case...
- L253 C20: new AABB :: return new AABB(new Vector3(-radius), new Vector3(radius));
- L253 C29: new Vector3 :: return new AABB(new Vector3(-radius), new Vector3(radius));
- L253 C51: new Vector3 :: return new AABB(new Vector3(-radius), new Vector3(radius));
- L293 C37: new InvalidOperationException :: => adaptor.BVH ?? throw new InvalidOperationException("Adaptor must be bound to a BVH instance.");
- L332 C33: new RotOpt :: Rot.NONE => new RotOpt(mySA, Rot.NONE),
- L334 C27: new RotOpt :: ? new RotOpt(float.MaxValue, Rot.NONE)
- L335 C27: new RotOpt :: : new RotOpt(SA(rightChild.left) + SA(AABBofPair(leftChild, rightChild.right)), rot),
- L337 C27: new RotOpt :: ? new RotOpt(float.MaxValue, Rot.NONE)
- L338 C27: new RotOpt :: : new RotOpt(SA(rightChild.right) + SA(AABBofPair(leftChild, rightChild.left)), rot),
- L340 C27: new RotOpt :: ? new RotOpt(float.MaxValue, Rot.NONE)
- L341 C27: new RotOpt :: : new RotOpt(SA(AABBofPair(rightChild, leftChild.right)) + SA(leftChild.left), rot),
- L343 C27: new RotOpt :: ? new RotOpt(float.MaxValue, Rot.NONE)
- L344 C27: new RotOpt :: : new RotOpt(SA(AABBofPair(rightChild, leftChild.left)) + SA(leftChild.right), rot),
- L346 C27: new RotOpt :: ? new RotOpt(float.MaxValue, Rot.NONE)
- L347 C27: new RotOpt :: : new RotOpt(SA(AABBofPair(rightChild.right, leftChild.right)) + SA(AABBofPair(rightChild.left, leftChild.left)), rot),
- L349 C27: new RotOpt :: ? new RotOpt(float.MaxValue, Rot.NONE)
- L350 C27: new RotOpt :: : new RotOpt(SA(AABBofPair(rightChild.left, leftChild.right)) + SA(AABBofPair(leftChild.left, rightChild.right)), rot),
- L351 C32: new NotImplementedException :: _ => throw new NotImplementedException($"missing implementation for BVH Rotation SAH Computation .. {rot}"),
- L353 C19: new RotOpt :: }) ?? new RotOpt(mySA, Rot.NONE);
- L451 C31: new NotImplementedException :: throw new NotImplementedException($"missing implementation for BVH Rotation .. {bestRot.rot}");
- L495 C23: new InvalidOperationException :: throw new InvalidOperationException("Cannot split a BVH node without objects.");
- L503 C35: new List :: var orderedlist = new List<GO>(splitlist);
- L516 C31: new NotImplementedException :: throw new NotImplementedException("unknown split axis: " + axis.ToString());
- L523 C24: new SplitAxisOpt :: return new SplitAxisOpt(SAH, axis, left_s, right_s);
- L526 C60: new InvalidOperationException :: SplitAxisOpt bestSplit = bestSplitOpt ?? throw new InvalidOperationException("Unable to determine BVH split axis.");
- L531 C20: new BVHNode :: left = new BVHNode<GO>(bvh, this, bestSplit.left, bestSplit.axis, this.depth + 1); // Split the Hierarchy to the left
- L532 C21: new BVHNode :: right = new BVHNode<GO>(bvh, this, bestSplit.right, bestSplit.axis, this.depth + 1); // Split the Hierarchy to the right
- L551 C46: new InvalidOperationException :: var left = curNode.left ?? throw new InvalidOperationException("Left child missing during pushdown.");
- L552 C48: new InvalidOperationException :: var right = curNode.right ?? throw new InvalidOperationException("Right child missing during pushdown.");
- L556 C33: new BVHNode :: var mergedSubnode = new BVHNode<GO>(bvh)
- L568 C30: new BVHNode :: var newSubnode = new BVHNode<GO>(bvh)
- L579 C65: new depths :: curNode.SetDepth(nAda, curNode.depth); // propagate new depths to our children.
- L595 C27: new InvalidOperationException :: throw new InvalidOperationException("Interior BVH nodes must have both children.");
- L640 C23: new Exception :: throw new Exception("removeObject() called on nonLeaf!");
- L673 C23: new Exception :: throw new Exception("bad intermediate node");
- L682 C23: new Exception :: throw new Exception("removeLeaf doesn't match any leaf!");
- L796 C23: new AABB :: box = new AABB(
- L797 C21: new Vector3 :: new Vector3(minX, minY, minZ),
- L798 C21: new Vector3 :: new Vector3(maxX, maxY, maxZ));
- L840 C26: new AABB :: newBox = new AABB(
- L841 C21: new Vector3 :: new Vector3(newMinX, newMinY, newMinZ),
- L842 C21: new Vector3 :: new Vector3(newMaxX, newMaxY, newMaxZ));
- L883 C23: new Exception :: throw new Exception("ssBVHNode constructed with invalid paramaters");


## XREngine.Data/Trees/BVH/SphereBVH.cs
- L7 C23: new SphereBVHNodeAdaptor :: : BVH<Sphere>(new SphereBVHNodeAdaptor(), [], maxSpheresPerLeaf) { }


## XREngine.Data/Trees/BVH/SphereBVHNodeAdaptor.cs
- L24 C23: new Exception :: throw new Exception("missing map for a shuffled child");


## XREngine.Data/Trees/Octree/Octree.cs
- L20 C24: new OctreeNode :: => _head = new OctreeNode<T>(bounds, 0, 0, null, this);
- L40 C21: new OctreeNode :: _head = new OctreeNode<T>(newBounds, 0, 0, null, this);
- L58 C82: new ConcurrentQueue :: internal ConcurrentQueue<(T item, ETreeCommand)> SwapCommands { get; } = new ConcurrentQueue<(T item, ETreeCommand command)>();
- L59 C283: new ConcurrentQueue :: internal ConcurrentQueue<(Segment segment, SortedDictionary<float, List<(T item, object? data)>> items, Func<T, Segment, (float? distance, object? data)> directTest, Action<SortedDictionary<float, List<(T item, object? data)>>> finishedCallback)> RaycastCommands { get; } = new ConcurrentQueue<(Segment segment, SortedDictionary<float, List<(T item, object? data)>> items, Func<T, Segment, (float? distance, object? data)> directTest, Action<SortedDictionary<float, List<(T item, object? data)>>> finishedCallback)>();
- L62 C67: new() :: private readonly Dictionary<T, int> _swapCommandIndices = new();


## XREngine.Data/Trees/Octree/OctreeArray.cs
- L36 C22: new OctreeNode :: _nodes = new OctreeNode[_maxNodes];
- L86 C17: new Vector3 :: new Vector3(
- L122 C17: new Vector3 :: new Vector3(


## XREngine.Data/Trees/Octree/OctreeNode.cs
- L19 C41: new() :: protected EventList<T> _items = new() { ThreadSafe = false };
- L20 C48: new OctreeNode :: protected OctreeNode<T>?[] _subNodes = new OctreeNode<T>[OctreeBase.MaxChildNodeCount];
- L466 C45: new OctreeNode :: return _subNodes[index] ??= new OctreeNode<T>(bounds, index, _subDivLevel + 1, this, Owner);


## XREngine.Data/Trees/Octree/OctreeNodeBase.cs
- L39 C26: new Vector3 :: 0 => new(new Vector3(Min.X, Min.Y, Min.Z), new Vector3(center.X, center.Y, center.Z)),
- L39 C60: new Vector3 :: 0 => new(new Vector3(Min.X, Min.Y, Min.Z), new Vector3(center.X, center.Y, center.Z)),
- L40 C26: new Vector3 :: 1 => new(new Vector3(Min.X, Min.Y, center.Z), new Vector3(center.X, center.Y, Max.Z)),
- L40 C63: new Vector3 :: 1 => new(new Vector3(Min.X, Min.Y, center.Z), new Vector3(center.X, center.Y, Max.Z)),
- L41 C26: new Vector3 :: 2 => new(new Vector3(Min.X, center.Y, Min.Z), new Vector3(center.X, Max.Y, center.Z)),
- L41 C63: new Vector3 :: 2 => new(new Vector3(Min.X, center.Y, Min.Z), new Vector3(center.X, Max.Y, center.Z)),
- L42 C26: new Vector3 :: 3 => new(new Vector3(Min.X, center.Y, center.Z), new Vector3(center.X, Max.Y, Max.Z)),
- L42 C66: new Vector3 :: 3 => new(new Vector3(Min.X, center.Y, center.Z), new Vector3(center.X, Max.Y, Max.Z)),
- L43 C26: new Vector3 :: 4 => new(new Vector3(center.X, Min.Y, Min.Z), new Vector3(Max.X, center.Y, center.Z)),
- L43 C63: new Vector3 :: 4 => new(new Vector3(center.X, Min.Y, Min.Z), new Vector3(Max.X, center.Y, center.Z)),
- L44 C26: new Vector3 :: 5 => new(new Vector3(center.X, Min.Y, center.Z), new Vector3(Max.X, center.Y, Max.Z)),
- L44 C66: new Vector3 :: 5 => new(new Vector3(center.X, Min.Y, center.Z), new Vector3(Max.X, center.Y, Max.Z)),
- L45 C26: new Vector3 :: 6 => new(new Vector3(center.X, center.Y, Min.Z), new Vector3(Max.X, Max.Y, center.Z)),
- L45 C66: new Vector3 :: 6 => new(new Vector3(center.X, center.Y, Min.Z), new Vector3(Max.X, Max.Y, center.Z)),
- L46 C26: new Vector3 :: 7 => new(new Vector3(center.X, center.Y, center.Z), new Vector3(Max.X, Max.Y, Max.Z)),
- L46 C69: new Vector3 :: 7 => new(new Vector3(center.X, center.Y, center.Z), new Vector3(Max.X, Max.Y, Max.Z)),


## XREngine.Data/Trees/Quadtree/Quadtree.cs
- L18 C24: new QuadtreeNode :: => _head = new QuadtreeNode<T>(bounds, 0, 0, null, this);
- L31 C59: new ConcurrentQueue :: internal ConcurrentQueue<T> AddedItems { get; } = new ConcurrentQueue<T>();
- L32 C61: new ConcurrentQueue :: internal ConcurrentQueue<T> RemovedItems { get; } = new ConcurrentQueue<T>();
- L33 C59: new ConcurrentQueue :: internal ConcurrentQueue<T> MovedItems { get; } = new ConcurrentQueue<T>();
- L78 C21: new QuadtreeNode :: _head = new QuadtreeNode<T>(_remakeRequested!.Value, 0, 0, null, this);
- L271 C24: new SortedDictionary :: var list = new SortedDictionary<int, List<T>>();


## XREngine.Data/Trees/Quadtree/Quadtree2.cs
- L47 C17: new Vector3 :: new Vector3(
- L51 C17: new Vector3 :: new Vector3(


## XREngine.Data/Trees/Quadtree/QuadtreeNode.cs
- L11 C41: new() :: protected EventList<T> _items = new() { ThreadSafe = false };
- L12 C50: new QuadtreeNode :: protected QuadtreeNode<T>?[] _subNodes = new QuadtreeNode<T>?[QuadtreeBase.MaxChildNodeCount];
- L36 C22: new BoundingRectangleF :: 0 => new BoundingRectangleF(min.X, min.Y, halfExtents.X, halfExtents.Y),
- L37 C22: new BoundingRectangleF :: 1 => new BoundingRectangleF(min.X, min.Y + halfExtents.Y, halfExtents.X, halfExtents.Y),
- L38 C22: new BoundingRectangleF :: 2 => new BoundingRectangleF(min.X + halfExtents.X, min.Y + halfExtents.Y, halfExtents.X, halfExtents.Y),
- L39 C22: new BoundingRectangleF :: 3 => new BoundingRectangleF(min.X + halfExtents.X, min.Y, halfExtents.X, halfExtents.Y),
- L629 C45: new QuadtreeNode :: return _subNodes[index] ??= new QuadtreeNode<T>(bounds, index, _subDivLevel + 1, this, Owner);


## XREngine.Data/Unity/UnityAnimationClip.cs
- L23 C42: new DeserializerBuilder :: IDeserializer deserializer = new DeserializerBuilder().WithTagMapping(new TagName("tag:unity3d.com,2011:74"), typeof(Wrapper)).Build();
- L23 C83: new TagName :: IDeserializer deserializer = new DeserializerBuilder().WithTagMapping(new TagName("tag:unity3d.com,2011:74"), typeof(Wrapper)).Build();


## XREngine.Data/Vectors/BoolVector2.cs
- L48 C23: new IndexOutOfRangeException :: throw new IndexOutOfRangeException("Cannot access vector at index " + index);
- L54 C23: new IndexOutOfRangeException :: throw new IndexOutOfRangeException("Cannot access vector at index " + index);


## XREngine.Data/Vectors/BoolVector3.cs
- L57 C23: new IndexOutOfRangeException :: throw new IndexOutOfRangeException("Cannot access vector at index " + index);
- L63 C23: new IndexOutOfRangeException :: throw new IndexOutOfRangeException("Cannot access vector at index " + index);


## XREngine.Data/Vectors/BoolVector4.cs
- L59 C23: new IndexOutOfRangeException :: throw new IndexOutOfRangeException("Cannot access vector at index " + index);
- L65 C23: new IndexOutOfRangeException :: throw new IndexOutOfRangeException("Cannot access vector at index " + index);


## XREngine.Data/Vectors/DVector2.cs
- L48 C23: new IndexOutOfRangeException :: throw new IndexOutOfRangeException("Cannot access vector at index " + index);
- L54 C23: new IndexOutOfRangeException :: throw new IndexOutOfRangeException("Cannot access vector at index " + index);


## XREngine.Data/Vectors/DVector3.cs
- L53 C23: new IndexOutOfRangeException :: throw new IndexOutOfRangeException("Cannot access vector at index " + index);
- L59 C23: new IndexOutOfRangeException :: throw new IndexOutOfRangeException("Cannot access vector at index " + index);


## XREngine.Data/Vectors/DVector4.cs
- L58 C23: new IndexOutOfRangeException :: throw new IndexOutOfRangeException("Cannot access vector at index " + index);
- L64 C23: new IndexOutOfRangeException :: throw new IndexOutOfRangeException("Cannot access vector at index " + index);


## XREngine.Data/Vectors/IVector2.cs
- L71 C23: new IndexOutOfRangeException :: throw new IndexOutOfRangeException("Cannot access vector at index " + index);
- L77 C23: new IndexOutOfRangeException :: throw new IndexOutOfRangeException("Cannot access vector at index " + index);
- L153 C12: new() :: => new()


## XREngine.Data/Vectors/IVector3.cs
- L51 C67: new IndexOutOfRangeException :: get => index is not < 0 and not > 2 ? Data[index] : throw new IndexOutOfRangeException($"Cannot access vector at index {index}");
- L58 C27: new IndexOutOfRangeException :: throw new IndexOutOfRangeException($"Cannot access vector at index {index}");


## XREngine.Data/Vectors/IVector4.cs
- L65 C23: new IndexOutOfRangeException :: throw new IndexOutOfRangeException("Cannot access vector at index " + index);
- L71 C23: new IndexOutOfRangeException :: throw new IndexOutOfRangeException("Cannot access vector at index " + index);


## XREngine.Data/Vectors/UVector2.cs
- L48 C23: new IndexOutOfRangeException :: throw new IndexOutOfRangeException("Cannot access vector at index " + index);
- L54 C23: new IndexOutOfRangeException :: throw new IndexOutOfRangeException("Cannot access vector at index " + index);


## XREngine.Data/Vectors/UVector3.cs
- L53 C23: new IndexOutOfRangeException :: throw new IndexOutOfRangeException("Cannot access vector at index " + index);
- L59 C23: new IndexOutOfRangeException :: throw new IndexOutOfRangeException("Cannot access vector at index " + index);


## XREngine.Data/Vectors/UVector4.cs
- L58 C23: new IndexOutOfRangeException :: throw new IndexOutOfRangeException("Cannot access vector at index " + index);
- L64 C23: new IndexOutOfRangeException :: throw new IndexOutOfRangeException("Cannot access vector at index " + index);


## XREngine.Data/XRSingleton.cs
- L5 C52: new() :: public class XRSingleton<T> : XRBase where T : new()
- L7 C45: new Lazy :: static XRSingleton() => _instance = new Lazy<T>(() => new T(), true);
- L7 C63: new T :: static XRSingleton() => _instance = new Lazy<T>(() => new T(), true);


## XREngine.Editor/ArchiveImport/ArchiveImportUtilities.cs
- L28 C55: new() :: public List<ArchiveEntryNode> Children { get; } = new();
- L57 C19: new ArgumentException :: throw new ArgumentException("Archive path must be provided.", nameof(archivePath));
- L60 C19: new FileNotFoundException :: throw new FileNotFoundException("Archive file not found.", archivePath);
- L79 C20: new ArchiveEntryNode :: var root = new ArchiveEntryNode("/", string.Empty, true);
- L95 C16: new ArchiveTreeResult :: return new ArchiveTreeResult(root, false, null);
- L100 C20: new ArchiveEntryNode :: var root = new ArchiveEntryNode("/", string.Empty, true);
- L101 C24: new Dictionary :: var builders = new Dictionary<string, UnityPackageEntryBuilder>(StringComparer.OrdinalIgnoreCase);
- L103 C32: new FileStream :: using var fileStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
- L104 C32: new GZipInputStream :: using var gzipStream = new GZipInputStream(fileStream);
- L105 C31: new TarInputStream :: using var tarStream = new TarInputStream(gzipStream, Encoding.Default);
- L132 C27: new UnityPackageEntryBuilder :: builder = new UnityPackageEntryBuilder();
- L138 C32: new MemoryStream :: using var ms = new MemoryStream();
- L161 C28: new Dictionary :: var unityEntries = new Dictionary<string, UnityPackageAssetRecord>(StringComparer.OrdinalIgnoreCase);
- L172 C44: new UnityPackageAssetRecord :: unityEntries[normalizedPath] = new UnityPackageAssetRecord(folder, normalizedPath, builder.HasMeta, builder.AssetSize);
- L176 C16: new ArchiveTreeResult :: return new ArchiveTreeResult(root, true, unityEntries);
- L186 C19: new ArgumentNullException :: throw new ArgumentNullException(nameof(selectedEntries));
- L191 C19: new ArgumentException :: throw new ArgumentException("Destination path must be provided.", nameof(destinationRoot));
- L196 C25: new HashSet :: var selection = new HashSet<string>(selectedEntries.Select(NormalizeKey), StringComparer.OrdinalIgnoreCase);
- L216 C26: new ArchiveExtractionProgress :: yield return new ArchiveExtractionProgress(
- L227 C26: new ArchiveExtractionProgress :: yield return new ArchiveExtractionProgress(
- L233 C22: new ArchiveExtractionProgress :: yield return new ArchiveExtractionProgress(1f, "Import complete.", null);
- L244 C19: new ArgumentNullException :: throw new ArgumentNullException(nameof(selectedEntries));
- L249 C19: new ArgumentException :: throw new ArgumentException("Destination path must be provided.", nameof(destinationRoot));
- L254 C25: new HashSet :: var selection = new HashSet<string>(selectedEntries.Select(NormalizeKey), StringComparer.OrdinalIgnoreCase);
- L265 C32: new FileStream :: using var fileStream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
- L266 C32: new GZipInputStream :: using var gzipStream = new GZipInputStream(fileStream);
- L267 C31: new TarInputStream :: using var tarStream = new TarInputStream(gzipStream, Encoding.Default);
- L299 C30: new ArchiveExtractionProgress :: yield return new ArchiveExtractionProgress(
- L310 C30: new ArchiveExtractionProgress :: yield return new ArchiveExtractionProgress(
- L334 C22: new ArchiveExtractionProgress :: yield return new ArchiveExtractionProgress(1f, "Import complete.", null);
- L374 C23: new ArchiveEntryNode :: var created = new ArchiveEntryNode(name, childPath, isDirectory);
- L415 C22: new byte :: var buffer = new byte[8192];


## XREngine.Editor/AssetEditors/RenderPipelineInspector.cs
- L30 C86: new() :: private readonly ConditionalWeakTable<RenderPipeline, EditorState> _stateCache = new();
- L48 C53: new EditorImGuiUI.InspectorTargetSet :: EditorImGuiUI.DrawDefaultAssetInspector(new EditorImGuiUI.InspectorTargetSet(pipelines, targets.CommonType), visitedObjects);
- L54 C57: new EditorState :: var state = _stateCache.GetValue(pipeline, _ => new EditorState());
- L333 C70: new Vector3 :: DrawTonemappedSwatch("##AutoExposure_AverageSwatch", new Vector3(rgba.X, rgba.Y, rgba.Z), new Vector2(160f, 160f));
- L333 C107: new Vector2 :: DrawTonemappedSwatch("##AutoExposure_AverageSwatch", new Vector3(rgba.X, rgba.Y, rgba.Z), new Vector2(160f, 160f));
- L485 C17: new Vector2 :: new Vector2(180f, 90f));
- L486 C25: new Vector2 :: ImGui.Dummy(new Vector2(0f, 6f));
- L490 C17: new Vector3 :: new Vector3(meteredLum, meteredLum, meteredLum),
- L491 C17: new Vector2 :: new Vector2(180f, 90f));
- L580 C16: new Vector3 :: return new Vector3(MathF.Max(0f, r), MathF.Max(0f, g), MathF.Max(0f, b));
- L587 C15: new Vector3 :: rgb = new Vector3(Sanitize(rgb.X), Sanitize(rgb.Y), Sanitize(rgb.Z));
- L588 C19: new Vector3 :: weights = new Vector3(Sanitize(weights.X), Sanitize(weights.Y), Sanitize(weights.Z));
- L603 C31: new Vector4 :: ImGui.ColorButton(id, new Vector4(tonemapped, 1f), ImGuiColorEditFlags.NoTooltip, size);
- L621 C26: new Vector2 :: drawList.AddText(new Vector2(pos.X + 1f, pos.Y + 1f), shadow, overlayText);
- L737 C18: new Vector3 :: mapped = new Vector3(
- L741 C16: new Vector3 :: return new Vector3(Saturate(mapped.X), Saturate(mapped.Y), Saturate(mapped.Z));
- L799 C20: new Vector4 :: rgba = new Vector4(tmp[o + 0], tmp[o + 1], tmp[o + 2], tmp[o + 3]);
- L882 C41: new Vector2 :: Vector2 uv0 = flipPreview ? new Vector2(0f, 1f) : Vector2.Zero;
- L883 C41: new Vector2 :: Vector2 uv1 = flipPreview ? new Vector2(1f, 0f) : Vector2.One;
- L886 C29: new List :: var infoParts = new List<string>
- L909 C26: new List :: var unique = new List<XRTexture>();
- L924 C16: new List :: return new List<XRTexture>();
- L1044 C16: new ImGuiChildScope :: using (new ImGuiChildScope("RenderPipelineCommandTree", treeSize))
- L1053 C16: new ImGuiChildScope :: using (new ImGuiChildScope("RenderPipelineCommandDetails", new Vector2(0f, treeSize.Y)))
- L1053 C68: new Vector2 :: using (new ImGuiChildScope("RenderPipelineCommandDetails", new Vector2(0f, treeSize.Y)))
- L1138 C20: new CommandTreeNode :: var root = new CommandTreeNode(label, path, null);
- L1139 C19: new Dictionary :: nodeMap = new Dictionary<string, CommandTreeNode>(StringComparer.Ordinal);
- L1140 C23: new HashSet :: var visited = new HashSet<ViewportRenderCommandContainer>(ReferenceComparer<ViewportRenderCommandContainer>.Instance);
- L1160 C31: new CommandTreeNode :: var commandNode = new CommandTreeNode(commandLabel, commandPath, command);
- L1249 C24: new CommandTreeNode :: var node = new CommandTreeNode(label, childPath, null);
- L1250 C20: new ChildContainerInfo :: return new ChildContainerInfo(node, container);
- L1280 C23: new StringBuilder :: var builder = new StringBuilder(input.Length * 2);
- L1297 C23: new StringBuilder :: var builder = new StringBuilder(baseSegment.Length);
- L1409 C22: new List :: var badges = new List<string>(4);
- L1475 C23: new Vector2 :: displaySize = new Vector2(64f, 64f);
- L1516 C23: new Vector2 :: displaySize = new Vector2(pixelSize.X * scale, pixelSize.Y * scale);
- L1553 C92: new TextureViewCacheKeyComparer :: private static readonly Dictionary<XRTexture, TexturePreviewState> PreviewStates = new(new TextureViewCacheKeyComparer());
- L1554 C123: new TextureViewCacheKeyComparer :: private static readonly Dictionary<XRTexture, Dictionary<(int mip, int layer), XRTextureViewBase>> PreviewViews = new(new TextureViewCacheKeyComparer());
- L1560 C21: new TexturePreviewState :: state = new TexturePreviewState();
- L1682 C21: new Dictionary :: views = new Dictionary<(int mip, int layer), XRTextureViewBase>();
- L1694 C20: new XRTexture2DView :: var view = new XRTexture2DView(texture, (uint)mip, 1u, texture.SizedInternalFormat, false, texture.MultiSample);
- L1703 C21: new Dictionary :: views = new Dictionary<(int mip, int layer), XRTextureViewBase>();
- L1717 C20: new XRTexture2DArrayView :: var view = new XRTexture2DArrayView(texture, (uint)mip, 1u, (uint)layer, 1u, texture.SizedInternalFormat, false, texture.MultiSample);
- L1729 C34: new Vector2 :: XRTexture2D tex2D => new Vector2(Shifted(tex2D.Width, mipLevel), Shifted(tex2D.Height, mipLevel)),
- L1730 C39: new Vector2 :: XRTexture2DArray array => new Vector2(Shifted(array.Width, mipLevel), Shifted(array.Height, mipLevel)),
- L1732 C39: new Vector2 :: XRTextureViewBase view => new Vector2(
- L1735 C18: new Vector2 :: _ => new Vector2(1f, 1f),
- L1830 C58: new() :: public List<CommandTreeNode> Children { get; } = new();
- L1839 C64: new() :: public static ReferenceComparer<T> Instance { get; } = new();


## XREngine.Editor/AssetEditors/TextFileInspector.cs
- L28 C80: new() :: private readonly ConditionalWeakTable<TextFile, EditorState> _stateCache = new();
- L42 C53: new EditorImGuiUI.InspectorTargetSet :: EditorImGuiUI.DrawDefaultAssetInspector(new EditorImGuiUI.InspectorTargetSet(textFiles, targets.CommonType), visitedObjects);
- L48 C57: new EditorState :: var state = _stateCache.GetValue(textFile, _ => new EditorState());
- L82 C15: new Vector4 :: ? new Vector4(0.95f, 0.45f, 0.45f, 1f)
- L83 C15: new Vector4 :: : new Vector4(0.5f, 0.8f, 0.5f, 1f);
- L92 C16: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!hasPath))
- L100 C16: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!hasPath))


## XREngine.Editor/AssetEditors/XRShaderInspector.cs
- L15 C64: new() :: private static readonly TextFileInspector _textInspector = new();
- L30 C53: new EditorImGuiUI.InspectorTargetSet :: EditorImGuiUI.DrawDefaultAssetInspector(new EditorImGuiUI.InspectorTargetSet(shaders, targets.CommonType), visitedObjects);
- L123 C38: new EditorImGuiUI.InspectorTargetSet :: _textInspector.DrawInspector(new EditorImGuiUI.InspectorTargetSet(new object[] { source }, source.GetType()), visitedObjects);
- L123 C75: new object :: _textInspector.DrawInspector(new EditorImGuiUI.InspectorTargetSet(new object[] { source }, source.GetType()), visitedObjects);


## XREngine.Editor/CodeManager.cs
- L205 C13: new XAttribute :: new XAttribute("Sdk", "Microsoft.NET.Sdk"),
- L206 C13: new XElement :: new XElement("PropertyGroup",
- L207 C17: new XElement :: new XElement("OutputType", executable ? "Exe" : "Library"),
- L208 C17: new XElement :: new XElement("TargetFramework", TargetFramework),
- L209 C17: new XElement :: new XElement("RootNamespace", rootNamespace),
- L210 C17: new XElement :: new XElement("AssemblyName", Path.GetFileNameWithoutExtension(projectFilePath)),
- L211 C17: new XElement :: new XElement("ImplicitUsings", implicitUsings ? "enable" : "disable"),
- L212 C17: new XElement :: new XElement("AllowUnsafeBlocks", allowUnsafeBlocks ? "true" : "false"),
- L213 C17: new XElement :: new XElement("PublishAot", aot ? "true" : "false"),
- L214 C17: new XElement :: new XElement("LangVersion", languageVersion), //https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/configure-language-version
- L215 C17: new XElement :: new XElement("Nullable", nullableEnable ? "enable" : "disable"),
- L216 C17: new XElement :: new XElement("Platforms", string.Join(";", platforms)),
- L217 C17: new XElement :: new XElement("PublishSingleFile", publishSingleFile ? "true" : "false"), //https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview?tabs=cli
- L218 C17: new XElement :: new XElement("SelfContained", selfContained ? "true" : "false"),
- L219 C17: new XElement :: new XElement("RuntimeIdentifier", "win-x64"),
- L220 C17: new XElement :: new XElement("BaseOutputPath", "Build")
- L227 C29: new XElement :: content.Add(new XElement("PropertyGroup",
- L228 C21: new XAttribute :: new XAttribute("Condition", $" '$(Configuration)|$(Platform)' == '{build}|{platform}' "),
- L229 C21: new XElement :: new XElement("IsTrimmable", "True"),
- L230 C21: new XElement :: new XElement("IsAotCompatible", "True"),
- L231 C21: new XElement :: new XElement("Optimize", "False"),
- L232 C21: new XElement :: new XElement("DebugType", "embedded")
- L236 C21: new XElement :: content.Add(new XElement("ItemGroup",
- L238 C33: new XElement :: .Select(file => new XElement("Compile", new XAttribute("Include", file)))
- L238 C57: new XAttribute :: .Select(file => new XElement("Compile", new XAttribute("Include", file)))
- L242 C25: new XElement :: content.Add(new XElement("ItemGroup",
- L243 C47: new XElement :: packageReferences.Select(x => new XElement("PackageReference",
- L244 C21: new XAttribute :: new XAttribute("Include", x.name),
- L245 C21: new XAttribute :: new XAttribute("Version", x.version)
- L250 C25: new XElement :: content.Add(new XElement("ItemGroup",
- L251 C50: new XElement :: includedProjectPaths.Select(x => new XElement("ProjectReference",
- L252 C21: new XAttribute :: new XAttribute("Include", x)
- L257 C25: new XElement :: content.Add(new XElement("ItemGroup",
- L258 C52: new XElement :: assemblyReferencePaths.Select(x => new XElement("Reference",
- L259 C21: new XAttribute :: new XAttribute("Include", Path.GetFileNameWithoutExtension(x)),
- L260 C21: new XElement :: new XElement("HintPath", x)
- L264 C23: new XDocument :: var project = new XDocument(new XElement("Project", content));
- L264 C37: new XElement :: var project = new XDocument(new XElement("Project", content));
- L271 C31: new StringBuilder :: var solutionContent = new StringBuilder();
- L318 C28: new StringLogger :: var stringLogger = new StringLogger(LoggerVerbosity.Diagnostic);
- L320 C33: new ProjectCollection :: var projectCollection = new ProjectCollection();
- L321 C31: new BuildParameters :: var buildParameters = new BuildParameters(projectCollection)
- L323 C24: new ConsoleLogger :: Loggers = [new ConsoleLogger(LoggerVerbosity.Detailed), stringLogger]
- L326 C45: new() :: Dictionary<string, string?> props = new()
- L431 C30: new Dictionary :: var extraProps = new Dictionary<string, string?>
- L442 C23: new InvalidOperationException :: throw new InvalidOperationException("Failed to publish launcher executable. Check build output for details.");
- L447 C23: new FileNotFoundException :: throw new FileNotFoundException("Launcher executable was not produced by publish.", launcherExePath);
- L456 C23: new InvalidOperationException :: throw new InvalidOperationException("Failed to build launcher executable. Check build output for details.");
- L462 C23: new FileNotFoundException :: throw new FileNotFoundException("Launcher executable was not produced by the build.", launcherExePath);
- L514 C33: new List :: var referenceElements = new List<XElement>();
- L517 C35: new XElement :: referenceElements.Add(new XElement("Reference",
- L518 C17: new XAttribute :: new XAttribute("Include", Path.GetFileNameWithoutExtension(assemblyPath)),
- L519 C17: new XElement :: new XElement("HintPath", assemblyPath)));
- L522 C23: new XDocument :: var project = new XDocument(
- L523 C13: new XElement :: new XElement("Project",
- L524 C17: new XAttribute :: new XAttribute("Sdk", "Microsoft.NET.Sdk"),
- L525 C17: new XElement :: new XElement("PropertyGroup",
- L526 C21: new XElement :: new XElement("OutputType", "WinExe"),
- L527 C21: new XElement :: new XElement("TargetFramework", TargetFramework),
- L528 C21: new XElement :: new XElement("ImplicitUsings", "enable"),
- L529 C21: new XElement :: new XElement("Nullable", "enable"),
- L530 C21: new XElement :: new XElement("AllowUnsafeBlocks", "true"),
- L531 C21: new XElement :: new XElement("PublishAot", "false"),
- L532 C21: new XElement :: new XElement("SelfContained", "false"),
- L533 C21: new XElement :: new XElement("Platforms", platform),
- L534 C21: new XElement :: new XElement("RuntimeIdentifier", "win-x64"),
- L535 C21: new XElement :: new XElement("AssemblyName", assemblyName),
- L536 C21: new XElement :: new XElement("RootNamespace", assemblyName.Replace('.', '_')),
- L537 C21: new XElement :: new XElement("BaseOutputPath", "Build")
- L539 C17: new XElement :: new XElement("ItemGroup",
- L540 C21: new XElement :: new XElement("Compile", new XAttribute("Include", relativeProgramPath)))
- L540 C45: new XAttribute :: new XElement("Compile", new XAttribute("Include", relativeProgramPath)))
- L547 C17: new XElement :: new XElement("ItemGroup",
- L548 C21: new XElement :: new XElement("ProjectReference", new XAttribute("Include", relativeGameProjectPath))));
- L548 C54: new XAttribute :: new XElement("ProjectReference", new XAttribute("Include", relativeGameProjectPath))));
- L552 C31: new XElement :: project.Root?.Add(new XElement("ItemGroup", referenceElements));
- L559 C28: new StringLogger :: var stringLogger = new StringLogger(LoggerVerbosity.Minimal);
- L560 C33: new ProjectCollection :: var projectCollection = new ProjectCollection();
- L561 C31: new BuildParameters :: var buildParameters = new BuildParameters(projectCollection)
- L563 C24: new ConsoleLogger :: Loggers = [new ConsoleLogger(LoggerVerbosity.Minimal), stringLogger]
- L566 C45: new() :: Dictionary<string, string?> props = new()
- L572 C23: new BuildRequestData :: var request = new BuildRequestData(projectFilePath, props, null, ["Build"], null);
- L586 C28: new StringLogger :: var stringLogger = new StringLogger(LoggerVerbosity.Minimal);
- L587 C33: new ProjectCollection :: var projectCollection = new ProjectCollection();
- L588 C31: new BuildParameters :: var buildParameters = new BuildParameters(projectCollection)
- L590 C24: new ConsoleLogger :: Loggers = [new ConsoleLogger(LoggerVerbosity.Minimal), stringLogger]
- L593 C45: new() :: Dictionary<string, string?> props = new()
- L605 C23: new BuildRequestData :: var request = new BuildRequestData(projectFilePath, props, null, targets, null);
- L645 C18: new StringBuilder :: var sb = new StringBuilder();


## XREngine.Editor/CodeManager.StringLogger.cs
- L9 C47: new() :: private readonly StringBuilder _log = new();


## XREngine.Editor/ComponentEditors/CameraComponentEditor.cs
- L82 C15: new Vector4 :: ? new Vector4(0.2f, 0.8f, 0.2f, 1.0f)  // Green for active
- L83 C15: new Vector4 :: : new Vector4(0.6f, 0.6f, 0.6f, 1.0f); // Gray for inactive
- L98 C31: new Vector4 :: ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), $"Local Player: {(int)player.LocalPlayerIndex + 1}");
- L112 C31: new Vector4 :: ImGui.TextColored(new Vector4(0.4f, 1.0f, 0.6f, 1.0f), $"Pawn: {pawnName}");
- L125 C31: new Vector4 :: ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.4f, 1.0f), $"Viewports: {viewportCount}");
- L149 C31: new Vector4 :: ImGui.TextColored(new Vector4(1.0f, 0.7f, 0.2f, 1.0f), "⚠ Camera is not rendering");
- L210 C44: new LayerMask :: component.Camera.CullingMask = new LayerMask(cullingMask);
- L409 C17: new XROVRCameraParameters :: new XROVRCameraParameters(true, previous.NearZ, previous.FarZ),
- L411 C21: new XRPerspectiveCameraParameters :: ?? new XRPerspectiveCameraParameters(previous.NearZ, previous.FarZ)
- L422 C24: new XRPerspectiveCameraParameters :: return new XRPerspectiveCameraParameters(prior.VerticalFieldOfView, prior.InheritAspectRatio ? null : prior.AspectRatio, prior.NearZ, prior.FarZ)
- L428 C20: new XRPerspectiveCameraParameters :: return new XRPerspectiveCameraParameters(previous.NearZ, previous.FarZ);
- L434 C24: new XROrthographicCameraParameters :: return new XROrthographicCameraParameters(ortho.Width, ortho.Height, ortho.NearZ, ortho.FarZ);
- L437 C20: new XROrthographicCameraParameters :: return new XROrthographicCameraParameters(MathF.Max(1.0f, frustum.X), MathF.Max(1.0f, frustum.Y), previous.NearZ, previous.FarZ);
- L462 C16: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(parameters.InheritAspectRatio))
- L715 C51: new Vector3 :: value = NormalizeLuminanceWeights(new Vector3(0.2126f, 0.7152f, 0.0722f), Engine.Rendering.Settings.DefaultLuminance);
- L722 C51: new Vector3 :: value = NormalizeLuminanceWeights(new Vector3(0.299f, 0.587f, 0.114f), Engine.Rendering.Settings.DefaultLuminance);
- L729 C51: new Vector3 :: value = NormalizeLuminanceWeights(new Vector3(1.0f, 1.0f, 1.0f), Engine.Rendering.Settings.DefaultLuminance);
- L750 C13: new Vector3 :: w = new Vector3(Sanitize(w.X), Sanitize(w.Y), Sanitize(w.Z));
- L783 C39: new Vector2 :: return (T)(object)new Vector2(v3.X, v3.Y);
- L785 C39: new Vector2 :: return (T)(object)new Vector2(v4.X, v4.Y);
- L789 C35: new Vector3 :: return (T)(object)new Vector3(v4Value.X, v4Value.Y, v4Value.Z);
- L988 C16: new Vector2 :: return new Vector2(width, height);
- L994 C16: new Vector2 :: return new Vector2(MathF.Max(1.0f, dims.X), MathF.Max(1.0f, dims.Y));


## XREngine.Editor/ComponentEditors/ComponentEditorLayout.cs
- L27 C93: new() :: private static readonly ConditionalWeakTable<XRComponent, InspectorModeState> s_modes = new();
- L37 C69: new InspectorModeState :: InspectorModeState state = s_modes.GetValue(component, _ => new InspectorModeState());
- L68 C29: new PreviewDialogState :: s_previewDialog ??= new PreviewDialogState();
- L72 C37: new Vector2 :: s_previewDialog.PixelSize = new Vector2(MathF.Max(1f, pixelSize.X), MathF.Max(1f, pixelSize.Y));
- L88 C33: new Vector2 :: ImGui.SetNextWindowSize(new Vector2(512f, 512f), ImGuiCond.Appearing);
- L94 C51: new Vector2 :: Vector2 uv0 = dialog.FlipVertically ? new Vector2(0f, 1f) : Vector2.Zero;
- L95 C51: new Vector2 :: Vector2 uv1 = dialog.FlipVertically ? new Vector2(1f, 0f) : Vector2.One;
- L125 C16: new Vector2 :: return new Vector2(width * scale, height * scale);


## XREngine.Editor/ComponentEditors/GLObjectEditorAttribute.cs
- L31 C19: new ArgumentException :: throw new ArgumentException($"Target type must be assignable to {nameof(IGLObject)}", nameof(targetType));


## XREngine.Editor/ComponentEditors/GLObjectEditorRegistry.cs
- L22 C81: new() :: private static readonly Dictionary<Type, GLObjectEditorDelegate> _editors = new();
- L24 C44: new() :: private static readonly object _lock = new();
- L232 C35: new System.Numerics.Vector4 :: ImGui.TextColored(new System.Numerics.Vector4(1.0f, 0.3f, 0.3f, 1.0f),


## XREngine.Editor/ComponentEditors/GLObjectEditors.cs
- L190 C20: new Vector4 :: return new Vector4(1.0f, 1.0f, 1.0f, 1.0f);
- L196 C20: new Vector4 :: return new Vector4(0.9f, 0.7f, 0.4f, 1.0f);
- L246 C113: new() :: List<(IFrameBufferAttachement target, EFrameBufferAttachment attachment, int index)> colorAttachments = new();
- L247 C113: new() :: List<(IFrameBufferAttachement target, EFrameBufferAttachment attachment, int index)> depthAttachments = new();
- L248 C115: new() :: List<(IFrameBufferAttachement target, EFrameBufferAttachment attachment, int index)> stencilAttachments = new();
- L387 C45: new Vector2 :: _previewDialogTextureSize = new Vector2(width, height);
- L400 C41: new Vector2 :: _previewDialogTextureSize = new Vector2(width, height);
- L414 C33: new Vector2 :: ImGui.SetNextWindowSize(new Vector2(400, 400), ImGuiCond.FirstUseEver);
- L441 C29: new Vector2 :: imageSize = new Vector2(availableSize.X, availableSize.X / aspectRatio);
- L445 C29: new Vector2 :: imageSize = new Vector2(availableSize.Y * aspectRatio, availableSize.Y);
- L1095 C20: new Vector2 :: return new Vector2(TexturePreviewFallbackEdge, TexturePreviewFallbackEdge);
- L1099 C20: new Vector2 :: return new Vector2(width, height);
- L1102 C16: new Vector2 :: return new Vector2(width * scale, height * scale);


## XREngine.Editor/ComponentEditors/GPULandscapeComponentEditor.cs
- L42 C94: new() :: private static readonly ConditionalWeakTable<LandscapeComponent, EditorState> s_states = new();
- L72 C55: new EditorState :: var state = s_states.GetValue(landscape, _ => new EditorState());
- L222 C39: new Vector2 :: ImGui.BeginChild("LayerList", new Vector2(0, 150), ImGuiChildFlags.Border);
- L233 C78: new Vector2 :: ImGui.ColorButton("##tint", tint, ImGuiColorEditFlags.NoTooltip, new Vector2(16, 16));
- L271 C28: new TerrainLayer :: var newLayer = new TerrainLayer { Name = $"Layer {layers.Count}" };
- L297 C26: new ColorF4 :: layer.Tint = new ColorF4(tint.X, tint.Y, tint.Z, 1.0f);
- L302 C40: new Vector2 :: layer.Tiling = Vector2.Max(new Vector2(0.001f), tiling);
- L357 C40: new Vector2 :: ImGui.BeginChild("ModuleList", new Vector2(0, 150), ImGuiChildFlags.Border);
- L576 C55: new Vector2 :: Vector2 center = ImGui.GetCursorScreenPos() + new Vector2(50, 50);
- L582 C83: new Vector4 :: drawList.AddCircleFilled(center, maxRadius, ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.5f, 1.0f, 0.2f)), 32);
- L583 C85: new Vector4 :: drawList.AddCircleFilled(center, innerRadius, ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.5f, 1.0f, 0.5f)), 32);
- L584 C77: new Vector4 :: drawList.AddCircle(center, maxRadius, ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.5f, 1.0f, 1.0f)), 32, 2.0f);
- L586 C21: new Vector2 :: ImGui.Dummy(new Vector2(100, 100));


## XREngine.Editor/ComponentEditors/GPUParticleEmitterComponentEditor.cs
- L28 C100: new() :: private static readonly ConditionalWeakTable<ParticleEmitterComponent, EditorState> s_states = new();
- L62 C53: new EditorState :: var state = s_states.GetValue(emitter, _ => new EditorState());
- L144 C36: new ColorF4 :: emitter.InitialColor = new ColorF4(initialColor.X, initialColor.Y, initialColor.Z, initialColor.W);
- L152 C44: new Vector3 :: emitter.ScaleMin = Vector3.Max(new Vector3(0.001f), scaleMin);
- L169 C31: new Vector3 :: emitter.Gravity = new Vector3(0, -9.81f, 0);
- L172 C31: new Vector3 :: emitter.Gravity = new Vector3(0, -1.62f, 0);
- L178 C31: new Vector3 :: emitter.Gravity = new Vector3(0, 9.81f, 0);
- L211 C35: new Data.Geometry.AABB :: emitter.LocalBounds = new Data.Geometry.AABB(boundsMin, boundsMax);
- L233 C40: new Vector2 :: ImGui.BeginChild("ModuleList", new Vector2(0, 200), ImGuiChildFlags.Border);
- L392 C33: new ColorF4 :: module.StartColor = new ColorF4(startColor.X, startColor.Y, startColor.Z, startColor.W);
- L396 C31: new ColorF4 :: module.EndColor = new ColorF4(endColor.X, endColor.Y, endColor.Z, endColor.W);
- L409 C30: new Vector2 :: drawList.AddLine(new Vector2(pos.X + i, pos.Y), new Vector2(pos.X + i, pos.Y + size.Y), color);
- L409 C61: new Vector2 :: drawList.AddLine(new Vector2(pos.X + i, pos.Y), new Vector2(pos.X + i, pos.Y + size.Y), color);
- L451 C33: new Vector2 :: module.SpeedRange = new Vector2(MathF.Max(0, speedRange.X), MathF.Max(speedRange.X, speedRange.Y));
- L463 C34: new Vector2 :: ImGui.ProgressBar(usage, new Vector2(-1, 0), $"{usage:P1}");


## XREngine.Editor/ComponentEditors/LightComponentEditorShared.cs
- L18 C109: new() :: private static readonly ConditionalWeakTable<XRTextureCube, CubemapPreviewCache> CubemapPreviewCaches = new();
- L269 C75: new CubemapPreviewCache :: var previewCache = CubemapPreviewCaches.GetValue(cubemap, cube => new CubemapPreviewCache(cube));
- L345 C29: new Vector2 :: pixelSize = new Vector2(extent, extent);
- L394 C20: new Vector2 :: return new Vector2(PreviewFallbackEdge, PreviewFallbackEdge);
- L397 C20: new Vector2 :: return new Vector2(width, height);
- L400 C16: new Vector2 :: return new Vector2(width * scale, height * scale);
- L406 C34: new Vector2 :: XRTexture2D tex2D => new Vector2(MathF.Max(1.0f, tex2D.Width), MathF.Max(1.0f, tex2D.Height)),
- L408 C17: new Vector2 :: new Vector2(MathF.Max(1.0f, cubeView.ViewedTexture.Extent), MathF.Max(1.0f, cubeView.ViewedTexture.Extent)),
- L409 C18: new Vector2 :: _ => new Vector2(MathF.Max(1.0f, texture.WidthHeightDepth.X), MathF.Max(1.0f, texture.WidthHeightDepth.Y)),
- L427 C59: new XRTextureCubeView :: private readonly XRTextureCubeView[] _faceViews = new XRTextureCubeView[6];
- L440 C24: new XRTextureCubeView :: var view = new XRTextureCubeView(


## XREngine.Editor/ComponentEditors/LightProbeComponentEditor.cs
- L86 C109: new() :: private static readonly ConditionalWeakTable<XRTextureCube, CubemapPreviewCache> CubemapPreviewCaches = new();
- L103 C46: new Vector3 :: lightProbe.ProxyBoxHalfExtents = new Vector3(
- L115 C21: new Quaternion :: var q = new Quaternion(proxyRotation.X, proxyRotation.Y, proxyRotation.Z, proxyRotation.W);
- L208 C75: new CubemapPreviewCache :: var previewCache = CubemapPreviewCaches.GetValue(cubemap, cube => new CubemapPreviewCache(cube));
- L341 C29: new Vector2 :: pixelSize = new Vector2(extent, extent);
- L390 C20: new Vector2 :: return new Vector2(PreviewFallbackEdge, PreviewFallbackEdge);
- L393 C20: new Vector2 :: return new Vector2(width, height);
- L396 C16: new Vector2 :: return new Vector2(width * scale, height * scale);
- L402 C34: new Vector2 :: XRTexture2D tex2D => new Vector2(MathF.Max(1.0f, tex2D.Width), MathF.Max(1.0f, tex2D.Height)),
- L404 C17: new Vector2 :: new Vector2(MathF.Max(1.0f, cubeView.ViewedTexture.Extent), MathF.Max(1.0f, cubeView.ViewedTexture.Extent)),
- L405 C18: new Vector2 :: _ => new Vector2(MathF.Max(1.0f, texture.WidthHeightDepth.X), MathF.Max(1.0f, texture.WidthHeightDepth.Y)),
- L423 C59: new XRTextureCubeView :: private readonly XRTextureCubeView[] _faceViews = new XRTextureCubeView[6];
- L436 C24: new XRTextureCubeView :: var view = new XRTextureCubeView(


## XREngine.Editor/ComponentEditors/ModelComponentEditor.cs
- L68 C62: new() :: public readonly HashSet<XRMesh> AttemptedBvhBuilds = new();
- L69 C100: new() :: public readonly Dictionary<RenderableMesh, RenderInfo.DelPreRenderCallback> MeshHandlers = new();
- L70 C49: new() :: private readonly object _handlersLock = new();
- L79 C100: new() :: private static readonly ConditionalWeakTable<ModelComponent, ImpostorState> s_impostorStates = new();
- L80 C104: new() :: private static readonly ConditionalWeakTable<ModelComponent, BvhPreviewState> s_bvhPreviewStates = new();
- L168 C70: new BvhPreviewState :: var state = s_bvhPreviewStates.GetValue(modelComponent, _ => new BvhPreviewState());
- L186 C43: new ColorF4 :: state.InternalNodeColor = new ColorF4(internalColor.X, internalColor.Y, internalColor.Z, internalColor.W);
- L190 C39: new ColorF4 :: state.LeafNodeColor = new ColorF4(leafColor.X, leafColor.Y, leafColor.Z, leafColor.W);
- L220 C27: new List :: var removed = new List<RenderableMesh>();
- L296 C41: new Stack :: var nodeStack = t_nodeStack ??= new Stack<BVHNode<Triangle>>();
- L338 C68: new ImpostorState :: var state = s_impostorStates.GetValue(modelComponent, _ => new ImpostorState());
- L350 C58: new Vector2 :: if (ImGui.Button("Generate Octahedral Impostor", new Vector2(-1f, 0f)))
- L352 C85: new OctahedralImposterGenerator.Settings :: state.LastResult = OctahedralImposterGenerator.Generate(modelComponent, new OctahedralImposterGenerator.Settings(state.SheetSize, 1.15f, state.CaptureDepth));
- L386 C59: new Vector2 :: if (ImGui.Button("Create Billboard Impostor", new Vector2(-1f, 0f)))
- L859 C34: new Vector2 :: XRTexture2D tex2D => new Vector2(tex2D.Width, tex2D.Height),
- L860 C18: new Vector2 :: _ => new Vector2(texture.WidthHeightDepth.X, texture.WidthHeightDepth.Y),
- L870 C20: new Vector2 :: return new Vector2(TexturePreviewFallbackEdge, TexturePreviewFallbackEdge);
- L874 C20: new Vector2 :: return new Vector2(width, height);
- L877 C16: new Vector2 :: return new Vector2(width * scale, height * scale);
- L882 C90: new() :: List<(int, SubMeshLOD, LinkedListNode<RenderableMesh.RenderableLOD>?)> entries = new();
- L1405 C26: new List :: var attributes = new List<string>();


## XREngine.Editor/ComponentEditors/RigidBodyComponentEditors.cs
- L26 C47: new() :: public readonly Stopwatch Stopwatch = new();
- L34 C110: new() :: private static readonly ConditionalWeakTable<PhysicsActorComponent, GenerationState> _generationStates = new();
- L35 C108: new() :: private static readonly ConditionalWeakTable<PhysicsActorComponent, HullPreviewState> _previewStates = new();
- L42 C64: new GenerationState :: var state = _generationStates.GetValue(component, _ => new GenerationState());
- L46 C16: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!hasModel || state.InProgress))
- L49 C37: new Vector2 :: if (ImGui.Button(label, new Vector2(-1f, 0f)) && hasModel && !state.InProgress)
- L76 C28: new Progress :: var progress = new Progress<PhysicsActorComponent.ConvexHullGenerationProgress>(p =>
- L116 C68: new HullPreviewState :: var previewState = _previewStates.GetValue(component, _ => new HullPreviewState());
- L121 C16: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!hasHulls))
- L131 C31: new Vector4 :: ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.9f, 1f), "Wireframe preview queued for rendering.");
- L206 C31: new Vector4 :: ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.9f, 1f), $"{spinner} {message}");
- L212 C44: new Vector2 :: ImGui.ProgressBar(percent, new Vector2(-1f, 0f), $"{percent * 100f:0}%");
- L275 C20: new Quaternion :: var quat = new Quaternion(value.X, value.Y, value.Z, value.W);
- L336 C31: new Vector4 :: ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.2f, 1.0f), "Rigid body not created.");
- L603 C31: new Vector4 :: ImGui.TextColored(new Vector4(1.0f, 0.8f, 0.2f, 1.0f), "Rigid body not created.");


## XREngine.Editor/ConsoleHelper.cs
- L35 C28: new StreamWriter :: Console.SetOut(new StreamWriter(standardOutput) { AutoFlush = true });
- L36 C30: new StreamWriter :: Console.SetError(new StreamWriter(standardError) { AutoFlush = true });
- L37 C27: new StreamReader :: Console.SetIn(new StreamReader(standardInput));


## XREngine.Editor/EditorFileDropHandler.cs
- L109 C31: new() :: XRTexture2D texture = new();


## XREngine.Editor/EditorFlyingCameraPawnComponent.cs
- L419 C61: new() :: private PhysxScene.PhysxQueryFilter _physxQueryFilter = new();
- L453 C128: new() :: private readonly ConcurrentQueue<SortedDictionary<float, List<(RenderInfo3D item, object? data)>>> _octreePickResultPool = new();
- L454 C129: new() :: private readonly ConcurrentQueue<SortedDictionary<float, List<(XRComponent? item, object? data)>>> _physicsPickResultPool = new();
- L499 C42: new() :: private readonly Lock _raycastLock = new();
- L654 C13: new ShaderVector4 :: new ShaderVector4(HoveredFaceFillColor, "FillColor"),
- L655 C13: new ShaderFloat :: new ShaderFloat(StippleScale, "StippleScale"),
- L656 C13: new ShaderFloat :: new ShaderFloat(StippleThickness, "StippleThickness"),
- L657 C13: new ShaderFloat :: new ShaderFloat(_stippleDepthOffset, "DepthOffset"),
- L659 C19: new XRMaterial :: var mat = new XRMaterial(vars, fragShader);
- L666 C37: new XRMeshRenderer :: _stippledTriangleRenderer = new XRMeshRenderer(_stippledTriangleMesh, mat);
- L894 C30: new ColorF4 :: var nearPlaneColor = new ColorF4(DebugFrustumNearPlaneColor.R, DebugFrustumNearPlaneColor.G, DebugFrustumNearPlaneColor.B, 0.15f);
- L896 C13: new Triangle :: new Triangle(frustum.LeftTopNear, frustum.RightTopNear, frustum.RightBottomNear),
- L899 C13: new Triangle :: new Triangle(frustum.LeftTopNear, frustum.RightBottomNear, frustum.LeftBottomNear),
- L903 C29: new ColorF4 :: var farPlaneColor = new ColorF4(DebugFrustumFarPlaneColor.R, DebugFrustumFarPlaneColor.G, DebugFrustumFarPlaneColor.B, 0.1f);
- L905 C13: new Triangle :: new Triangle(frustum.LeftTopFar, frustum.RightBottomFar, frustum.RightTopFar),
- L908 C13: new Triangle :: new Triangle(frustum.LeftTopFar, frustum.LeftBottomFar, frustum.RightBottomFar),
- L938 C27: new[] :: var baseCorners = new[]
- L1115 C80: new() :: private readonly List<(RenderInfo3D item, object? data)> _firstHitBuffer = new();
- L1354 C60: new Vector2 :: uv = Vector2.Clamp(uv, Vector2.Zero, Vector2.One - new Vector2(epsilon));
- L1383 C47: new Vector3 :: DepthHitNormalizedViewportPoint = new Vector3(p.X, p.Y, depth!.Value);
- L1509 C32: new Vector2 :: _lastRotateDelta = new Vector2(-x * MouseRotateSpeed, y * MouseRotateSpeed);
- L1543 C42: new Vector2 :: _lastMouseTranslationDelta = new Vector2(-x, -y);
- L1592 C28: new CameraFocusLerpState :: _cameraFocusLerp = new CameraFocusLerpState
- L1667 C38: new() :: Stack<TransformBase> stack = new(); stack.Push(focusTransform);


## XREngine.Editor/EditorJobTracker.cs
- L42 C44: new() :: private static readonly object _lock = new();
- L43 C73: new() :: private static readonly Dictionary<Guid, TrackedJob> _trackedJobs = new();
- L58 C27: new TrackedJob :: var tracked = new TrackedJob(job, label, payloadFormatter);
- L96 C30: new TrackedJobSnapshot :: .Select(t => new TrackedJobSnapshot(


## XREngine.Editor/EditorPlayModeController.cs
- L188 C36: new ShortcutHandlers :: _shortcutHandlers[local] = new ShortcutHandlers(playPauseHandler, stopHandler, stepFrameHandler);


## XREngine.Editor/EditorProjectInitializer.cs
- L62 C33: new GameStartupSettings :: Engine.GameSettings ??= new GameStartupSettings();


## XREngine.Editor/EditorVR.cs
- L16 C33: new ActionManifest :: settings.ActionManifest = new ActionManifest<EVRActionCategory, EVRGameAction>()
- L22 C13: new DefaultBinding :: new DefaultBinding()
- L29 C29: new VrManifest :: settings.VRManifest = new VrManifest()
- L43 C10: new() :: new()
- L47 C30: new Dictionary :: LocalizedNames = new Dictionary<string, string>
- L63 C10: new() :: new()
- L67 C30: new Dictionary :: LocalizedNames = new Dictionary<string, string>
- L83 C10: new() :: new()
- L87 C30: new Dictionary :: LocalizedNames = new Dictionary<string, string>
- L103 C10: new() :: new()
- L107 C30: new Dictionary :: LocalizedNames = new Dictionary<string, string>
- L123 C10: new() :: new()
- L127 C30: new Dictionary :: LocalizedNames = new Dictionary<string, string>
- L147 C7: new OpenVR.NET.Manifest.Action :: new OpenVR.NET.Manifest.Action<EVRActionCategory, EVRGameAction>()
- L153 C27: new Dictionary :: LocalizedNames = new Dictionary<string, string>
- L169 C7: new OpenVR.NET.Manifest.Action :: new OpenVR.NET.Manifest.Action<EVRActionCategory, EVRGameAction>()
- L175 C27: new Dictionary :: LocalizedNames = new Dictionary<string, string>
- L191 C7: new OpenVR.NET.Manifest.Action :: new OpenVR.NET.Manifest.Action<EVRActionCategory, EVRGameAction>()
- L197 C27: new Dictionary :: LocalizedNames = new Dictionary<string, string>
- L213 C7: new OpenVR.NET.Manifest.Action :: new OpenVR.NET.Manifest.Action<EVRActionCategory, EVRGameAction>()
- L219 C27: new Dictionary :: LocalizedNames = new Dictionary<string, string>
- L235 C7: new OpenVR.NET.Manifest.Action :: new OpenVR.NET.Manifest.Action<EVRActionCategory, EVRGameAction>()
- L241 C27: new Dictionary :: LocalizedNames = new Dictionary<string, string>
- L257 C7: new OpenVR.NET.Manifest.Action :: new OpenVR.NET.Manifest.Action<EVRActionCategory, EVRGameAction>()
- L263 C27: new Dictionary :: LocalizedNames = new Dictionary<string, string>
- L279 C7: new OpenVR.NET.Manifest.Action :: new OpenVR.NET.Manifest.Action<EVRActionCategory, EVRGameAction>()
- L285 C27: new Dictionary :: LocalizedNames = new Dictionary<string, string>
- L301 C7: new OpenVR.NET.Manifest.Action :: new OpenVR.NET.Manifest.Action<EVRActionCategory, EVRGameAction>()
- L307 C27: new Dictionary :: LocalizedNames = new Dictionary<string, string>
- L323 C7: new OpenVR.NET.Manifest.Action :: new OpenVR.NET.Manifest.Action<EVRActionCategory, EVRGameAction>()
- L329 C27: new Dictionary :: LocalizedNames = new Dictionary<string, string>
- L345 C7: new OpenVR.NET.Manifest.Action :: new OpenVR.NET.Manifest.Action<EVRActionCategory, EVRGameAction>()
- L351 C27: new Dictionary :: LocalizedNames = new Dictionary<string, string>
- L367 C7: new OpenVR.NET.Manifest.Action :: new OpenVR.NET.Manifest.Action<EVRActionCategory, EVRGameAction>()
- L373 C27: new Dictionary :: LocalizedNames = new Dictionary<string, string>
- L389 C7: new OpenVR.NET.Manifest.Action :: new OpenVR.NET.Manifest.Action<EVRActionCategory, EVRGameAction>()
- L395 C27: new Dictionary :: LocalizedNames = new Dictionary<string, string>
- L411 C7: new OpenVR.NET.Manifest.Action :: new OpenVR.NET.Manifest.Action<EVRActionCategory, EVRGameAction>()
- L417 C27: new Dictionary :: LocalizedNames = new Dictionary<string, string>


## XREngine.Editor/IMGUI/AssetBrowser/AssetExplorerEntryComparer.cs
- L7 C70: new() :: public static readonly AssetExplorerEntryComparer Instance = new();


## XREngine.Editor/IMGUI/EditorImGuiUI.ArchiveImport.cs
- L54 C29: new ArchiveImportDialogState :: var state = new ArchiveImportDialogState
- L128 C53: new Vector4 :: ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.9f, 0.4f, 0.4f, 1f));
- L136 C45: new Vector2 :: ImGui.BeginChild("ArchiveTree", new Vector2(640, 360), ImGuiChildFlags.Border, ImGuiWindowFlags.None);
- L153 C53: new Vector4 :: ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.9f, 0.4f, 0.4f, 1f));
- L171 C40: new Vector2 :: if (ImGui.Button("Import", new Vector2(120, 0)))
- L180 C40: new Vector2 :: if (ImGui.Button("Cancel", new Vector2(120, 0)))
- L318 C25: new Stack :: var stack = new Stack<ArchiveEntryNode>();
- L435 C34: new JobProgress :: yield return new JobProgress(update.Progress, update);


## XREngine.Editor/IMGUI/EditorImGuiUI.AssetExplorerPanel.cs
- L205 C62: new Vector2 :: if (ImGui.BeginChild($"{state.Id}DirectoryPane", new Vector2(directoryPaneWidth, 0f), ImGuiChildFlags.Border))
- L386 C58: new AssetExplorerEntry :: _assetExplorerScratchEntries.Add(new AssetExplorerEntry(name, normalized, true, 0L, modifiedUtc));
- L411 C40: new FileInfo :: var info = new FileInfo(file);
- L420 C58: new AssetExplorerEntry :: _assetExplorerScratchEntries.Add(new AssetExplorerEntry(name, normalized, false, size, modifiedUtc));
- L733 C44: new Vector2 :: Vector2 previewPos = tilePos + new Vector2(padding, padding);
- L778 C42: new Vector2 :: Vector2 labelPos = tilePos + new Vector2(padding, tileHeight - labelHeight + padding * 0.5f);
- L1135 C35: new ThirdPartyImportSelection :: inspectorTarget = new ThirdPartyImportSelection(path, descriptor.Type);
- L1355 C20: new HashSet :: keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
- L1359 C32: new FileStream :: using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
- L1360 C36: new StreamReader :: using var reader = new StreamReader(fs);
- L1361 C28: new YamlStream :: var yaml = new YamlStream();
- L1394 C32: new FileStream :: using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
- L1395 C36: new StreamReader :: using var reader = new StreamReader(fs);
- L1511 C20: new AssetTypeDescriptor :: return new AssetTypeDescriptor(resolved, displayName, category, properties, extensions, inspectorTypeName, contextMenus);
- L1523 C24: new[] :: .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
- L1568 C30: new HashSet :: var categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
- L1582 C38: new Dictionary :: var previousSelections = new Dictionary<string, bool>(_assetExplorerCategoryFilterSelections, StringComparer.OrdinalIgnoreCase);
- L1658 C25: new List :: var parts = new List<string>(3);
- L1917 C22: new SHFILEOPSTRUCT :: var op = new SHFILEOPSTRUCT
- L1926 C23: new IOException :: throw new IOException($"SHFileOperation failed (code={result}, aborted={op.fAnyOperationsAborted}) for '{path}'.");
- L2174 C66: new HashSet :: _assetExplorerYamlKeyCache[transferredKey] = new HashSet<string>(value, StringComparer.OrdinalIgnoreCase);
- L2188 C65: new HashSet :: _assetExplorerYamlKeyCache[newNormalized] = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
- L2255 C47: new AssetTypeDescriptor :: _assetTypeDescriptors.Add(new AssetTypeDescriptor(type, displayName, category, properties, extensions, inspectorTypeName, contextMenus));
- L2357 C42: new object :: handler.Invoke(null, new object[] { context });
- L2380 C27: new XRAssetContextMenuContext :: context = new XRAssetContextMenuContext(path, assetInstance);
- L2437 C25: new AssetExplorerPreviewCacheEntry :: entry = new AssetExplorerPreviewCacheEntry(path);
- L2455 C49: new XRTexture2D :: XRTexture2D seed = entry.Texture ?? new XRTexture2D();
- L2483 C27: new Vector2 :: displaySize = new Vector2(AssetExplorerPreviewFallbackEdge, AssetExplorerPreviewFallbackEdge);
- L2512 C24: new Vector2 :: return new Vector2(AssetExplorerPreviewFallbackEdge, AssetExplorerPreviewFallbackEdge);
- L2516 C24: new Vector2 :: return new Vector2(width, height);
- L2519 C20: new Vector2 :: return new Vector2(width * scale, height * scale);
- L2559 C31: new ProcessStartInfo :: Process.Start(new ProcessStartInfo("explorer.exe", arguments)


## XREngine.Editor/IMGUI/EditorImGuiUI.ConsolePanel.cs
- L15 C63: new byte :: private static readonly byte[] _consoleFilterBuffer = new byte[256];
- L16 C63: new() :: private static List<LogEntry> _consoleCachedEntries = new();
- L102 C57: new Vector2 :: if (ImGui.BeginChild("ConsoleScrollRegion", new Vector2(0, -footerHeight), ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar))
- L178 C41: new Vector4 :: ELogCategory.General => new Vector4(0.9f, 0.9f, 0.9f, 1.0f),   // White/Gray
- L179 C43: new Vector4 :: ELogCategory.Rendering => new Vector4(0.4f, 0.8f, 1.0f, 1.0f), // Light Blue
- L180 C40: new Vector4 :: ELogCategory.OpenGL => new Vector4(0.4f, 1.0f, 0.4f, 1.0f),    // Light Green
- L181 C41: new Vector4 :: ELogCategory.Physics => new Vector4(1.0f, 0.8f, 0.4f, 1.0f),   // Orange
- L182 C22: new Vector4 :: _ => new Vector4(0.9f, 0.9f, 0.9f, 1.0f),


## XREngine.Editor/IMGUI/EditorImGuiUI.HierarchyPanel.cs
- L380 C35: new Vector4 :: ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1.0f), "Scene is hidden");
- L403 C56: new Vector2 :: ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4.0f, 2.0f));
- L404 C55: new Vector2 :: ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(4.0f, 2.0f));
- L405 C55: new Vector2 :: ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(4.0f, 2.0f));
- L447 C24: new HashSet :: var assigned = new HashSet<SceneNode>();
- L477 C26: new List :: var unassigned = new List<SceneNode>();


## XREngine.Editor/IMGUI/EditorImGuiUI.Icons.cs
- L17 C74: new() :: private static readonly Dictionary<string, XRTexture2D> _iconCache = new();
- L18 C53: new() :: private static readonly object _iconCacheLock = new();
- L93 C31: new() :: using SKSvg svg = new();


## XREngine.Editor/IMGUI/EditorImGuiUI.ImGui.cs
- L38 C56: new byte :: private static readonly byte[] _renameBuffer = new byte[256];
- L40 C95: new() :: private static readonly Dictionary<Type, IXRComponentEditor?> _componentEditorCache = new();
- L41 C93: new() :: private static readonly Dictionary<Type, IXRAssetInspector?> _assetInspectorCache = new();
- L43 C95: new() :: private static readonly Dictionary<Type, IXRTransformEditor?> _transformEditorCache = new();
- L52 C98: new() :: private static readonly Dictionary<int, ProfilerThreadCacheEntry> _profilerThreadCache = new();
- L69 C83: new() :: private static readonly Dictionary<string, bool> _profilerNodeOpenCache = new();
- L94 C89: new() :: private static readonly List<AssetExplorerEntry> _assetExplorerScratchEntries = new();
- L95 C69: new byte :: private static readonly byte[] _assetExplorerRenameBuffer = new byte[256];
- L103 C82: new() :: private static readonly List<string> _assetExplorerCategoryFilterOrder = new();
- L158 C103: new() :: private static readonly List<AssetExplorerContextAction> _assetExplorerGlobalContextActions = new();
- L176 C115: new() :: private static readonly Dictionary<Type, List<CollectionTypeDescriptor>> _collectionTypeDescriptorCache = new();
- L186 C77: new() :: private static readonly ConcurrentQueue<Action> _queuedSceneEdits = new();
- L271 C45: new XRViewport :: _viewportPanelImGuiViewport ??= new XRViewport(window);
- L280 C40: new XREngine.Data.Geometry.BoundingRectangle :: renderer.SetRenderArea(new XREngine.Data.Geometry.BoundingRectangle(0, 0, fbSize2.X, fbSize2.Y));
- L281 C37: new XREngine.Data.Colors.ColorF4 :: renderer.ClearColor(new XREngine.Data.Colors.ColorF4(0f, 0f, 0f, 1f));
- L359 C23: new ArgumentException :: throw new ArgumentException("Label must be provided.", nameof(label));
- L361 C23: new ArgumentNullException :: throw new ArgumentNullException(nameof(handler));
- L363 C26: new AssetExplorerContextAction :: var action = new AssetExplorerContextAction(label, handler, predicate);
- L494 C36: new Vector2 :: ImGui.SetNextWindowPos(new Vector2(viewport.Pos.X, viewport.Pos.Y + totalReservedHeight));
- L495 C37: new Vector2 :: ImGui.SetNextWindowSize(new Vector2(viewport.Size.X, viewport.Size.Y - totalReservedHeight));
- L563 C53: new Vector2 :: Vector2 dockMax = dockMin + new Vector2(viewport.Size.X, viewport.Size.Y - totalReservedHeight);
- L658 C43: new Vector2 :: 2 => [windowName, new Vector2(-100000f, -100000f)],
- L659 C43: new Vector2 :: 3 => [windowName, new Vector2(-100000f, -100000f), ImGuiCond.Always],
- L700 C61: new Vector2 :: ImGuiDockBuilderNative.SetNodeSize(dockSpaceId, new Vector2(availableWidth, availableHeight));
- L701 C60: new Vector2 :: ImGuiDockBuilderNative.SetNodePos(dockSpaceId, new Vector2(viewport.Pos.X, viewport.Pos.Y + ImGui.GetFrameHeight() + GetToolbarReservedHeight()));
- L736 C61: new Vector2 :: ImGuiDockBuilderNative.SetNodeSize(dockSpaceId, new Vector2(availableWidth, availableHeight));
- L737 C60: new Vector2 :: ImGuiDockBuilderNative.SetNodePos(dockSpaceId, new Vector2(viewport.Pos.X, viewport.Pos.Y + ImGui.GetFrameHeight() + GetToolbarReservedHeight()));
- L922 C32: new Vector2 :: ImGui.SetCursorPos(new Vector2(centeredX, originalCursor.Y));
- L926 C36: new Vector4 :: var warningColor = new Vector4(0.96f, 0.78f, 0.32f, 1f);
- L956 C32: new Vector2 :: ImGui.SetCursorPos(new Vector2(desiredX, cursorY));
- L963 C27: new Vector2 :: var barSize = new Vector2(indicatorWidth, barHeight);
- L1006 C31: new Dictionary :: var uniqueRoots = new Dictionary<Guid, XRAsset>(dirtySnapshot.Length);
- L1025 C61: new Vector4 :: EditorJobTracker.TrackedJobState.Faulted => new Vector4(0.9f, 0.25f, 0.25f, 1f),
- L1026 C62: new Vector4 :: EditorJobTracker.TrackedJobState.Canceled => new Vector4(0.6f, 0.6f, 0.6f, 1f),
- L1070 C35: new Vector2 :: style.WindowPadding = new Vector2(14.0f, 10.0f);
- L1071 C34: new Vector2 :: style.FramePadding = new Vector2(10.0f, 6.0f);
- L1072 C33: new Vector2 :: style.ItemSpacing = new Vector2(10.0f, 8.0f);
- L1073 C38: new Vector2 :: style.ItemInnerSpacing = new Vector2(6.0f, 4.0f);
- L1091 C45: new Vector4 :: colors[(int)ImGuiCol.ChildBg] = new Vector4(darkBg.X, darkBg.Y, darkBg.Z, 0.75f);
- L1092 C45: new Vector4 :: colors[(int)ImGuiCol.PopupBg] = new Vector4(0.10f, 0.11f, 0.13f, 0.98f);
- L1093 C44: new Vector4 :: colors[(int)ImGuiCol.Border] = new Vector4(0.20f, 0.22f, 0.27f, 1.00f);
- L1094 C50: new Vector4 :: colors[(int)ImGuiCol.BorderShadow] = new Vector4(0f, 0f, 0f, 0f);
- L1100 C54: new Vector4 :: colors[(int)ImGuiCol.TitleBgCollapsed] = new Vector4(midBg.X, midBg.Y, midBg.Z, 0.60f);
- L1102 C49: new Vector4 :: colors[(int)ImGuiCol.ScrollbarBg] = new Vector4(darkBg.X, darkBg.Y, darkBg.Z, 1.00f);
- L1115 C47: new Vector4 :: colors[(int)ImGuiCol.Separator] = new Vector4(0.28f, 0.30f, 0.34f, 1.00f);
- L1124 C50: new Vector4 :: colors[(int)ImGuiCol.TabUnfocused] = new Vector4(midBg.X, midBg.Y, midBg.Z, 0.90f);
- L1126 C52: new Vector4 :: colors[(int)ImGuiCol.DockingPreview] = new Vector4(accent.X, accent.Y, accent.Z, 0.35f);
- L1127 C52: new Vector4 :: colors[(int)ImGuiCol.DockingEmptyBg] = new Vector4(0.08f, 0.08f, 0.09f, 1.00f);
- L1130 C51: new Vector4 :: colors[(int)ImGuiCol.PlotHistogram] = new Vector4(accent.X, accent.Y, accent.Z, 0.70f);
- L1133 C55: new Vector4 :: colors[(int)ImGuiCol.TableBorderStrong] = new Vector4(0.20f, 0.22f, 0.27f, 1.00f);
- L1134 C54: new Vector4 :: colors[(int)ImGuiCol.TableBorderLight] = new Vector4(0.16f, 0.18f, 0.21f, 1.00f);
- L1135 C52: new Vector4 :: colors[(int)ImGuiCol.TextSelectedBg] = new Vector4(accent.X, accent.Y, accent.Z, 0.35f);
- L1136 C52: new Vector4 :: colors[(int)ImGuiCol.DragDropTarget] = new Vector4(accent.X, accent.Y, accent.Z, 0.90f);
- L1138 C59: new Vector4 :: colors[(int)ImGuiCol.NavWindowingHighlight] = new Vector4(accent.X, accent.Y, accent.Z, 0.70f);
- L1139 C55: new Vector4 :: colors[(int)ImGuiCol.NavWindowingDimBg] = new Vector4(0.05f, 0.05f, 0.05f, 0.60f);
- L1140 C54: new Vector4 :: colors[(int)ImGuiCol.ModalWindowDimBg] = new Vector4(0.05f, 0.05f, 0.05f, 0.45f);
- L1216 C23: new ArgumentNullException :: throw new ArgumentNullException(nameof(target));


## XREngine.Editor/IMGUI/EditorImGuiUI.InspectorPanel.cs
- L29 C65: new byte :: private static readonly byte[] _componentRenameBuffer = new byte[256];
- L77 C109: new() :: private static readonly Dictionary<Type, ComponentInspectorLabels> _componentInspectorLabelsCache = new();
- L87 C26: new ComponentInspectorLabels :: var labels = new ComponentInspectorLabels(header, footer);
- L112 C37: new Vector2 :: ImGui.SetNextWindowSize(new Vector2(640f, 520f), ImGuiCond.FirstUseEver);
- L128 C57: new Vector4 :: ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.4f, 0.4f, 1.0f));
- L145 C55: new Vector2 :: if (ImGui.BeginChild("ComponentList", new Vector2(0, componentListHeight), ImGuiChildFlags.Border, ImGuiWindowFlags.HorizontalScrollbar))
- L270 C27: new HashSet :: var visited = new HashSet<object>(ReferenceEqualityComparer.Instance)
- L311 C43: new InspectorTargetSet :: if (TryDrawAssetInspector(new InspectorTargetSet(new[] { asset }, asset.GetType()), visited))
- L311 C66: new[] :: if (TryDrawAssetInspector(new InspectorTargetSet(new[] { asset }, asset.GetType()), visited))
- L315 C35: new InspectorTargetSet :: DrawInspectableObject(new InspectorTargetSet(new[] { target }, target.GetType()), "StandaloneInspectorProperties", visited);
- L315 C58: new[] :: DrawInspectableObject(new InspectorTargetSet(new[] { target }, target.GetType()), "StandaloneInspectorProperties", visited);
- L356 C35: new InspectorTargetSet :: DrawInspectableObject(new InspectorTargetSet(new[] { importOptions! }, importOptions!.GetType()), "ThirdPartyImportOptions", visited);
- L356 C58: new[] :: DrawInspectableObject(new InspectorTargetSet(new[] { importOptions! }, importOptions!.GetType()), "ThirdPartyImportOptions", visited);
- L412 C35: new InspectorTargetSet :: DrawInspectableObject(new InspectorTargetSet(new[] { importOptions! }, importOptions!.GetType()), "ThirdPartyImportOptions", visited);
- L412 C58: new[] :: DrawInspectableObject(new InspectorTargetSet(new[] { importOptions! }, importOptions!.GetType()), "ThirdPartyImportOptions", visited);
- L436 C42: new InspectorAssetContextScope :: using var inspectorContext = new InspectorAssetContextScope(assetContext?.SourceAsset);
- L452 C27: new HashSet :: var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
- L597 C55: new Vector2 :: if (ImGui.BeginChild("TransformTypeList", new Vector2(0f, 260f), ImGuiChildFlags.Border))
- L658 C20: new TransformTypeEntry :: return new TransformTypeEntry(type, label, tooltip);
- L770 C28: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(component.IsDestroyed))
- L780 C28: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(component.IsDestroyed))
- L789 C28: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(component.IsDestroyed))
- L793 C52: new List :: componentsToRemove ??= new List<XRComponent>();
- L931 C38: new InspectorTargetSet :: => DrawInspectableObject(new InspectorTargetSet(new[] { component }, component.GetType()), "ComponentProperties", visited);
- L931 C61: new[] :: => DrawInspectableObject(new InspectorTargetSet(new[] { component }, component.GetType()), "ComponentProperties", visited);
- L934 C38: new InspectorTargetSet :: => DrawInspectableObject(new InspectorTargetSet(new[] { transform }, transform.GetType()), "TransformProperties", visited);
- L934 C61: new[] :: => DrawInspectableObject(new InspectorTargetSet(new[] { transform }, transform.GetType()), "TransformProperties", visited);
- L937 C38: new InspectorTargetSet :: => DrawInspectableObject(new InspectorTargetSet(new[] { asset }, asset.GetType()), "AssetProperties", visited);
- L937 C61: new[] :: => DrawInspectableObject(new InspectorTargetSet(new[] { asset }, asset.GetType()), "AssetProperties", visited);
- L944 C27: new HashSet :: var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
- L945 C27: new InspectorTargetSet :: var targets = new InspectorTargetSet(new[] { asset }, asset.GetType());
- L945 C50: new[] :: var targets = new InspectorTargetSet(new[] { asset }, asset.GetType());
- L1195 C51: new ComponentTypeDescriptor :: _componentTypeDescriptors.Add(new ComponentTypeDescriptor(type, displayName, ns, assemblyName));


## XREngine.Editor/IMGUI/EditorImGuiUI.Mipmap2DInspector.cs
- L26 C138: new() :: private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Mipmap2D, Mipmap2DPreviewState> _mipmap2DPreviewState = new();
- L86 C36: new XRTexture2D :: state.PreviewTexture = new XRTexture2D
- L160 C37: new ImageMagick.MagickImage :: using var img = new ImageMagick.MagickImage(result.SelectedPath);


## XREngine.Editor/IMGUI/EditorImGuiUI.MissingAssetsPanel.cs
- L92 C54: new Vector2 :: if (ImGui.BeginChild("MissingAssetList", new Vector2(-1.0f, listHeight), ImGuiChildFlags.Border))
- L94 C82: new Vector2 :: if (ImGui.BeginTable("ProfilerMissingAssetTable", 6, tableFlags, new Vector2(-1.0f, -1.0f)))
- L188 C29: new Vector2 :: ImGui.Dummy(new Vector2(0.0f, spacing));
- L189 C60: new Vector2 :: if (ImGui.BeginChild("MissingAssetEditor", new Vector2(-1.0f, editorHeight), ImGuiChildFlags.Border))
- L265 C20: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!hasReplacement))


## XREngine.Editor/IMGUI/EditorImGuiUI.ModelDropSpawn.cs
- L174 C20: new SceneNode :: node = new SceneNode(parent);
- L178 C20: new SceneNode :: node = new SceneNode(world);


## XREngine.Editor/IMGUI/EditorImGuiUI.NetworkingPanel.cs
- L26 C37: new Vector2 :: ImGui.SetNextWindowSize(new Vector2(440, 520), ImGuiCond.FirstUseEver);
- L65 C47: new Vector2 :: if (ImGui.Button("Start / Apply", new Vector2(-1, 0)))
- L68 C44: new Vector2 :: if (ImGui.Button("Disconnect", new Vector2(-1, 0)))
- L164 C44: new() :: GameStartupSettings settings = new()
- L188 C40: new() :: GameStartupSettings settings = new() { NetworkingType = GameStartupSettings.ENetworkingType.Local };


## XREngine.Editor/IMGUI/EditorImGuiUI.OpenGLPanel.cs
- L16 C84: new() :: private static readonly List<OpenGLApiObjectRow> _openGlApiObjectScratch = new();
- L101 C37: new Dictionary :: var pipelineOwnership = new Dictionary<GenericRenderObject, string>(ReferenceEqualityComparer.Instance);
- L203 C30: new OpenGLApiObjectRow :: rows.Add(new OpenGLApiObjectRow(
- L332 C58: new Vector2 :: if (ImGui.BeginChild("OpenGLApiObjectsList", new Vector2(-1.0f, contentHeight.Y), ImGuiChildFlags.Border))
- L341 C91: new Vector2 :: else if (ImGui.BeginTable("ProfilerOpenGLApiObjectsTable", 4, tableFlags, new Vector2(-1.0f, -1.0f)))
- L464 C77: new Vector2 :: if (ImGui.BeginTable("ProfilerOpenGLErrorTable", 7, tableFlags, new Vector2(-1.0f, estimatedHeight)))
- L489 C61: new Vector4 :: ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.45f, 0.45f, 1.0f));
- L522 C58: new Vector2 :: if (ImGui.BeginChild("OpenGLExtensionsList", new Vector2(-1.0f, listHeight), ImGuiChildFlags.Border))
- L532 C39: new ImGuiListClipper :: var clipper = new ImGuiListClipper();
- L580 C26: new Dictionary :: var lookup = new Dictionary<string, List<OpenGLApiObjectRow>>(comparer);
- L595 C37: new List :: unownedList ??= new List<OpenGLApiObjectRow>();
- L602 C28: new List :: list = new List<OpenGLApiObjectRow>();


## XREngine.Editor/IMGUI/EditorImGuiUI.ProfilerPanel.cs
- L13 C108: new() :: private static readonly Dictionary<string, ProfilerRootMethodAggregate> _profilerRootMethodCache = new();
- L23 C82: new() :: private static readonly List<FpsDropSpikePathEntry> _fpsDropSpikePaths = new();
- L26 C72: new() :: private static readonly List<int> _fpsDropSpikeSortedIndices = new();
- L210 C25: new Vector2 :: new Vector2(-1.0f, estimatedHeight)))
- L348 C61: new Vector4 :: ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.65f, 0.65f, 0.70f, 1.0f));
- L368 C161: new Vector2 :: ImGui.PlotLines($"##ProfilerRootMethodPlot_{rootMethod.Name}", ref rootMethod.Samples[0], rootMethod.Samples.Length, 0, null, min, max, new Vector2(-1.0f, 40.0f));
- L433 C37: new ProfilerRootMethodAggregate :: aggregate = new ProfilerRootMethodAggregate { Name = rootNode.Name };
- L477 C45: new float :: aggregate.Samples = new float[maxLength];
- L550 C32: new AggregatedChildNode :: aggChild = new AggregatedChildNode { Name = child.Name };
- L625 C33: new FpsDropSpikePathEntry :: var candidate = new FpsDropSpikePathEntry(
- L723 C30: new float :: float[] window = new float[count];
- L899 C80: new() :: public Dictionary<string, AggregatedChildNode> Children { get; } = new();
- L912 C54: new() :: public HashSet<int> ThreadIds { get; } = new();
- L913 C88: new() :: public List<Engine.CodeProfiler.ProfilerNodeSnapshot> RootNodes { get; } = new();
- L916 C80: new() :: public Dictionary<string, AggregatedChildNode> Children { get; } = new();
- L934 C29: new ProfilerThreadCacheEntry :: entry = new ProfilerThreadCacheEntry { ThreadId = thread.ThreadId };


## XREngine.Editor/IMGUI/EditorImGuiUI.PropertyEditor.cs
- L42 C23: new ArgumentException :: throw new ArgumentException("Inspector target list must contain at least one object.", nameof(targets));
- L45 C20: new InspectorTargetSet :: return new InspectorTargetSet(targetList, commonType);
- L90 C78: new() :: private static readonly NullabilityInfoContext _nullabilityContext = new();
- L121 C25: new List :: var names = new List<string>();
- L158 C113: new() :: private static readonly Dictionary<Type, List<CollectionTypeDescriptor>> _propertyTypeDescriptorCache = new();
- L203 C44: new InspectorAssetContextScope :: using var assetScope = new InspectorAssetContextScope(asset.SourceAsset ?? asset);
- L234 C47: new[] :: DrawRuntimeObjectInspector(label, new[] { target }, visited, defaultOpen, description);
- L272 C34: new List :: var values = new List<object?>();
- L290 C28: new SettingPropertyDescriptor :: return new SettingPropertyDescriptor
- L326 C34: new List :: var values = new List<object?>();
- L344 C28: new SettingFieldDescriptor :: return new SettingFieldDescriptor
- L391 C27: new List :: var allRows = new List<InspectorMemberRow>(propertyInfos.Count + fieldInfos.Count);
- L392 C56: new InspectorMemberRow :: allRows.AddRange(propertyInfos.Select(p => new InspectorMemberRow(p)));
- L393 C53: new InspectorMemberRow :: allRows.AddRange(fieldInfos.Select(f => new InspectorMemberRow(f)));
- L613 C44: new InspectorAssetContextScope :: using var assetScope = new InspectorAssetContextScope(asset.SourceAsset ?? asset);
- L619 C73: new InspectorTargetSet :: handledByAssetInspector = TryDrawAssetInspector(new InspectorTargetSet(new[] { asset }, asset.GetType()), visited);
- L619 C96: new[] :: handledByAssetInspector = TryDrawAssetInspector(new InspectorTargetSet(new[] { asset }, asset.GetType()), visited);
- L628 C44: new InspectorTargetSet :: DrawSettingsProperties(new InspectorTargetSet(new[] { value }, value.GetType()), visited);
- L628 C67: new[] :: DrawSettingsProperties(new InspectorTargetSet(new[] { value }, value.GetType()), visited);
- L890 C48: new InspectorTargetSet :: DrawSettingsObject(new InspectorTargetSet(new[] { item }, item.GetType()), $"{label}[{i}]", description, visited, false, property.Name + i.ToString(CultureInfo.InvariantCulture));
- L890 C71: new[] :: DrawSettingsObject(new InspectorTargetSet(new[] { item }, item.GetType()), $"{label}[{i}]", description, visited, false, property.Name + i.ToString(CultureInfo.InvariantCulture));
- L931 C36: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(true))
- L973 C36: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(true))
- L1005 C28: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(true))
- L1038 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canMutate))
- L1132 C48: new InspectorTargetSet :: DrawSettingsObject(new InspectorTargetSet(new[] { entryValue! }, entryValue!.GetType()), childLabel, description, visited, false, property.Name + "_" + i.ToString(CultureInfo.InvariantCulture));
- L1132 C71: new[] :: DrawSettingsObject(new InspectorTargetSet(new[] { entryValue! }, entryValue!.GetType()), childLabel, description, visited, false, property.Name + "_" + i.ToString(CultureInfo.InvariantCulture));
- L1192 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canModifyElements))
- L1213 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canModifyElements || enumNames.Length == 0))
- L1231 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canModifyElements))
- L1248 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canModifyElements))
- L1265 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canModifyElements))
- L1282 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canModifyElements))
- L1311 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canModifyElements))
- L1615 C23: new CollectionBufferAdapter :: adapter = new CollectionBufferAdapter(collection, elementType);
- L1632 C66: new ArgumentNullException :: private readonly LinkedList<T> _list = list ?? throw new ArgumentNullException(nameof(list));
- L1636 C49: new ArgumentOutOfRangeException :: get => (GetNode(index) ?? throw new ArgumentOutOfRangeException(nameof(index))).Value;
- L1639 C56: new ArgumentOutOfRangeException :: var node = GetNode(index) ?? throw new ArgumentOutOfRangeException(nameof(index));
- L1681 C27: new ArgumentOutOfRangeException :: throw new ArgumentOutOfRangeException(nameof(index));
- L1689 C52: new ArgumentOutOfRangeException :: var node = GetNode(index) ?? throw new ArgumentOutOfRangeException(nameof(index));
- L1714 C52: new ArgumentOutOfRangeException :: var node = GetNode(index) ?? throw new ArgumentOutOfRangeException(nameof(index));
- L1749 C27: new List :: _buffer = new List<object?>();
- L1846 C46: new[] :: _add!.Invoke(collection, new[] { ConvertCollectionElement(entry, _addParameter ?? fallbackType) });
- L1857 C24: new CollectionAccessor :: return new CollectionAccessor(clear, add);
- L1962 C31: new List :: var descriptors = new List<CollectionTypeDescriptor>();
- L1968 C33: new CollectionTypeDescriptor :: descriptors.Add(new CollectionTypeDescriptor(
- L2003 C37: new CollectionTypeDescriptor :: descriptors.Add(new CollectionTypeDescriptor(
- L2053 C56: new Vector2 :: if (ImGui.BeginChild("##PropertyTypeList", new Vector2(0f, 280f), ImGuiChildFlags.Border))
- L2265 C58: new Vector2 :: if (ImGui.BeginChild("##CollectionTypeList", new Vector2(0f, 240f), ImGuiChildFlags.Border))
- L2315 C31: new List :: var descriptors = new List<CollectionTypeDescriptor>();
- L2341 C37: new CollectionTypeDescriptor :: descriptors.Add(new CollectionTypeDescriptor(
- L2487 C31: new object :: .Invoke(null, new object?[] { id, current, assign, allowClear, allowCreateOrReplace });
- L2526 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canModifyElements))
- L2547 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canModifyElements || enumNames.Length == 0))
- L2565 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canModifyElements))
- L2582 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canModifyElements))
- L2599 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canModifyElements))
- L2616 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canModifyElements))
- L2646 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canModifyElements))
- L2805 C34: new SettingPropertyDescriptor :: var descriptor = new SettingPropertyDescriptor
- L2866 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canWrite))
- L2900 C28: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canWrite))
- L2903 C60: new Vector2 :: ImGui.SetNextWindowSizeConstraints(new Vector2(420.0f, 0.0f), new Vector2(float.MaxValue, float.MaxValue));
- L2903 C87: new Vector2 :: ImGui.SetNextWindowSizeConstraints(new Vector2(420.0f, 0.0f), new Vector2(float.MaxValue, float.MaxValue));
- L2969 C28: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canWrite || enumNames.Length == 0))
- L2998 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canWrite))
- L3016 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canWrite))
- L3034 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canWrite))
- L3052 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canWrite))
- L3070 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canWrite))
- L3075 C39: new LayerMask :: var newMask = new LayerMask(maskValue);
- L3209 C20: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canWrite))
- L3303 C40: new InspectorTargetSet :: DrawSettingsProperties(new InspectorTargetSet(new[] { value }, value.GetType()), visited);
- L3303 C63: new[] :: DrawSettingsProperties(new InspectorTargetSet(new[] { value }, value.GetType()), visited);
- L3388 C28: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!isEmpty))
- L3501 C27: new XRPersistentCall :: calls.Add(new XRPersistentCall());
- L3547 C43: new Vector2 :: if (ImGui.Button("X", new Vector2(removeButtonSize, removeButtonSize)))
- L3578 C21: new List :: calls = new List<XRPersistentCall>();
- L3587 C25: new EventSignatureOption :: return [new EventSignatureOption(Array.Empty<Type>(), tupleExpanded: false)];
- L3590 C27: new List :: var options = new List<EventSignatureOption>
- L3592 C17: new EventSignatureOption :: new EventSignatureOption([payloadType], tupleExpanded: false)
- L3596 C29: new EventSignatureOption :: options.Add(new EventSignatureOption(tupleTypes, tupleExpanded: true));
- L3608 C41: new Vector2 :: if (ImGui.Button(nodeLabel, new Vector2(-1f, 0f)))
- L3639 C19: new List :: ? new List<EventMethodOption>()
- L3767 C27: new List :: var results = new List<EventMethodOption>();
- L3817 C38: new EventMethodOption :: yield return new EventMethodOption
- L3835 C24: new List :: var list = new List<Type>();
- L3951 C20: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!overrideCanWrite || !hasOverride))
- L3980 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canWrite))
- L4012 C28: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canWrite))
- L4015 C60: new Vector2 :: ImGui.SetNextWindowSizeConstraints(new Vector2(420.0f, 0.0f), new Vector2(float.MaxValue, float.MaxValue));
- L4015 C87: new Vector2 :: ImGui.SetNextWindowSizeConstraints(new Vector2(420.0f, 0.0f), new Vector2(float.MaxValue, float.MaxValue));
- L4079 C28: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canWrite || enumNames.Length == 0))
- L4099 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canWrite))
- L4117 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canWrite))
- L4135 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canWrite))
- L4153 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canWrite))
- L4171 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canWrite))
- L4176 C39: new LayerMask :: var newMask = new LayerMask(maskValue);
- L4194 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canWrite))
- L4199 C40: new ColorF3 :: var newColor = new ColorF3(colorVec.X, colorVec.Y, colorVec.Z);
- L4212 C110: new Vector4 :: Vector4 colorVec = currentValue is ColorF4 color ? new(color.R, color.G, color.B, color.A) : new Vector4(0f, 0f, 0f, 1f);
- L4213 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canWrite))
- L4218 C40: new ColorF4 :: var newColor = new ColorF4(colorVec.X, colorVec.Y, colorVec.Z, colorVec.W);
- L4240 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canWrite))
- L4258 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canWrite))
- L4276 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canWrite))
- L4307 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canWrite))
- L4325 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canWrite))
- L4343 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canWrite))
- L4361 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canWrite))
- L4379 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canWrite))
- L4397 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canWrite))
- L4415 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canWrite))
- L4433 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canWrite))
- L4453 C37: new List :: var previousValueList = new List<object?>(previousValues);
- L4475 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canWrite))
- L4480 C40: new ColorF3 :: var newColor = new ColorF3(colorVec.X, colorVec.Y, colorVec.Z);
- L4493 C110: new Vector4 :: Vector4 colorVec = currentValue is ColorF4 color ? new(color.R, color.G, color.B, color.A) : new Vector4(0f, 0f, 0f, 1f);
- L4494 C24: new ImGuiDisabledScope :: using (new ImGuiDisabledScope(!canWrite))
- L4499 C40: new ColorF4 :: var newColor = new ColorF4(colorVec.X, colorVec.Y, colorVec.Z, colorVec.W);
- L4518 C63: new IntPtr :: bool changed = ImGui.InputScalar(label, dataType, new IntPtr(ptr));
- L4562 C16: new InspectorAssetContextScope :: => new InspectorAssetContextScope(asset);
- L4750 C73: new() :: public static readonly ReferenceEqualityComparer Instance = new();


## XREngine.Editor/IMGUI/EditorImGuiUI.RenderPipelineGraphPanel.cs
- L26 C46: new WeakReference :: _renderPipelineGraphPinnedPipeline = new WeakReference<RenderPipeline>(pipeline);
- L35 C66: new() :: public readonly Dictionary<int, Vector2> NodePositions = new();
- L44 C66: new() :: public readonly Dictionary<int, Vector2> NodePositions = new();
- L54 C87: new() :: private static readonly ConditionalWeakTable<object, GraphNodeId> _graphNodeIds = new();
- L57 C105: new() :: private static readonly Dictionary<Guid, RenderPipelineGraphViewState> _renderPipelineGraphStates = new();
- L58 C119: new() :: private static readonly Dictionary<Guid, RenderPipelineCommandGraphViewState> _renderPipelineCommandGraphStates = new();
- L65 C33: new Vector2 :: ImGui.SetNextWindowSize(new Vector2(900, 700), ImGuiCond.FirstUseEver);
- L112 C41: new Vector2 :: passState.Pan = new Vector2(40, 40);
- L136 C23: new RenderPipelineGraphViewState :: var created = new RenderPipelineGraphViewState();
- L149 C23: new RenderPipelineCommandGraphViewState :: var created = new RenderPipelineCommandGraphViewState();
- L191 C62: new GraphNodeId :: var holder = _graphNodeIds.GetValue(obj, static _ => new GraphNodeId(Interlocked.Increment(ref _nextGraphNodeId)));
- L210 C29: new Vector2 :: state.Pan = new Vector2(40, 40);
- L254 C22: new Dictionary :: var widths = new Dictionary<int, int>();
- L255 C20: new HashSet :: var seen = new HashSet<ViewportRenderCommandContainer>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
- L292 C24: new List :: var nodeList = new List<CommandGraphNode>(256);
- L293 C24: new List :: var edgeList = new List<CommandGraphEdge>(512);
- L294 C30: new HashSet :: var seenContainers = new HashSet<ViewportRenderCommandContainer>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
- L302 C26: new CommandGraphNode :: nodeList.Add(new CommandGraphNode(containerId, label, IsContainer: true, container));
- L305 C30: new CommandGraphEdge :: edgeList.Add(new CommandGraphEdge(parentCommandNodeId.Value, containerId, edgeLabel));
- L311 C30: new CommandGraphNode :: nodeList.Add(new CommandGraphNode(cmdId, cmd.GetType().Name, IsContainer: false, cmd));
- L314 C34: new CommandGraphEdge :: edgeList.Add(new CommandGraphEdge(containerId, cmdId, null));
- L316 C34: new CommandGraphEdge :: edgeList.Add(new CommandGraphEdge(prev.Value, cmdId, null));
- L331 C17: new ReadOnlyCollection :: nodes = new ReadOnlyCollection<CommandGraphNode>(nodeList
- L335 C17: new ReadOnlyCollection :: edges = new ReadOnlyCollection<CommandGraphEdge>(edgeList);
- L382 C30: new HashSet :: var seenContainers = new HashSet<ViewportRenderCommandContainer>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
- L398 C47: new Vector2 :: view.NodePositions[containerId] = new Vector2(colStartX, y);
- L409 C45: new Vector2 :: view.NodePositions[cmdId] = new Vector2(cmdX, cmdY);
- L420 C39: new List :: var childWidths = new List<int>(children.Count);
- L470 C13: new Vector2 :: new Vector2(0, 0),
- L510 C51: new RenderPipelineGraphViewState :: DrawGrid(drawList, canvasPos, canvasSize, new RenderPipelineGraphViewState { Pan = view.Pan, Zoom = view.Zoom });
- L553 C23: new Vector2 :: ? p + new Vector2(xLocal, h * view.Zoom)
- L554 C23: new Vector2 :: : p + new Vector2(xLocal, 0);
- L558 C24: new Dictionary :: var outIndex = new Dictionary<(int From, int To), int>();
- L563 C23: new Dictionary :: var outSeen = new Dictionary<int, int>();
- L587 C30: new Vector2 :: Vector2 c1 = a + new Vector2(0, dy);
- L588 C30: new Vector2 :: Vector2 c2 = b - new Vector2(0, dy);
- L594 C40: new Vector2 :: drawList.AddText(mid + new Vector2(6f, -10f), labelColor, e.Label);
- L630 C35: new Vector2 :: Vector2 pMax = pMin + new Vector2(w, h) * view.Zoom;
- L642 C60: new Vector2 :: ImGui.InvisibleButton($"##RPCmdNode{node.Id}", new Vector2(w, h) * view.Zoom);
- L651 C37: new Vector2 :: drawList.AddText(pMin + new Vector2(textPad, 4f * view.Zoom), ImGui.GetColorU32(ImGuiCol.Text), node.Label);
- L658 C45: new Vector2 :: drawList.AddText(pMin + new Vector2(textPad, 34f * view.Zoom), ImGui.GetColorU32(ImGuiCol.TextDisabled), sub);
- L673 C13: new Vector2 :: new Vector2(0, 0),
- L745 C30: new Vector2 :: drawList.AddLine(new Vector2(canvasPos.X + x, canvasPos.Y), new Vector2(canvasPos.X + x, canvasPos.Y + canvasSize.Y), gridColor);
- L745 C73: new Vector2 :: drawList.AddLine(new Vector2(canvasPos.X + x, canvasPos.Y), new Vector2(canvasPos.X + x, canvasPos.Y + canvasSize.Y), gridColor);
- L748 C30: new Vector2 :: drawList.AddLine(new Vector2(canvasPos.X, canvasPos.Y + y), new Vector2(canvasPos.X + canvasSize.X, canvasPos.Y + y), gridColor);
- L748 C73: new Vector2 :: drawList.AddLine(new Vector2(canvasPos.X, canvasPos.Y + y), new Vector2(canvasPos.X + canvasSize.X, canvasPos.Y + y), gridColor);
- L751 C26: new Vector2 :: drawList.AddLine(new Vector2(origin.X, canvasPos.Y), new Vector2(origin.X, canvasPos.Y + canvasSize.Y), originColor, 2.0f);
- L751 C62: new Vector2 :: drawList.AddLine(new Vector2(origin.X, canvasPos.Y), new Vector2(origin.X, canvasPos.Y + canvasSize.Y), originColor, 2.0f);
- L752 C26: new Vector2 :: drawList.AddLine(new Vector2(canvasPos.X, origin.Y), new Vector2(canvasPos.X + canvasSize.X, origin.Y), originColor, 2.0f);
- L752 C62: new Vector2 :: drawList.AddLine(new Vector2(canvasPos.X, origin.Y), new Vector2(canvasPos.X + canvasSize.X, origin.Y), originColor, 2.0f);
- L770 C77: new Vector2 :: Vector2 passCenterIn = WorldToScreen(canvasPos, view, passPos + new Vector2(0, nodeH * 0.5f));
- L777 C80: new Vector2 :: Vector2 depCenterOut = WorldToScreen(canvasPos, view, depPos + new Vector2(nodeW, nodeH * 0.5f));
- L780 C45: new Vector2 :: Vector2 c1 = depCenterOut + new Vector2(dx, 0);
- L781 C45: new Vector2 :: Vector2 c2 = passCenterIn - new Vector2(dx, 0);
- L805 C35: new Vector2 :: Vector2 pMax = pMin + new Vector2(nodeW, nodeH) * view.Zoom;
- L819 C69: new Vector2 :: ImGui.InvisibleButton($"##RPGraphNode{pass.PassIndex}", new Vector2(nodeW, nodeH) * view.Zoom);
- L831 C39: new Vector2 :: Vector2 titlePos = pMin + new Vector2(textPad, 5f * view.Zoom);
- L835 C37: new Vector2 :: Vector2 subPos = pMin + new Vector2(textPad, 34f * view.Zoom);
- L840 C37: new Vector2 :: Vector2 depPos = pMin + new Vector2(textPad, 54f * view.Zoom);
- L860 C25: new Dictionary :: var memoDepth = new Dictionary<int, int>();
- L894 C54: new Vector2 :: view.NodePositions[pass.PassIndex] = new Vector2(group.Key * xStep, i * yStep);


## XREngine.Editor/IMGUI/EditorImGuiUI.SettingsPanel.cs
- L110 C31: new HashSet :: var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
- L111 C36: new InspectorTargetSet :: DrawSettingsObject(new InspectorTargetSet(new[] { settingsRoot }, settingsRoot.GetType()), headerLabel, null, visited, true);
- L111 C59: new[] :: DrawSettingsObject(new InspectorTargetSet(new[] { settingsRoot }, settingsRoot.GetType()), headerLabel, null, visited, true);


## XREngine.Editor/IMGUI/EditorImGuiUI.ShaderGraphPanel.cs
- L18 C66: new() :: public readonly Dictionary<int, Vector2> NodePositions = new();
- L23 C69: new() :: private static readonly ShaderGraphViewState _shaderGraphView = new();
- L33 C33: new Vector2 :: ImGui.SetNextWindowSize(new Vector2(1150, 780), ImGuiCond.FirstUseEver);
- L62 C36: new Vector2 :: _shaderGraphView.Pan = new Vector2(40, 40);
- L72 C98: new Vector2 :: ImGui.InputTextMultiline("##ShaderGraphSource", ref _shaderGraphSource, (uint)(1 << 18), new Vector2(-1, 170), ImGuiInputTextFlags.AllowTabInput);
- L75 C31: new Vector4 :: ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), _shaderGraphError);
- L90 C48: new Vector2 :: ImGui.BeginChild("ShaderGraphSidebar", new Vector2(sidebarWidth, -inspectorHeight - ImGui.GetStyle().ItemSpacing.Y), ImGuiChildFlags.Border | ImGuiChildFlags.ResizeX);
- L96 C53: new Vector2 :: ImGui.BeginChild("ShaderGraphCanvasRegion", new Vector2(0, -inspectorHeight - ImGui.GetStyle().ItemSpacing.Y), ImGuiChildFlags.Border);
- L102 C50: new Vector2 :: ImGui.BeginChild("ShaderGraphInspector", new Vector2(0, inspectorHeight), ImGuiChildFlags.Border);
- L108 C104: new Vector2 :: ImGui.InputTextMultiline("##ShaderGraphGenerated", ref _shaderGraphGenerated, (uint)(1 << 18), new Vector2(-1, 180), ImGuiInputTextFlags.ReadOnly | ImGuiInputTextFlags.AllowTabInput);
- L184 C49: new Vector2 :: ImGui.BeginChild("##ShaderGraphCanvas", new Vector2(0, 0), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
- L244 C35: new Vector2 :: Vector2 pMax = pMin + new Vector2(nodeW, nodeH) * _shaderGraphView.Zoom;
- L253 C66: new Vector2 :: ImGui.InvisibleButton($"##ShaderGraphNode{node.Id}", new Vector2(nodeW, nodeH) * _shaderGraphView.Zoom);
- L271 C37: new Vector2 :: drawList.AddText(pMin + new Vector2(textPad, 6f * _shaderGraphView.Zoom), ImGui.GetColorU32(ImGuiCol.Text), title);
- L276 C41: new Vector2 :: drawList.AddText(pMin + new Vector2(textPad, 38f * _shaderGraphView.Zoom), ImGui.GetColorU32(ImGuiCol.TextDisabled), inputs);
- L301 C88: new Vector2 :: Vector2 fromAnchor = WorldToScreen(canvasPos, _shaderGraphView, fromPos) + new Vector2(nodeW * 0.5f * _shaderGraphView.Zoom, nodeH * _shaderGraphView.Zoom);
- L302 C84: new Vector2 :: Vector2 toAnchor = WorldToScreen(canvasPos, _shaderGraphView, toPos) + new Vector2(nodeW * 0.5f * _shaderGraphView.Zoom, 0);
- L305 C39: new Vector2 :: Vector2 c1 = fromAnchor + new Vector2(0, dy);
- L306 C37: new Vector2 :: Vector2 c2 = toAnchor - new Vector2(0, dy);
- L391 C29: new ShaderGraphGenerator :: var generator = new ShaderGraphGenerator(_activeShaderGraph);
- L421 C47: new Vector2 :: view.NodePositions[node.Id] = new Vector2(column * columnSpacing, currentRow * rowSpacing);


## XREngine.Editor/IMGUI/EditorImGuiUI.StatePanel.cs
- L41 C40: new Vector4 :: EPlayModeState.Edit => new Vector4(0.4f, 0.7f, 1.0f, 1.0f),      // Blue for edit
- L42 C40: new Vector4 :: EPlayModeState.Play => new Vector4(0.3f, 1.0f, 0.3f, 1.0f),      // Green for play
- L43 C42: new Vector4 :: EPlayModeState.Paused => new Vector4(1.0f, 1.0f, 0.3f, 1.0f),    // Yellow for paused
- L44 C48: new Vector4 :: EPlayModeState.EnteringPlay => new Vector4(0.5f, 1.0f, 0.5f, 1.0f),
- L45 C47: new Vector4 :: EPlayModeState.ExitingPlay => new Vector4(1.0f, 0.5f, 0.5f, 1.0f),
- L46 C22: new Vector4 :: _ => new Vector4(1.0f, 1.0f, 1.0f, 1.0f)
- L68 C52: new Vector2 :: if (ImGui.Button("▶ Play", new Vector2(80, 0)))
- L73 C49: new Vector2 :: if (ImGui.Button("⏸ Pause", new Vector2(80, 0)))
- L76 C48: new Vector2 :: if (ImGui.Button("⏹ Stop", new Vector2(80, 0)))
- L81 C50: new Vector2 :: if (ImGui.Button("▶ Resume", new Vector2(80, 0)))
- L84 C48: new Vector2 :: if (ImGui.Button("⏹ Stop", new Vector2(80, 0)))
- L87 C48: new Vector2 :: if (ImGui.Button("⏭ Step", new Vector2(80, 0)))
- L100 C39: new Vector4 :: ImGui.TextColored(new Vector4(0.8f, 0.8f, 1.0f, 1.0f), gameMode.GetType().Name);
- L111 C39: new Vector4 :: ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "No active GameMode");
- L132 C39: new Vector4 :: ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "No local players initialized");
- L162 C51: new Vector4 :: ImGui.TextColored(new Vector4(0.3f, 1.0f, 0.3f, 1.0f), "Yes");
- L164 C51: new Vector4 :: ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "None");
- L207 C35: new Vector4 :: ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "<no input>");
- L231 C35: new Vector4 :: ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "<none>");
- L235 C31: new Vector4 :: ImGui.TextColored(new Vector4(0.8f, 0.8f, 1.0f, 1.0f), pawn.GetType().Name);
- L241 C35: new Vector4 :: ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), $"({pawn.SceneNode.Name})");
- L257 C39: new Vector4 :: ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "No world instances active");
- L281 C51: new Vector4 :: ImGui.TextColored(new Vector4(0.3f, 1.0f, 0.3f, 1.0f), "Yes");
- L283 C51: new Vector4 :: ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "No");
- L287 C51: new Vector4 :: ImGui.TextColored(new Vector4(0.3f, 1.0f, 0.3f, 1.0f), "On");
- L289 C51: new Vector4 :: ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "Off");
- L336 C39: new Vector4 :: ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "No windows active");
- L345 C63: new Silk.NET.Maths.Vector2D :: var size = window.Window?.Size ?? new Silk.NET.Maths.Vector2D<int>(0, 0);
- L354 C51: new Vector4 :: ImGui.TextColored(new Vector4(0.8f, 0.8f, 1.0f, 1.0f),
- L359 C51: new Vector4 :: ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "No world instance");
- L391 C63: new Vector4 :: ImGui.TextColored(new Vector4(0.3f, 1.0f, 0.3f, 1.0f),
- L396 C63: new Vector4 :: ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "<none>");
- L443 C51: new Vector4 :: ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), "No viewports");
- L539 C38: new Vector2 :: XRTexture2D tex2D => new Vector2(tex2D.Width, tex2D.Height),
- L540 C22: new Vector2 :: _ => new Vector2(texture.WidthHeightDepth.X, texture.WidthHeightDepth.Y),
- L559 C20: new Vector2 :: return new Vector2(width, height);


## XREngine.Editor/IMGUI/EditorImGuiUI.Toolbar.cs
- L32 C32: new Vector2 :: ImGui.SetNextWindowPos(new Vector2(viewport.Pos.X, viewport.Pos.Y + menuBarHeight));
- L33 C33: new Vector2 :: ImGui.SetNextWindowSize(new Vector2(viewport.Size.X, ToolbarHeight));
- L46 C57: new Vector2 :: ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(8f, 4f));
- L161 C15: new Vector4 :: ? new Vector4(0.15f, 0.55f, 0.95f, 1.0f)
- L162 C15: new Vector4 :: : new Vector4(0.4f, 0.4f, 0.4f, 1.0f);
- L244 C19: new Vector4 :: ? new Vector4(0.4f, 0.4f, 0.4f, 1.0f)
- L245 C19: new Vector4 :: : new Vector4(0.9f, 0.3f, 0.3f, 1.0f);
- L258 C19: new Vector4 :: ? new Vector4(0.5f, 0.7f, 0.9f, 1.0f)
- L259 C19: new Vector4 :: : new Vector4(0.4f, 0.4f, 0.4f, 1.0f);
- L277 C26: new Vector4 :: stateColor = new Vector4(0.8f, 0.8f, 0.2f, 1.0f);
- L282 C26: new Vector4 :: stateColor = new Vector4(0.3f, 1.0f, 0.3f, 1.0f);
- L287 C26: new Vector4 :: stateColor = new Vector4(1.0f, 0.8f, 0.2f, 1.0f);
- L336 C43: new Vector2 :: ImGui.GetWindowDrawList().AddText(new Vector2(start.X, y), color, label);
- L339 C21: new Vector2 :: ImGui.Dummy(new Vector2(size.X, ToolbarButtonSize));
- L356 C26: new Vector4 :: hoverColor = new Vector4(
- L361 C25: new Vector4 :: textColor = new Vector4(1f, 1f, 1f, 1f);
- L379 C43: new Vector2 :: clicked = ImGui.Button(label, new Vector2(ToolbarButtonSize, ToolbarButtonSize));
- L415 C43: new Vector2 :: clicked = ImGui.Button(label, new Vector2(ToolbarButtonSize, ToolbarButtonSize));
- L428 C26: new Vector2 :: var buttonSize = new Vector2(ToolbarButtonSize, ToolbarButtonSize);
- L439 C39: new Vector2 :: Vector2 iconMin = buttonMin + new Vector2(padding, padding);
- L440 C39: new Vector2 :: Vector2 iconMax = buttonMax - new Vector2(padding, padding);
- L445 C51: new Vector4 :: uint tint = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 1f));
- L457 C20: new Vector4 :: return new Vector4(0.3f, 0.3f, 0.3f, 1.0f);
- L461 C41: new Vector4 :: ETransformMode.Translate => new Vector4(0.2f, 0.6f, 0.9f, 1.0f), // Blue
- L462 C38: new Vector4 :: ETransformMode.Rotate => new Vector4(0.2f, 0.8f, 0.4f, 1.0f),    // Green
- L463 C37: new Vector4 :: ETransformMode.Scale => new Vector4(0.9f, 0.6f, 0.2f, 1.0f),     // Orange
- L464 C18: new Vector4 :: _ => new Vector4(0.5f, 0.5f, 0.5f, 1.0f)
- L474 C15: new Vector4 :: ? new Vector4(0.15f, 0.55f, 0.95f, 1.0f)  // Active blue
- L475 C15: new Vector4 :: : new Vector4(0.3f, 0.3f, 0.3f, 1.0f);    // Inactive gray


## XREngine.Editor/IMGUI/EditorImGuiUI.ViewportPanel.cs
- L170 C38: new BoundingRectangle :: _viewportPanelRenderRegion = new BoundingRectangle(x, y, w, h);


## XREngine.Editor/IMGUI/ImGuiDragDropNative.cs
- L32 C20: new ImRect :: var rect = new ImRect(min, max);


## XREngine.Editor/ImGuiAssetUtilities.cs
- L29 C85: new() :: private static readonly Dictionary<AssetPickerKey, object> _assetPickerStates = new();
- L38 C74: new() :: public static readonly AssetReferenceEqualityComparer Instance = new();
- L130 C85: new Vector2 :: if (ImGui.Selectable(preview, false, ImGuiSelectableFlags.AllowDoubleClick, new Vector2(fieldWidth, 0.0f)))
- L216 C35: new HashSet :: _inlineInspectorStack ??= new HashSet<XRAsset>(AssetReferenceEqualityComparer.Instance);
- L299 C35: new ImGuiListClipper :: var clipper = new ImGuiListClipper();
- L373 C19: new AssetPickerKey :: var key = new AssetPickerKey(typeof(TAsset), extensionKey);
- L378 C21: new AssetPickerState :: var state = new AssetPickerState<TAsset>(options.ResolveExtensions(typeof(TAsset)));
- L397 C21: new List :: var roots = new List<(string Path, bool IsEngine)>();
- L406 C25: new HashSet :: var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
- L439 C38: new AssetCandidate :: state.Candidates.Add(new AssetCandidate<TAsset>(file, isEngine, displayName, existing));
- L463 C32: new FileStream :: using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
- L464 C32: new StreamReader :: using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
- L618 C76: new() :: private static readonly Dictionary<Type, string[]> DefaultExtensions = new()
- L620 C26: new[] :: { typeof(Model), new[] { ".asset" } },
- L621 C27: new[] :: { typeof(XRMesh), new[] { ".asset", ".mesh", ".model" } },
- L622 C31: new[] :: { typeof(XRMaterial), new[] { ".asset", ".material" } }
- L751 C85: new XRTexture2D :: XRTexture2D seedTexture = (_asset as XRTexture2D) ?? _previewTexture ?? new XRTexture2D();
- L783 C62: new XRTexture2D :: XRTexture2D placeholder = _previewTexture ?? new XRTexture2D();
- L863 C32: new Vector2 :: ImGui.SetCursorPos(new Vector2(cursor.X + offsetX, cursor.Y));
- L881 C21: new Vector2 :: pixelSize = new Vector2(texture.Width, texture.Height);
- L882 C23: new Vector2 :: displaySize = new Vector2(AssetPickerPreviewFallbackEdge, AssetPickerPreviewFallbackEdge);
- L923 C20: new Vector2 :: return new Vector2(AssetPickerPreviewFallbackEdge, AssetPickerPreviewFallbackEdge);
- L927 C20: new Vector2 :: return new Vector2(width, height);
- L930 C16: new Vector2 :: return new Vector2(width * scale, height * scale);
- L995 C71: new() :: private static readonly Dictionary<Guid, CacheEntry> _cache = new();
- L1019 C36: new CacheEntry :: _cache[asset.ID] = new CacheEntry(new WeakReference<XRAsset>(asset), now + CacheDuration, count);
- L1019 C51: new WeakReference :: _cache[asset.ID] = new CacheEntry(new WeakReference<XRAsset>(asset), now + CacheDuration, count);
- L1044 C105: new() :: private static readonly ConcurrentDictionary<Type, List<Func<object, object?>>> AccessorCache = new();
- L1062 C26: new AssetReferenceWalker :: var walker = new AssetReferenceWalker(target);
- L1162 C33: new List :: var accessors = new List<Func<object, object?>>();
- L1206 C69: new() :: public static readonly ReferenceEqualityComparer Instance = new();
- L1208 C16: new bool :: public new bool Equals(object? x, object? y)


## XREngine.Editor/ImGuiEditorUtilities.cs
- L19 C35: new ArgumentNullException :: _list = list ?? throw new ArgumentNullException(nameof(list));
- L30 C24: new CollectionEditorAdapter :: return new CollectionEditorAdapter(array, elementType, null);
- L44 C20: new CollectionEditorAdapter :: return new CollectionEditorAdapter(array, elementType, ReplacementFactory);
- L148 C26: new List :: var values = new List<object?>(_list.Count);


## XREngine.Editor/ImGuiSceneNodeDragDrop.cs
- L17 C19: new ArgumentNullException :: throw new ArgumentNullException(nameof(node));


## XREngine.Editor/MeshEditingPawnComponent.cs
- L258 C60: new InvalidOperationException :: => _mesh?.GenerateAccelerationStructure() ?? throw new InvalidOperationException("No mesh is assigned to the MeshEditingPawnComponent.");
- L261 C35: new InvalidOperationException :: => _mesh?.Bake() ?? throw new InvalidOperationException("No mesh is assigned to the MeshEditingPawnComponent.");


## XREngine.Editor/Program.cs
- L134 C21: new XRScene :: var scene = new XRScene("Main Scene");
- L135 C24: new SceneNode :: var rootNode = new SceneNode("Root Node");
- L148 C81: new Vector3 :: UnitTestingWorld.Lighting.AddLightProbes(rootNode, 1, 1, 1, 10, 10, 10, new Vector3(0.0f, 50.0f, 0.0f));
- L153 C21: new XRWorld :: var world = new XRWorld("Default World", scene);
- L174 C27: new Vector3 :: debug.AddLine(new Vector3(x, y, -extent), new Vector3(x, y, extent), color);
- L174 C55: new Vector3 :: debug.AddLine(new Vector3(x, y, -extent), new Vector3(x, y, extent), color);
- L183 C27: new Vector3 :: debug.AddLine(new Vector3(-extent, y, z), new Vector3(extent, y, z), color);
- L183 C55: new Vector3 :: debug.AddLine(new Vector3(-extent, y, z), new Vector3(extent, y, z), color);
- L206 C68: new() :: private static JsonSerializerSettings DefaultJsonSettings() => new()
- L214 C23: new StringEnumConverter :: Converters = [new StringEnumConverter()]
- L229 C113: new UnitTestingWorld.Settings :: UnitTestingWorld.Toggles = JsonConvert.DeserializeObject<UnitTestingWorld.Settings>(content) ?? new UnitTestingWorld.Settings();
- L253 C24: new VRGameStartupSettings :: var settings = new VRGameStartupSettings<EVRActionCategory, EVRGameAction>()
- L258 C17: new() :: new()
- L270 C35: new UserSettings :: DefaultUserSettings = new UserSettings()


## XREngine.Editor/ProjectBuilder.cs
- L33 C49: new() :: private static readonly object _buildLock = new();
- L79 C22: new JobProgress :: yield return new JobProgress(0f, "Starting build...");
- L88 C26: new JobProgress :: yield return new JobProgress(1f, "Nothing to build.");
- L98 C26: new JobProgress :: yield return new JobProgress(progress, step.Description);
- L101 C22: new JobProgress :: yield return new JobProgress(1f, "Build completed");
- L105 C43: new InvalidOperationException :: => Engine.CurrentProject ?? throw new InvalidOperationException("No project is currently loaded.");
- L109 C47: new BuildSettings :: var current = Engine.BuildSettings ?? new BuildSettings();
- L111 C78: new BuildSettings :: return AssetManager.Deserializer.Deserialize<BuildSettings>(yaml) ?? new BuildSettings();
- L129 C16: new BuildContext :: return new BuildContext(
- L150 C19: new InvalidOperationException :: throw new InvalidOperationException("PublishLauncherAsNativeAot requires BuildLauncherExecutable to be enabled.");
- L158 C23: new BuildStep :: steps.Add(new BuildStep("Saving project settings", Engine.SaveProjectSettings));
- L161 C19: new BuildStep :: steps.Add(new BuildStep("Preparing output directories", () => PrepareOutputDirectories(context, settings.CleanOutputDirectory)));
- L165 C23: new BuildStep :: steps.Add(new BuildStep("Cooking content", () => CookContent(context)));
- L171 C23: new BuildStep :: steps.Add(new BuildStep("Generating config archive", () => GenerateConfigArchive(context)));
- L176 C23: new BuildStep :: steps.Add(new BuildStep("Compiling managed assemblies", () => BuildManagedAssemblies(configuration, platform)));
- L181 C23: new BuildStep :: steps.Add(new BuildStep("Copying game assemblies", () => CopyGameAssemblies(context, configuration, platform, settings.IncludePdbFiles)));
- L186 C23: new BuildStep :: steps.Add(new BuildStep("Copying engine binaries", () => CopyEngineBinaries(context, settings.IncludePdbFiles)));
- L191 C23: new BuildStep :: steps.Add(new BuildStep("Building launcher executable", () => BuildLauncherExecutable(context, settings, configuration, platform)));
- L206 C19: new IOException :: throw new IOException($"Failed to clean build output at '{context.BuildRoot}'.", ex);
- L222 C19: new DirectoryNotFoundException :: throw new DirectoryNotFoundException($"Assets directory not found at '{context.AssetsDirectory}'.");
- L240 C49: new GameStartupSettings :: WriteCookedAsset(Engine.GameSettings ?? new GameStartupSettings(), Path.Combine(staging, StartupAssetName));
- L245 C49: new UserSettings :: WriteCookedAsset(Engine.UserSettings ?? new UserSettings(), Path.Combine(staging, XRProject.UserSettingsFileName));
- L258 C19: new InvalidOperationException :: throw new InvalidOperationException("Asset system unavailable; cannot build assemblies.");
- L263 C19: new InvalidOperationException :: throw new InvalidOperationException("Managed build failed. See log for details.");
- L271 C19: new DirectoryNotFoundException :: throw new DirectoryNotFoundException($"Managed build output not found for {configuration}|{platform}.");
- L313 C19: new FileNotFoundException :: throw new FileNotFoundException("Config archive not found. Enable config generation before building the launcher.", context.ConfigArchivePath);
- L386 C19: new ArgumentException :: throw new ArgumentException("Assets directory must be provided.", nameof(assetsDirectory));
- L388 C19: new DirectoryNotFoundException :: throw new DirectoryNotFoundException($"Assets directory not found at '{assetsDirectory}'.");
- L391 C19: new ArgumentException :: throw new ArgumentException("Intermediate directory must be provided.", nameof(intermediateDirectory));
- L397 C23: new XRProject :: var project = new XRProject("UnitTestProject")
- L403 C23: new BuildContext :: var context = new BuildContext(
- L405 C13: new BuildSettings :: new BuildSettings(),
- L424 C19: new InvalidOperationException :: throw new InvalidOperationException($"Asset '{sourcePath}' is missing an __assetType hint and cannot be cooked.");
- L427 C22: new InvalidOperationException :: ?? throw new InvalidOperationException($"Unable to resolve asset type '{typeHint}' referenced by '{sourcePath}'.");
- L430 C22: new InvalidOperationException :: ?? throw new InvalidOperationException($"Failed to deserialize asset '{sourcePath}' as '{assetType.FullName}'.");
- L448 C16: new CookedAssetBlob :: return new CookedAssetBlob(typeName, CookedAssetFormat.BinaryV1, payload);
- L454 C28: new StringReader :: using var reader = new StringReader(yaml);
- L480 C28: new StringReader :: using var reader = new StringReader(yaml);
- L515 C19: new InvalidOperationException :: throw new InvalidOperationException($"Project {friendlyName} directory is not configured.");


## XREngine.Editor/UI/ConsolePanel.cs
- L7 C55: new TraceListener :: public TraceListener TraceListener { get; } = new TraceListener();


## XREngine.Editor/UI/EditorDragDropUtility.cs
- L82 C19: new ArgumentNullException :: throw new ArgumentNullException(nameof(source));
- L90 C26: new DragSession :: _activeSession = new DragSession(source, payload, startCanvasPosition);
- L102 C19: new ArgumentNullException :: throw new ArgumentNullException(nameof(transform));
- L104 C19: new ArgumentNullException :: throw new ArgumentNullException(nameof(onDrop));
- L106 C28: new DropTargetRegistration :: var registration = new DropTargetRegistration(transform, onDrop, canAccept, hoverChanged);
- L108 C16: new DropTargetHandle :: return new DropTargetHandle(registration);
- L207 C38: new ArgumentNullException :: TypeId = typeId ?? throw new ArgumentNullException(nameof(typeId));
- L208 C34: new ArgumentNullException :: Data = data ?? throw new ArgumentNullException(nameof(data));
- L230 C29: new WeakReference :: _transformRef = new WeakReference<UIBoundableTransform>(transform);


## XREngine.Editor/UI/ImGuiFileBrowser.cs
- L61 C49: new byte :: public byte[] FileNameBuffer { get; } = new byte[256];
- L62 C45: new byte :: public byte[] PathBuffer { get; } = new byte[512];
- L68 C50: new byte :: public byte[] NewFolderBuffer { get; } = new byte[256];
- L69 C53: new() :: public Stack<string> BackHistory { get; } = new();
- L70 C56: new() :: public Stack<string> ForwardHistory { get; } = new();
- L95 C78: new() :: private static readonly Dictionary<string, DialogState> _activeDialogs = new();
- L96 C49: new() :: private static readonly object _stateLock = new();
- L114 C21: new DialogState :: var state = new DialogState
- L206 C28: new GameWindowStartupSettings :: var settings = new GameWindowStartupSettings
- L258 C21: new XRScene :: var scene = new XRScene($"FileBrowserScene_{state.Id}");
- L259 C29: new SceneNode :: scene.RootNodes.Add(new SceneNode("FileBrowserRoot"));
- L260 C16: new XRWorld :: return new XRWorld($"FileBrowserWorld_{state.Id}", scene);
- L343 C57: new Vector2 :: ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10f, 10f));
- L375 C47: new Vector4 :: ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.2f, 0.2f, 1f));
- L376 C54: new Vector4 :: ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.8f, 0.3f, 0.3f, 1f));
- L377 C53: new Vector4 :: ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.9f, 0.1f, 0.1f, 1f));
- L378 C31: new Vector2 :: if (ImGui.Button("X", new Vector2(closeButtonWidth, 0)))
- L395 C26: new Vector2 :: var dialogSize = new Vector2(800, 550);
- L423 C41: new Vector2 :: if (ImGui.BeginChild("Sidebar", new Vector2(sidebarWidth, contentHeight), ImGuiChildFlags.Border))
- L431 C42: new Vector2 :: if (ImGui.BeginChild("FileList", new Vector2(-1, contentHeight), ImGuiChildFlags.Border))
- L439 C31: new Vector4 :: ImGui.TextColored(new Vector4(1f, 0.3f, 0.3f, 1f), state.ErrorMessage);
- L461 C37: new Vector2 :: if (ImGui.Button("<##Back", new Vector2(buttonSize, 0)))
- L472 C40: new Vector2 :: if (ImGui.Button(">##Forward", new Vector2(buttonSize, 0)))
- L483 C35: new Vector2 :: if (ImGui.Button("^##Up", new Vector2(buttonSize, 0)))
- L506 C37: new Vector2 :: if (ImGui.Button("Refresh", new Vector2(refreshWidth, 0)))
- L513 C40: new Vector2 :: if (ImGui.Button("New Folder", new Vector2(newFolderWidth, 0)))
- L532 C40: new Vector2 :: if (ImGui.Button("Create", new Vector2(80f, 0)))
- L537 C51: new Vector2 :: if (ImGui.Button("Cancel##NewFolder", new Vector2(80f, 0)))
- L671 C35: new Vector2 :: if (ImGui.Button(confirm, new Vector2(buttonWidth, 0)))
- L678 C36: new Vector2 :: if (ImGui.Button("Cancel", new Vector2(buttonWidth, 0)))
- L742 C40: new[] :: var paths = path is not null ? new[] { path } : null;
- L804 C22: new DialogResult :: var result = new DialogResult
- L928 C27: new Vector4 :: ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "Quick Access");
- L936 C27: new Vector4 :: ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "Drives");
- L1036 C27: new DirectoryInfo :: var dirInfo = new DirectoryInfo(state.CurrentDirectory);
- L1042 C39: new FileSystemEntry :: state.Entries.Add(new FileSystemEntry
- L1062 C43: new FileSystemEntry :: state.Entries.Add(new FileSystemEntry
- L1143 C22: new List :: var result = new List<(string, string[])>();


## XREngine.Editor/UI/Panels/EditorPanel.cs
- L35 C13: new ShaderVector4 :: new ShaderVector4(new ColorF4(166/255.0f, 179/255.0f, 178/255.0f, 1.0f), "MatColor"),
- L35 C31: new ColorF4 :: new ShaderVector4(new ColorF4(166/255.0f, 179/255.0f, 178/255.0f, 1.0f), "MatColor"),
- L36 C13: new ShaderFloat :: new ShaderFloat(10.0f, "BlurStrength"),
- L37 C13: new ShaderInt :: new ShaderInt(15, "SampleCount"),
- L40 C21: new XRMaterial :: var bgMat = new XRMaterial(parameters, [grabTex], bgShader)
- L48 C68: new() :: private static RenderingParameters RenderParameters { get; } = new()
- L51 C21: new() :: DepthTest = new()


## XREngine.Editor/UI/Panels/HierarchyPanel.cs
- L27 C88: new() :: private readonly Dictionary<UIInteractableComponent, Vector2> _pendingDragStarts = new();
- L107 C27: new Vector4 :: listTfm.Padding = new Vector4(0.0f);
- L142 C35: new Vector2 :: buttonTfm.MaxAnchor = new Vector2(1.0f, 1.0f);
- L143 C33: new Vector4 :: buttonTfm.Margins = new Vector4(0.0f);
- L149 C31: new Vector4 :: textTfm.Margins = new Vector4(10.0f, Margin, 10.0f, Margin);
- L153 C35: new Vector2 :: textTfm.Translation = new Vector2(node.Transform.Depth * DepthIncrement, 0.0f);
- L155 C33: new Vector2 :: textTfm.MaxAnchor = new Vector2(0.0f, 1.0f);
- L160 C36: new Vector2 :: previewTfm.MinAnchor = new Vector2(0.0f, 0.0f);
- L161 C36: new Vector2 :: previewTfm.MaxAnchor = new Vector2(1.0f, 1.0f);
- L162 C34: new Vector4 :: previewTfm.Margins = new Vector4(10.0f, Margin, 10.0f, Margin);
- L166 C33: new ColorF4 :: dropPreview.Color = new ColorF4(1.0f, 1.0f, 1.0f, 0.75f);
- L199 C73: new Vector2 :: Vector2 canvasPoint = comp.BoundableTransform.LocalToCanvas(new Vector2(x, y));
- L289 C29: new ColorF4 :: preview.Color = new ColorF4(1.0f, 1.0f, 1.0f, 0.45f);
- L300 C25: new ColorF4 :: preview.Color = new ColorF4(1.0f, 1.0f, 1.0f, 0.75f);


## XREngine.Editor/UI/Panels/Inspector/Editors/DataTransformers/InspectorPropertyEditors.LayerMaskTransformer.cs
- L43 C27: new LayerMask :: var newMask = new LayerMask(Value ?? 0);


## XREngine.Editor/UI/Panels/Inspector/Editors/DataTransformers/InspectorPropertyEditors.Vector2Transformer.cs
- L38 C29: new Vector2 :: var newVector = new Vector2(


## XREngine.Editor/UI/Panels/Inspector/Editors/DataTransformers/InspectorPropertyEditors.Vector3Transformer.cs
- L45 C29: new Vector3 :: var newVector = new Vector3(


## XREngine.Editor/UI/Panels/Inspector/Editors/DataTransformers/InspectorPropertyEditors.Vector4Transformer.cs
- L52 C29: new Vector4 :: var newVector = new Vector4(


## XREngine.Editor/UI/Panels/Inspector/Editors/InspectorPropertyEditors.CollectionTypes.cs
- L25 C97: new() :: private static readonly Dictionary<Type, CollectionAccessor?> CollectionAccessorCache = new();
- L40 C31: new CollectionEditorContext :: var context = new CollectionEditorContext(prop, objects, propType, container);
- L182 C52: new Vector4 :: label.BoundableTransform.Margins = new Vector4(4.0f, 2.0f, 4.0f, 2.0f);
- L198 C41: new() :: List<object> bindings = new();
- L418 C52: new Vector4 :: label.BoundableTransform.Margins = new Vector4(4.0f, 2.0f, 4.0f, 2.0f);
- L434 C41: new() :: List<object> bindings = new();
- L578 C52: new Vector4 :: label.BoundableTransform.Margins = new Vector4(4.0f, 2.0f, 4.0f, 2.0f);
- L594 C41: new() :: List<object> bindings = new();
- L621 C65: new[] :: var addLast = listType.GetMethod("AddLast", new[] { elementType });
- L627 C52: new[] :: addLast.Invoke(collection, new[] { ConvertValue(newValue, elementType) });
- L654 C69: new[] :: var removeMethod = listType.GetMethod("Remove", new[] { node.GetType() })
- L655 C57: new[] :: ?? listType.GetMethod("Remove", new[] { elementType });
- L660 C61: new[] :: removeMethod.Invoke(collection, new[] { node });
- L662 C61: new[] :: removeMethod.Invoke(collection, new[] { node.GetType().GetProperty("Value")?.GetValue(node) });
- L743 C52: new Vector4 :: label.BoundableTransform.Margins = new Vector4(4.0f, 2.0f, 4.0f, 2.0f);
- L759 C115: new[] :: BuildValueEditor(valueNode, elementType, bindingProperty, proxy is null ? Array.Empty<object>() : new[] { proxy });
- L822 C46: new[] :: _add!.Invoke(collection, new[] { ConvertValue(entry, _addParameter ?? fallbackType) });
- L833 C24: new CollectionAccessor :: return new CollectionAccessor(clear, add);
- L891 C35: new() :: List<object> owners = new();
- L903 C36: new() :: List<object?> buffer = new();
- L1028 C27: new Vector4 :: tfm.Margins = new Vector4(2.0f);


## XREngine.Editor/UI/Panels/Inspector/Editors/InspectorPropertyEditors.cs
- L70 C13: new ShaderFloat :: new ShaderFloat(2.0f, OutlineWidthUniformName),
- L71 C13: new ShaderVector4 :: new ShaderVector4(ColorF4.Transparent, OutlineColorUniformName),
- L72 C13: new ShaderVector4 :: new ShaderVector4(ColorF4.Transparent, FillColorUniformName),
- L74 C19: new XRMaterial :: var mat = new XRMaterial(parameters, frag);
- L98 C47: new Vector4 :: textComp.BoundableTransform.Margins = new Vector4(5.0f, 2.0f, 5.0f, 2.0f);
- L124 C47: new Vector4 :: textComp.BoundableTransform.Margins = new Vector4(5.0f, 2.0f, 5.0f, 2.0f);


## XREngine.Editor/UI/Panels/Inspector/Editors/InspectorPropertyEditors.Custom.cs
- L35 C27: new HashSet :: var visited = new HashSet<XRAsset>(ReferenceEqualityComparer.Instance);
- L63 C27: new HashSet :: var visited = new HashSet<OpenGLRenderer.GLObjectBase>(ReferenceEqualityComparer.Instance);
- L100 C114: new List :: var embedded = asset.EmbeddedAssets?.Where(x => x is not null && !ReferenceEquals(x, asset)).ToList() ?? new List<XRAsset>();
- L113 C32: new Vector4 :: embeddedList.Padding = new Vector4(18.0f, 0.0f, 4.0f, 2.0f);
- L116 C86: new Vector4 :: AddInfoLabel(embeddedListNode, $"• {GetAssetDescriptor(embeddedAsset)}", new Vector4(2.0f, 0.0f, 2.0f, 0.0f));
- L167 C39: new Vector4 :: attachmentsList.Padding = new Vector4(18.0f, 0.0f, 4.0f, 2.0f);
- L170 C92: new Vector4 :: AddInfoLabel(attachmentsNode, $"• {DescribeAttachmentTarget(attachment)}", new Vector4(2.0f, 0.0f, 2.0f, 0.0f));
- L195 C36: new Vector4 :: previewTransform.Margins = new Vector4(6.0f, 4.0f, 6.0f, 8.0f);
- L340 C26: new() :: List<T> values = new();
- L375 C32: new Vector4 :: btnTransform.Margins = new Vector4(4.0f, 2.0f, 4.0f, 2.0f);
- L383 C44: new Vector4 :: label.BoundableTransform.Margins = new Vector4(6.0f, 0.0f, 6.0f, 0.0f);
- L404 C24: new Vector4 :: list.Padding = new Vector4(0.0f);
- L416 C30: new Vector4 :: cardLayout.Padding = new Vector4(6.0f, 4.0f, 6.0f, 4.0f);
- L417 C30: new Vector4 :: cardLayout.Margins = new Vector4(0.0f, 4.0f, 0.0f, 4.0f);
- L423 C50: new Vector4 :: var header = AddInfoLabel(parent, title, new Vector4(4.0f, 4.0f, 4.0f, 2.0f), EditorUI.Styles.PropertyNameTextColor);
- L436 C55: new Vector4 :: label.BoundableTransform.Margins = margins ?? new Vector4(6.0f, 2.0f, 6.0f, 2.0f);


## XREngine.Editor/UI/Panels/Inspector/Editors/InspectorPropertyEditors.DataTransformerBase.cs
- L47 C98: new InvalidOperationException :: => GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance) ?? throw new InvalidOperationException($"Property '{name}' not found.");


## XREngine.Editor/UI/Panels/Inspector/Editors/InspectorPropertyEditors.PrimitiveTypes.cs
- L158 C33: new Vector2 :: tfm.MinAnchor = new Vector2(0.0f, 0.0f);
- L159 C33: new Vector2 :: tfm.MaxAnchor = new Vector2(0.0f, 0.0f);


## XREngine.Editor/UI/Panels/Inspector/InspectorPanel.cs
- L108 C27: new Vector4 :: listTfm.Padding = new Vector4(0.0f);
- L168 C27: new Vector4 :: nameTfm.Margins = new Vector4(leftMargin, verticalSpacing, rightMargin, verticalSpacing);


## XREngine.Editor/UI/Toolbar/UIToolbarComponent.cs
- L81 C27: new Vector4 :: listTfm.Padding = new Vector4(0.0f);
- L126 C31: new Vector2 :: buttonTfm.MaxAnchor = new Vector2(0.0f, 1.0f);
- L127 C29: new Vector4 :: buttonTfm.Margins = new Vector4(Margin);
- L131 C27: new Vector4 :: textTfm.Margins = new Vector4(10.0f, Margin, 10.0f, Margin);
- L137 C113: new ToolbarButton :: UIListTransform submenuList = CreateMenu(buttonNode, false, null, null, [.. tbd.Options.Select(x => new ToolbarButton(x))], true, SubmenuItemHeight, toolbar);
- L143 C19: new Vector2 :: ? new Vector2(Margin, Margin)
- L144 C19: new Vector2 :: : new Vector2(-Margin, -Margin);
- L146 C43: new Vector2 :: submenuList.NormalizedPivot = new Vector2(0.0f, 1.0f);
- L150 C41: new Vector2 :: submenuList.MaxAnchor = new Vector2(1.0f, 1.0f);
- L151 C41: new Vector2 :: submenuList.MinAnchor = new Vector2(1.0f, 1.0f);
- L156 C41: new Vector2 :: submenuList.MaxAnchor = new Vector2(0.0f, 0.0f);
- L157 C41: new Vector2 :: submenuList.MinAnchor = new Vector2(0.0f, 0.0f);
- L167 C32: new Vector4 :: separatorTfm.Padding = new Vector4(0.0f);
- L185 C31: new Vector2 :: buttonTfm.MaxAnchor = new Vector2(0.0f, 1.0f);
- L186 C29: new Vector4 :: buttonTfm.Margins = new Vector4(Margin);
- L191 C27: new Vector4 :: textTfm.Margins = new Vector4(10.0f, Margin, 10.0f, Margin);
- L207 C19: new Vector2 :: ? new Vector2(Margin, Margin)
- L208 C19: new Vector2 :: : new Vector2(-Margin, -Margin);
- L211 C43: new Vector2 :: submenuList.NormalizedPivot = new Vector2(0.0f, 1.0f);
- L215 C41: new Vector2 :: submenuList.MaxAnchor = new Vector2(1.0f, 1.0f);
- L216 C41: new Vector2 :: submenuList.MinAnchor = new Vector2(1.0f, 1.0f);
- L221 C41: new Vector2 :: submenuList.MaxAnchor = new Vector2(0.0f, 0.0f);
- L222 C41: new Vector2 :: submenuList.MinAnchor = new Vector2(0.0f, 0.0f);


## XREngine.Editor/UI/Tools/ShaderAnalyzerTool.cs
- L36 C77: new Dictionary :: public IReadOnlyDictionary<string, int> CategoryTotals { get; init; } = new Dictionary<string, int>();
- L57 C21: new GlslCostEstimatorOptions :: options ??= new GlslCostEstimatorOptions();
- L59 C19: new ArgumentException :: throw new ArgumentException("GLSL source is empty.", nameof(glslSource));
- L61 C19: new ArgumentOutOfRangeException :: throw new ArgumentOutOfRangeException(nameof(options.InvocationsPerFrame));
- L64 C22: new Dictionary :: var counts = new Dictionary<string, int>(StringComparer.Ordinal);
- L70 C26: new List :: var operations = new List<OperationResult>(counts.Count);
- L71 C30: new Dictionary :: var categoryTotals = new Dictionary<string, int>(StringComparer.Ordinal);
- L87 C28: new OperationResult :: operations.Add(new OperationResult(info, kvp.Value, cycleCost, opCost));
- L93 C16: new ShaderCostReport :: return new ShaderCostReport
- L200 C23: new StringBuilder :: var builder = new StringBuilder(source.Length);
- L290 C23: new Dictionary :: var all = new Dictionary<string, OperationInfo>(functions.Count + keywords.Count + operators.Count, StringComparer.Ordinal);
- L305 C35: new HashSet :: SingleCharOperators = new HashSet<char>(operators.Keys
- L315 C20: new OperationCatalog :: return new OperationCatalog(functions, keywords, operators);
- L333 C20: new Regex :: return new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled);
- L338 C24: new Dictionary :: var dict = new Dictionary<string, OperationInfo>(StringComparer.Ordinal);
- L555 C30: new OperationInfo :: yield return new OperationInfo(name, name, category, cycles, kind);


## XREngine.Editor/UI/Tools/ShaderAnalyzerWindow.cs
- L15 C66: new ShaderAnalyzerWindow :: public static ShaderAnalyzerWindow Instance => _instance ??= new ShaderAnalyzerWindow();
- L17 C53: new() :: private readonly GlslCostEstimator _estimator = new();
- L61 C33: new Vector2 :: ImGui.SetNextWindowSize(new Vector2(1000, 750), ImGuiCond.FirstUseEver);
- L146 C41: new Vector2 :: ImGui.BeginChild("SourcePanel", new Vector2(400, availableHeight), ImGuiChildFlags.Border | ImGuiChildFlags.ResizeX);
- L216 C31: new Vector4 :: ImGui.TextColored(new Vector4(1, 0.3f, 0.3f, 1), _errorMessage);
- L251 C31: new Vector4 :: ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1),
- L306 C13: new Vector4 :: new Vector4(0.2f, 0.4f, 0.8f, 1));
- L312 C13: new Vector4 :: new Vector4(0.2f, 0.6f, 0.4f, 1));
- L318 C13: new Vector4 :: new Vector4(0.6f, 0.4f, 0.2f, 1));
- L324 C13: new Vector4 :: new Vector4(0.5f, 0.3f, 0.6f, 1));
- L335 C43: new Vector2 :: drawList.AddRectFilled(pos, pos + new Vector2(width, height),
- L336 C43: new Vector4 :: ImGui.ColorConvertFloat4ToU32(new Vector4(color.X * 0.3f, color.Y * 0.3f, color.Z * 0.3f, 0.8f)), 4);
- L337 C37: new Vector2 :: drawList.AddRect(pos, pos + new Vector2(width, height),
- L341 C40: new Vector2 :: ImGui.SetCursorScreenPos(pos + new Vector2(8, 4));
- L342 C27: new Vector4 :: ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1), label);
- L344 C40: new Vector2 :: ImGui.SetCursorScreenPos(pos + new Vector2(8, 22));
- L349 C40: new Vector2 :: ImGui.SetCursorScreenPos(pos + new Vector2(8, 42));
- L350 C27: new Vector4 :: ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1), unit);
- L352 C40: new Vector2 :: ImGui.SetCursorScreenPos(pos + new Vector2(width + 4, 0));
- L353 C21: new Vector2 :: ImGui.Dummy(new Vector2(0, height));
- L480 C47: new Vector2 :: drawList.AddRectFilled(pos, pos + new Vector2(chartWidth, barHeight),
- L481 C47: new Vector4 :: ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 0.2f, 0.2f, 1)), 2);
- L485 C47: new Vector2 :: drawList.AddRectFilled(pos, pos + new Vector2(barWidth, barHeight),
- L490 C36: new Vector2 :: drawList.AddText(pos + new Vector2(barWidth + 5, (barHeight - 14) / 2),
- L491 C47: new Vector4 :: ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 1)), label);
- L493 C25: new Vector2 :: ImGui.Dummy(new Vector2(chartWidth + 80, barHeight + 4));
- L506 C43: new Vector2 :: drawList.AddRectFilled(pos, pos + new Vector2(width, height),
- L507 C43: new Vector4 :: ImGui.ColorConvertFloat4ToU32(new Vector4(0.2f, 0.2f, 0.2f, 1)), 2);
- L510 C43: new Vector2 :: drawList.AddRectFilled(pos, pos + new Vector2(width * fraction, height),
- L513 C21: new Vector2 :: ImGui.Dummy(new Vector2(width, height));
- L520 C35: new Vector4 :: "Texture Sampling" => new Vector4(0.9f, 0.3f, 0.3f, 1),
- L521 C32: new Vector4 :: "Texture Query" => new Vector4(0.8f, 0.4f, 0.4f, 1),
- L522 C35: new Vector4 :: "Image Operations" => new Vector4(0.9f, 0.5f, 0.3f, 1),
- L523 C32: new Vector4 :: "Image Atomics" => new Vector4(1.0f, 0.4f, 0.2f, 1),
- L524 C31: new Vector4 :: "Trigonometry" => new Vector4(0.3f, 0.7f, 0.9f, 1),
- L525 C30: new Vector4 :: "Exponential" => new Vector4(0.4f, 0.6f, 0.9f, 1),
- L526 C27: new Vector4 :: "Geometry" => new Vector4(0.3f, 0.9f, 0.5f, 1),
- L527 C25: new Vector4 :: "Matrix" => new Vector4(0.5f, 0.8f, 0.4f, 1),
- L528 C29: new Vector4 :: "Arithmetic" => new Vector4(0.7f, 0.7f, 0.3f, 1),
- L529 C29: new Vector4 :: "Comparison" => new Vector4(0.6f, 0.6f, 0.4f, 1),
- L530 C26: new Vector4 :: "Logical" => new Vector4(0.5f, 0.5f, 0.5f, 1),
- L531 C26: new Vector4 :: "Bitwise" => new Vector4(0.6f, 0.4f, 0.7f, 1),
- L532 C31: new Vector4 :: "Control Flow" => new Vector4(0.9f, 0.6f, 0.2f, 1),
- L533 C25: new Vector4 :: "Common" => new Vector4(0.5f, 0.7f, 0.7f, 1),
- L534 C30: new Vector4 :: "Derivatives" => new Vector4(0.7f, 0.5f, 0.8f, 1),
- L535 C27: new Vector4 :: "Barriers" => new Vector4(1.0f, 0.3f, 0.5f, 1),
- L536 C26: new Vector4 :: "Atomics" => new Vector4(0.9f, 0.2f, 0.4f, 1),
- L537 C24: new Vector4 :: "Noise" => new Vector4(0.4f, 0.8f, 0.8f, 1),
- L538 C18: new Vector4 :: _ => new Vector4(0.6f, 0.6f, 0.6f, 1)
- L553 C27: new GlslCostEstimatorOptions :: var options = new GlslCostEstimatorOptions
- L684 C22: new System.Text.StringBuilder :: var sb = new System.Text.StringBuilder();


## XREngine.Editor/UI/Tools/ShaderLockingTool.cs
- L136 C28: new UniformLockInfo :: var info = new UniformLockInfo
- L179 C35: new UniformLockInfo :: _uniforms[name] = new UniformLockInfo
- L213 C24: new int :: "ivec2" => new int[] { 0, 0 },
- L214 C24: new int :: "ivec3" => new int[] { 0, 0, 0 },
- L215 C24: new int :: "ivec4" => new int[] { 0, 0, 0, 0 },
- L231 C22: new StringBuilder :: var result = new StringBuilder(source);
- L243 C22: new StringBuilder :: result = new StringBuilder(Regex.Replace(result.ToString(), uniformPattern, constDeclaration));
- L601 C26: new StringBuilder :: var header = new StringBuilder();


## XREngine.Editor/UI/Tools/ShaderLockingWindow.cs
- L16 C65: new ShaderLockingWindow :: public static ShaderLockingWindow Instance => _instance ??= new ShaderLockingWindow();
- L18 C39: new() :: private ShaderLockingTool _tool = new();
- L49 C33: new Vector2 :: ImGui.SetNextWindowSize(new Vector2(900, 700), ImGuiCond.FirstUseEver);
- L138 C39: new Vector2 :: ImGui.BeginChild("LeftPanel", new Vector2(leftWidth, 0), ImGuiChildFlags.Border | ImGuiChildFlags.ResizeX);
- L159 C46: new Vector2 :: ImGui.BeginChild("PatternsList", new Vector2(0, 100), ImGuiChildFlags.Border);
- L231 C19: new Vector4 :: ? new Vector4(0.3f, 0.8f, 0.3f, 1.0f)  // Green for animated
- L232 C19: new Vector4 :: : new Vector4(0.8f, 0.5f, 0.2f, 1.0f); // Orange for locked
- L285 C31: new Vector4 :: ImGui.TextColored(new Vector4(1, 1, 0, 1), "No shader loaded. Use File > Load Shader or Load Material.");
- L318 C31: new Vector4 :: ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1), "No preview available.");
- L434 C26: new XRShader :: var shader = new XRShader(XRShader.ResolveType(Path.GetExtension(path)))
- L436 C26: new Core.Files.TextFile :: Source = new Core.Files.TextFile { Text = source, FilePath = path }


## XREngine.Editor/UI/UIEditorComponent.cs
- L110 C23: new XRMaterial :: var mat = new XRMaterial(bgShader)
- L113 C33: new RenderingParameters :: RenderOptions = new RenderingParameters
- L116 C33: new DepthTest :: DepthTest = new DepthTest()


## XREngine.Editor/UI/UIProjectBrowserComponent.cs
- L106 C30: new FileInfo :: _fileCache.Add(path, new FileInfo(args.FullPath));
- L116 C39: new FileInfo :: _fileCache.Add(args.FullPath, new FileInfo(args.FullPath));


## XREngine.Editor/Undo.cs
- L45 C44: new() :: private static readonly object _sync = new();
- L57 C62: new() :: private static readonly Stack<ChangeScope> _scopeStack = new();
- L63 C60: new() :: private static readonly Stack<UndoAction> _undoStack = new();
- L69 C60: new() :: private static readonly Stack<UndoAction> _redoStack = new();
- L81 C91: new() :: private static readonly AsyncLocal<UserInteractionContext?> _userInteractionContext = new();
- L104 C87: new() :: private static readonly ConcurrentQueue<TransformBase> _pendingTransformRefresh = new();
- L231 C21: new ChangeScope :: var scope = new ChangeScope(string.IsNullOrWhiteSpace(description) ? "Change" : description.Trim());
- L268 C23: new UserInteractionContext :: context = new UserInteractionContext();
- L274 C16: new UserInteractionScope :: return new UserInteractionScope(context);
- L414 C27: new TrackedObject :: var context = new TrackedObject(instance);
- L565 C40: new ShortcutHandlers :: _shortcutHandlers[local] = new ShortcutHandlers(undoHandler, redoHandler);
- L656 C36: new SceneNodeContext :: var sceneContext = new SceneNodeContext(node);
- L744 C24: new PropertyChangeStep :: var step = new PropertyChangeStep(target, propertyName, previousValue, newValue);
- L745 C26: new UndoAction :: var action = new UndoAction(BuildDescription(step), [step], DateTime.UtcNow);
- L747 C58: new changes :: _redoStack.Clear(); // Clear redo stack when new changes are made
- L762 C23: new RecordingSuppressionScope :: using var _ = new RecordingSuppressionScope();
- L773 C23: new RecordingSuppressionScope :: using var _ = new RecordingSuppressionScope();
- L842 C31: new UndoAction :: committedAction = new UndoAction(scope.Description, steps, scope.TimestampUtc);
- L859 C20: new List :: var list = new List<UndoEntry>(entries.Length);
- L1030 C25: new HashSet :: var processed = new HashSet<TransformBase>(ReferenceEqualityComparer.Instance);
- L1409 C29: new PropertyChangeStep :: Changes.Add(new PropertyChangeStep(target, propertyName, previousValue, newValue));
- L1497 C20: new WeakReference :: => new(new WeakReference<XRBase>(Target), GetDisplayName(Target), Target.GetType(), PropertyName, OriginalValue, CurrentValue);


## XREngine.Editor/Undo/ImGuiUndoHelper.cs
- L17 C67: new() :: private static readonly Dictionary<uint, ScopeInfo> _scopes = new();
- L83 C31: new ScopeInfo :: _scopes[itemId] = new ScopeInfo(scope, interaction, frame);


## XREngine.Editor/Undo/TransformTool3D.Undo.cs
- L10 C41: new() :: private static readonly object _sync = new();


## XREngine.Editor/Unit Tests/Default/UnitTestingWorld.Audio.cs
- L45 C25: new SceneNode :: var sound = new SceneNode(rootNode) { Name = "TestSoundNode" };


## XREngine.Editor/Unit Tests/Default/UnitTestingWorld.cs
- L78 C21: new XRScene :: var scene = new XRScene("Main Scene");
- L79 C24: new SceneNode :: var rootNode = new SceneNode("Root Node");
- L108 C24: new() :: Random r = new();
- L112 C72: new Vector3 :: Lighting.AddLightProbes(rootNode, 1, 1, 1, 10, 10, 10, new Vector3(0.0f, 50.0f, 0.0f));
- L131 C21: new XRWorld :: var world = new XRWorld("Default World", scene);
- L150 C33: new Vector3 :: mirrorTfm.Translation = new Vector3(0.0f, 0.0f, -20.0f);
- L151 C27: new Vector3 :: mirrorTfm.Scale = new Vector3(160.0f, 90.0f, 1.0f);
- L160 C31: new Vector3 :: tfmRoot.Translation = new Vector3(0.0f, 0.0f, 0.0f);
- L165 C28: new Vector3 :: tfm1.Translation = new Vector3(0.0f, 5.0f, 0.0f);
- L170 C28: new Vector3 :: tfm2.Translation = new Vector3(0.0f, 5.0f, 0.0f);
- L177 C33: new Vector3 :: targetTfm.Translation = new Vector3(2.0f, 5.0f, 0.0f);
- L190 C25: new SceneNode :: var decalNode = new SceneNode(rootNode) { Name = "TestDecalNode" };
- L192 C32: new Vector3 :: decalTfm.Translation = new Vector3(0.0f, 5.0f, 0.0f);
- L194 C26: new Vector3 :: decalTfm.Scale = new Vector3(7.0f);
- L203 C32: new() :: PropAnimVector3 anim = new();
- L204 C20: new() :: Random r = new();
- L212 C32: new Vector3Keyframe :: anim.Keyframes.Add(new Vector3Keyframe(t, value, tangent, EVectorInterpType.Smooth));


## XREngine.Editor/Unit Tests/Default/UnitTestingWorld.Lighting.cs
- L22 C29: new SceneNode :: var probeRoot = new SceneNode(rootNode) { Name = "LightProbeRoot" };
- L37 C37: new SceneNode :: var probe = new SceneNode(probeRoot) { Name = $"LightProbe_{i}_{j}_{k}" };
- L39 C63: new Vector3 :: probeTransform.Translation = center + new Vector3(w - halfWidth, h, d - halfDepth);
- L56 C32: new SceneNode :: var dirLightNode = new SceneNode(rootNode) { Name = "TestDirectionalLightNode" };
- L58 C45: new Vector3 :: dirLightTransform.Translation = new Vector3(0.0f, 0.0f, 0.0f);
- L66 C34: new Vector3 :: dirLightComp.Color = new Vector3(1, 1, 1);
- L68 C34: new Vector3 :: dirLightComp.Scale = new Vector3(100.0f, 100.0f, 900.0f);
- L75 C33: new SceneNode :: var dirLightNode2 = new SceneNode(rootNode) { Name = "TestDirectionalLightNode2" };
- L77 C46: new Vector3 :: dirLightTransform2.Translation = new Vector3(0.0f, 10.0f, 0.0f);
- L83 C35: new Vector3 :: dirLightComp2.Color = new Vector3(1.0f, 0.8f, 0.8f);
- L85 C35: new Vector3 :: dirLightComp2.Scale = new Vector3(1000.0f, 1000.0f, 1000.0f);
- L91 C33: new SceneNode :: var spotLightNode = new SceneNode(rootNode) { Name = "TestSpotLightNode" };
- L93 C46: new Vector3 :: spotLightTransform.Translation = new Vector3(0.0f, 10.0f, 0.0f);
- L99 C35: new Vector3 :: spotLightComp.Color = new Vector3(1.0f, 1.0f, 1.0f);
- L110 C30: new SceneNode :: var pointLight = new SceneNode(rootNode) { Name = "TestPointLightNode" };
- L112 C47: new Vector3 :: pointLightTransform.Translation = new Vector3(0.0f, 2.0f, 0.0f);
- L117 C36: new Vector3 :: pointLightComp.Color = new Vector3(1.0f, 1.0f, 1.0f);


## XREngine.Editor/Unit Tests/Default/UnitTestingWorld.Math.cs
- L18 C21: new XRScene :: var scene = new XRScene("Math Intersections Scene");
- L19 C24: new SceneNode :: var rootNode = new SceneNode("Root Node");
- L33 C21: new XRWorld :: var world = new XRWorld("Math Intersections World", scene);
- L46 C27: new Vector3 :: debug.AddLine(new Vector3(x, 0.0f, -extent), new Vector3(x, 0.0f, extent), x == 0.0f ? ColorF4.White : ColorF4.Gray);
- L46 C58: new Vector3 :: debug.AddLine(new Vector3(x, 0.0f, -extent), new Vector3(x, 0.0f, extent), x == 0.0f ? ColorF4.White : ColorF4.Gray);
- L50 C27: new Vector3 :: debug.AddLine(new Vector3(-extent, 0.0f, z), new Vector3(extent, 0.0f, z), z == 0.0f ? ColorF4.White : ColorF4.Gray);
- L50 C58: new Vector3 :: debug.AddLine(new Vector3(-extent, 0.0f, z), new Vector3(extent, 0.0f, z), z == 0.0f ? ColorF4.White : ColorF4.Gray);
- L52 C23: new Vector3 :: debug.AddLine(new Vector3(0.0f, 0.0f, 0.0f), new Vector3(0.0f, 4.0f, 0.0f), ColorF4.LightGold);
- L52 C54: new Vector3 :: debug.AddLine(new Vector3(0.0f, 0.0f, 0.0f), new Vector3(0.0f, 4.0f, 0.0f), ColorF4.LightGold);
- L61 C35: new Vector3 :: frustumATfm.Translation = new Vector3(-2.5f, 2.0f, 7.5f);
- L67 C35: new Vector3 :: frustumBTfm.Translation = new Vector3(3.5f, 2.5f, 5.0f);
- L84 C39: new Vector3 :: frustumBTfm.Translation = new Vector3(
- L105 C50: new Vector3 :: Vector3 rayDirection = Vector3.Normalize(new Vector3(1.0f, 0.05f, 1.0f));
- L140 C34: new Vector3 :: frustumTfm.Translation = new Vector3(0.0f, 2.0f, -6.0f);
- L165 C26: new Sphere :: var sphere = new Sphere(
- L166 C17: new Vector3 :: new Vector3(MathF.Sin(t * 0.8f) * 4.0f, 2.0f + MathF.Sin(t * 0.55f) * 0.8f, 4.0f + MathF.Cos(t * 0.7f) * 2.0f),
- L174 C24: new AABB :: var aabb = new AABB(boxCenter - boxHalf, boxCenter + boxHalf);
- L180 C35: new Vector3 :: Vector3 capB = capA + new Vector3(0.0f, 2.3f, 0.0f);
- L185 C27: new Capsule :: var capsule = new Capsule((capA + capB) * 0.5f, capUp, capR, capLen * 0.5f);
- L244 C45: new Vector3 :: Vector3 dir = Vector3.Normalize(new Vector3(1.0f, MathF.Sin(t * 0.35f) * 0.15f, 0.85f));
- L271 C16: new Frustum :: return new Frustum(fovY, aspect, nearZ, farZ, forward, up, position);


## XREngine.Editor/Unit Tests/Default/UnitTestingWorld.Models.cs
- L36 C51: new Vector3 :: var mesh = XRMesh.Shapes.SolidBox(new Vector3(-0.5f), new Vector3(0.5f), true, XRMesh.Shapes.ECubemapTextureUVs.None);
- L36 C71: new Vector3 :: var mesh = XRMesh.Shapes.SolidBox(new Vector3(-0.5f), new Vector3(0.5f), true, XRMesh.Shapes.ECubemapTextureUVs.None);
- L40 C33: new Model :: boxComp.Model = new Model([new SubMesh(mesh, material)
- L40 C44: new SubMesh :: boxComp.Model = new Model([new SubMesh(mesh, material)
- L43 C25: new AABB :: new AABB(new Vector3(-0.5f),
- L43 C34: new Vector3 :: new AABB(new Vector3(-0.5f),
- L44 C25: new Vector3 :: new Vector3(0.5f)),
- L71 C50: new ModelImporter :: using var importer = new ModelImporter(resolvedPath, null, null);
- L86 C58: new SceneNode :: importedStaticModelsRootNode ??= new SceneNode(rootNode) { Name = "Static Model Root", Layer = DefaultLayers.StaticIndex };
- L150 C26: new SceneNode :: var skybox = new SceneNode(rootNode) { Name = "TestSkyboxNode" };
- L265 C81: new TransformState :: ikTargetNode.GetTransformAs<Transform>(true)!.SetFrameState(new TransformState()
- L269 C33: new Vector3 :: Scale = new Vector3(1.0f),
- L540 C34: new Vector3 :: phys.Force = new Vector3(0.0f, 0.0f, 0.0f);
- L541 C36: new Vector3 :: phys.Gravity = new Vector3(0.0f, -0.1f, 0.0f);
- L789 C52: new() :: Dictionary<string, string> pathRemap = new()
- L968 C23: new XRTexture2D :: tex = new XRTexture2D()
- L999 C17: new ShaderVector3 :: new ShaderVector3(new Vector3(1.0f, 1.0f, 1.0f), "BaseColor"),
- L999 C35: new Vector3 :: new ShaderVector3(new Vector3(1.0f, 1.0f, 1.0f), "BaseColor"),
- L1000 C17: new ShaderFloat :: new ShaderFloat(1.0f, "Opacity"),
- L1001 C17: new ShaderFloat :: new ShaderFloat(1.0f, "Roughness"),
- L1002 C17: new ShaderFloat :: new ShaderFloat(0.0f, "Metallic"),
- L1003 C17: new ShaderFloat :: new ShaderFloat(0.0f, "Specular"),
- L1004 C17: new ShaderFloat :: new ShaderFloat(0.0f, "Emission"),


## XREngine.Editor/Unit Tests/Default/UnitTestingWorld.Pawns.cs
- L113 C35: new Vector3 :: footTfm.Translation = new Vector3(0.0f, -movementComp.HalfHeight, 0.0f);
- L210 C37: new Vector3 :: movementComp.Velocity = new Vector3(0.0f, 0.0f, 0.0f);
- L365 C35: new Vector3 :: footTfm.Translation = new Vector3(0.0f, -movementComp.HalfHeight, 0.0f);
- L372 C43: new Vector3 :: cameraOffsetTfm.Translation = new Vector3(0.0f, (movementComp.HalfHeight * 1.8f), 0.0f);
- L414 C30: new SceneNode :: var cameraNode = new SceneNode(parentNode, "TestCameraNode");


## XREngine.Editor/Unit Tests/Default/UnitTestingWorld.Physics.cs
- L39 C29: new() :: Random random = new();
- L48 C25: new SceneNode :: var floor = new SceneNode(rootNode) { Name = "Floor" };
- L57 C34: new IPhysicsGeometry.Box :: floorComp.Geometry = new IPhysicsGeometry.Box(floorHalfExtents);
- L58 C45: new Vector3 :: floorTfm.SetPositionAndRotation(new Vector3(0.0f, -floorHalfExtents.Y, 0.0f), Quaternion.Identity);
- L100 C32: new Model :: floorModel.Model = new Model([new SubMesh(XRMesh.Create(VertexQuad.PosY(10000.0f)), floorMat)
- L100 C43: new SubMesh :: floorModel.Model = new Model([new SubMesh(XRMesh.Create(VertexQuad.PosY(10000.0f)), floorMat)
- L129 C24: new SceneNode :: var ball = new SceneNode(rootNode) { Name = "Ball" };
- L134 C33: new IPhysicsGeometry.Sphere :: ballComp.Geometry = new IPhysicsGeometry.Sphere(ballRadius);
- L181 C31: new Model :: ballModel.Model = new Model([new SubMesh(XRMesh.Shapes.SolidSphere(Vector3.Zero, ballRadius, 32), ballMat)]);
- L181 C42: new SubMesh :: ballModel.Model = new Model([new SubMesh(XRMesh.Shapes.SolidSphere(Vector3.Zero, ballRadius, 32), ballMat)]);


## XREngine.Editor/Unit Tests/Default/UnitTestingWorld.Toggles.cs
- L10 C52: new() :: public static Settings Toggles { get; set; } = new();


## XREngine.Editor/Unit Tests/Default/UnitTestingWorld.UserInterface.cs
- L30 C56: new() :: private static readonly Queue<float> _fpsAvg = new();
- L62 C43: new Vector2 :: textTransform.MinAnchor = new Vector2(0.0f, 1.0f);
- L63 C43: new Vector2 :: textTransform.MaxAnchor = new Vector2(0.0f, 1.0f);
- L64 C49: new Vector2 :: textTransform.NormalizedPivot = new Vector2(0.0f, 1.0f);
- L68 C43: new Vector2 :: textTransform.MinAnchor = new Vector2(1.0f, 0.0f);
- L69 C43: new Vector2 :: textTransform.MaxAnchor = new Vector2(1.0f, 0.0f);
- L70 C49: new Vector2 :: textTransform.NormalizedPivot = new Vector2(1.0f, 0.0f);
- L72 C37: new Vector4 :: textTransform.Margins = new Vector4(10.0f, 10.0f, 10.0f, 10.0f);
- L73 C35: new Vector3 :: textTransform.Scale = new Vector3(1.0f);
- L92 C34: new SceneNode :: var rootCanvasNode = new SceneNode(parent.World, "TestUINode") { IsEditorOnly = true };
- L98 C33: new Vector4 :: canvasTfm.Padding = new Vector4(0.0f);
- L143 C33: new Vector2 :: tfm.MaxAnchor = new Vector2(1.0f, 1.0f);
- L144 C33: new Vector2 :: tfm.MinAnchor = new Vector2(0.0f, 0.0f);
- L145 C39: new Vector2 :: tfm.NormalizedPivot = new Vector2(0.0f, 0.0f);
- L179 C33: new Vector2 :: tfm.MinAnchor = new Vector2(0.0f, 0.0f);
- L180 C33: new Vector2 :: tfm.MaxAnchor = new Vector2(1.0f, 1.0f);
- L181 C39: new Vector2 :: tfm.NormalizedPivot = new Vector2(0.0f, 0.0f);
- L196 C37: new Vector2 :: tfm.MinAnchor = new Vector2(0.0f, 0.0f);
- L197 C37: new Vector2 :: tfm.MaxAnchor = new Vector2(1.0f, 1.0f);
- L198 C43: new Vector2 :: tfm.NormalizedPivot = new Vector2(0.0f, 0.0f);
- L199 C39: new Vector2 :: tfm.Translation = new Vector2(0.0f, 0.0f);
- L226 C36: new Vector2 :: previewTfm.MinAnchor = new Vector2(0.0f, 0.0f);
- L227 C36: new Vector2 :: previewTfm.MaxAnchor = new Vector2(1.0f, 1.0f);
- L228 C42: new Vector2 :: previewTfm.NormalizedPivot = new Vector2(0.0f, 0.0f);
- L234 C33: new Vector2 :: leftTfm.MinAnchor = new Vector2(0.0f, 0.0f);
- L235 C33: new Vector2 :: leftTfm.MaxAnchor = new Vector2(0.5f, 1.0f);
- L236 C39: new Vector2 :: leftTfm.NormalizedPivot = new Vector2(0.0f, 0.0f);
- L242 C34: new Vector2 :: rightTfm.MinAnchor = new Vector2(0.5f, 0.0f);
- L243 C34: new Vector2 :: rightTfm.MaxAnchor = new Vector2(1.0f, 1.0f);
- L244 C40: new Vector2 :: rightTfm.NormalizedPivot = new Vector2(0.0f, 0.0f);
- L305 C27: new XRMaterial :: var mat = new XRMaterial([texture], frag);
- L364 C55: new byte :: private static byte[] _newProjectNameBuffer = new byte[256];
- L365 C55: new byte :: private static byte[] _newProjectPathBuffer = new byte[512];
- L408 C97: new System.Numerics.Vector2 :: ImGuiNET.ImGui.SetNextWindowPos(viewport.GetCenter(), ImGuiNET.ImGuiCond.Appearing, new System.Numerics.Vector2(0.5f, 0.5f));
- L409 C46: new System.Numerics.Vector2 :: ImGuiNET.ImGui.SetNextWindowSize(new System.Numerics.Vector2(500, 180));
- L426 C53: new System.Numerics.Vector2 :: if (ImGuiNET.ImGui.Button("Create", new System.Numerics.Vector2(120, 0)))
- L445 C53: new System.Numerics.Vector2 :: if (ImGuiNET.ImGui.Button("Cancel", new System.Numerics.Vector2(120, 0)))
- L505 C44: new ToolbarButton :: _saveMenu.ChildOptions.Add(new ToolbarButton("No asset manager available"));
- L512 C44: new ToolbarButton :: _saveMenu.ChildOptions.Add(new ToolbarButton("No modified assets"));
- L520 C44: new ToolbarButton :: _saveMenu.ChildOptions.Add(new ToolbarButton(displayName, _ => SaveSingleAsset(capturedAsset.Value)));
- L549 C27: new ToolbarButton :: _saveMenu ??= new ToolbarButton("Save");
- L553 C17: new ToolbarButton :: new ToolbarButton("File", [Key.ControlLeft, Key.F],
- L556 C17: new ToolbarButton :: new ToolbarButton("Save All", SaveAll, [Key.ControlLeft, Key.ShiftLeft, Key.S]),
- L557 C17: new ToolbarButton :: new ToolbarButton("Open", [
- L558 C21: new ToolbarButton :: new ToolbarButton("Project", OpenProjectDialog),
- L560 C17: new ToolbarButton :: new ToolbarButton("New Project", _ => ShowNewProjectDialog()),
- L563 C13: new ToolbarButton :: new ToolbarButton("Assets"),
- L564 C13: new ToolbarButton :: new ToolbarButton("Tools", [Key.ControlLeft, Key.T],
- L566 C17: new ToolbarButton :: new ToolbarButton("Take Screenshot", TakeScreenshot),
- L567 C17: new ToolbarButton :: new ToolbarButton("Shader Locking Tool", _ => ShaderLockingWindow.Instance.Open()),
- L568 C17: new ToolbarButton :: new ToolbarButton("Shader Analyzer Tool", _ => ShaderAnalyzerWindow.Instance.Open()),
- L570 C13: new ToolbarButton :: new ToolbarButton("View"),
- L571 C13: new ToolbarButton :: new ToolbarButton("Window"),
- L572 C13: new ToolbarButton :: new ToolbarButton("Help"),
- L627 C30: new ToolbarButton :: var undoButton = new ToolbarButton("Undo", OnToolbarUndo, [Key.ControlLeft, Key.Z]);
- L628 C30: new ToolbarButton :: var redoButton = new ToolbarButton("Redo", OnToolbarRedo, [Key.ControlLeft, Key.Y]);
- L629 C34: new ToolbarButton :: _undoHistoryMenu ??= new ToolbarButton("Undo History");
- L632 C20: new ToolbarButton :: return new ToolbarButton("Edit", undoButton, redoButton, _undoHistoryMenu);
- L655 C51: new ToolbarButton :: _undoHistoryMenu.ChildOptions.Add(new ToolbarButton("No undo steps available"));
- L664 C51: new ToolbarButton :: _undoHistoryMenu.ChildOptions.Add(new ToolbarButton(label, _ => UndoMultiple(targetIndex)));


## XREngine.Editor/Unit Tests/Mesh Editing/UnitTestingWorld.MeshEditing.cs
- L24 C21: new XRScene :: var scene = new XRScene("Mesh Editing Scene");
- L25 C24: new SceneNode :: var rootNode = new SceneNode("Root Node");
- L33 C37: new Vector3 :: meshTransform.Translation = new Vector3(0.0f, 1.5f, 6.0f);
- L52 C21: new XRWorld :: var world = new XRWorld("Mesh Editing World", scene);
- L82 C16: new EditableMesh :: return new EditableMesh(vertices, indices);
- L96 C23: new SubMesh :: var subMesh = new SubMesh(mesh, material)
- L101 C27: new Model :: modelComp.Model = new Model(subMesh);
- L107 C25: new List :: var triangles = new List<VertexTriangle>(editable.Faces.Count);
- L118 C27: new VertexTriangle :: triangles.Add(new VertexTriangle(
- L119 C17: new Vertex :: new Vertex(a, normal, Vector2.Zero),
- L120 C17: new Vertex :: new Vertex(b, normal, Vector2.Zero),
- L121 C17: new Vertex :: new Vertex(c, normal, Vector2.Zero)));
- L128 C23: new AABB :: return (mesh, new AABB(min, max));


## XREngine.Editor/Unit Tests/Physx/UnitTestingWorld.PhysxTesting.cs
- L26 C21: new XRScene :: var scene = new XRScene("PhysX Testing Scene");
- L27 C24: new SceneNode :: var rootNode = new SceneNode("Root Node");
- L36 C21: new XRWorld :: var world = new XRWorld("PhysX Testing World", scene);
- L53 C30: new Vector3 :: halfExtents: new Vector3(0.5f, 0.5f, 0.5f),
- L54 C27: new Vector3 :: position: new Vector3(-6.0f, 1.0f + i * 1.05f, 8.0f));
- L61 C26: new Vector3 :: halfExtents: new Vector3(5.0f, 0.35f, 2.5f),
- L62 C23: new Vector3 :: position: new Vector3(6.0f, 0.65f, 10.0f),
- L69 C26: new Vector3 :: halfExtents: new Vector3(1.25f, 1.25f, 1.25f),
- L70 C23: new Vector3 :: position: new Vector3(6.0f, 4.5f, 4.0f),
- L83 C20: new SceneNode :: var node = new SceneNode(rootNode) { Name = name };
- L88 C25: new IPhysicsGeometry.Box :: body.Geometry = new IPhysicsGeometry.Box(halfExtents);
- L95 C23: new Model :: model.Model = new Model([
- L96 C13: new SubMesh :: new SubMesh(XRMesh.Shapes.SolidBox(Vector3.Zero, halfExtents * 2.0f), mat)
- L102 C20: new SceneNode :: var node = new SceneNode(rootNode) { Name = name };
- L107 C25: new IPhysicsGeometry.Box :: body.Geometry = new IPhysicsGeometry.Box(halfExtents);
- L112 C23: new Model :: model.Model = new Model([
- L113 C13: new SubMesh :: new SubMesh(XRMesh.Shapes.SolidBox(Vector3.Zero, halfExtents * 2.0f), mat)


## XREngine.Editor/Unit Tests/Uber Shader/UnitTestingWorld.UberShader.cs
- L22 C21: new XRScene :: var scene = new XRScene("Uber Shader Scene");
- L23 C24: new SceneNode :: var rootNode = new SceneNode("Root Node");
- L32 C21: new XRWorld :: var world = new XRWorld("Uber Shader World", scene);
- L48 C22: new UberMaterialConfig :: ("Base", new UberMaterialConfig()),
- L49 C26: new UberMaterialConfig :: ("Emission", new UberMaterialConfig { EnableEmission = true }),
- L50 C24: new UberMaterialConfig :: ("Matcap", new UberMaterialConfig { EnableMatcap = true }),
- L51 C33: new UberMaterialConfig :: ("Emission+Matcap", new UberMaterialConfig { EnableEmission = true, EnableMatcap = true }),
- L73 C27: new Model :: model.Model = new Model([
- L74 C17: new SubMesh :: new SubMesh(
- L81 C28: new Vector3 :: debug.AddPoint(new Vector3(0, radius + 0.2f, 0), ColorF4.White);
- L82 C27: new Vector3 :: debug.AddLine(new Vector3(0, radius + 0.2f, 0), new Vector3(0, radius + 1.0f, 0), ColorF4.White);
- L82 C61: new Vector3 :: debug.AddLine(new Vector3(0, radius + 0.2f, 0), new Vector3(0, radius + 1.0f, 0), ColorF4.White);
- L91 C30: new Vector3 :: refDebug.AddLine(new Vector3(x, 0.0f, -extent), new Vector3(x, 0.0f, extent), x == 0.0f ? ColorF4.White : ColorF4.Gray);
- L91 C61: new Vector3 :: refDebug.AddLine(new Vector3(x, 0.0f, -extent), new Vector3(x, 0.0f, extent), x == 0.0f ? ColorF4.White : ColorF4.Gray);
- L93 C30: new Vector3 :: refDebug.AddLine(new Vector3(-extent, 0.0f, z), new Vector3(extent, 0.0f, z), z == 0.0f ? ColorF4.White : ColorF4.Gray);
- L93 C61: new Vector3 :: refDebug.AddLine(new Vector3(-extent, 0.0f, z), new Vector3(extent, 0.0f, z), z == 0.0f ? ColorF4.White : ColorF4.Gray);
- L122 C24: new XRTexture :: var textures = new XRTexture?[] { main, bump };
- L126 C26: new ShaderVar :: var parameters = new ShaderVar[]
- L128 C13: new ShaderVector4 :: new ShaderVector4(new Vector4(config.Tint.R, config.Tint.G, config.Tint.B, config.Tint.A), "_Color"),
- L128 C31: new Vector4 :: new ShaderVector4(new Vector4(config.Tint.R, config.Tint.G, config.Tint.B, config.Tint.A), "_Color"),
- L131 C13: new ShaderVector4 :: new ShaderVector4(new Vector4(1, 1, 0, 0), "_MainTex_ST"),
- L131 C31: new Vector4 :: new ShaderVector4(new Vector4(1, 1, 0, 0), "_MainTex_ST"),
- L132 C13: new ShaderVector2 :: new ShaderVector2(Vector2.Zero, "_MainTexPan"),
- L133 C13: new ShaderInt :: new ShaderInt(0, "_MainTexUV"),
- L136 C13: new ShaderVector4 :: new ShaderVector4(new Vector4(1, 1, 0, 0), "_BumpMap_ST"),
- L136 C31: new Vector4 :: new ShaderVector4(new Vector4(1, 1, 0, 0), "_BumpMap_ST"),
- L137 C13: new ShaderVector2 :: new ShaderVector2(Vector2.Zero, "_BumpMapPan"),
- L138 C13: new ShaderInt :: new ShaderInt(0, "_BumpMapUV"),
- L139 C13: new ShaderFloat :: new ShaderFloat(0.0f, "_BumpScale"),
- L142 C13: new ShaderFloat :: new ShaderFloat(1.0f, "_ShadingEnabled"),
- L143 C13: new ShaderInt :: new ShaderInt(6, "_LightingMode"), // Realistic (simple lambert) to avoid ramp dependencies.
- L144 C13: new ShaderVector3 :: new ShaderVector3(new Vector3(1, 1, 1), "_LightingShadowColor"),
- L144 C31: new Vector3 :: new ShaderVector3(new Vector3(1, 1, 1), "_LightingShadowColor"),
- L145 C13: new ShaderFloat :: new ShaderFloat(1.0f, "_ShadowStrength"),
- L146 C13: new ShaderFloat :: new ShaderFloat(0.0f, "_LightingMinLightBrightness"),
- L147 C13: new ShaderFloat :: new ShaderFloat(0.0f, "_LightingMonochromatic"),
- L148 C13: new ShaderFloat :: new ShaderFloat(0.0f, "_LightingCapEnabled"),
- L149 C13: new ShaderFloat :: new ShaderFloat(10.0f, "_LightingCap"),
- L152 C13: new ShaderInt :: new ShaderInt(0, "_MainAlphaMaskMode"),
- L153 C13: new ShaderFloat :: new ShaderFloat(0.0f, "_AlphaMod"),
- L154 C13: new ShaderFloat :: new ShaderFloat(1.0f, "_AlphaForceOpaque"),
- L155 C13: new ShaderFloat :: new ShaderFloat(0.5f, "_Cutoff"),
- L156 C13: new ShaderInt :: new ShaderInt(0, "_Mode"),
- L159 C13: new ShaderFloat :: new ShaderFloat(config.EnableEmission ? 1.0f : 0.0f, "_EnableEmission"),
- L160 C13: new ShaderVector4 :: new ShaderVector4(new Vector4(1, 0.7f, 0.2f, 1), "_EmissionColor"),
- L160 C31: new Vector4 :: new ShaderVector4(new Vector4(1, 0.7f, 0.2f, 1), "_EmissionColor"),
- L161 C13: new ShaderFloat :: new ShaderFloat(2.5f, "_EmissionStrength"),
- L164 C13: new ShaderFloat :: new ShaderFloat(config.EnableMatcap ? 1.0f : 0.0f, "_MatcapEnable"),
- L165 C13: new ShaderVector4 :: new ShaderVector4(new Vector4(1, 1, 1, 1), "_MatcapColor"),
- L165 C31: new Vector4 :: new ShaderVector4(new Vector4(1, 1, 1, 1), "_MatcapColor"),
- L166 C13: new ShaderFloat :: new ShaderFloat(1.0f, "_MatcapIntensity"),
- L167 C13: new ShaderFloat :: new ShaderFloat(0.0f, "_MatcapReplace"),
- L168 C13: new ShaderFloat :: new ShaderFloat(1.0f, "_MatcapMultiply"),
- L169 C13: new ShaderFloat :: new ShaderFloat(0.0f, "_MatcapAdd"),
- L174 C27: new List :: var textureList = new List<XRTexture?>(textures);
- L182 C24: new XRMaterial :: var material = new XRMaterial(parameters, [.. textureList], vert, frag)
- L194 C17: new XRTexture2D :: var t = new XRTexture2D


## XREngine.Extensions/Array.cs
- L18 C46: new StringBuilder :: return list.Cast<object>().Aggregate(new StringBuilder(),
- L39 C37: new() :: StringBuilder builder = new();
- L59 C37: new() :: StringBuilder builder = new();
- L79 C37: new() :: StringBuilder builder = new();
- L104 C37: new() :: StringBuilder builder = new();
- L127 C37: new() :: StringBuilder builder = new();
- L172 C37: new() :: StringBuilder builder = new();
- L196 C37: new() :: StringBuilder builder = new();
- L224 C37: new() :: StringBuilder builder = new();
- L275 C37: new() :: StringBuilder builder = new();
- L298 C37: new() :: StringBuilder builder = new();
- L391 C26: new T :: T[] result = new T[length];
- L397 C25: new T :: T[] final = new T[data.Length + appended.Length];
- L430 C61: new() :: public static T[] Fill<T>(this T[] array) where T : new()
- L433 C28: new T :: array[i] = new T();
- L458 C47: new() :: ParallelOptions options = new()
- L480 C47: new() :: ParallelOptions options = new()


## XREngine.Extensions/Enumerable.cs
- L61 C47: new() :: ParallelOptions options = new()
- L83 C47: new() :: ParallelOptions options = new()


## XREngine.Extensions/Generic.cs
- L22 C30: new byte :: byte[] dataArr = new byte[sizeof(T)];
- L32 C23: new() :: T value = new();


## XREngine.Extensions/List.cs
- L53 C37: new Queue :: Queue<uint>[] buckets = new Queue<uint>[15];
- L55 C30: new Queue :: buckets[i] = new Queue<uint>();
- L104 C47: new() :: ParallelOptions options = new()
- L126 C47: new() :: ParallelOptions options = new()


## XREngine.Extensions/Memory/MarshalExtension.cs
- L11 C20: new CoTaskMemoryHandle :: return new CoTaskMemoryHandle(ptr);
- L17 C20: new CoTaskMemoryHandle :: return new CoTaskMemoryHandle(ptr);
- L23 C20: new CoTaskMemoryHandle :: return new CoTaskMemoryHandle(ptr);
- L29 C20: new CoTaskMemoryHandle :: return new CoTaskMemoryHandle(ptr);
- L35 C20: new CoTaskMemoryHandle :: return new CoTaskMemoryHandle(ptr);
- L41 C20: new CoTaskMemoryHandle :: return new CoTaskMemoryHandle(ptr);


## XREngine.Extensions/Numbers/MatrixExtension.cs
- L22 C13: new Vector3 :: new Vector3(matrix.M11, matrix.M12, matrix.M13).Length(),
- L23 C13: new Vector3 :: new Vector3(matrix.M21, matrix.M22, matrix.M23).Length(),
- L24 C13: new Vector3 :: new Vector3(matrix.M31, matrix.M32, matrix.M33).Length());


## XREngine.Extensions/ReaderWriterLockSlim.cs
- L40 C16: new ReadLockToken :: => new ReadLockToken(obj);
- L42 C16: new WriteLockToken :: => new WriteLockToken(obj);


## XREngine.Extensions/Reflection/Type.cs
- L18 C38: new() :: NewClass,           //class, new()
- L19 C31: new() :: NewStructOrClass,   //new()
- L23 C78: new() :: private static readonly Dictionary<Type, string> DefaultDictionary = new()
- L140 C23: new Exception :: throw new Exception();
- L161 C28: new() :: List<T> list = new();
- L174 C40: new() :: List<MemberInfo> members = new();


## XREngine.Extensions/Stream.cs
- L10 C28: new byte :: byte[] bytes = new byte[size];
- L20 C28: new byte :: byte[] bytes = new byte[size];
- L30 C26: new byte :: byte[] arr = new byte[size];
- L40 C26: new byte :: byte[] arr = new byte[size];


## XREngine.Extensions/String.cs
- L102 C67: new char :: string[] mainParts = Path.GetFullPath(mainPath).Split(new char[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
- L103 C69: new char :: string[] otherParts = Path.GetFullPath(otherPath).Split(new char[] { Path.DirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
- L333 C42: new() :: ConcurrentBag<int> bag = new();
- L345 C31: new() :: List<int> o = new();
- L357 C27: new() :: List<int> o = new();


## XREngine.Input/Devices/InputDevice.cs
- L11 C99: new() :: private static readonly Dictionary<EInputDeviceType, List<InputDevice>> _currentDevices = new()
- L13 C41: new List :: { EInputDeviceType.Gamepad, new List<InputDevice>() },
- L14 C41: new List :: { EInputDeviceType.Keyboard,new List<InputDevice>() },
- L15 C39: new List :: { EInputDeviceType.Mouse, new List<InputDevice>() },
- L40 C29: new ButtonManager :: _buttonStates = new ButtonManager[GetButtonCount()];
- L41 C27: new AxisManager :: _axisStates = new AxisManager[GetAxisCount()];
- L63 C23: new ButtonManager :: var man = new ButtonManager(index, name.ToString());
- L71 C23: new ButtonManager :: var man = new ButtonManager(index, name.ToString());
- L79 C23: new ButtonManager :: var man = new ButtonManager(index, name.ToString());
- L87 C23: new AxisManager :: var man = new AxisManager(index, name.ToString());


## XREngine.Input/Devices/InputInterfaces/LocalInputInterface.cs
- L216 C27: new GlfwGamepad :: Gamepad = new GlfwGamepad(gamepads[_localPlayerIndex]);
- L223 C28: new GlfwKeyboard :: Keyboard = new GlfwKeyboard(keyboards[0]);
- L226 C25: new GlfwMouse :: Mouse = new GlfwMouse(mice[0]);
- L411 C59: new OpenVRActionSetInputs :: _registeredOpenVRActions.Add(c, actions = new OpenVRActionSetInputs());
- L422 C59: new OpenVRActionSetInputs :: _registeredOpenVRActions.Add(c, actions = new OpenVRActionSetInputs());
- L433 C59: new OpenVRActionSetInputs :: _registeredOpenVRActions.Add(c, actions = new OpenVRActionSetInputs());
- L444 C59: new OpenVRActionSetInputs :: _registeredOpenVRActions.Add(c, actions = new OpenVRActionSetInputs());


## XREngine.Input/Devices/Managers/ButtonManager.cs
- L18 C24: new Dictionary :: _actions = new Dictionary<EButtonInputType, List<Action?>?>(4)
- L41 C37: new() :: private Lock _actionsLock = new();


## XREngine.Input/Devices/Managers/CursorManager.cs
- L13 C70: new List :: private readonly List<DelCursorUpdate?>?[] _onCursorUpdate = new List<DelCursorUpdate?>?[2];


## XREngine.Input/Devices/Types/BaseGamepad.cs
- L47 C38: new DXGamepad :: EInputType.XInput => new DXGamepad(index),
- L48 C28: new InvalidOperationException :: _ => throw new InvalidOperationException(),


## XREngine.Input/Devices/Types/BaseMouse.cs
- L17 C43: new() :: protected CursorManager _cursor = new();
- L18 C47: new() :: protected ScrollWheelManager _wheel = new();


## XREngine.Input/Devices/Types/DirectX/DXGamepad.cs
- L10 C69: new DXGamepadConfiguration :: public static DXGamepadConfiguration Config { get; set; } = new DXGamepadConfiguration();
- L14 C27: new() :: Vibration v = new()


## XREngine.Input/Devices/Types/DirectX/DXGamepadAwaiter.cs
- L19 C16: new DXGamepad :: => new DXGamepad(controllerIndex);


## XREngine.Input/Devices/Types/Glfw/GlfwGamepad.cs
- L129 C23: new GlfwButtonManager :: var man = new GlfwButtonManager(index, name.ToString(), GetGlfwButtonFactory(name));
- L141 C27: new GlfwAxisManager :: man = new GlfwAxisManager(index, name.ToString(), () => _controller.Triggers[0].Position);
- L144 C27: new GlfwAxisManager :: man = new GlfwAxisManager(index, name.ToString(), () => _controller.Triggers[1].Position);
- L148 C27: new GlfwAxisManager :: man = new GlfwAxisManager(index, name.ToString(), () => _controller.Thumbsticks[0].X);
- L151 C27: new GlfwAxisManager :: man = new GlfwAxisManager(index, name.ToString(), () => -_controller.Thumbsticks[0].Y);
- L155 C27: new GlfwAxisManager :: man = new GlfwAxisManager(index, name.ToString(), () => _controller.Thumbsticks[1].X);
- L158 C27: new GlfwAxisManager :: man = new GlfwAxisManager(index, name.ToString(), () => -_controller.Thumbsticks[1].Y);


## XREngine.Modeling/EditableMesh.cs
- L33 C21: new List :: _vertices = new List<Vector3>(vertices);
- L38 C24: new EditableFaceData :: _faces.Add(new EditableFaceData(indexArray[i], indexArray[i + 1], indexArray[i + 2]));
- L98 C34: new EditableFaceData :: _faces[faceId] = new EditableFaceData(edge.A, newIndex, opposite);
- L99 C28: new EditableFaceData :: _faces.Add(new EditableFaceData(newIndex, edge.B, opposite));
- L122 C30: new EditableFaceData :: _faces[faceId] = new EditableFaceData(vertexA, vertexB, third);
- L147 C48: new() :: Dictionary<int, HashSet<int>> result = new();
- L164 C16: new MeshAccelerationData :: return new MeshAccelerationData
- L224 C22: new EdgeKey :: yield return new EdgeKey(A, B);
- L225 C22: new EdgeKey :: yield return new EdgeKey(B, C);
- L226 C22: new EdgeKey :: yield return new EdgeKey(C, A);
- L232 C19: new InvalidOperationException :: throw new InvalidOperationException("Edge does not belong to this face.");


## XREngine.Modeling/MeshGenerator.cs
- L33 C39: new() :: MeshGenerator generator = new();
- L41 C23: new Vector3 :: AddVertex(new Vector3(-1, t, 0) * radius);
- L42 C23: new Vector3 :: AddVertex(new Vector3(1, t, 0) * radius);
- L43 C23: new Vector3 :: AddVertex(new Vector3(-1, -t, 0) * radius);
- L44 C23: new Vector3 :: AddVertex(new Vector3(1, -t, 0) * radius);
- L46 C23: new Vector3 :: AddVertex(new Vector3(0, -1, t) * radius);
- L47 C23: new Vector3 :: AddVertex(new Vector3(0, 1, t) * radius);
- L48 C23: new Vector3 :: AddVertex(new Vector3(0, -1, -t) * radius);
- L49 C23: new Vector3 :: AddVertex(new Vector3(0, 1, -t) * radius);
- L51 C23: new Vector3 :: AddVertex(new Vector3(t, 0, -1) * radius);
- L52 C23: new Vector3 :: AddVertex(new Vector3(t, 0, 1) * radius);
- L53 C23: new Vector3 :: AddVertex(new Vector3(-t, 0, -1) * radius);
- L54 C23: new Vector3 :: AddVertex(new Vector3(-t, 0, 1) * radius);
- L80 C55: new Dictionary :: IDictionary<long, int> middlePointCache = new Dictionary<long, int>();
- L113 C39: new() :: MeshGenerator generator = new();
- L134 C38: new Vector3 :: Vector3 vertex = new Vector3(x, y, z) * radius;
- L152 C38: new Vector3 :: Vector3 vertex = new Vector3(x, y, z) * radius;
- L179 C39: new() :: MeshGenerator generator = new();
- L191 C39: new() :: MeshGenerator generator = new();
- L208 C38: new Vector3 :: Vector3 vertex = new Vector3(x, y, z) * radius;
- L275 C37: new Vector3 :: Vector3[] newVertices = new Vector3[Vertices.Count + edgeVertices.Count];
- L286 C34: new Edge :: edgeVertices[new Edge(cornerVertices[0], cornerVertices[1])],
- L287 C34: new Edge :: edgeVertices[new Edge(cornerVertices[1], cornerVertices[2])],
- L288 C34: new Edge :: edgeVertices[new Edge(cornerVertices[2], cornerVertices[0])]
- L316 C33: new Vector3 :: Vector3[] normals = new Vector3[Vertices.Count];


## XREngine.Server/Authenticator.cs
- L14 C32: new JwtSecurityTokenHandler :: var tokenHandler = new JwtSecurityTokenHandler();
- L16 C35: new SecurityTokenDescriptor :: var tokenDescriptor = new SecurityTokenDescriptor
- L18 C27: new ClaimsIdentity :: Subject = new ClaimsIdentity(
- L23 C38: new SigningCredentials :: SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
- L23 C61: new SymmetricSecurityKey :: SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
- L32 C32: new JwtSecurityTokenHandler :: var tokenHandler = new JwtSecurityTokenHandler();
- L37 C44: new TokenValidationParameters :: var validationParameters = new TokenValidationParameters
- L40 C40: new SymmetricSecurityKey :: IssuerSigningKey = new SymmetricSecurityKey(key),


## XREngine.Server/Commands/CommandProcessor.Auth.cs
- L68 C66: new string :: command.Parameters.AddWithValue("@sessionToken", new string(sessionToken));
- L126 C36: new byte :: byte[] saltBytes = new byte[16];
- L135 C41: new() :: StringBuilder builder = new();


## XREngine.Server/Commands/CommandProcessor.cs
- L35 C19: new NotImplementedException :: throw new NotImplementedException();
- L40 C19: new NotImplementedException :: throw new NotImplementedException();
- L45 C19: new NotImplementedException :: throw new NotImplementedException();
- L50 C19: new NotImplementedException :: throw new NotImplementedException();
- L55 C19: new NotImplementedException :: throw new NotImplementedException();
- L60 C19: new NotImplementedException :: throw new NotImplementedException();
- L65 C19: new NotImplementedException :: throw new NotImplementedException();
- L70 C19: new NotImplementedException :: throw new NotImplementedException();


## XREngine.Server/Commands/CommandServer.cs
- L63 C26: new byte :: var buffer = new byte[1024];


## XREngine.Server/Controllers/LoadBalancerController.cs
- L27 C26: new Server :: var server = new Server
- L122 C20: new() :: => new()


## XREngine.Server/Instances/ServerInstanceManager.cs
- L18 C82: new() :: private readonly ConcurrentDictionary<Guid, ServerInstance> _instances = new();
- L46 C27: new ServerInstance :: var created = new ServerInstance(id, locator, worldInstance, enableDevRendering);
- L85 C74: new() :: private readonly Dictionary<int, ServerPlayerBinding> _players = new();


## XREngine.Server/Instances/WorldDownloadService.cs
- L24 C41: new HttpClient :: _httpClient = httpClient ?? new HttpClient();
- L69 C29: new() :: XRWorld world = new()
- L118 C31: new StringBuilder :: var builder = new StringBuilder();
- L130 C24: new Uri :: return new Uri(builder.ToString());
- L141 C28: new InvalidOperationException :: _ => throw new InvalidOperationException("World locator did not provide a resolvable download URI.")


## XREngine.Server/LoadBalance/Balancers/ConsistentHashingLoadBalancer.cs
- L10 C47: new() :: private readonly object _circleLock = new();


## XREngine.Server/LoadBalance/LoadBalancerService.cs
- L24 C41: new() :: private readonly object _gate = new();
- L154 C20: new ServerStatus :: return new ServerStatus(


## XREngine.Server/Program.cs
- L80 C32: new WorldDownloadService :: _worldDownloader = new WorldDownloadService();
- L81 C32: new ServerInstanceManager :: _instanceManager = new ServerInstanceManager(_worldDownloader);
- L110 C117: new UnitTestingWorld.Settings :: UnitTestingWorld.Toggles = JsonConvert.DeserializeObject<UnitTestingWorld.Settings>(content) ?? new UnitTestingWorld.Settings();
- L124 C32: new RoundRobinLeastLoadBalancer :: var strategy = new RoundRobinLeastLoadBalancer(Array.Empty<Server>());
- L125 C40: new LoadBalancerService :: _loadBalancerService = new LoadBalancerService(strategy, TimeSpan.FromSeconds(60));
- L159 C51: new WorldLocator :: var locator = request.WorldLocator ?? new WorldLocator
- L172 C20: new ServerInstanceContext :: return new ServerInstanceContext(instance.InstanceId, instance.WorldInstance);
- L180 C26: new Server :: var server = new Server
- L201 C25: new XRScene :: var scene = new XRScene("Main Scene");
- L202 C28: new SceneNode :: var rootNode = new SceneNode("Root Node");
- L208 C85: new Vector3 :: UnitTestingWorld.Lighting.AddLightProbes(rootNode, 1, 1, 1, 10, 10, 10, new Vector3(0.0f, 50.0f, 0.0f));
- L212 C25: new XRWorld :: var world = new XRWorld("Default World", scene);
- L218 C25: new XRScene :: var scene = new XRScene("Server Console Scene");
- L219 C28: new SceneNode :: var rootNode = new SceneNode("Root Node");
- L230 C30: new SceneNode :: var uiRootNode = new SceneNode("Server UI Root");
- L234 C20: new XRWorld :: return new XRWorld("Server World", scene);
- L253 C31: new Vector3 :: debug.AddLine(new Vector3(x, y, -extent), new Vector3(x, y, extent), color);
- L253 C59: new Vector3 :: debug.AddLine(new Vector3(x, y, -extent), new Vector3(x, y, extent), color);
- L262 C31: new Vector3 :: debug.AddLine(new Vector3(-extent, y, z), new Vector3(extent, y, z), color);
- L262 C59: new Vector3 :: debug.AddLine(new Vector3(-extent, y, z), new Vector3(extent, y, z), color);
- L268 C30: new SceneNode :: var cameraNode = new SceneNode(parentNode, "TestCameraNode");
- L315 C33: new Vector4 :: canvasTfm.Padding = new Vector4(0.0f);
- L338 C32: new Vector2 :: logTfm.MinAnchor = new Vector2(0.0f, 0.0f);
- L339 C32: new Vector2 :: logTfm.MaxAnchor = new Vector2(1.0f, 1.0f);
- L340 C30: new Vector4 :: logTfm.Margins = new Vector4(10.0f, 10.0f, 10.0f, 10.0f);
- L381 C39: new Vector2 :: textTransform.MinAnchor = new Vector2(1.0f, 0.0f);
- L382 C39: new Vector2 :: textTransform.MaxAnchor = new Vector2(1.0f, 0.0f);
- L383 C45: new Vector2 :: textTransform.NormalizedPivot = new Vector2(1.0f, 0.0f);
- L386 C37: new Vector4 :: textTransform.Margins = new Vector4(10.0f, 10.0f, 10.0f, 10.0f);
- L387 C35: new Vector3 :: textTransform.Scale = new Vector3(1.0f);
- L391 C56: new() :: private static readonly Queue<float> _fpsAvg = new();
- L418 C29: new Vector2 :: tfm.MinAnchor = new Vector2(0.5f, 0.5f);
- L419 C29: new Vector2 :: tfm.MaxAnchor = new Vector2(0.5f, 0.5f);
- L420 C35: new Vector2 :: tfm.NormalizedPivot = new Vector2(0.5f, 0.5f);
- L438 C17: new ShaderVector4 :: new ShaderVector4(new ColorF4(0.0f, 1.0f), "MatColor"),
- L438 C35: new ColorF4 :: new ShaderVector4(new ColorF4(0.0f, 1.0f), "MatColor"),
- L439 C17: new ShaderFloat :: new ShaderFloat(10.0f, "BlurStrength"),
- L440 C17: new ShaderInt :: new ShaderInt(30, "SampleCount"),
- L443 C28: new XRMaterial :: var floorMat = new XRMaterial(floorUniforms, [grabTex], floorShader);
- L461 C20: new GameStartupSettings :: return new GameStartupSettings()
- L467 C21: new() :: new()
- L470 C54: new XRWorld :: TargetWorld = targetWorld ?? new XRWorld(),
- L481 C43: new XREngine.Data.Core.OverrideableSetting :: OutputVerbosityOverride = new XREngine.Data.Core.OverrideableSetting<EOutputVerbosity>(EOutputVerbosity.Verbose, true),
- L487 C39: new UserSettings :: DefaultUserSettings = new UserSettings()


## XREngine.Server/VirtualizedConsoleUIComponent.cs
- L13 C66: new() :: private readonly ConcurrentQueue<string> _pendingLines = new();
- L225 C56: new Vector2 :: BoundableTransform.LocalPivotTranslation = new Vector2(0, yOffset);
- L302 C31: new ConsoleWriter :: => Console.SetOut(new ConsoleWriter(this));
- L314 C36: new TraceWriter :: => Trace.Listeners.Add(new TraceWriter(this));


## XREngine.UnitTests/Core/AssetCacheTests.cs
- L18 C29: new AssetCacheSandbox :: using var sandbox = new AssetCacheSandbox();
- L19 C23: new AssetManager :: var manager = new AssetManager();


## XREngine.UnitTests/Core/XRAssetMemoryPackCoverageTests.cs
- L31 C30: new TestCaseData :: yield return new TestCaseData(type)
- L58 C30: new HashSet :: var assemblies = new HashSet<Assembly>();


## XREngine.UnitTests/Core/XRAssetSerializationTests.cs
- L14 C24: new StubAsset :: var original = new StubAsset
- L34 C24: new StubAsset :: var original = new StubAsset
- L54 C20: new List :: var list = new List<XRAsset>
- L56 C13: new StubAsset :: new StubAsset { Name = "A", Payload = "p1", Value = 1 },
- L57 C13: new StubAsset :: new StubAsset { Name = "B", Payload = "p2", Value = 2 }
- L75 C20: new Dictionary :: var dict = new Dictionary<string, XRAsset>
- L77 C25: new StubAsset :: ["first"] = new StubAsset { Name = "One", Payload = "p-one", Value = 11 },
- L78 C26: new StubAsset :: ["second"] = new StubAsset { Name = "Two", Payload = "p-two", Value = 22 }
- L81 C59: new StubAssetContainer :: byte[] bytes = XRAssetMemoryPackAdapter.Serialize(new StubAssetContainer { Assets = dict });
- L102 C67: new() :: public Dictionary<string, XRAsset> Assets { get; set; } = new();


## XREngine.UnitTests/Editor/AssetCookingTests.cs
- L30 C27: new GameStartupSettings :: var startup = new GameStartupSettings


## XREngine.UnitTests/Physics/ConvexHullUtilityTests.cs
- L51 C28: new CoACD.ConvexHullMesh :: var expectedHull = new CoACD.ConvexHullMesh(
- L52 C13: new[] :: new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ },
- L53 C13: new[] :: new[] { 0, 1, 2, 0, 2, 3 });
- L74 C13: new CoACD.ConvexHullMesh :: new CoACD.ConvexHullMesh(
- L75 C17: new[] :: new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY },
- L76 C17: new[] :: new[] { 0, 1, 2 })
- L89 C20: new SceneNode :: var node = new SceneNode("ConvexHullTests");
- L99 C13: new Vector3 :: new Vector3(0f, 0f, 0f),
- L100 C13: new Vector3 :: new Vector3(1f, 0f, 0f),
- L101 C13: new Vector3 :: new Vector3(0f, 1f, 0f),
- L102 C13: new Vector3 :: new Vector3(0f, 1f, 0f),
- L103 C13: new Vector3 :: new Vector3(1f, 0f, 0f),
- L104 C13: new Vector3 :: new Vector3(1f, 1f, 0f));
- L106 C19: new SubMeshLOD :: var lod = new SubMeshLOD(material: null, mesh, maxVisibleDistance: 0f);
- L107 C23: new SubMesh :: var subMesh = new SubMesh(lod);
- L108 C27: new Model :: component.Model = new Model(subMesh);
- L113 C47: new() :: private readonly StubRunner _runner = new();


## XREngine.UnitTests/Rendering/GpuBvhAndIndirectIntegrationTests.cs
- L251 C17: new Vector4 :: new Vector4(0, 0, 0, 1), new Vector4(1, 1, 1, 1),
- L251 C42: new Vector4 :: new Vector4(0, 0, 0, 1), new Vector4(1, 1, 1, 1),
- L252 C17: new Vector4 :: new Vector4(2, 0, 0, 1), new Vector4(3, 1, 1, 1),
- L252 C42: new Vector4 :: new Vector4(2, 0, 0, 1), new Vector4(3, 1, 1, 1),
- L253 C17: new Vector4 :: new Vector4(0, 2, 0, 1), new Vector4(1, 3, 1, 1),
- L253 C42: new Vector4 :: new Vector4(0, 2, 0, 1), new Vector4(1, 3, 1, 1),
- L254 C17: new Vector4 :: new Vector4(-1, -1, -1, 1), new Vector4(0, 0, 0, 1),
- L254 C45: new Vector4 :: new Vector4(-1, -1, -1, 1), new Vector4(0, 0, 0, 1),
- L270 C31: new uint :: uint[] nodeData = new uint[nodeScalars];
- L273 C29: new uint :: uint[] ranges = new uint[nodeCount * 2u];
- L279 C31: new uint :: uint[] counters = new uint[nodeCount];
- L282 C34: new float :: float[] transforms = new float[numPrimitives * 16u];
- L541 C38: new float :: float[] culledCommands = new float[numCommands * COMMAND_FLOATS];
- L587 C36: new uint :: uint[] indirectDraws = new uint[numCommands * 5];
- L593 C34: new uint :: uint[] submeshData = new uint[numCommands * 4];
- L696 C38: new float :: float[] culledCommands = new float[numCommands * COMMAND_FLOATS];
- L720 C36: new uint :: uint[] indirectDraws = new uint[numCommands * 5];
- L725 C34: new uint :: uint[] submeshData = new uint[numCommands * 4];
- L820 C37: new float :: float[] inputCommands = new float[numCommands * COMMAND_FLOATS];
- L823 C55: new Vector3 :: SetupCullingTestCommand(inputCommands, 0, new Vector3(0, 0, -5), 1f, layerMask: 1, renderPass: 0);
- L826 C55: new Vector3 :: SetupCullingTestCommand(inputCommands, 1, new Vector3(2, 0, -5), 1f, layerMask: 1, renderPass: 0);
- L829 C55: new Vector3 :: SetupCullingTestCommand(inputCommands, 2, new Vector3(-100, 0, -5), 1f, layerMask: 1, renderPass: 0);
- L832 C55: new Vector3 :: SetupCullingTestCommand(inputCommands, 3, new Vector3(0, 0, 10), 1f, layerMask: 1, renderPass: 0);
- L838 C17: new Vector3 :: new Vector3(0, 0, 0),
- L839 C17: new Vector3 :: new Vector3(0, 0, -1),
- L859 C38: new float :: float[] outputCommands = new float[numCommands * COMMAND_FLOATS];
- L877 C28: new uint :: uint[] stats = new uint[20];
- L889 C37: new float :: float[] planeData = new float[24];
- L1025 C28: new uint :: uint[] stats = new uint[20];
- L1130 C38: new float :: float[] culledCommands = new float[numCommands * COMMAND_FLOATS];
- L1133 C49: new Vector3 :: SetupTestCommand(culledCommands, 0, new Vector3(0, 0, -1), 1f);
- L1134 C49: new Vector3 :: SetupTestCommand(culledCommands, 1, new Vector3(0, 0, -2), 1f);
- L1135 C49: new Vector3 :: SetupTestCommand(culledCommands, 2, new Vector3(0, 0, -3), 1f);
- L1154 C34: new uint :: uint[] keyIndexOut = new uint[numCommands * 2];
- L1229 C34: new float :: float[] inCommands = new float[numCommands * COMMAND_FLOATS];
- L1251 C35: new float :: float[] outCommands = new float[numCommands * COMMAND_FLOATS];
- L1261 C28: new uint :: uint[] debug = new uint[16];
- L1337 C37: new float :: float[] inputCommands = new float[numCommands * COMMAND_FLOATS];
- L1338 C55: new Vector3 :: SetupCullingTestCommand(inputCommands, 0, new Vector3(0, 0, -5), 1f, layerMask: 1, renderPass: 0);
- L1339 C55: new Vector3 :: SetupCullingTestCommand(inputCommands, 1, new Vector3(2, 0, -5), 1f, layerMask: 1, renderPass: 0);
- L1340 C55: new Vector3 :: SetupCullingTestCommand(inputCommands, 2, new Vector3(-100, 0, -5), 1f, layerMask: 1, renderPass: 0);
- L1341 C55: new Vector3 :: SetupCullingTestCommand(inputCommands, 3, new Vector3(0, 0, 10), 1f, layerMask: 1, renderPass: 0);
- L1344 C53: new Vector3 :: Matrix4x4 view = Matrix4x4.CreateLookAt(new Vector3(0, 0, 0), new Vector3(0, 0, -1), Vector3.UnitY);
- L1344 C75: new Vector3 :: Matrix4x4 view = Matrix4x4.CreateLookAt(new Vector3(0, 0, 0), new Vector3(0, 0, -1), Vector3.UnitY);
- L1359 C38: new float :: float[] culledCommands = new float[numCommands * COMMAND_FLOATS];
- L1374 C28: new uint :: uint[] stats = new uint[20];
- L1383 C37: new float :: float[] planeData = new float[24];
- L1440 C36: new uint :: uint[] indirectDraws = new uint[numCommands * 5];
- L1445 C34: new uint :: uint[] submeshData = new uint[numCommands * 4];
- L1545 C24: new Vector2D :: options.Size = new Vector2D<int>(Width, Height);
- L1547 C23: new GraphicsAPI :: options.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 6));
- L1547 C111: new APIVersion :: options.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 6));
- L1581 C19: new InvalidOperationException :: throw new InvalidOperationException($"Failed to compile compute shader:\n{infoLog}");
- L1598 C19: new InvalidOperationException :: throw new InvalidOperationException($"Failed to link compute program:\n{infoLog}");
- L1607 C28: new System.Text.RegularExpressions.Regex :: var includeRegex = new System.Text.RegularExpressions.Regex(
- L1650 C21: new System.Text.RegularExpressions.Regex :: var regex = new System.Text.RegularExpressions.Regex(
- L1694 C26: new float :: float[] planes = new float[24]; // 6 planes * 4 floats (a, b, c, d)
- L1830 C28: new Vector4 :: Vector4[] planes = new Vector4[6];
- L1833 C21: new Vector4 :: planes[0] = new Vector4(
- L1840 C21: new Vector4 :: planes[1] = new Vector4(
- L1847 C21: new Vector4 :: planes[2] = new Vector4(
- L1854 C21: new Vector4 :: planes[3] = new Vector4(
- L1861 C21: new Vector4 :: planes[4] = new Vector4(
- L1868 C21: new Vector4 :: planes[5] = new Vector4(


## XREngine.UnitTests/Rendering/GpuCullingPipelineTests.cs
- L51 C19: new FileNotFoundException :: throw new FileNotFoundException($"Shader file not found: {fullPath}");
- L161 C26: new Vector3 :: var normal = new Vector3(plane.X, plane.Y, plane.Z);
- L169 C22: new Vector4 :: var planes = new Vector4[6];
- L175 C21: new Vector4 :: planes[0] = new Vector4(m.M14 + m.M11, m.M24 + m.M21, m.M34 + m.M31, m.M44 + m.M41);
- L177 C21: new Vector4 :: planes[1] = new Vector4(m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41);
- L179 C21: new Vector4 :: planes[2] = new Vector4(m.M14 + m.M12, m.M24 + m.M22, m.M34 + m.M32, m.M44 + m.M42);
- L181 C21: new Vector4 :: planes[3] = new Vector4(m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42);
- L183 C21: new Vector4 :: planes[4] = new Vector4(m.M14 + m.M13, m.M24 + m.M23, m.M34 + m.M33, m.M44 + m.M43);
- L185 C21: new Vector4 :: planes[5] = new Vector4(m.M14 - m.M13, m.M24 - m.M23, m.M34 - m.M33, m.M44 - m.M43);
- L190 C26: new Vector3 :: var normal = new Vector3(planes[i].X, planes[i].Y, planes[i].Z);
- L209 C18: new Vector3 :: eye: new Vector3(0, 0, 5),
- L229 C18: new Vector3 :: eye: new Vector3(0, 0, 5),
- L241 C45: new Vector3 :: bool visible = TestSphereVisibility(new Vector3(0, 0, 10), 1f, planes);
- L249 C18: new Vector3 :: eye: new Vector3(0, 0, 5),
- L261 C45: new Vector3 :: bool visible = TestSphereVisibility(new Vector3(0, 0, -200), 1f, planes);
- L269 C18: new Vector3 :: eye: new Vector3(0, 0, 5),
- L281 C45: new Vector3 :: bool visible = TestSphereVisibility(new Vector3(10, 0, -5), 12f, planes);
- L298 C26: new Vector3 :: var normal = new Vector3(plane.X, plane.Y, plane.Z);
- L315 C18: new Vector3 :: eye: new Vector3(0, 0, 10),
- L325 C19: new AABB :: var box = new AABB(new Vector3(-1), new Vector3(1));
- L325 C28: new Vector3 :: var box = new AABB(new Vector3(-1), new Vector3(1));
- L325 C45: new Vector3 :: var box = new AABB(new Vector3(-1), new Vector3(1));
- L335 C18: new Vector3 :: eye: new Vector3(0, 0, 10),
- L345 C19: new AABB :: var box = new AABB(new Vector3(-100, -1, -1), new Vector3(-90, 1, 1));
- L345 C28: new Vector3 :: var box = new AABB(new Vector3(-100, -1, -1), new Vector3(-90, 1, 1));
- L345 C55: new Vector3 :: var box = new AABB(new Vector3(-100, -1, -1), new Vector3(-90, 1, 1));
- L355 C26: new Vector3 :: var normal = new Vector3(plane.X, plane.Y, plane.Z);
- L358 C27: new Vector3 :: var pVertex = new Vector3(
- L379 C24: new CullingCounters :: var counters = new CullingCounters();
- L391 C24: new CullingCounters :: var counters = new CullingCounters();
- L406 C24: new CullingCounters :: var counters = new CullingCounters
- L525 C19: new AABB :: var box = new AABB(new Vector3(-1, -1, -5), new Vector3(1, 1, -5));
- L525 C28: new Vector3 :: var box = new AABB(new Vector3(-1, -1, -5), new Vector3(1, 1, -5));
- L525 C53: new Vector3 :: var box = new AABB(new Vector3(-1, -1, -5), new Vector3(1, 1, -5));
- L528 C21: new Vector3 :: target: new Vector3(0, 0, -1),
- L545 C23: new[] :: var corners = new[]
- L547 C13: new Vector3 :: new Vector3(box.Min.X, box.Min.Y, box.Min.Z),
- L548 C13: new Vector3 :: new Vector3(box.Max.X, box.Min.Y, box.Min.Z),
- L549 C13: new Vector3 :: new Vector3(box.Min.X, box.Max.Y, box.Min.Z),
- L550 C13: new Vector3 :: new Vector3(box.Max.X, box.Max.Y, box.Min.Z),
- L551 C13: new Vector3 :: new Vector3(box.Min.X, box.Min.Y, box.Max.Z),
- L552 C13: new Vector3 :: new Vector3(box.Max.X, box.Min.Y, box.Max.Z),
- L553 C13: new Vector3 :: new Vector3(box.Min.X, box.Max.Y, box.Max.Z),
- L554 C13: new Vector3 :: new Vector3(box.Max.X, box.Max.Y, box.Max.Z)
- L557 C25: new Vector2 :: var screenMin = new Vector2(float.MaxValue);
- L558 C25: new Vector2 :: var screenMax = new Vector2(float.MinValue);
- L562 C42: new Vector4 :: var clip = Vector4.Transform(new Vector4(corner, 1), viewProjection);
- L565 C23: new Vector2 :: var ndc = new Vector2(clip.X / clip.W, clip.Y / clip.W);
- L626 C23: new uint :: var indices = new uint[10];
- L679 C22: new[] :: var stages = new[]
- L706 C23: new float :: var centerX = new float[commandCount];
- L707 C23: new float :: var centerY = new float[commandCount];
- L708 C23: new float :: var centerZ = new float[commandCount];
- L709 C22: new float :: var radius = new float[commandCount];
- L733 C20: new Vector4 :: var row0 = new Vector4[commandCount];
- L734 C20: new Vector4 :: var row1 = new Vector4[commandCount];
- L735 C20: new Vector4 :: var row2 = new Vector4[commandCount];
- L736 C20: new Vector4 :: var row3 = new Vector4[commandCount];
- L739 C19: new Vector4 :: row0[0] = new Vector4(1, 0, 0, 0);
- L740 C19: new Vector4 :: row1[0] = new Vector4(0, 1, 0, 0);
- L741 C19: new Vector4 :: row2[0] = new Vector4(0, 0, 1, 0);
- L742 C19: new Vector4 :: row3[0] = new Vector4(0, 0, 0, 1);


## XREngine.UnitTests/Rendering/GpuIndirectRenderDispatchTests.cs
- L52 C19: new FileNotFoundException :: throw new FileNotFoundException($"Shader file not found: {fullPath}");
- L166 C19: new DrawElementsIndirectCommand :: var cmd = new DrawElementsIndirectCommand
- L182 C19: new DrawElementsIndirectCommand :: var cmd = new DrawElementsIndirectCommand();
- L194 C19: new DrawElementsIndirectCommand :: var cmd = new DrawElementsIndirectCommand
- L214 C19: new DrawElementsIndirectCommand :: var cmd = new DrawElementsIndirectCommand
- L399 C25: new uint :: uint[] buffer = new uint[1] { expectedDrawCount };
- L464 C27: new uint :: var materialIds = new uint[] { 1, 1, 1, 1, 1 };
- L478 C27: new uint :: var materialIds = new uint[] { 1, 1, 2, 2, 2, 3 };
- L508 C27: new uint :: var materialIds = new uint[] { 1, 2, 1, 2, 1, 2 };
- L528 C23: new List :: var batches = new List<DrawBatch>();
- L537 C29: new DrawBatch :: batches.Add(new DrawBatch
- L555 C21: new DrawBatch :: batches.Add(new DrawBatch
- L679 C24: new GPURenderPassCollection.IndirectDebugSettings :: var settings = new GPURenderPassCollection.IndirectDebugSettings();
- L689 C24: new GPURenderPassCollection.IndirectDebugSettings :: var settings = new GPURenderPassCollection.IndirectDebugSettings
- L700 C24: new GPURenderPassCollection.IndirectDebugSettings :: var settings = new GPURenderPassCollection.IndirectDebugSettings
- L715 C19: new GPUIndirectRenderCommand :: var cmd = new GPUIndirectRenderCommand
- L720 C24: new Vector3 :: var localPos = new Vector3(1, 2, 3);
- L731 C19: new GPUIndirectRenderCommand :: var cmd = new GPUIndirectRenderCommand
- L736 C24: new Vector3 :: var localPos = new Vector3(1, 2, 3);
- L747 C19: new GPUIndirectRenderCommand :: var cmd = new GPUIndirectRenderCommand
- L752 C24: new Vector3 :: var localPos = new Vector3(1, 2, 3);
- L766 C19: new GPUIndirectRenderCommand :: var cmd = new GPUIndirectRenderCommand
- L773 C24: new Vector3 :: var localPos = new Vector3(0, 0, 0);
- L791 C30: new Vector3 :: var objectPosition = new Vector3(0, 0, -50); // 50 units away
- L804 C30: new Vector3 :: var objectPosition = new Vector3(0, 0, -150); // 150 units away
- L820 C30: new Vector3 :: var objectPosition = new Vector3(0, 0, -50);


## XREngine.UnitTests/Rendering/GpuSceneBvhTests.cs
- L53 C19: new FileNotFoundException :: throw new FileNotFoundException($"Shader file not found: {fullPath}");
- L135 C30: new Vector3 :: var halfExtent = new Vector3(radius);
- L136 C34: new AABB :: LocalCullingVolume = new AABB(center - halfExtent, center + halfExtent);
- L244 C24: new GpuBvhNode :: var leafNode = new GpuBvhNode
- L264 C22: new AABB :: var bounds = new AABB(Vector3.Zero, new Vector3(100f));
- L264 C45: new Vector3 :: var bounds = new AABB(Vector3.Zero, new Vector3(100f));
- L265 C22: new OctreeGPU :: var octree = new OctreeGPU<MockOctreeItem>(bounds);
- L266 C20: new MockOctreeItem :: var item = new MockOctreeItem(Vector3.Zero, 1f);
- L279 C22: new AABB :: var bounds = new AABB(Vector3.Zero, new Vector3(100f));
- L279 C45: new Vector3 :: var bounds = new AABB(Vector3.Zero, new Vector3(100f));
- L280 C22: new OctreeGPU :: var octree = new OctreeGPU<MockOctreeItem>(bounds);
- L281 C21: new[] :: var items = new[]
- L283 C13: new MockOctreeItem :: new MockOctreeItem(new Vector3(-10, 0, 0), 1f),
- L283 C32: new Vector3 :: new MockOctreeItem(new Vector3(-10, 0, 0), 1f),
- L284 C13: new MockOctreeItem :: new MockOctreeItem(new Vector3(0, 0, 0), 1f),
- L284 C32: new Vector3 :: new MockOctreeItem(new Vector3(0, 0, 0), 1f),
- L285 C13: new MockOctreeItem :: new MockOctreeItem(new Vector3(10, 0, 0), 1f),
- L285 C32: new Vector3 :: new MockOctreeItem(new Vector3(10, 0, 0), 1f),
- L286 C13: new MockOctreeItem :: new MockOctreeItem(new Vector3(0, 10, 0), 1f),
- L286 C32: new Vector3 :: new MockOctreeItem(new Vector3(0, 10, 0), 1f),
- L287 C13: new MockOctreeItem :: new MockOctreeItem(new Vector3(0, -10, 0), 1f)
- L287 C32: new Vector3 :: new MockOctreeItem(new Vector3(0, -10, 0), 1f)
- L301 C22: new AABB :: var bounds = new AABB(Vector3.Zero, new Vector3(100f));
- L301 C45: new Vector3 :: var bounds = new AABB(Vector3.Zero, new Vector3(100f));
- L302 C22: new OctreeGPU :: var octree = new OctreeGPU<MockOctreeItem>(bounds);
- L303 C20: new MockOctreeItem :: var item = new MockOctreeItem(Vector3.Zero, 1f);
- L317 C29: new AABB :: var initialBounds = new AABB(Vector3.Zero, new Vector3(50f));
- L317 C52: new Vector3 :: var initialBounds = new AABB(Vector3.Zero, new Vector3(50f));
- L318 C22: new OctreeGPU :: var octree = new OctreeGPU<MockOctreeItem>(initialBounds);
- L321 C25: new AABB :: var newBounds = new AABB(new Vector3(-100f), new Vector3(100f));
- L321 C34: new Vector3 :: var newBounds = new AABB(new Vector3(-100f), new Vector3(100f));
- L321 C54: new Vector3 :: var newBounds = new AABB(new Vector3(-100f), new Vector3(100f));
- L333 C22: new AABB :: var bounds = new AABB(Vector3.Zero, new Vector3(100f));
- L333 C45: new Vector3 :: var bounds = new AABB(Vector3.Zero, new Vector3(100f));
- L334 C22: new OctreeGPU :: var octree = new OctreeGPU<MockOctreeItem>(bounds);
- L348 C22: new AABB :: var bounds = new AABB(Vector3.Zero, new Vector3(100f));
- L348 C45: new Vector3 :: var bounds = new AABB(Vector3.Zero, new Vector3(100f));
- L349 C22: new OctreeGPU :: var octree = new OctreeGPU<MockOctreeItem>(bounds);
- L362 C22: new AABB :: var bounds = new AABB(Vector3.Zero, new Vector3(100f));
- L362 C45: new Vector3 :: var bounds = new AABB(Vector3.Zero, new Vector3(100f));
- L363 C22: new OctreeGPU :: var octree = new OctreeGPU<MockOctreeItem>(bounds);
- L376 C22: new AABB :: var bounds = new AABB(Vector3.Zero, new Vector3(100f));
- L376 C45: new Vector3 :: var bounds = new AABB(Vector3.Zero, new Vector3(100f));
- L377 C22: new OctreeGPU :: var octree = new OctreeGPU<MockOctreeItem>(bounds);
- L390 C22: new AABB :: var bounds = new AABB(Vector3.Zero, new Vector3(100f));
- L390 C45: new Vector3 :: var bounds = new AABB(Vector3.Zero, new Vector3(100f));
- L391 C22: new OctreeGPU :: var octree = new OctreeGPU<MockOctreeItem>(bounds);
- L394 C20: new MockOctreeItem :: var item = new MockOctreeItem(Vector3.Zero, 1f);
- L408 C22: new AABB :: var bounds = new AABB(Vector3.Zero, new Vector3(100f));
- L408 C45: new Vector3 :: var bounds = new AABB(Vector3.Zero, new Vector3(100f));
- L409 C22: new OctreeGPU :: var octree = new OctreeGPU<MockOctreeItem>(bounds);
- L425 C22: new OctreeGPU :: var octree = new OctreeGPU<MockOctreeItem>();
- L442 C22: new OctreeGPU :: var octree = new OctreeGPU<MockOctreeItem>();
- L459 C22: new OctreeGPU :: var octree = new OctreeGPU<MockOctreeItem>();
- L475 C22: new OctreeGPU :: var octree = new OctreeGPU<MockOctreeItem>();
- L489 C22: new OctreeGPU :: var octree = new OctreeGPU<MockOctreeItem>();
- L509 C25: new[] :: var positions = new[]
- L511 C13: new Vector3 :: new Vector3(0, 0, 0),
- L512 C13: new Vector3 :: new Vector3(1, 0, 0),
- L513 C13: new Vector3 :: new Vector3(0, 1, 0),
- L514 C13: new Vector3 :: new Vector3(0, 0, 1),
- L515 C13: new Vector3 :: new Vector3(1, 1, 1)
- L518 C27: new uint :: var mortonCodes = new uint[positions.Length];
- L521 C78: new Vector3 :: mortonCodes[i] = CalculateMortonCode(positions[i], Vector3.Zero, new Vector3(2f));
- L525 C27: new HashSet :: var uniqueCodes = new HashSet<uint>(mortonCodes);
- L533 C20: new Vector3 :: var pos1 = new Vector3(0.1f, 0.1f, 0.1f);
- L534 C20: new Vector3 :: var pos2 = new Vector3(0.11f, 0.11f, 0.11f);
- L535 C20: new Vector3 :: var pos3 = new Vector3(0.9f, 0.9f, 0.9f);
- L538 C24: new Vector3 :: var sceneMax = new Vector3(1f);
- L625 C17: new AABB :: var a = new AABB(new Vector3(-1, -1, -1), new Vector3(1, 1, 1));
- L625 C26: new Vector3 :: var a = new AABB(new Vector3(-1, -1, -1), new Vector3(1, 1, 1));
- L625 C51: new Vector3 :: var a = new AABB(new Vector3(-1, -1, -1), new Vector3(1, 1, 1));
- L626 C17: new AABB :: var b = new AABB(new Vector3(0, 0, 0), new Vector3(2, 2, 2));
- L626 C26: new Vector3 :: var b = new AABB(new Vector3(0, 0, 0), new Vector3(2, 2, 2));
- L626 C48: new Vector3 :: var b = new AABB(new Vector3(0, 0, 0), new Vector3(2, 2, 2));
- L642 C24: new AABB :: var unitCube = new AABB(Vector3.Zero, Vector3.One);
- L647 C21: new AABB :: var cube2 = new AABB(Vector3.Zero, new Vector3(2f));
- L647 C44: new Vector3 :: var cube2 = new AABB(Vector3.Zero, new Vector3(2f));
- L661 C19: new AABB :: var box = new AABB(new Vector3(-1, -1, -1), new Vector3(1, 1, 1));
- L661 C28: new Vector3 :: var box = new AABB(new Vector3(-1, -1, -1), new Vector3(1, 1, 1));
- L661 C53: new Vector3 :: var box = new AABB(new Vector3(-1, -1, -1), new Vector3(1, 1, 1));
- L664 C27: new Vector3 :: box.ContainsPoint(new Vector3(0.5f, 0.5f, 0.5f)).ShouldBeTrue();
- L665 C27: new Vector3 :: box.ContainsPoint(new Vector3(2f, 0f, 0f)).ShouldBeFalse();
- L671 C17: new AABB :: var a = new AABB(new Vector3(-1, -1, -1), new Vector3(1, 1, 1));
- L671 C26: new Vector3 :: var a = new AABB(new Vector3(-1, -1, -1), new Vector3(1, 1, 1));
- L671 C51: new Vector3 :: var a = new AABB(new Vector3(-1, -1, -1), new Vector3(1, 1, 1));
- L672 C17: new AABB :: var b = new AABB(new Vector3(0, 0, 0), new Vector3(2, 2, 2));
- L672 C26: new Vector3 :: var b = new AABB(new Vector3(0, 0, 0), new Vector3(2, 2, 2));
- L672 C48: new Vector3 :: var b = new AABB(new Vector3(0, 0, 0), new Vector3(2, 2, 2));
- L673 C17: new AABB :: var c = new AABB(new Vector3(5, 5, 5), new Vector3(6, 6, 6));
- L673 C26: new Vector3 :: var c = new AABB(new Vector3(5, 5, 5), new Vector3(6, 6, 6));
- L673 C48: new Vector3 :: var c = new AABB(new Vector3(5, 5, 5), new Vector3(6, 6, 6));
- L696 C19: new GPUIndirectRenderCommand :: var cmd = new GPUIndirectRenderCommand();
- L697 C22: new Vector3 :: var center = new Vector3(1f, 2f, 3f);
- L711 C19: new GPUIndirectRenderCommand :: var cmd = new GPUIndirectRenderCommand
- L724 C19: new GPUIndirectRenderCommand :: var cmd = new GPUIndirectRenderCommand();
- L748 C28: new Vector3 :: var sphereCenter = new Vector3(0, 0, -10); // In front of camera
- L765 C28: new Vector3 :: var sphereCenter = new Vector3(0, 0, 5); // Behind camera (positive Z)
- L782 C28: new Vector3 :: var sphereCenter = new Vector3(0, 0, -200); // Way past far plane
- L800 C28: new Vector3 :: var sphereCenter = new Vector3(10, 0, -10);
- L815 C22: new Vector4 :: var planes = new Vector4[6];
- L818 C21: new Vector4 :: planes[0] = new Vector4(0, 0, -1, -nearZ); // points into frustum
- L821 C21: new Vector4 :: planes[1] = new Vector4(0, 0, 1, farZ);
- L832 C21: new Vector4 :: planes[2] = new Vector4(cosAngle, 0, -sinAngle, 0);
- L834 C21: new Vector4 :: planes[3] = new Vector4(-cosAngle, 0, -sinAngle, 0);
- L836 C21: new Vector4 :: planes[4] = new Vector4(0, -cosAngle, -sinAngle, 0);
- L838 C21: new Vector4 :: planes[5] = new Vector4(0, cosAngle, -sinAngle, 0);
- L850 C26: new Vector3 :: var normal = new Vector3(plane.X, plane.Y, plane.Z);
- L868 C22: new AABB :: var bounds = new AABB(Vector3.Zero, new Vector3(100f));
- L868 C45: new Vector3 :: var bounds = new AABB(Vector3.Zero, new Vector3(100f));
- L869 C22: new OctreeGPU :: var octree = new OctreeGPU<MockOctreeItem>(bounds);
- L870 C20: new MockOctreeItem :: var item = new MockOctreeItem(Vector3.Zero, 1f);
- L884 C22: new AABB :: var bounds = new AABB(Vector3.Zero, new Vector3(100f));
- L884 C45: new Vector3 :: var bounds = new AABB(Vector3.Zero, new Vector3(100f));
- L885 C22: new OctreeGPU :: var octree = new OctreeGPU<MockOctreeItem>(bounds);


## XREngine.UnitTests/Rendering/IndirectMultiDrawTests.cs
- L90 C24: new Vector2D :: options.Size = new Vector2D<int>(Width, Height);
- L92 C23: new GraphicsAPI :: options.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 6));
- L92 C111: new APIVersion :: options.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 6));
- L224 C24: new Vector2D :: options.Size = new Vector2D<int>(Width, Height);
- L226 C23: new GraphicsAPI :: options.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 6));
- L226 C111: new APIVersion :: options.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 6));
- L368 C24: new Vector2D :: options.Size = new Vector2D<int>(Width, Height);
- L370 C23: new GraphicsAPI :: options.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 6));
- L370 C111: new APIVersion :: options.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 6));
- L554 C24: new float :: float[] data = new float[16 * 6];
- L596 C26: new uint :: uint[] indices = new uint[8 * indicesPerCube];
- L611 C13: new() :: new()
- L619 C13: new() :: new()
- L632 C43: new Vector3 :: var view = Matrix4x4.CreateLookAt(new Vector3(0f, 0f, 4f), Vector3.Zero, Vector3.UnitY);
- L665 C42: new Vector4 :: Vector4 clip = Vector4.Transform(new Vector4(position, 1f), mvp);
- L694 C19: new InvalidOperationException :: throw new InvalidOperationException($"Failed to compile {type}: {infoLog}");
- L712 C19: new InvalidOperationException :: throw new InvalidOperationException($"Failed to link program: {infoLog}");
- L752 C24: new float :: float[] data = new float[8 * 8 * 6]; // 8 cubes, 8 vertices, 6 floats per vertex
- L788 C26: new uint :: uint[] indices = new uint[8 * 36]; // 8 cubes, 36 indices each
- L802 C50: new DrawElementsIndirectCommand :: DrawElementsIndirectCommand[] commands = new DrawElementsIndirectCommand[4];
- L809 C34: new DrawElementsIndirectCommand :: commands[batchIdx] = new DrawElementsIndirectCommand
- L825 C43: new Vector3 :: var view = Matrix4x4.CreateLookAt(new Vector3(0f, 0f, 6f), Vector3.Zero, Vector3.UnitY);


## XREngine.UnitTests/Rendering/LightProbeOctaTests.cs
- L58 C24: new Vector2D :: options.Size = new Vector2D<int>(Width, Height);
- L60 C23: new GraphicsAPI :: options.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 6));
- L60 C111: new APIVersion :: options.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 6));
- L107 C18: new Vector2D :: (new Vector2D<int>(Width / 2, Height / 2), new Vector3(0.0f, 0.0f, 1.0f), "+Y center"),
- L107 C60: new Vector3 :: (new Vector2D<int>(Width / 2, Height / 2), new Vector3(0.0f, 0.0f, 1.0f), "+Y center"),
- L108 C18: new Vector2D :: (new Vector2D<int>((int)(Width * 0.9f), Height / 2), new Vector3(1.0f, 0.0f, 0.0f), "+X right"),
- L108 C70: new Vector3 :: (new Vector2D<int>((int)(Width * 0.9f), Height / 2), new Vector3(1.0f, 0.0f, 0.0f), "+X right"),
- L109 C18: new Vector2D :: (new Vector2D<int>((int)(Width * 0.1f), Height / 2), new Vector3(0.0f, 1.0f, 0.0f), "-X left"),
- L109 C70: new Vector3 :: (new Vector2D<int>((int)(Width * 0.1f), Height / 2), new Vector3(0.0f, 1.0f, 0.0f), "-X left"),
- L110 C18: new Vector2D :: (new Vector2D<int>(Width / 2, (int)(Height * 0.9f)), new Vector3(1.0f, 0.0f, 1.0f), "+Z top"),
- L110 C70: new Vector3 :: (new Vector2D<int>(Width / 2, (int)(Height * 0.9f)), new Vector3(1.0f, 0.0f, 1.0f), "+Z top"),
- L111 C18: new Vector2D :: (new Vector2D<int>(Width / 2, (int)(Height * 0.1f)), new Vector3(0.0f, 1.0f, 1.0f), "-Z bottom"),
- L111 C70: new Vector3 :: (new Vector2D<int>(Width / 2, (int)(Height * 0.1f)), new Vector3(0.0f, 1.0f, 1.0f), "-Z bottom"),
- L112 C18: new Vector2D :: (new Vector2D<int>((int)(Width * 0.15f), (int)(Height * 0.15f)), new Vector3(1.0f, 1.0f, 0.0f), "-Y corner"),
- L112 C82: new Vector3 :: (new Vector2D<int>((int)(Width * 0.15f), (int)(Height * 0.15f)), new Vector3(1.0f, 1.0f, 0.0f), "-Y corner"),
- L143 C24: new Vector2D :: options.Size = new Vector2D<int>(32, 32); // Irradiance is usually lower resolution
- L145 C23: new GraphicsAPI :: options.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 6));
- L145 C111: new APIVersion :: options.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 6));
- L158 C65: new Vector4 :: uint octaEnvTex = CreateFilledTexture2D(gl, 64, 64, new Vector4(0.5f, 0.3f, 0.7f, 1.0f));
- L218 C13: new Vector4 :: new Vector4(1.0f, 0.0f, 0.0f, 1.0f), // +X Red
- L219 C13: new Vector4 :: new Vector4(0.0f, 1.0f, 0.0f, 1.0f), // -X Green
- L220 C13: new Vector4 :: new Vector4(0.0f, 0.0f, 1.0f, 1.0f), // +Y Blue
- L221 C13: new Vector4 :: new Vector4(1.0f, 1.0f, 0.0f, 1.0f), // -Y Yellow
- L222 C13: new Vector4 :: new Vector4(1.0f, 0.0f, 1.0f, 1.0f), // +Z Magenta
- L223 C13: new Vector4 :: new Vector4(0.0f, 1.0f, 1.0f, 1.0f), // -Z Cyan
- L228 C28: new float :: float[] data = new float[64 * 64 * 4];
- L288 C24: new float :: float[] data = new float[width * height * 4];
- L513 C19: new InvalidOperationException :: throw new InvalidOperationException($"Failed to compile {type}: {infoLog}");
- L531 C19: new InvalidOperationException :: throw new InvalidOperationException($"Failed to link program: {infoLog}");
- L549 C29: new Vector3 :: var expectedBytes = new Vector3(expected.X, expected.Y, expected.Z) * 255.0f;


## XREngine.UnitTests/Rendering/MortonCodeAndSortingTests.cs
- L43 C19: new FileNotFoundException :: throw new FileNotFoundException($"Shader file not found: {fullPath}");
- L136 C25: new Vector3 :: var positionX = new Vector3(1f, 0f, 0f);
- L151 C25: new Vector3 :: var positionY = new Vector3(0f, 1f, 0f);
- L165 C25: new Vector3 :: var positionZ = new Vector3(0f, 0f, 1f);
- L179 C20: new Vector3 :: var pos1 = new Vector3(0.5f, 0.5f, 0.5f);
- L180 C20: new Vector3 :: var pos2 = new Vector3(0.5f, 0.5f, 0.6f);  // Only Z differs slightly
- L181 C20: new Vector3 :: var pos3 = new Vector3(0.1f, 0.1f, 0.1f);  // Different octant entirely
- L207 C24: new Vector3 :: var sceneMin = new Vector3(-100, -100, -100);
- L208 C24: new Vector3 :: var sceneMax = new Vector3(100, 100, 100);
- L225 C26: new Vector3 :: var outsidePos = new Vector3(2f, 2f, 2f);
- L270 C21: new uint :: var codes = new uint[] { 100, 50, 200, 25, 150, 75 };
- L271 C23: new uint :: var indices = new uint[] { 0, 1, 2, 3, 4, 5 };
- L285 C29: new uint :: var originalCodes = new uint[] { 100, 50, 200, 25, 150, 75 };
- L287 C23: new uint :: var indices = new uint[] { 0, 1, 2, 3, 4, 5 };
- L301 C21: new uint :: var codes = new uint[] { 10, 20, 30, 40, 50 };
- L303 C23: new uint :: var indices = new uint[] { 0, 1, 2, 3, 4 };
- L313 C21: new uint :: var codes = new uint[] { 50, 40, 30, 20, 10 };
- L314 C23: new uint :: var indices = new uint[] { 0, 1, 2, 3, 4 };
- L318 C24: new uint :: codes.ShouldBe(new uint[] { 10, 20, 30, 40, 50 });
- L324 C21: new uint :: var codes = new uint[] { 50, 50, 50, 50 };
- L325 C23: new uint :: var indices = new uint[] { 0, 1, 2, 3 };
- L339 C21: new uint :: var codes = new uint[] { 42 };
- L340 C23: new uint :: var indices = new uint[] { 0 };
- L361 C21: new uint :: var codes = new uint[count];
- L362 C23: new uint :: var indices = new uint[count];
- L363 C22: new Random :: var random = new Random(12345);
- L418 C20: new uint :: var data = new uint[] { 8, 4, 2, 6, 1, 5, 3, 7 };
- L422 C23: new uint :: data.ShouldBe(new uint[] { 1, 2, 3, 4, 5, 6, 7, 8 });
- L428 C20: new uint :: var data = new uint[] { 2, 1 };
- L432 C23: new uint :: data.ShouldBe(new uint[] { 1, 2 });
- L495 C20: new uint :: var data = new uint[] { 0x10, 0x20, 0x10, 0x30, 0x10 };
- L497 C25: new uint :: var histogram = new uint[256];
- L515 C25: new uint :: var histogram = new uint[] { 3, 2, 1, 4 };
- L517 C25: new uint :: var prefixSum = new uint[histogram.Length];
- L535 C20: new uint :: var keys = new uint[] { 2, 0, 1, 0 };
- L536 C22: new uint :: var output = new uint[4];
- L537 C23: new uint :: var offsets = new uint[] { 0, 2, 3 }; // prefix sums for buckets 0, 1, 2
- L544 C25: new uint :: output.ShouldBe(new uint[] { 0, 0, 1, 2 });
- L642 C27: new uint :: var sortedCodes = new uint[]
- L687 C21: new Vector3Int :: var cellA = new Vector3Int(0, 0, 0);
- L688 C21: new Vector3Int :: var cellB = new Vector3Int(1, 0, 0);
- L699 C21: new Vector3Int :: var cellA = new Vector3Int(5, 10, 15);
- L700 C21: new Vector3Int :: var cellB = new Vector3Int(5, 10, 15);
- L711 C20: new Vector3Int :: var cell = new Vector3Int(-5, -10, -15);


## XREngine.UnitTests/Rendering/OctreeGpuOverflowTests.cs
- L25 C22: new OctreeGPU :: var octree = new OctreeGPU<DummyItem>();
- L37 C22: new OctreeGPU :: var octree = new OctreeGPU<DummyItem>();
- L49 C22: new OctreeGPU :: var octree = new OctreeGPU<DummyItem>();


## XREngine.UnitTests/Rendering/SurfelGiComputeIntegrationTests.cs
- L50 C24: new Vector2D :: options.Size = new Vector2D<int>(Width, Height);
- L52 C23: new GraphicsAPI :: options.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 6));
- L52 C111: new APIVersion :: options.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 6));
- L86 C19: new InvalidOperationException :: throw new InvalidOperationException($"Failed to compile compute shader:\n{infoLog}");
- L103 C19: new InvalidOperationException :: throw new InvalidOperationException($"Failed to link compute program:\n{infoLog}");
- L207 C20: new float :: var data = new float[width * height * 4];
- L235 C20: new float :: var data = new float[width * height];
- L256 C20: new uint :: var data = new uint[width * height];
- L468 C24: new uint :: var seed = new uint[cellCount];
- L524 C42: new SurfelGpu :: UploadSsbo(gl, surfelBuffer, new SurfelGpu[(int)maxSurfels]);
- L527 C40: new uint :: UploadSsbo(gl, gridCounts, new uint[cellCount]);
- L528 C28: new uint :: var sentinel = new uint[cellCount * maxPerCell];
- L534 C46: new SurfelGpu :: WriteSurfel(gl, surfelBuffer, 0, new SurfelGpu
- L536 C29: new Vector4 :: PosRadius = new Vector4(0.1f, 0.1f, 0.1f, 0.1f),
- L537 C26: new Vector4 :: Normal = new Vector4(0, 0, 1, 0),
- L538 C26: new Vector4 :: Albedo = new Vector4(1, 0, 0, 1),
- L544 C46: new SurfelGpu :: WriteSurfel(gl, surfelBuffer, 1, new SurfelGpu
- L546 C29: new Vector4 :: PosRadius = new Vector4(1.1f, 0.1f, 0.1f, 0.1f),
- L547 C26: new Vector4 :: Normal = new Vector4(0, 0, 1, 0),
- L548 C26: new Vector4 :: Albedo = new Vector4(0, 1, 0, 1),
- L555 C46: new SurfelGpu :: WriteSurfel(gl, surfelBuffer, 2, new SurfelGpu
- L557 C29: new Vector4 :: PosRadius = new Vector4(0.2f, 0.2f, 0.2f, 0.1f),
- L558 C26: new Vector4 :: Normal = new Vector4(0, 0, 1, 0),
- L559 C26: new Vector4 :: Albedo = new Vector4(0, 0, 1, 1),
- L566 C46: new SurfelGpu :: WriteSurfel(gl, surfelBuffer, 3, new SurfelGpu
- L568 C29: new Vector4 :: PosRadius = new Vector4(1000f, 0, 0, 0.1f),
- L569 C26: new Vector4 :: Normal = new Vector4(0, 0, 1, 0),
- L570 C26: new Vector4 :: Albedo = new Vector4(1, 1, 1, 1),
- L645 C42: new SurfelGpu :: UploadSsbo(gl, surfelBuffer, new SurfelGpu[(int)maxSurfels]);
- L646 C40: new uint :: UploadSsbo(gl, gridCounts, new uint[cellCount]);
- L648 C28: new uint :: var sentinel = new uint[cellCount * maxPerCell + extraSentinel];
- L656 C50: new SurfelGpu :: WriteSurfel(gl, surfelBuffer, i, new SurfelGpu
- L658 C33: new Vector4 :: PosRadius = new Vector4(1.0f + i * 0.01f, 1.0f, 1.0f, 0.1f),
- L659 C30: new Vector4 :: Normal = new Vector4(0, 0, 1, 0),
- L660 C30: new Vector4 :: Albedo = new Vector4(1, 1, 1, 1),
- L737 C42: new SurfelGpu :: UploadSsbo(gl, surfelBuffer, new SurfelGpu[(int)maxSurfels]);
- L738 C40: new uint :: UploadSsbo(gl, gridCounts, new uint[cellCount]);
- L739 C41: new uint :: UploadSsbo(gl, gridIndices, new uint[cellCount * maxPerCell]);
- L743 C26: new float :: var culled = new float[48];
- L751 C46: new SurfelGpu :: WriteSurfel(gl, surfelBuffer, 0, new SurfelGpu
- L753 C29: new Vector4 :: PosRadius = new Vector4(0.1f, 0.1f, 0.1f, 0.1f),
- L754 C26: new Vector4 :: Normal = new Vector4(0, 0, 1, 0),
- L755 C26: new Vector4 :: Albedo = new Vector4(1, 0, 0, 1),
- L836 C50: new SurfelGpu :: WriteSurfel(gl, surfelBuffer, oldId, new SurfelGpu
- L838 C29: new Vector4 :: PosRadius = new Vector4(0, 0, 0, 0.1f),
- L839 C26: new Vector4 :: Normal = new Vector4(0, 0, 1, 0),
- L840 C26: new Vector4 :: Albedo = new Vector4(1, 1, 1, 1),
- L914 C40: new uint :: UploadSsbo(gl, gridCounts, new uint[cellCount]);
- L915 C41: new uint :: UploadSsbo(gl, gridIndices, new uint[cellCount * maxPerCell]);
- L925 C65: new Vector4 :: uint normalTex = CreateFloatTexture2D(gl, res, res, new Vector4(0.5f, 0.5f, 1.0f, 1.0f));
- L926 C65: new Vector4 :: uint albedoTex = CreateFloatTexture2D(gl, res, res, new Vector4(1.0f, 0.0f, 0.0f, 1.0f));
- L945 C56: new Vector3 :: SetUniform(gl, spawnProgram, "gridOrigin", new Vector3(-10f, -10f, -10f));
- L1036 C42: new SurfelGpu :: UploadSsbo(gl, surfelBuffer, new SurfelGpu[(int)maxSurfels]);
- L1037 C40: new uint :: UploadSsbo(gl, gridCounts, new uint[cellCount]);
- L1038 C41: new uint :: UploadSsbo(gl, gridIndices, new uint[cellCount * maxPerCell]);
- L1042 C26: new float :: var culled = new float[48];
- L1057 C65: new Vector4 :: uint normalTex = CreateFloatTexture2D(gl, res, res, new Vector4(0.5f, 0.5f, 1.0f, 1.0f));
- L1058 C65: new Vector4 :: uint albedoTex = CreateFloatTexture2D(gl, res, res, new Vector4(1.0f, 0.0f, 0.0f, 1.0f));
- L1067 C30: new Vector3 :: var gridOrigin = new Vector3(-2f, -2f, -2f);
- L1097 C28: new Vector3 :: var worldPos = new Vector3(uvx * 2f - 1f, uvy * 2f - 1f, 0f);
- L1098 C44: new Vector3 :: var expectedLocal = worldPos - new Vector3(tx, 0f, 0f);
- L1183 C42: new SurfelGpu :: UploadSsbo(gl, surfelBuffer, new SurfelGpu[(int)maxSurfels]);
- L1184 C40: new uint :: UploadSsbo(gl, gridCounts, new uint[cellCount]);
- L1185 C41: new uint :: UploadSsbo(gl, gridIndices, new uint[cellCount * maxPerCell]);
- L1186 C45: new uint :: UploadSsbo(gl, freeStackBuffer, new uint[maxSurfels]);
- L1193 C65: new Vector4 :: uint normalTex = CreateFloatTexture2D(gl, res, res, new Vector4(0.5f, 0.5f, 1.0f, 1.0f));
- L1194 C65: new Vector4 :: uint albedoTex = CreateFloatTexture2D(gl, res, res, new Vector4(1.0f, 0.0f, 0.0f, 1.0f));
- L1211 C56: new Vector3 :: SetUniform(gl, spawnProgram, "gridOrigin", new Vector3(-10f, -10f, -10f));
- L1280 C40: new uint :: UploadSsbo(gl, gridCounts, new uint[cellCount]);
- L1281 C41: new uint :: UploadSsbo(gl, gridIndices, new uint[cellCount * maxPerCell]);
- L1295 C53: new SurfelGpu :: WriteSurfel(gl, surfelBuffer, surfelId, new SurfelGpu
- L1297 C29: new Vector4 :: PosRadius = new Vector4(0f, 0f, 1f, 0.5f),
- L1298 C26: new Vector4 :: Normal = new Vector4(0f, 0f, -1f, 0f),
- L1299 C26: new Vector4 :: Albedo = new Vector4(1f, 0f, 0f, 1f),
- L1308 C65: new Vector4 :: uint normalTex = CreateFloatTexture2D(gl, res, res, new Vector4(0.5f, 0.5f, 1.0f, 1.0f));
- L1309 C65: new Vector4 :: uint albedoTex = CreateFloatTexture2D(gl, res, res, new Vector4(0.0f, 0.0f, 0.0f, 1.0f));
- L1326 C51: new Vector3 :: SetUniform(gl, program, "gridOrigin", new Vector3(-10f, -10f, -10f));


## XREngine.UnitTests/Rendering/XRMeshBvhTests.cs
- L64 C29: new Vector3 :: mesh.SetPosition(0, new Vector3(-1f, -1f, 0f));
- L82 C13: new Vector3 :: new Vector3(0f, 0f, 0f),
- L83 C13: new Vector3 :: new Vector3(1f, 0f, 0f),
- L84 C13: new Vector3 :: new Vector3(0f, 1f, 0f),
- L85 C13: new Vector3 :: new Vector3(1f, 0f, 0f),
- L86 C13: new Vector3 :: new Vector3(1f, 1f, 0f),
- L87 C13: new Vector3 :: new Vector3(0f, 1f, 0f)


## XREngine.UnitTests/Rendering/XRMeshRendererTests.cs
- L25 C24: new XRMeshRenderer :: var renderer = new XRMeshRenderer();
- L26 C30: new EventList :: renderer.Submeshes = new EventList<XRMeshRenderer.SubMesh>();
- L28 C32: new XRMeshRenderer.SubMesh :: renderer.Submeshes.Add(new XRMeshRenderer.SubMesh
- L33 C32: new XRMeshRenderer.SubMesh :: renderer.Submeshes.Add(new XRMeshRenderer.SubMesh
- L66 C24: new XRMeshRenderer :: var renderer = new XRMeshRenderer();
- L67 C30: new EventList :: renderer.Submeshes = new EventList<XRMeshRenderer.SubMesh>();
- L76 C13: new Vector3 :: new Vector3(0f, 0f, 0f),
- L77 C13: new Vector3 :: new Vector3(1f, 0f, 0f),
- L78 C13: new Vector3 :: new Vector3(0f, 1f, 0f)
- L87 C13: new Vector3 :: new Vector3(0f, 0f, 1f),
- L88 C13: new Vector3 :: new Vector3(1f, 0f, 1f),
- L89 C13: new Vector3 :: new Vector3(0f, 1f, 1f)


## XREngine.UnitTests/XRMath/RotationBetweenVectorsTests.cs
- L74 C43: new Vector3 :: axis = den > XRMath.Epsilon ? new Vector3(rotation.X / den, rotation.Y / den, rotation.Z / den) : Vector3.UnitX;


## XREngine.VRClient/Program.cs
- L51 C55: new GameState :: Engine.Run((GameStartupSettings)settings, new GameState());
- L80 C54: new AssemblyName :: => AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(dynamicAssemblyName), AssemblyBuilderAccess.Run).DefineDynamicModule(dynamicAssemblyName);
- L111 C34: new ManagementObjectSearcher :: using var searcher = new ManagementObjectSearcher(wmiQueryString);
- L129 C28: new VRGameStartupSettings :: var settings = new VRGameStartupSettings<TActionCategory, TGameAction>()
- L136 C30: new VrManifest :: VRManifest = new VrManifest()
- L145 C21: new GameWindowStartupSettings :: new GameWindowStartupSettings()
- L156 C43: new XREngine.Data.Core.OverrideableSetting :: OutputVerbosityOverride = new XREngine.Data.Core.OverrideableSetting<EOutputVerbosity>(EOutputVerbosity.Verbose, true),
- L157 C50: new XREngine.Data.Core.OverrideableSetting :: UseIntegerWeightingIdsOverride = new XREngine.Data.Core.OverrideableSetting<bool>(true, true),
- L158 C39: new UserSettings :: DefaultUserSettings = new UserSettings()
- L164 C34: new ActionManifest :: ActionManifest = new ActionManifest<TActionCategory, TGameAction>()
- L187 C25: new XRWorld :: var world = new XRWorld();
- L188 C25: new XRScene :: var scene = new XRScene() { Name = "FillerScene" };


## XRENGINE/Core/Attributes/XRAssetAttributes.cs
- L19 C19: new ArgumentException :: throw new ArgumentException("Inspector type name must be provided.", nameof(inspectorTypeName));
- L35 C25: new ArgumentException :: ?? throw new ArgumentException("Inspector type did not provide a name.", nameof(inspectorType));
- L53 C19: new ArgumentException :: throw new ArgumentException("Menu label must be provided.", nameof(label));
- L55 C19: new ArgumentException :: throw new ArgumentException("Handler type name must be provided.", nameof(handlerTypeName));
- L57 C19: new ArgumentException :: throw new ArgumentException("Handler method name must be provided.", nameof(handlerMethodName));
- L86 C25: new ArgumentException :: ?? throw new ArgumentException("Handler type did not provide a name.", nameof(handlerType));


## XRENGINE/Core/Attributes/XRComponentEditorAttribute.cs
- L17 C19: new ArgumentException :: throw new ArgumentException("Editor type name must be provided.", nameof(editorTypeName));


## XRENGINE/Core/Editor/EditorState.cs
- L16 C71: new Dictionary :: private Dictionary<string, List<object>> _changedProperties = new Dictionary<string, List<object>>();
- L88 C64: new List :: public static List<EditorState> DirtyStates { get; } = new List<EditorState>();
- L103 C53: new List :: private List<GlobalChange> _globalChanges = new List<GlobalChange>();
- L125 C17: new void :: private new void OnSelectedChanged(bool selected)
- L145 C41: new GlobalChange :: GlobalChange globalChange = new GlobalChange
- L147 C33: new ConcurrentBag :: ChangedStates = new ConcurrentBag<(EditorState State, LocalValueChange Change)>()


## XRENGINE/Core/Engine/AssetDiagnostics.cs
- L32 C56: new() :: private static readonly object _missingAssetLock = new();
- L46 C29: new MissingAssetAggregate :: aggregate = new MissingAssetAggregate
- L60 C40: new HashSet :: aggregate.Contexts ??= new HashSet<string>(StringComparer.Ordinal);
- L73 C28: new MissingAssetInfo :: var snapshot = new MissingAssetInfo[_missingAssets.Count];
- L81 C37: new MissingAssetInfo :: snapshot[index++] = new MissingAssetInfo


## XRENGINE/Core/Engine/AssetManager.cs
- L62 C23: new TaskCompletionSource :: var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
- L78 C23: new TaskCompletionSource :: var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
- L138 C23: new DirectoryNotFoundException :: throw new DirectoryNotFoundException($"Could not find the engine assets directory at '{EngineAssetsPath}'.");
- L206 C23: new ArgumentException :: throw new ArgumentException($"{argumentName} cannot be null or empty.", argumentName);
- L540 C67: new AssetMetadata :: AssetMetadata meta = TryReadMetadata(metaPath) ?? new AssetMetadata();
- L571 C41: new AssetImportMetadata :: meta.Import ??= new AssetImportMetadata();
- L670 C40: new FileStream :: using var stream = new FileStream(
- L676 C40: new StreamReader :: using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
- L735 C23: new Dictionary :: var map = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
- L1378 C27: new HashSet :: var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
- L1379 C25: new Stack :: var stack = new Stack<object>();
- L1383 C26: new Dictionary :: var depths = new Dictionary<object, int>(ReferenceEqualityComparer.Instance)
- L1660 C80: new() :: public static XRAssetReferenceEqualityComparer Instance { get; } = new();
- L1846 C57: new FileSystemWatcher :: public FileSystemWatcher GameWatcher { get; } = new FileSystemWatcher();
- L1847 C59: new FileSystemWatcher :: public FileSystemWatcher EngineWatcher { get; } = new FileSystemWatcher();
- L1898 C49: new() :: private readonly object _metadataLock = new();
- L1994 C173: new() :: public T LoadEngineAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(params string[] relativePathFolders) where T : XRAsset, new()
- L1997 C217: new() :: public T LoadEngineAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(JobPriority priority, bool bypassJobThread, params string[] relativePathFolders) where T : XRAsset, new()
- L2000 C70: new FileNotFoundException :: return Load<T>(path, priority, bypassJobThread) ?? throw new FileNotFoundException($"Unable to find engine file at {path}");
- L2003 C195: new() :: public T LoadEngineAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(JobPriority priority, params string[] relativePathFolders) where T : XRAsset, new()
- L2006 C53: new FileNotFoundException :: return Load<T>(path, priority) ?? throw new FileNotFoundException($"Unable to find engine file at {path}");
- L2009 C184: new() :: public Task<T> LoadEngineAssetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(params string[] relativePathFolders) where T : XRAsset, new()
- L2012 C234: new() :: public async Task<T> LoadEngineAssetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(JobPriority priority, bool bypassJobThread, params string[] relativePathFolders) where T : XRAsset, new()
- L2015 C81: new FileNotFoundException :: return await LoadAsync<T>(path, priority, bypassJobThread) ?? throw new FileNotFoundException($"Unable to find engine file at {path}");
- L2018 C212: new() :: public async Task<T> LoadEngineAssetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(JobPriority priority, params string[] relativePathFolders) where T : XRAsset, new()
- L2021 C64: new FileNotFoundException :: return await LoadAsync<T>(path, priority) ?? throw new FileNotFoundException($"Unable to find engine file at {path}");
- L2024 C342: new() :: public T LoadEngineAssetRemote<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(RemoteAssetLoadMode mode = RemoteAssetLoadMode.RequestFromRemote, JobPriority priority = JobPriority.Normal, IReadOnlyDictionary<string, string>? metadata = null, params string[] relativePathFolders) where T : XRAsset, new()
- L2027 C341: new() :: public T? LoadGameAssetRemote<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(RemoteAssetLoadMode mode = RemoteAssetLoadMode.RequestFromRemote, JobPriority priority = JobPriority.Normal, IReadOnlyDictionary<string, string>? metadata = null, params string[] relativePathFolders) where T : XRAsset, new()
- L2030 C359: new() :: public async Task<T> LoadEngineAssetRemoteAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(RemoteAssetLoadMode mode = RemoteAssetLoadMode.RequestFromRemote, JobPriority priority = JobPriority.Normal, IReadOnlyDictionary<string, string>? metadata = null, params string[] relativePathFolders) where T : XRAsset, new()
- L2034 C26: new FileNotFoundException :: ?? throw new FileNotFoundException($"Unable to load engine file at {path} via remote path.");
- L2037 C172: new() :: public T? LoadGameAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(params string[] relativePathFolders) where T : XRAsset, new()
- L2043 C189: new() :: public async Task<T?> LoadGameAssetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(params string[] relativePathFolders) where T : XRAsset, new()
- L2049 C358: new() :: public async Task<T?> LoadGameAssetRemoteAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(RemoteAssetLoadMode mode = RemoteAssetLoadMode.RequestFromRemote, JobPriority priority = JobPriority.Normal, IReadOnlyDictionary<string, string>? metadata = null, params string[] relativePathFolders) where T : XRAsset, new()
- L2055 C313: new() :: public T? LoadByIdRemote<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(Guid assetId, RemoteAssetLoadMode mode = RemoteAssetLoadMode.RequestFromRemote, JobPriority priority = JobPriority.Normal, IReadOnlyDictionary<string, string>? metadata = null) where T : XRAsset, new()
- L2058 C377: new() :: public async Task<T?> LoadByIdRemoteAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(Guid assetId, RemoteAssetLoadMode mode = RemoteAssetLoadMode.RequestFromRemote, JobPriority priority = JobPriority.Normal, IReadOnlyDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default) where T : XRAsset, new()
- L2083 C182: new() :: public T LoadEngineAssetImmediate<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(params string[] relativePathFolders) where T : XRAsset, new()
- L2086 C86: new FileNotFoundException :: return Load<T>(path, JobPriority.Normal, bypassJobThread: true) ?? throw new FileNotFoundException($"Unable to find engine file at {path}");
- L2089 C164: new() :: public T? LoadImmediate<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string filePath) where T : XRAsset, new()
- L2203 C333: new() :: private async Task<T?> LoadAssetRemoteAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string filePath, RemoteAssetLoadMode mode, JobPriority priority, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? additionalMetadata = null) where T : XRAsset, new()
- L2209 C19: new Dictionary :: ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
- L2210 C19: new Dictionary :: : new Dictionary<string, string>(additionalMetadata, StringComparer.OrdinalIgnoreCase);
- L2225 C27: new RemoteJobRequest :: var request = new RemoteJobRequest
- L2276 C19: new Dictionary :: ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
- L2277 C19: new Dictionary :: : new Dictionary<string, string>(additionalMetadata, StringComparer.OrdinalIgnoreCase);
- L2282 C27: new RemoteJobRequest :: var request = new RemoteJobRequest
- L2337 C19: new Dictionary :: ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
- L2338 C19: new Dictionary :: : new Dictionary<string, string>(additionalMetadata, StringComparer.OrdinalIgnoreCase);
- L2343 C27: new RemoteJobRequest :: var request = new RemoteJobRequest
- L2403 C160: new() :: private T? LoadCore<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string filePath) where T : XRAsset, new()
- L2530 C245: new() :: public async Task<T?> LoadAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string filePath, JobPriority priority = JobPriority.Normal, bool bypassJobThread = false) where T : XRAsset, new()
- L2546 C228: new() :: public T? Load<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string filePath, JobPriority priority = JobPriority.Normal, bool bypassJobThread = false) where T : XRAsset, new()
- L2695 C27: new SerializerBuilder :: var builder = new SerializerBuilder()
- L2699 C62: new PolymorphicTypeGraphVisitor :: .WithEmissionPhaseObjectGraphVisitor(args => new PolymorphicTypeGraphVisitor(args.InnerVisitor))
- L2700 C50: new DepthTrackingEventEmitter :: .WithEventEmitter(nextEmitter => new DepthTrackingEventEmitter(nextEmitter))
- L2711 C39: new XRAssetYamlConverter :: builder.WithTypeConverter(new XRAssetYamlConverter());
- L2718 C27: new DeserializerBuilder :: var builder = new DeserializerBuilder()
- L2725 C30: new DepthTrackingNodeDeserializer :: inner => new DepthTrackingNodeDeserializer(inner),
- L2728 C30: new NotSupportedAnnotatingNodeDeserializer :: inner => new NotSupportedAnnotatingNodeDeserializer(inner),
- L2736 C42: new XRAssetDeserializer :: builder.WithNodeDeserializer(new XRAssetDeserializer(), w => w.OnTop());
- L2739 C42: new PolymorphicYamlNodeDeserializer :: builder.WithNodeDeserializer(new PolymorphicYamlNodeDeserializer(), w => w.OnTop());
- L2784 C162: new() :: private static T? DeserializeAssetFile<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(string filePath) where T : XRAsset, new()
- L2787 C28: new FileStream :: using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
- L2788 C32: new StreamReader :: using var reader = new StreamReader(fs);
- L2795 C28: new FileStream :: using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
- L2796 C32: new StreamReader :: using var reader = new StreamReader(fs);
- L2800 C179: new() :: private static async Task<T?> DeserializeAssetFileAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(string filePath) where T : XRAsset, new()
- L2806 C32: new FileStream :: using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
- L2807 C36: new StreamReader :: using var reader = new StreamReader(fs);
- L2818 C32: new FileStream :: using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
- L2819 C36: new StreamReader :: using var reader = new StreamReader(fs);
- L2824 C168: new() :: private T? Load3rdPartyWithCache<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(string filePath, string ext) where T : XRAsset, new()
- L2872 C185: new() :: private async Task<T?> Load3rdPartyWithCacheAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(string filePath, string ext) where T : XRAsset, new()
- L2933 C244: new() :: private static bool TryLoadCachedAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string cachePath, string originalPath, DateTime sourceTimestampUtc, out T? asset) where T : XRAsset, new()
- L2985 C238: new() :: private async Task<T?> TryLoadCachedAssetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string cachePath, string originalPath, DateTime sourceTimestampUtc) where T : XRAsset, new()
- L3041 C171: new() :: private static T? Load3rdPartyAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(string filePath, string ext) where T : XRAsset, new()
- L3072 C33: new T :: var asset = new T
- L3147 C188: new() :: private static async Task<T?> Load3rdPartyAssetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(string filePath, string ext) where T : XRAsset, new()
- L3187 C33: new T :: var asset = new T


## XRENGINE/Core/Engine/Debug.cs
- L45 C93: new() :: private static readonly ConcurrentDictionary<string, DateTime> RecentMessageCache = new();
- L46 C67: new Queue :: public static Queue<(string, DateTime)> Output { get; } = new Queue<(string, DateTime)>();
- L56 C56: new() :: private static readonly object LogWriterLock = new();
- L57 C61: new() :: private static readonly object ConsoleEntriesLock = new();
- L58 C66: new() :: private static readonly List<LogEntry> _consoleEntries = new();
- L59 C86: new() :: private static readonly Dictionary<ELogCategory, StreamWriter?> LogWriters = new()
- L75 C24: new List :: return new List<LogEntry>(_consoleEntries);
- L119 C30: new LogEntry :: addedEntry = new LogEntry(message, category, DateTime.Now);
- L129 C91: new() :: private static readonly List<(string Token, bool RequireBoundary)> OpenGlTokens = new()
- L141 C94: new() :: private static readonly List<(string Token, bool RequireBoundary)> RenderingTokens = new()
- L174 C92: new() :: private static readonly List<(string Token, bool RequireBoundary)> PhysicsTokens = new()
- L445 C30: new StreamWriter :: var writer = new StreamWriter(new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read))
- L445 C47: new FileStream :: var writer = new StreamWriter(new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read))
- L623 C29: new string :: var sanitized = new string(segment


## XRENGINE/Core/Engine/PolymorphicTypeGraphVisitor.cs
- L49 C26: new Scalar :: emitter.Emit(new Scalar(TypeKey));
- L50 C26: new Scalar :: emitter.Emit(new Scalar(typeName));


## XRENGINE/Core/Engine/PolymorphicYamlNodeDeserializer.cs
- L57 C27: new YamlException :: throw new YamlException($"Polymorphic YAML type '{typeName}' is not assignable to '{expectedType.FullName}'.");
- L59 C50: new ReplayParser :: value = nestedObjectDeserializer(new ReplayParser(events), concreteType);
- L66 C50: new ReplayParser :: value = nestedObjectDeserializer(new ReplayParser(events), typeof(XRTexture2D));
- L73 C50: new ReplayParser :: value = nestedObjectDeserializer(new ReplayParser(events), inferred);
- L229 C26: new List :: var events = new List<ParsingEvent>();
- L281 C19: new YamlException :: throw new YamlException("Unsupported YAML node encountered while capturing polymorphic data.");
- L291 C27: new Queue :: _events = new Queue<ParsingEvent>(events);
- L295 C62: new InvalidOperationException :: public ParsingEvent Current => _current ?? throw new InvalidOperationException("The parser is not positioned on an event.");


## XRENGINE/Core/Engine/ShaderVarYamlTypeConverter.cs
- L41 C23: new YamlException :: throw new YamlException("Expected a mapping to deserialize a ShaderVar.");
- L86 C23: new YamlException :: throw new YamlException($"Failed to create a ShaderVar instance of type '{concreteType.FullName}'.");
- L204 C23: new YamlException :: throw new YamlException(errorMessage);
- L238 C19: new YamlException :: throw new YamlException("Unsupported YAML node encountered while skipping a value.");
- L273 C24: new Vector2 :: return new Vector2(ParseFloat(parts, 0), ParseFloat(parts, 1));
- L275 C24: new Vector3 :: return new Vector3(ParseFloat(parts, 0), ParseFloat(parts, 1), ParseFloat(parts, 2));
- L277 C24: new Vector4 :: return new Vector4(ParseFloat(parts, 0), ParseFloat(parts, 1), ParseFloat(parts, 2), ParseFloat(parts, 3));
- L281 C27: new YamlException :: throw new YamlException("Expected Matrix4x4 format with 16 components.");
- L283 C24: new Matrix4x4 :: return new Matrix4x4(
- L291 C24: new IVector2 :: return new IVector2(ParseInt(parts, 0), ParseInt(parts, 1));
- L293 C24: new IVector3 :: return new IVector3(ParseInt(parts, 0), ParseInt(parts, 1), ParseInt(parts, 2));
- L295 C24: new IVector4 :: return new IVector4(ParseInt(parts, 0), ParseInt(parts, 1), ParseInt(parts, 2), ParseInt(parts, 3));
- L298 C24: new UVector2 :: return new UVector2(ParseUInt(parts, 0), ParseUInt(parts, 1));
- L300 C24: new UVector3 :: return new UVector3(ParseUInt(parts, 0), ParseUInt(parts, 1), ParseUInt(parts, 2));
- L302 C24: new UVector4 :: return new UVector4(ParseUInt(parts, 0), ParseUInt(parts, 1), ParseUInt(parts, 2), ParseUInt(parts, 3));
- L305 C24: new DVector2 :: return new DVector2(ParseDouble(parts, 0), ParseDouble(parts, 1));
- L307 C24: new DVector3 :: return new DVector3(ParseDouble(parts, 0), ParseDouble(parts, 1), ParseDouble(parts, 2));
- L309 C24: new DVector4 :: return new DVector4(ParseDouble(parts, 0), ParseDouble(parts, 1), ParseDouble(parts, 2), ParseDouble(parts, 3));
- L312 C24: new BoolVector2 :: return new BoolVector2(ParseBool(parts, 0), ParseBool(parts, 1));
- L314 C24: new BoolVector3 :: return new BoolVector3(ParseBool(parts, 0), ParseBool(parts, 1), ParseBool(parts, 2));
- L316 C24: new BoolVector4 :: return new BoolVector4(ParseBool(parts, 0), ParseBool(parts, 1), ParseBool(parts, 2), ParseBool(parts, 3));
- L323 C19: new YamlException :: throw new YamlException($"Unsupported ShaderVar Value type '{targetType.FullName}'.");
- L329 C23: new YamlException :: throw new YamlException("Not enough components in scalar value.");
- L336 C23: new YamlException :: throw new YamlException("Not enough components in scalar value.");
- L343 C23: new YamlException :: throw new YamlException("Not enough components in scalar value.");
- L350 C23: new YamlException :: throw new YamlException("Not enough components in scalar value.");
- L357 C23: new YamlException :: throw new YamlException("Not enough components in scalar value.");


## XRENGINE/Core/Engine/TransformBaseYamlTypeConverter.cs
- L53 C31: new InvalidOperationException :: throw new InvalidOperationException($"Deserialized type '{deserialized.GetType().FullName}' is not a {nameof(TransformBase)}.");
- L67 C20: new Transform :: result ??= new Transform();
- L79 C26: new Scalar :: emitter.Emit(new Scalar(null, null, "null", ScalarStyle.Plain, true, false));
- L85 C22: new MappingStart :: emitter.Emit(new MappingStart(null, null, false, MappingStyle.Block));
- L86 C22: new Scalar :: emitter.Emit(new Scalar(TypeKey));
- L87 C22: new Scalar :: emitter.Emit(new Scalar(runtimeType.AssemblyQualifiedName ?? runtimeType.FullName ?? runtimeType.Name));
- L88 C22: new Scalar :: emitter.Emit(new Scalar(ValueKey));
- L92 C22: new MappingEnd :: emitter.Emit(new MappingEnd());
- L101 C22: new Scalar :: emitter.Emit(new Scalar(ChildrenKey));
- L102 C22: new SequenceStart :: emitter.Emit(new SequenceStart(null, null, false, SequenceStyle.Block));
- L105 C22: new SequenceEnd :: emitter.Emit(new SequenceEnd());
- L119 C29: new List :: results ??= new List<TransformBase>(source.Count);
- L187 C15: new YamlException :: throw new YamlException("Unsupported YAML node encountered while skipping transform metadata.");


## XRENGINE/Core/Engine/XRAssetYamlTypeConverter.cs
- L125 C36: new ReplayParser :: var replayParser = new ReplayParser(capturedEvents);
- L260 C26: new List :: var events = new List<ParsingEvent>();
- L344 C19: new YamlException :: throw new YamlException("Unsupported YAML node encountered while capturing XRAsset data.");
- L444 C27: new Queue :: _events = new Queue<ParsingEvent>(events);
- L449 C62: new InvalidOperationException :: public ParsingEvent Current => _current ?? throw new InvalidOperationException("The parser is not positioned on an event.");
- L553 C22: new NotSupportedException :: => throw new NotSupportedException("XRAssetYamlConverter is write-only; reading is handled by XRAssetDeserializer.");
- L598 C26: new MappingStart :: emitter.Emit(new MappingStart(null, null, false, MappingStyle.Block));
- L599 C26: new Scalar :: emitter.Emit(new Scalar("ID"));
- L600 C26: new Scalar :: emitter.Emit(new Scalar(asset.ID.ToString()));
- L601 C26: new MappingEnd :: emitter.Emit(new MappingEnd());


## XRENGINE/Core/Engine/XRMeshBufferCollectionYamlTypeConverter.cs
- L37 C23: new YamlException :: throw new YamlException($"Unexpected scalar while deserializing {nameof(XRMesh)}.{nameof(XRMesh.BufferCollection)}: '{scalar.Value}'.");
- L45 C34: new XRMesh.BufferCollection :: var collection = new XRMesh.BufferCollection
- L65 C34: new Scalar :: emitter.Emit(new Scalar("~"));
- L70 C27: new YamlException :: throw new YamlException($"Expected {nameof(XRMesh)}.{nameof(XRMesh.BufferCollection)} but got '{value.GetType()}'.");


## XRENGINE/Core/Engine/XRTextureYamlTypeConverter.cs
- L38 C23: new YamlException :: throw new YamlException($"Unexpected scalar while deserializing {nameof(XRTexture)}: '{scalar.Value}'.");


## XRENGINE/Core/Extensions/EnumerableExtension.cs
- L7 C20: new ThreadSafeList :: return new ThreadSafeList<T>(enumerable);


## XRENGINE/Core/Extensions/StringExtension.cs
- L42 C28: new InvalidOperationException :: _ => throw new InvalidOperationException(t.ToString() + " is not parsable"),


## XRENGINE/Core/Files/AssetPacker.cs
- L55 C23: new FileNotFoundException :: throw new FileNotFoundException($"Archive '{archiveFilePath}' not found.", archiveFilePath);
- L63 C40: new CookedBinaryReader :: using var reader = new CookedBinaryReader((byte*)sourceMap.Address, sourceMap.Length);
- L65 C31: new InvalidOperationException :: throw new InvalidOperationException("Invalid asset archive format.");
- L77 C35: new InvalidOperationException :: throw new InvalidOperationException($"Unsupported archive version '{version}'.");
- L97 C23: new InvalidOperationException :: throw new InvalidOperationException("Cannot repack archive without any assets.");
- L111 C23: new InvalidOperationException :: throw new InvalidOperationException("Cannot repack archive without any assets.");
- L119 C23: new DirectoryNotFoundException :: throw new DirectoryNotFoundException($"Input directory '{inputDir}' does not exist.");
- L127 C47: new PackedAsset :: return new PackedAsset
- L138 C23: new InvalidOperationException :: throw new InvalidOperationException("No files found to pack.");
- L146 C23: new InvalidOperationException :: throw new InvalidOperationException("Archive must contain at least one asset.");
- L158 C56: new TocEntry :: var tocEntries = assets.Select(static a => new TocEntry
- L166 C36: new StringCompressor :: var stringCompressor = new StringCompressor(tocEntries.Select(static e => e.Path));
- L191 C32: new CookedBinaryWriter :: using var writer = new CookedBinaryWriter((byte*)map.Address, (int)totalSize, map);
- L225 C31: new StringCompressor :: var stringTable = new StringCompressor(reader);
- L236 C28: new PackedAsset :: assets.Add(new PackedAsset
- L281 C37: new() :: PackedAsset asset = new()
- L321 C27: new List :: var buckets = new List<TocEntry>[bucketCount];
- L331 C28: new int :: int[] starts = new int[bucketCount];
- L332 C28: new int :: int[] counts = new int[bucketCount];
- L348 C30: new BucketLayout :: return (ordered, new BucketLayout(bucketCount, starts, counts));
- L403 C27: new InvalidOperationException :: throw new InvalidOperationException("Existing asset data requires a source archive.");
- L405 C28: new ReadOnlySpan :: var span = new ReadOnlySpan<byte>((byte*)sourceMap.Address + asset.ExistingDataOffset, asset.CompressedSize);
- L419 C36: new CookedBinaryReader :: using var reader = new CookedBinaryReader((byte*)map.Address, map.Length);
- L421 C27: new InvalidOperationException :: throw new InvalidOperationException("Invalid asset archive format.");
- L428 C32: new InvalidOperationException :: _ => throw new InvalidOperationException($"Unsupported archive version '{version}'."),
- L438 C36: new StringCompressor :: var stringCompressor = new StringCompressor(reader);
- L449 C36: new StringCompressor :: var stringCompressor = new StringCompressor(reader);
- L471 C20: new FooterInfo :: return new FooterInfo(tocPosition, stringTableOffset, dictionaryOffset, indexOffset);
- L492 C19: new FileNotFoundException :: throw new FileNotFoundException($"Asset {assetPath} not found");
- L513 C23: new FileNotFoundException :: throw new FileNotFoundException($"Asset {assetPath} not found");
- L524 C19: new FileNotFoundException :: throw new FileNotFoundException($"Asset {assetPath} not found");
- L574 C19: new FileNotFoundException :: throw new FileNotFoundException($"Asset {assetPath} not found");


## XRENGINE/Core/Files/AssetPacker.StringCompressor.cs
- L55 C39: new StringBuilder :: var builder = new StringBuilder();
- L105 C41: new() :: BufferBuilder builder = new();
- L123 C31: new InvalidOperationException :: throw new InvalidOperationException($"String '{str}' exceeds maximum supported length.");
- L140 C68: new() :: private readonly ArrayBufferWriter<byte> _buffer = new();


## XRENGINE/Core/Files/CookedAssetBlob.cs
- L43 C28: new NotSupportedException :: _ => throw new NotSupportedException($"Unsupported cooked asset format '{blob.Format}'.")
- L52 C26: new InvalidOperationException :: ?? throw new InvalidOperationException($"Unable to resolve cooked asset type '{blob.TypeName}'.");


## XRENGINE/Core/Files/CookedBinarySerializer.cs
- L66 C23: new ArgumentOutOfRangeException :: throw new ArgumentOutOfRangeException(nameof(value));
- L89 C21: new Span :: data.CopyTo(new Span<byte>(_cursor, data.Length));
- L115 C19: new ArgumentNullException :: throw new ArgumentNullException(nameof(data));
- L125 C39: new Span :: Encoding.UTF8.GetBytes(value, new Span<byte>(_cursor, byteCount));
- L134 C39: new Span :: Encoding.UTF8.GetBytes(value, new Span<byte>(_cursor, byteCount));
- L152 C19: new InvalidOperationException :: throw new InvalidOperationException("Cooked binary writer exceeded allocated buffer.");
- L220 C48: new ReadOnlySpan :: string value = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(_cursor, length));
- L232 C23: new ArgumentOutOfRangeException :: throw new ArgumentOutOfRangeException(nameof(value));
- L242 C25: new byte :: byte[] result = new byte[length];
- L243 C9: new ReadOnlySpan :: new ReadOnlySpan<byte>(_cursor, length).CopyTo(result);
- L273 C23: new FormatException :: throw new FormatException("7-bit encoded int is too large.");
- L287 C19: new EndOfStreamException :: throw new EndOfStreamException("Attempted to read beyond the end of the cooked buffer.");
- L305 C72: new() :: private static readonly AsyncLocal<int> MemoryPackRecursionDepth = new();
- L317 C23: new MemoryPackRecursionScope :: using var _ = new MemoryPackRecursionScope();
- L327 C19: new InvalidOperationException :: throw new InvalidOperationException($"Cooked payload exceeds maximum supported size ({length} bytes).");
- L329 C25: new byte :: byte[] buffer = new byte[(int)length];
- L334 C36: new CookedBinaryWriter :: using var writer = new CookedBinaryWriter(ptr, buffer.Length);
- L350 C36: new CookedBinaryReader :: using var reader = new CookedBinaryReader(ptr, data.Length);
- L620 C44: new Guid :: CookedBinaryTypeMarker.Guid => new Guid(reader.ReadBytes(16)),
- L622 C48: new TimeSpan :: CookedBinaryTypeMarker.TimeSpan => new TimeSpan(reader.ReadInt64()),
- L631 C24: new NotSupportedException :: _ => throw new NotSupportedException($"Unknown cooked binary marker '{marker}'.")
- L680 C56: new InvalidOperationException :: Type enumType = ResolveType(typeName) ?? throw new InvalidOperationException($"Failed to resolve enum type '{typeName}'.");
- L708 C58: new List :: IList list = (IList)(CreateInstance(listType) ?? new List<object?>());
- L725 C76: new Dictionary :: IDictionary dictionary = (IDictionary)(CreateInstance(dictType) ?? new Dictionary<object, object?>());
- L742 C74: new InvalidOperationException :: Type targetType = ResolveType(typeName) ?? expectedType ?? throw new InvalidOperationException($"Failed to resolve cooked asset type '{typeName}'.");
- L744 C19: new InvalidOperationException :: throw new InvalidOperationException($"Type '{targetType}' does not implement {nameof(ICookedBinarySerializable)}.");
- L755 C58: new InvalidOperationException :: Type targetType = ResolveType(typeName) ?? throw new InvalidOperationException($"Failed to resolve cooked asset type '{typeName}'.");
- L761 C24: new InvalidOperationException :: _ => throw new InvalidOperationException($"Unsupported cooked object encoding '{encoding}'.")
- L769 C63: new InvalidOperationException :: object instance = CreateInstance(targetType) ?? throw new InvalidOperationException($"Unable to create instance of '{targetType}'.");
- L784 C23: new InvalidOperationException :: throw new InvalidOperationException($"MemoryPack deserialization returned null for asset type '{targetType}'.");
- L790 C19: new InvalidOperationException :: throw new InvalidOperationException($"MemoryPack deserialization returned null for type '{targetType}'.");
- L798 C16: new DataSource :: return new DataSource(data);
- L824 C24: new Guid :: return new Guid(bytes);
- L861 C19: new InvalidOperationException :: throw new InvalidOperationException("Cooked binary payload is missing a type hint.");
- L876 C19: new InvalidOperationException :: throw new InvalidOperationException($"Unable to resolve type '{key}'.");
- L880 C76: new() :: private static readonly ConcurrentDictionary<string, Type> TypeCache = new();
- L882 C83: new() :: private static readonly AsyncLocal<ReferenceLoopGuard?> ReflectionLoopGuard = new();
- L886 C51: new ReferenceLoopGuard :: var guard = ReflectionLoopGuard.Value ??= new ReferenceLoopGuard();
- L899 C24: new ReferenceLoopScope :: return new ReferenceLoopScope(this, null);
- L903 C20: new ReferenceLoopScope :: return new ReferenceLoopScope(this, instance);
- L944 C23: new MemoryPackRecursionScope :: using var _ = new MemoryPackRecursionScope();
- L1185 C48: new MemberMetadata :: .Select(p => new MemberMetadata(p))
- L1228 C37: new[] :: Setter.Invoke(instance, new[] { value });
- L1234 C82: new() :: private static readonly ConcurrentDictionary<Type, TypeMetadata> Cache = new();
- L1239 C49: new TypeMetadata :: => Cache.GetOrAdd(type, static t => new TypeMetadata(t));


## XRENGINE/Core/Files/EventRaisingStreamWriter.cs
- L10 C45: new EventArgs :: StringWritten?.Invoke(this, new EventArgs<string>(str));
- L31 C29: new string :: OnStringWritten(new string(buffer));
- L36 C29: new string :: OnStringWritten(new string(buffer, index, count));
- L116 C29: new string :: OnStringWritten(new string(buffer) + NewLine);
- L121 C29: new string :: OnStringWritten(new string(buffer, index, count) + NewLine);


## XRENGINE/Core/Files/FileMap.cs
- L48 C30: new FileStream :: stream = new FileStream(path, FileMode.Open, (prot == FileMapProtect.ReadWrite) ? FileAccess.ReadWrite : FileAccess.Read, FileShare.Read, 8, options);
- L55 C26: new FileStream :: stream = new FileStream(tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 8, options | FileOptions.DeleteOnClose);
- L103 C39: new WFileMap :: PlatformID.Win32NT => new WFileMap(stream.SafeFileHandle.DangerousGetHandle(), prot, offset, (uint)length) { _path = stream.Name },
- L104 C22: new CFileMap :: _ => new CFileMap(stream, prot, offset, length) { _path = stream.Name },
- L119 C39: new WFileMap :: PlatformID.Win32NT => new WFileMap(stream.SafeFileHandle.DangerousGetHandle(), prot, offset, (uint)length) { _baseStream = stream, _path = stream.Name },
- L120 C22: new CFileMap :: _ => new CFileMap(stream, prot, offset, length) { _baseStream = stream, _path = stream.Name },


## XRENGINE/Core/Files/JsonAsset.cs
- L11 C59: new() :: public class JsonAsset<T> : TextFile where T : class, new()
- L15 C21: new T :: _data = new T();
- L21 C21: new T :: _data = new T();
- L61 C53: new T :: Data = string.IsNullOrEmpty(Text) ? new T() : JsonConvert.DeserializeObject<T>(Text) ?? new T();
- L61 C105: new T :: Data = string.IsNullOrEmpty(Text) ? new T() : JsonConvert.DeserializeObject<T>(Text) ?? new T();


## XRENGINE/Core/Files/ProjectUserSettings.cs
- L14 C42: new() :: private UserSettings _settings = new();
- L24 C37: new UserSettings :: _settings = settings ?? new UserSettings();
- L59 C37: new UserSettings :: var next = value ?? new UserSettings();


## XRENGINE/Core/Files/TextFile.cs
- L82 C16: new() :: => new() { Text = text };
- L127 C30: new byte :: byte[] bom = new byte[4];


## XRENGINE/Core/Files/XRAsset.MemoryPack.cs
- L35 C23: new InvalidOperationException :: throw new InvalidOperationException($"Unable to resolve asset type '{envelope.Value.TypeName}'.");
- L38 C23: new InvalidOperationException :: throw new InvalidOperationException($"MemoryPack asset type '{resolved}' does not match expected type '{expectedType}'.");


## XRENGINE/Core/Files/XRProject.cs
- L200 C27: new XRProject :: var project = new XRProject(projectName)


## XRENGINE/Core/Platform/Win32.cs
- L35 C81: new SafeHandle :: public static implicit operator SafeHandle(VoidPtr handle) { return new SafeHandle(handle); }
- L42 C24: new SafeHandle :: return new SafeHandle(hFile);


## XRENGINE/Core/Reflection/AssemblyQualifiedName.cs
- L36 C27: new Version :: Version = new Version(VersionMajor, VersionMinor, VersionBuild, VersionRevision),
- L67 C57: new CultureInfo :: CultureInfo = culture == "neutral" ? null : new CultureInfo(culture);


## XRENGINE/Core/Time/EngineTimer.cs
- L110 C45: new() :: private readonly Stopwatch _watch = new();
- L119 C47: new() :: public DeltaManager Render { get; } = new();
- L120 C47: new() :: public DeltaManager Update { get; } = new();
- L121 C48: new() :: public DeltaManager Collect { get; } = new();
- L122 C59: new() :: public DeltaManager FixedUpdateManager { get; } = new();
- L155 C27: new ManualResetEventSlim :: _renderDone = new ManualResetEventSlim(false);
- L156 C25: new ManualResetEventSlim :: _swapDone = new ManualResetEventSlim(true);


## XRENGINE/Core/Tools/DelegateBuilder.cs
- L10 C38: new Queue :: var queueMissingParams = new Queue<object>(missingParamValues);


## XRENGINE/Core/Tools/ExpressionParser.cs
- L17 C35: new() :: Queue<string> queue = new();
- L18 C35: new() :: Stack<string> stack = new();
- L41 C36: new InvalidCastException :: _ => throw new InvalidCastException(),
- L43 C23: new InvalidCastException :: throw new InvalidCastException();
- L152 C27: new Exception :: throw new Exception("Invalid string: " + token);
- L169 C27: new Exception :: throw new Exception("Invalid method: " + token);
- L188 C27: new Exception :: throw new Exception("Cannot invert numeric types.");
- L221 C23: new Exception :: throw new Exception("Cannot invert non-bool.");
- L277 C39: new InvalidOperationException :: throw new InvalidOperationException("Cannot negate an unsigned integer.");
- L304 C35: new InvalidOperationException :: throw new InvalidOperationException("Cannot negate an unsigned integer.");
- L349 C85: new() :: private static readonly Dictionary<string, string[]> _implicitConversions = new()
- L351 C25: new string :: { "SByte",  new string[] { "Int16", "Int32", "Int64", "Single", "Double", "Decimal" } },
- L352 C25: new string :: { "Byte",   new string[] { "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64", "Single", "Double", "Decimal" } },
- L353 C25: new string :: { "Int16",  new string[] { "Int32", "Int64", "Single", "Double", "Decimal" } },
- L354 C25: new string :: { "UInt16", new string[] { "Int32", "UInt32", "Int64", "UInt64", "Single", "Double", "Decimal" } },
- L355 C25: new string :: { "Int32",  new string[] { "Int64", "Single", "Double", "Decimal" } },
- L356 C25: new string :: { "UInt32", new string[] { "Int64", "UInt64", "Single", "Double", "Decimal" } },
- L357 C25: new string :: { "Int64",  new string[] { "Single", "Double", "Decimal" } },
- L358 C25: new string :: { "UInt64", new string[] { "Single", "Double", "Decimal" } },
- L359 C25: new string :: { "Char",   new string[] { "UInt16", "Int32", "UInt32", "Int64", "UInt64", "Single", "Double", "Decimal" } },
- L360 C25: new string :: { "Single", new string[] { "Single", "Double" } },
- L382 C35: new InvalidOperationException :: throw new InvalidOperationException($"Operator {token} needs operands on both sides.");
- L398 C35: new Exception :: throw new Exception();
- L408 C35: new Exception :: throw new Exception();
- L424 C39: new Exception :: throw new Exception("Cannot logical-and non-boolean types.");
- L429 C39: new Exception :: throw new Exception("Cannot logical-or non-boolean types.");
- L450 C35: new Exception :: throw new Exception(string.Format("Token \"{0}\" not supported.", token));
- L473 C23: new Exception :: throw new Exception($"Cannot evaluate {t1.Name} {token} {t2.Name}");
- L494 C27: new Exception :: throw new Exception($"Cannot evaluate {t1.Name} {token} {t2.Name}");
- L497 C23: new Exception :: throw new Exception($"Cannot evaluate {t1.Name} {token} {t2.Name}");
- L559 C28: new Exception :: _ => throw new Exception("Not a numeric primitive type"),
- L576 C28: new Exception :: _ => throw new Exception("Not a numeric primitive type"),
- L593 C28: new Exception :: _ => throw new Exception("Not a numeric primitive type"),
- L610 C28: new Exception :: _ => throw new Exception("Not a numeric primitive type"),
- L627 C28: new Exception :: _ => throw new Exception("Not a numeric primitive type"),
- L641 C60: new Exception :: "Single" or "Double" or "Decimal" => throw new Exception("Cannot left shift " + commonType),
- L642 C28: new Exception :: _ => throw new Exception("Not a numeric primitive type"),
- L656 C60: new Exception :: "Single" or "Double" or "Decimal" => throw new Exception("Cannot right shift " + commonType),
- L657 C28: new Exception :: _ => throw new Exception("Not a numeric primitive type"),
- L671 C60: new Exception :: "Single" or "Double" or "Decimal" => throw new Exception("Cannot bitwise-and " + commonType),
- L672 C28: new Exception :: _ => throw new Exception("Not a numeric primitive type"),
- L686 C60: new Exception :: "Single" or "Double" or "Decimal" => throw new Exception("Cannot bitwise-xor " + commonType),
- L687 C28: new Exception :: _ => throw new Exception("Not a numeric primitive type"),
- L701 C60: new Exception :: "Single" or "Double" or "Decimal" => throw new Exception("Cannot bitwise-or " + commonType),
- L702 C28: new Exception :: _ => throw new Exception("Not a numeric primitive type"),
- L719 C28: new Exception :: _ => throw new Exception("Not a numeric primitive type"),
- L736 C28: new Exception :: _ => throw new Exception("Not a numeric primitive type"),
- L753 C28: new Exception :: _ => throw new Exception("Not a numeric primitive type"),
- L770 C28: new Exception :: _ => throw new Exception("Not a numeric primitive type"),
- L788 C28: new Exception :: _ => throw new Exception("Not a primitive type"),
- L806 C28: new Exception :: _ => throw new Exception("Not a primitive type"),


## XRENGINE/Core/Tools/GLSLParser.cs
- L91 C28: new Variable :: var variable = new Variable
- L123 C24: new Variable :: Consts.Add(new Variable
- L141 C26: new Method :: var method = new Method
- L157 C43: new Parameter :: method.Parameters.Add(new Parameter
- L187 C29: new ParsedInvocation :: invocations.Add(new ParsedInvocation(


## XRENGINE/Core/Tools/Remapper.cs
- L24 C23: new InvalidOperationException :: throw new InvalidOperationException();
- L35 C31: new Hashtable :: Hashtable cache = new Hashtable();
- L37 C27: new int :: _remapTable = new int[count];
- L38 C25: new int :: _impTable = new int[count];
- L59 C32: new int :: int[] sorted = new int[impCount];


## XRENGINE/Core/Tools/Triangle Converter/CacheSimulator.cs
- L17 C22: new Deque :: _cache = new Deque<uint>();


## XRENGINE/Core/Tools/Triangle Converter/GraphArray.cs
- L47 C22: new List :: _nodes = new List<Node>();
- L49 C28: new Node :: _nodes.Add(new Node(this));
- L50 C21: new List :: _arcs = new List<Arc>();
- L71 C21: new Arc :: Arc r = new Arc(_nodes[(int)terminal]);


## XRENGINE/Core/Tools/Triangle Converter/HeapArray.cs
- L35 C21: new List :: _Heap = new List<Linker>();
- L36 C23: new List :: _Finder = new List<uint>();
- L97 C23: new Linker :: _Heap.Add(new Linker(Elem, Id));


## XRENGINE/Core/Tools/Triangle Converter/Policy.cs
- L48 C32: new Strip :: private Strip _Strip = new Strip();


## XRENGINE/Core/Tools/Triangle Converter/TriStripper.cs
- L18 C23: new List :: Indices = new List<uint>();
- L19 C23: new List :: NodeIDs = new List<ushort>();
- L47 C26: new GraphArray :: _Triangles = new GraphArray<Triangle>((uint)TriIndices.Length / 3);
- L50 C33: new List :: _PrimitivesVector = new List<Primitive>();
- L51 C24: new HeapArray :: _TriHeap = new HeapArray(CompareType.Less);
- L52 C27: new List :: _Candidates = new List<uint>();
- L53 C22: new CacheSimulator :: _Cache = new CacheSimulator();
- L54 C26: new CacheSimulator :: _BackCache = new CacheSimulator();
- L174 C27: new Primitive :: Primitive p = new Primitive(PrimType.TriangleList);
- L198 C26: new Policy :: Policy policy = new Policy(_MinStripSize, Cache);
- L246 C29: new List :: _CurrentNodes = new List<ushort>();
- L285 C20: new Strip :: return new Strip(Start, StartOrder, Size);
- L289 C29: new List :: _CurrentNodes = new List<ushort>();
- L344 C20: new Strip :: return new Strip(Node._elem._index, Order, Size);
- L441 C27: new Primitive :: Primitive p = new Primitive(PrimType.TriangleStrip);
- L556 C33: new TriangleEdge :: TriOrder.ABC => new TriangleEdge(Triangle.A, Triangle.B),
- L557 C33: new TriangleEdge :: TriOrder.BCA => new TriangleEdge(Triangle.B, Triangle.C),
- L558 C33: new TriangleEdge :: TriOrder.CAB => new TriangleEdge(Triangle.C, Triangle.A),
- L559 C22: new TriangleEdge :: _ => new TriangleEdge(0, 0),
- L566 C43: new TriangleEdge :: case TriOrder.ABC: return new TriangleEdge(Triangle.B, Triangle.C);
- L567 C43: new TriangleEdge :: case TriOrder.BCA: return new TriangleEdge(Triangle.C, Triangle.A);
- L568 C43: new TriangleEdge :: case TriOrder.CAB: return new TriangleEdge(Triangle.A, Triangle.B);
- L569 C33: new TriangleEdge :: default: return new TriangleEdge(0, 0);
- L595 C44: new Triangle :: Triangles[(uint)i]._elem = new Triangle(
- L601 C37: new List :: List<TriEdge> EdgeMap = new List<TriEdge>();
- L605 C29: new TriEdge :: EdgeMap.Add(new TriEdge(Tri.A, Tri.B, i));
- L606 C29: new TriEdge :: EdgeMap.Add(new TriEdge(Tri.B, Tri.C, i));
- L607 C29: new TriEdge :: EdgeMap.Add(new TriEdge(Tri.C, Tri.A, i));
- L615 C52: new TriEdge :: LinkNeighbours(Triangles, EdgeMap, new TriEdge(Tri.B, Tri.A, i));
- L616 C52: new TriEdge :: LinkNeighbours(Triangles, EdgeMap, new TriEdge(Tri.C, Tri.B, i));
- L617 C52: new TriEdge :: LinkNeighbours(Triangles, EdgeMap, new TriEdge(Tri.A, Tri.C, i));


## XRENGINE/Core/Tools/Unity/UnityConverter.cs
- L13 C25: new List :: var anims = new List<(string? path, string? attrib, BasePropAnim anim)>();
- L16 C28: new PropAnimFloat :: var anim = new PropAnimFloat
- L24 C60: new FloatKeyframe :: var kfs = curve.Curve?.Curve?.Select(kf => new FloatKeyframe
- L38 C24: new AnimationClip :: var tree = new AnimationClip();


## XRENGINE/Engine/Engine.CodeProfiler.cs
- L36 C85: new CodeProfilerTimer :: private readonly ResourcePool<CodeProfilerTimer> _timerPool = new(() => new CodeProfilerTimer());
- L41 C53: new() :: private readonly object _snapshotLock = new();
- L50 C34: new ThreadLocal :: _threadContext = new ThreadLocal<ThreadContext>(() => new ThreadContext(), trackAllValues: false);
- L50 C71: new ThreadContext :: _threadContext = new ThreadLocal<ThreadContext>(() => new ThreadContext(), trackAllValues: false);
- L145 C35: new Stack :: ActiveStack = new Stack<CodeProfilerTimer>(32);
- L154 C36: new() :: StringBuilder sb = new();
- L181 C37: new ProfilerThreadSnapshot :: threads.Add(new ProfilerThreadSnapshot(kvp.Key, kvp.Value.AsReadOnly()));
- L183 C37: new ProfilerFrameSnapshot :: frameSnapshot = new ProfilerFrameSnapshot(Time.Timer.Time(), threads.AsReadOnly());
- L195 C90: new Queue :: history = _threadFrameHistory[threadSnapshot.ThreadId] = new Queue<float>(ThreadHistoryCapacity);
- L223 C40: New() :: return StateObject.New();
- L403 C24: new ProfilerNodeSnapshot :: return new ProfilerNodeSnapshot(timer.Name, timer.ElapsedMs, childSnapshots.AsReadOnly());


## XRENGINE/Engine/Engine.cs
- L33 C84: new() :: private static readonly ConcurrentQueue<Action> _pendingUpdateThreadWork = new();
- L34 C85: new() :: private static readonly ConcurrentQueue<Action> _pendingPhysicsThreadWork = new();
- L48 C28: new UserSettings :: UserSettings = new UserSettings();
- L49 C28: new GameStartupSettings :: GameSettings = new GameStartupSettings();
- L50 C29: new BuildSettings :: BuildSettings = new BuildSettings();
- L83 C30: new ActionJob :: => Jobs.Schedule(new ActionJob(task), JobPriority.Normal, JobAffinity.CollectVisibleSwap);
- L90 C30: new CoroutineJob :: => Jobs.Schedule(new CoroutineJob(task), JobPriority.Normal, JobAffinity.CollectVisibleSwap);
- L97 C30: new ActionJob :: => Jobs.Schedule(new ActionJob(task), JobPriority.Normal, JobAffinity.MainThread);
- L128 C30: new CoroutineJob :: => Jobs.Schedule(new CoroutineJob(task), JobPriority.Normal, JobAffinity.MainThread);
- L169 C42: new UserSettings :: _userSettings = value ?? new UserSettings();
- L191 C55: new BuildSettings :: GameSettings.BuildSettings = value ?? new BuildSettings();
- L313 C42: new GameStartupSettings :: _gameSettings = value ?? new GameStartupSettings();
- L315 C49: new BuildSettings :: _gameSettings.BuildSettings ??= new BuildSettings();
- L332 C53: new() :: public static AudioManager Audio { get; } = new();
- L351 C54: new() :: public static AssetManager Assets { get; } = new();
- L355 C48: new() :: public static Random Random { get; } = new();
- L359 C56: new() :: public static CodeProfiler Profiler { get; } = new();
- L589 C34: new ServerNetworkingManager :: var server = new ServerNetworkingManager();
- L594 C34: new ClientNetworkingManager :: var client = new ClientNetworkingManager();
- L599 C37: new PeerToPeerNetworkingManager :: var p2pClient = new PeerToPeerNetworkingManager();
- L607 C40: new RemoteJobNetworkingTransport :: Jobs.RemoteTransport = new RemoteJobNetworkingTransport(net);
- L675 C40: new Dictionary :: responseMetadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
- L681 C24: new RemoteJobResponse :: return new RemoteJobResponse
- L693 C24: new RemoteJobResponse :: return new RemoteJobResponse
- L790 C26: new XRWindow :: window = new XRWindow(options, windowSettings.UseNativeTitleBar);
- L796 C31: new GraphicsAPI :: options.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 6));
- L796 C119: new APIVersion :: options.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 6));
- L797 C26: new XRWindow :: window = new XRWindow(options, windowSettings.UseNativeTitleBar);
- L844 C32: new Vector2D :: position = new Vector2D<int>(0, 0);
- L847 C28: new Vector2D :: size = new Vector2D<int>(primaryX, primaryY);
- L864 C23: new GraphicsAPI :: ? new GraphicsAPI(ContextAPI.Vulkan, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(1, 1))
- L864 C111: new APIVersion :: ? new GraphicsAPI(ContextAPI.Vulkan, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(1, 1))
- L865 C23: new GraphicsAPI :: : new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 6)),
- L865 C111: new APIVersion :: : new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(4, 6)),


## XRENGINE/Engine/Engine.Physics.cs
- L10 C71: new IPhysicsGeometry.Sphere :: public static IPhysicsGeometry NewSphere(float radius) => new IPhysicsGeometry.Sphere(radius);
- L12 C75: new IPhysicsGeometry.Box :: public static IPhysicsGeometry NewBox(Vector3 halfExtents) => new IPhysicsGeometry.Box(halfExtents);
- L14 C90: new IPhysicsGeometry.Capsule :: public static IPhysicsGeometry NewCapsule(float radius, float halfHeight) => new IPhysicsGeometry.Capsule(radius, halfHeight);


## XRENGINE/Engine/Engine.PlayMode.cs
- L18 C67: new() :: private static PlayModeConfiguration _configuration = new();
- L20 C57: new() :: private static readonly object _stateLock = new();
- L81 C50: new PlayModeConfiguration :: set => _configuration = value ?? new PlayModeConfiguration();
- L447 C24: new XRWorld :: return new XRWorld("Default");
- L468 C24: new GameMode :: return new GameMode();


## XRENGINE/Engine/Engine.Project.cs
- L143 C27: new ProjectUserSettings :: var created = new ProjectUserSettings(UserSettings)
- L229 C24: new ProjectUserSettings :: ?? new ProjectUserSettings(UserSettings);
- L270 C45: new BuildSettings :: var settings = BuildSettings ?? new BuildSettings();


## XRENGINE/Engine/Engine.Rendering.BvhStats.cs
- L15 C56: new() :: private static readonly object _lock = new();


## XRENGINE/Engine/Engine.Rendering.cs
- L68 C45: new JoltScene :: EPhysicsLibrary.Jolt => new JoltScene(),
- L69 C26: new PhysxScene :: _ => new PhysxScene(),
- L73 C20: new() :: => new();
- L77 C23: new DebugOpaqueRenderPipeline :: ? new DebugOpaqueRenderPipeline()
- L78 C23: new DefaultRenderPipeline :: : new DefaultRenderPipeline();
- L99 C59: new DebugOpaqueRenderPipeline :: viewport.RenderPipeline = new DebugOpaqueRenderPipeline();
- L103 C55: new DefaultRenderPipeline :: viewport.RenderPipeline = new DefaultRenderPipeline();


## XRENGINE/Engine/Engine.Rendering.Debug.cs
- L58 C94: new() :: private static readonly InstancedDebugVisualizer _instancedDebugVisualizer = new();
- L121 C41: new XRMeshRenderer :: _lineRenderer = new XRMeshRenderer(mesh, mat);
- L135 C42: new XRMeshRenderer :: _pointRenderer = new XRMeshRenderer(mesh, mat);
- L140 C69: new() :: private static readonly Lock _debugShapeQueueLock = new();
- L346 C50: new Vector3 :: Vector3[] circlePoints = new Vector3[segments + 1];
- L368 C50: new Vector3 :: Vector3[] circlePoints = new Vector3[segments + 1];
- L374 C50: new Vector3 :: Vector3 localPoint = new Vector3(x, 0, z);
- L393 C29: new Vector3 :: new Vector3(-extents.X, 0, -extents.Y),
- L394 C29: new Vector3 :: new Vector3(extents.X, 0, -extents.Y),
- L395 C29: new Vector3 :: new Vector3(extents.X, 0, extents.Y),
- L396 C29: new Vector3 :: new Vector3(-extents.X, 0, extents.Y),
- L408 C29: new Vector3 :: new Vector3(-extents.X, 0, -extents.Y),
- L409 C29: new Vector3 :: new Vector3(extents.X, 0, -extents.Y),
- L410 C29: new Vector3 :: new Vector3(extents.X, 0, extents.Y),
- L411 C29: new Vector3 :: new Vector3(-extents.X, 0, extents.Y),
- L432 C46: new Vector3 :: Vector3[] spherePoints = new Vector3[segments * rings];
- L491 C25: new Vector3 :: new Vector3(bounds.Center.X, bounds.Center.Y, 0.0f),
- L493 C25: new Vector2 :: new Vector2(bounds.Extents.X, bounds.Extents.Y),
- L601 C51: new Vector3 :: Vector3[] capsulePoints = new Vector3[segments * rings];
- L649 C50: new Vector3 :: Vector3[] circlePoints = new Vector3[segments];
- L729 C48: new Vector3 :: Vector3[] cylinderPoints = new Vector3[segments * 2];
- L794 C44: new Vector3 :: Vector3[] conePoints = new Vector3[segments + 1];
- L828 C118: new() :: private static readonly ConcurrentDictionary<int, (UIText text, float lastUpdatedTime)> DebugTexts = new();
- L829 C83: new() :: private static readonly ResourcePool<UIText> TextPool = new(() => new());
- L830 C136: new() :: private static readonly ConcurrentQueue<(Vector3 pos, string text, ColorF4 color, float scale)> DebugTextUpdateQueue = new();


## XRENGINE/Engine/Engine.Rendering.SecondaryContext.cs
- L22 C84: new() :: private readonly ConcurrentQueue<Action<AbstractRenderer>> _jobs = new();
- L43 C28: new CancellationTokenSource :: _cts = new CancellationTokenSource();
- L44 C31: new Thread :: _thread = new Thread(() => RunContext(templateWindow, _cts.Token))
- L148 C36: new Vector2D :: options.Size = new Vector2D<int>(Math.Max(64, templateWindow.Window.Size.X / 8), Math.Max(64, templateWindow.Window.Size.Y / 8));
- L156 C34: new XRWindow :: var window = new XRWindow(options, templateWindow.UseNativeTitleBar);
- L170 C46: new ManagementObjectSearcher :: using var searcher = new ManagementObjectSearcher(gpuQuery);
- L185 C75: new() :: public static SecondaryGpuContext SecondaryContext { get; } = new();
- L187 C89: new List :: public static IReadOnlyList<string> RecommendedSecondaryGpuTasks { get; } = new List<string>


## XRENGINE/Engine/Engine.Rendering.Settings.cs
- L33 C55: new() :: private static EngineSettings _settings = new();
- L58 C42: new EngineSettings :: _settings = value ?? new EngineSettings();
- L1171 C78: new() :: private PhysicsGpuMemorySettings _physicsGpuMemorySettings = new();
- L1183 C78: new() :: private PhysicsVisualizeSettings _physicsVisualizeSettings = new();


## XRENGINE/Engine/Engine.Rendering.State.cs
- L37 C98: new() :: private static Stack<XRRenderPipelineInstance> RenderingPipelineStack { get; } = new();
- L142 C72: new() :: private static Stack<uint> TransformIdStack { get; } = new();
- L216 C31: new TaskCompletionSource :: var tcs = new TaskCompletionSource<float>();
- L224 C31: new TaskCompletionSource :: var tcs = new TaskCompletionSource<ColorF4>();


## XRENGINE/Engine/Engine.State.cs
- L44 C25: new JobManager :: _jobs = new JobManager();
- L70 C20: new JobManager :: Jobs = new JobManager(
- L83 C71: new GameState :: => LoadOrGenerateAsset(() => generateFactory?.Invoke() ?? new GameState(), assetName, allowLoading);
- L94 C60: new() :: bool allowLoading = true) where T : GameState, new()
- L95 C71: new T :: => LoadOrGenerateAsset(() => generateFactory?.Invoke() ?? new T(), assetName, allowLoading);
- L100 C70: new() :: bool allowLoading = true) where T : GameStartupSettings, new()
- L101 C71: new T :: => LoadOrGenerateAsset(() => generateFactory?.Invoke() ?? new T(), assetName, allowLoading);
- L107 C61: new() :: params string[] folderNames) where T : XRAsset, new()
- L142 C20: new GameStartupSettings :: return new GameStartupSettings()
- L146 C21: new() :: new()
- L149 C39: new Scene.XRWorld :: TargetWorld = new Scene.XRWorld(),
- L157 C39: new UserSettings :: DefaultUserSettings = new UserSettings()
- L179 C76: new LocalPlayerController :: public static LocalPlayerController?[] LocalPlayers { get; } = new LocalPlayerController[4];
- L255 C27: new ArgumentException :: throw new ArgumentException($"Controller type {controllerType.FullName} must inherit from LocalPlayerController", nameof(controllerType));
- L258 C67: new[] :: var ctorWithIndex = controllerType.GetConstructor(new[] { typeof(ELocalPlayerIndex) });
- L260 C51: new object :: player = ctorWithIndex.Invoke(new object[] { index }) as LocalPlayerController;
- L265 C27: new InvalidOperationException :: throw new InvalidOperationException($"Failed to instantiate controller of type {controllerType.FullName}");


## XRENGINE/Engine/Engine.TickList.cs
- L34 C81: new() :: private readonly ConcurrentQueue<(bool Add, DelTick Func)> _queue = new();


## XRENGINE/Engine/Engine.Time.cs
- L39 C56: new EngineTimer :: public static EngineTimer Timer { get; } = new EngineTimer();
- L41 C38: new EngineTimer :: static Time() => Timer = new EngineTimer();


## XRENGINE/Engine/Engine.VRState.cs
- L34 C46: new VR :: public static VR Api => _api ??= new VR();
- L51 C87: new VRIKCalibrator.Settings :: public static VRIKCalibrator.Settings CalibrationSettings { get; set; } = new VRIKCalibrator.Settings();
- L191 C33: new OpenXRAPI :: _openXR ??= new OpenXRAPI();
- L417 C42: new XRRenderPipelineInstance :: _twoPassRenderPipeline = new XRRenderPipelineInstance(new DefaultRenderPipeline(false));
- L417 C71: new DefaultRenderPipeline :: _twoPassRenderPipeline = new XRRenderPipelineInstance(new DefaultRenderPipeline(false));
- L431 C111: new XRViewport :: VRLeftEyeRenderTarget = MakeTwoPassFBO(rW, rH, VRLeftEyeViewTexture = left, LeftEyeViewport = new XRViewport(window)
- L437 C115: new XRViewport :: VRRightEyeRenderTarget = MakeTwoPassFBO(rW, rH, VRRightEyeViewTexture = right, RightEyeViewport = new XRViewport(window)
- L450 C64: new XRViewport :: SetViewportParameters(rW, rH, StereoViewport = new XRViewport(window));
- L451 C49: new DefaultRenderPipeline :: StereoViewport.RenderPipeline = new DefaultRenderPipeline(true);
- L457 C38: new XRTexture2DArray :: var outputTextures = new XRTexture2DArray(left, right)
- L463 C40: new XRFrameBuffer :: VRStereoRenderTarget = new XRFrameBuffer((outputTextures, EFrameBufferAttachment.ColorAttachment0, 0, -1));
- L464 C41: new XRTexture2DArrayView :: StereoLeftViewTexture = new XRTexture2DArrayView(outputTextures, 0u, 1u, 0u, 1u, ESizedInternalFormat.Rgb8, false, false);
- L465 C42: new XRTexture2DArrayView :: StereoRightViewTexture = new XRTexture2DArrayView(outputTextures, 0u, 1u, 1u, 1u, ESizedInternalFormat.Rgb8, false, false);
- L491 C45: new Frustum :: _stereoCullingFrustum = new Frustum((_combinedProjectionMatrix = ProjectionMatrixCombiner.CombineProjectionMatrices(leftProj, rightProj, leftEyeView, rightEyeView)).Inverted());
- L707 C26: new XRMaterialFrameBuffer :: var rt = new XRMaterialFrameBuffer(new XRMaterial([tex], ShaderHelper.UnlitTextureFragForward()!));
- L707 C52: new XRMaterial :: var rt = new XRMaterialFrameBuffer(new XRMaterial([tex], ShaderHelper.UnlitTextureFragForward()!));
- L755 C36: new VrManifest :: applications = new VrManifest[] { vrManifest }
- L764 C70: new() :: private static readonly JsonSerializerOptions JSonOpts = new()
- L819 C65: new() :: private static VRTextureBounds_t _singleTexBounds = new()
- L843 C48: new() :: private static Texture_t _eyeTex = new()
- L1006 C55: new() :: Compositor_FrameTiming currentFrame = new();
- L1007 C56: new() :: Compositor_FrameTiming previousFrame = new();
- L1096 C48: new() :: private static VRInputData _data = new();
- L1208 C27: new Exception :: throw new Exception("Failed to duplicate handle.");
- L1247 C32: new IntPtr :: memoryHandle = new IntPtr(memoryHandleValue);
- L1248 C35: new IntPtr :: semaphoreHandle = new IntPtr(semaphoreHandleValue);


## XRENGINE/Engine/Networking/Engine.BaseNetworkingManager.cs
- L141 C93: new() :: protected ConcurrentQueue<(ushort sequenceNum, byte[])> UdpSendQueue { get; } = new();
- L156 C68: new() :: private readonly CancellationTokenSource _consumeCts = new();
- L304 C39: new() :: UdpClient udpClient = new() { /*ExclusiveAddressUse = false*/ };
- L306 C37: new IPEndPoint :: MulticastEndPoint = new IPEndPoint(udpMulticastIP, udpMulticastPort);
- L325 C39: new IPEndPoint :: udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, upMulticastServerPort));
- L330 C78: new() :: private readonly ConcurrentDictionary<ushort, byte[]> _mustAck = new();
- L374 C92: new() :: private readonly ConcurrentQueue<(float timestamp, int bytes)> _bytesSentLog = new();
- L592 C38: new StateChangeInfo :: ReplicateStateChange(new StateChangeInfo(EStateChangeType.RemoteJobRequest, serialized), compress, resendOnFailedAck);
- L599 C38: new StateChangeInfo :: ReplicateStateChange(new StateChangeInfo(EStateChangeType.RemoteJobResponse, serialized), compress, resendOnFailedAck);
- L606 C38: new StateChangeInfo :: ReplicateStateChange(new StateChangeInfo(EStateChangeType.ServerError, serialized), compress, resendOnFailedAck);
- L613 C38: new StateChangeInfo :: ReplicateStateChange(new StateChangeInfo(EStateChangeType.HumanoidPoseFrame, serialized), compress, resendOnFailedAck);
- L619 C38: new StateChangeInfo :: ReplicateStateChange(new StateChangeInfo(type, serialized), compress, resendOnFailedAck);
- L624 C66: new() :: private SevenZip.Compression.LZMA.Encoder _encoder = new();
- L625 C66: new() :: private SevenZip.Compression.LZMA.Decoder _decoder = new();
- L626 C50: new() :: private MemoryStream _compStreamIn = new();
- L627 C51: new() :: private MemoryStream _compStreamOut = new();
- L628 C52: new() :: private MemoryStream _decompStreamIn = new();
- L629 C53: new() :: private MemoryStream _decompStreamOut = new();
- L666 C41: new byte :: byte[] uncompData = new byte[GuidLen + uncompDataLen];
- L673 C31: new byte :: allData = new byte[HeaderLen + compDataLen];
- L680 C31: new byte :: allData = new byte[HeaderLen + GuidLen + uncompDataLen];
- L729 C51: new sequence :: return true; // This is a new sequence we haven't seen before
- L740 C36: new List :: var tempList = new List<ushort>(_receivedRemoteSequences.Cast<ushort>());
- L795 C46: new byte :: protected byte[] _decompBuffer = new byte[400000];
- L804 C39: new byte :: byte[] protocol = new byte[3];
- L939 C21: new Guid :: new Guid([.. inBuf.Skip(dataOffset).Take(GuidLen)]),
- L951 C21: new Guid :: new Guid([.. decompBuffer.Take(GuidLen)]),
- L1029 C48: new byte :: byte[] slice = new byte[dataLen];
- L1092 C40: new RemoteJobResponse :: var enriched = new RemoteJobResponse
- L1107 C48: new RemoteJobResponse :: BroadcastRemoteJobResponse(new RemoteJobResponse
- L1150 C32: new FileInfo :: var fileInfo = new FileInfo(filePath);
- L1153 C42: new() :: using TcpClient client = new();
- L1160 C33: new byte :: byte[] buffer = new byte[8192];
- L1175 C42: new() :: using TcpClient client = new();
- L1180 C33: new byte :: byte[] buffer = new byte[8192];
- L1197 C38: new byte :: byte[] lengthBytes = new byte[8];
- L1200 C33: new byte :: byte[] buffer = new byte[8192];
- L1218 C38: new byte :: byte[] lengthBytes = new byte[8];
- L1221 C33: new byte :: byte[] buffer = new byte[8192];
- L1370 C20: new IdValue :: return new IdValue(value.idStr, value.value);


## XRENGINE/Engine/Networking/Engine.ClientNetworkingManager.cs
- L38 C82: new() :: private readonly Dictionary<int, RemotePlayerState> _remotePlayers = new();
- L39 C65: new() :: private readonly HashSet<int> _localServerIndices = new();
- L87 C29: new UdpClient :: UdpSender = new UdpClient();
- L88 C28: new IPEndPoint :: ServerIP = new IPEndPoint(serverIP, udpMulticastServerPort);
- L183 C45: new() :: PlayerJoinRequest request = new()
- L208 C37: new PlayerHeartbeat :: var heartbeat = new PlayerHeartbeat
- L234 C36: new PlayerInputSnapshot :: var snapshot = new PlayerInputSnapshot
- L261 C48: new() :: PlayerTransformUpdate update = new()
- L286 C33: new PlayerLeaveNotice :: var leave = new PlayerLeaveNotice
- L367 C33: new() :: XRWorld world = new()
- L508 C101: new WorldSyncDescriptor :: XRWorldInstance? worldInstance = ResolvePrimaryWorldInstance() ?? EnsureClientWorld(new WorldSyncDescriptor());
- L516 C34: new RemotePlayerController :: var controller = new RemotePlayerController(serverPlayerIndex)
- L524 C30: new RemotePlayerState :: var remote = new RemotePlayerState(serverPlayerIndex, controller, pawn);
- L536 C28: new SceneNode :: var node = new SceneNode(worldInstance, nodeName);


## XRENGINE/Engine/Networking/Engine.ServerNetworkingManager.cs
- L23 C51: new() :: private readonly object _playerLock = new();
- L24 C89: new() :: private readonly Dictionary<int, NetworkPlayerConnection> _playersByIndex = new();
- L48 C38: new() :: UdpClient listener = new();
- L50 C38: new IPEndPoint :: listener.Client.Bind(new IPEndPoint(IPAddress.Any, udpPort));
- L113 C38: new NetworkPlayerConnection :: connection = new NetworkPlayerConnection
- L141 C34: new PlayerAssignment :: var assignment = new PlayerAssignment
- L173 C44: new GameMode :: worldInstance.GameMode ??= new GameMode { WorldInstance = worldInstance };
- L181 C38: new RemotePlayerController :: var controller = new RemotePlayerController(connection.ServerPlayerIndex)
- L194 C28: new SceneNode :: var node = new SceneNode(worldInstance, nodeName);
- L308 C38: new ServerConnectionInfo :: .Select(p => new ServerConnectionInfo(p.ServerPlayerIndex, p.ClientId, p.LastHeardUtc))
- L333 C28: new WorldSyncDescriptor :: return new WorldSyncDescriptor();
- L336 C24: new WorldSyncDescriptor :: return new WorldSyncDescriptor
- L399 C29: new PlayerLeaveNotice :: var leave = new PlayerLeaveNotice
- L412 C29: new ServerErrorMessage :: var error = new ServerErrorMessage


## XRENGINE/Engine/Networking/HumanoidPoseSync.cs
- L38 C71: new HumanoidQuantizationSettings :: public static HumanoidQuantizationSettings Default { get; } = new HumanoidQuantizationSettings();
- L47 C68: new HumanoidPoseDeltaSettings :: public static HumanoidPoseDeltaSettings Default { get; } = new HumanoidPoseDeltaSettings();
- L129 C47: new QuantizedTrackerPose :: QuantizedTrackerPose[] trackers = new QuantizedTrackerPose[TrackerCount];
- L137 C20: new QuantizedHumanoidPose :: return new QuantizedHumanoidPose(root, trackers);
- L151 C34: new Vector3 :: Vector3[] trackers = new Vector3[TrackerCount];
- L155 C20: new HumanoidPoseSample :: return new HumanoidPoseSample(
- L306 C27: new InvalidOperationException :: throw new InvalidOperationException("Humanoid delta exceeded reserved buffer size.");
- L346 C47: new QuantizedTrackerPose :: QuantizedTrackerPose[] trackers = new QuantizedTrackerPose[TrackerCount];
- L355 C22: new HumanoidPoseAvatarHeader :: header = new HumanoidPoseAvatarHeader(entity, flags, sequence);
- L356 C20: new QuantizedHumanoidPose :: pose = new QuantizedHumanoidPose(root, trackers);
- L385 C22: new HumanoidPoseAvatarHeader :: header = new HumanoidPoseAvatarHeader(entity, flags, 0);
- L420 C24: new QuantizedRootPose :: root = new QuantizedRootPose(baseline.Root.SectorX, baseline.Root.SectorZ, lx, ly, lz, yaw);
- L423 C47: new QuantizedTrackerPose :: QuantizedTrackerPose[] trackers = new QuantizedTrackerPose[TrackerCount];
- L455 C31: new QuantizedTrackerPose :: trackers[i] = new QuantizedTrackerPose(
- L461 C20: new QuantizedHumanoidPose :: pose = new QuantizedHumanoidPose(root, trackers);
- L473 C34: new() :: List<byte> scratch = new();
- L481 C23: new ArgumentException :: throw new ArgumentException($"Quantized pose must include exactly {TrackerCount} tracker positions.", nameof(pose));
- L509 C20: new QuantizedRootPose :: return new QuantizedRootPose(sectorX, sectorZ, localX, localY, localZ, yaw);
- L517 C20: new QuantizedTrackerPose :: return new QuantizedTrackerPose(x, y, z);
- L641 C47: new() :: private readonly List<byte> _buffer = new();
- L691 C23: new InvalidOperationException :: throw new InvalidOperationException("BeginFrame must be called before building a humanoid pose frame.");
- L693 C39: new() :: HumanoidPoseFrame frame = new()
- L708 C23: new InvalidOperationException :: throw new InvalidOperationException($"BeginFrame must be called with kind '{kind}' before adding avatars.");


## XRENGINE/Engine/Networking/RemoteJobNetworkingTransport.cs
- L15 C105: new() :: private readonly ConcurrentDictionary<Guid, TaskCompletionSource<RemoteJobResponse>> _pending = new();
- L20 C47: new ArgumentNullException :: _networking = networking ?? throw new ArgumentNullException(nameof(networking));
- L30 C23: new TaskCompletionSource :: var tcs = new TaskCompletionSource<RemoteJobResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
- L32 C23: new InvalidOperationException :: throw new InvalidOperationException($"A remote request with id {request.JobId} is already pending.");
- L43 C35: new RemoteJobRequest :: var enrichedRequest = new RemoteJobRequest
- L78 C37: new InvalidOperationException :: tcs.TrySetException(new InvalidOperationException(response.Error ?? "Remote job failed."));


## XRENGINE/Engine/ProjectionMatrixCombiner.cs
- L23 C30: new List :: var allCorners = new List<Vector3>(corners1);
- L35 C40: new Vector3 :: Vector3[] frustumCorners = new Vector3[8];
- L43 C56: new Vector4 :: Vector4 ws = Vector4.Transform(new Vector4(x, y, z, 1.0f), invViewProj);


## XRENGINE/Engine/SnapshotAssetReference.cs
- L29 C12: new() :: => new()
- L80 C46: new object :: if (method.Invoke(Engine.Assets, new object[] { AssetPath! }) is XRAsset asset)


## XRENGINE/Engine/SnapshotBinarySerializer.cs
- L12 C76: new() :: private static readonly CookedBinarySerializationCallbacks Callbacks = new()


## XRENGINE/Engine/SnapshotYamlSerializer.cs
- L23 C27: new SerializerBuilder :: var builder = new SerializerBuilder()
- L27 C50: new DepthTrackingEventEmitter :: .WithEventEmitter(nextEmitter => new DepthTrackingEventEmitter(nextEmitter))
- L28 C45: new SafeComponentTypeInspector :: .WithTypeInspector(inner => new SafeComponentTypeInspector(inner))
- L47 C41: new SafePropertyDescriptor :: yield return wrap ? new SafePropertyDescriptor(descriptor, type) : descriptor;
- L56 C24: new SafePropertyDescriptor :: return new SafePropertyDescriptor(descriptor, type);
- L68 C121: new() :: private static readonly ConcurrentDictionary<(Type DeclaringType, string PropertyName), byte> LoggedSkips = new();
- L120 C28: new ObjectDescriptor :: return new ObjectDescriptor(fallback, propertyType, propertyType);


## XRENGINE/Engine/StateObject.cs
- L7 C82: new() :: private static readonly ResourcePool<StateObject> _statePool = new(() => new());


## XRENGINE/Engine/WorldStateSnapshot.cs
- L67 C36: new Dictionary :: var serializedScenes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
- L120 C24: new WorldStateSnapshot :: return new WorldStateSnapshot(
- L131 C24: new WorldStateSnapshot :: return new WorldStateSnapshot(
- L210 C42: new HashSet :: var processedSceneKeys = new HashSet<string>(StringComparer.Ordinal);
- L311 C58: new List :: scene.RootNodes = restoredScene.RootNodes ?? new List<SceneNode>();


## XRENGINE/FontGlyphSet.cs
- L60 C33: new() :: using Library lib = new();
- L145 C35: new() :: using SKPaint paint = new()
- L168 C35: new ushort :: ushort[] glyphs = new ushort[character.Length];
- L176 C34: new float :: float[] widths = new float[glyphs.Length];
- L177 C35: new SKRect :: SKRect[] bounds = new SKRect[glyphs.Length];
- L205 C21: new IVector2 :: new IVector2(width, height),
- L206 C21: new Vector2 :: new Vector2(-glyphBounds.Left, -glyphBounds.Top))));
- L242 C51: new Vector2 :: glyphInfos[i].info.Position = new Vector2(x, y);
- L254 C21: new XRTexture2D :: Atlas = new XRTexture2D(outputAtlasPath)
- L326 C23: new InvalidOperationException :: throw new InvalidOperationException("Glyphs are not initialized.");
- L329 C23: new InvalidOperationException :: throw new InvalidOperationException("Atlas is not initialized.");
- L334 C17: new IVector2 :: new IVector2((int)Atlas.Width, (int)Atlas.Height),
- L465 C23: new InvalidOperationException :: throw new InvalidOperationException("Glyphs are not initialized.");
- L468 C23: new InvalidOperationException :: throw new InvalidOperationException("Atlas is not initialized.");
- L473 C17: new IVector2 :: new IVector2((int)Atlas.Width, (int)Atlas.Height),
- L640 C23: new InvalidOperationException :: throw new InvalidOperationException("Glyphs are not initialized.");
- L708 C23: new InvalidOperationException :: throw new InvalidOperationException("Glyphs are not initialized.");
- L734 C20: new Vector2 :: return new Vector2(width, height);


## XRENGINE/GameMode.cs
- L31 C27: new ArgumentException :: throw new ArgumentException("Default player controller must inherit from LocalPlayerController", nameof(value));
- L47 C27: new ArgumentException :: throw new ArgumentException("Default player pawn must inherit from PawnComponent", nameof(value));
- L174 C28: new SceneNode :: var pawnNode = new SceneNode(WorldInstance, pawnNodeName);
- L276 C54: new Queue :: PossessionQueue[pawnComponent] = new Queue<ELocalPlayerIndex>();


## XRENGINE/GameStartupSettings.cs
- L18 C48: new() :: private BuildSettings _buildSettings = new();
- L24 C53: new() :: private UserSettings _defaultUserSettings = new();
- L165 C58: new BuildSettings :: set => SetField(ref _buildSettings, value ?? new BuildSettings());
- L206 C64: new() :: private OverrideableSetting<int> _jobWorkersOverride = new();
- L207 C66: new() :: private OverrideableSetting<int> _jobWorkerCapOverride = new();
- L208 C67: new() :: private OverrideableSetting<int> _jobQueueLimitOverride = new();
- L209 C78: new() :: private OverrideableSetting<int> _jobQueueWarningThresholdOverride = new();
- L210 C82: new() :: private OverrideableSetting<EOutputVerbosity> _outputVerbosityOverride = new();
- L211 C84: new() :: private OverrideableSetting<bool> _enableGpuIndirectDebugLoggingOverride = new();
- L212 C83: new() :: private OverrideableSetting<bool> _enableGpuIndirectCpuFallbackOverride = new();
- L213 C89: new() :: private OverrideableSetting<bool> _enableGpuIndirectValidationLoggingOverride = new();
- L214 C64: new() :: private OverrideableSetting<bool> _useGpuBvhOverride = new();
- L215 C70: new() :: private OverrideableSetting<uint> _bvhLeafMaxPrimsOverride = new();
- L216 C66: new() :: private OverrideableSetting<EBvhMode> _bvhModeOverride = new();
- L217 C77: new() :: private OverrideableSetting<bool> _bvhRefitOnlyWhenStableOverride = new();
- L218 C72: new() :: private OverrideableSetting<uint> _raycastBufferSizeOverride = new();
- L219 C80: new() :: private OverrideableSetting<bool> _enableGpuBvhTimingQueriesOverride = new();
- L222 C84: new() :: private OverrideableSetting<EAntiAliasingMode> _antiAliasingModeOverride = new();
- L223 C70: new() :: private OverrideableSetting<uint> _msaaSampleCountOverride = new();
- L224 C66: new() :: private OverrideableSetting<EVSyncMode> _vSyncOverride = new();
- L225 C96: new() :: private OverrideableSetting<EGlobalIlluminationMode> _globalIlluminationModeOverride = new();
- L226 C81: new() :: private OverrideableSetting<bool> _tickGroupedItemsInParallelOverride = new();
- L227 C71: new() :: private OverrideableSetting<bool> _enableNvidiaDlssOverride = new();
- L228 C78: new() :: private OverrideableSetting<EDlssQualityMode> _dlssQualityOverride = new();
- L229 C70: new() :: private OverrideableSetting<bool> _enableIntelXessOverride = new();
- L230 C78: new() :: private OverrideableSetting<EXessQualityMode> _xessQualityOverride = new();
- L233 C75: new() :: private OverrideableSetting<bool> _allowShaderPipelinesOverride = new();
- L234 C77: new() :: private OverrideableSetting<bool> _useIntegerWeightingIdsOverride = new();
- L235 C87: new() :: private OverrideableSetting<ELoopType> _recalcChildMatricesLoopTypeOverride = new();
- L236 C87: new() :: private OverrideableSetting<bool> _calculateSkinningInComputeShaderOverride = new();
- L237 C90: new() :: private OverrideableSetting<bool> _calculateBlendshapesInComputeShaderOverride = new();
- L239 C95: new() :: private OverrideableSetting<float> _transformReplicationKeyframeIntervalSecOverride = new();
- L240 C79: new() :: private OverrideableSetting<float> _timeBetweenReplicationsOverride = new();
- L252 C63: new() :: set => SetField(ref _jobWorkersOverride, value ?? new());
- L265 C65: new() :: set => SetField(ref _jobWorkerCapOverride, value ?? new());
- L278 C66: new() :: set => SetField(ref _jobQueueLimitOverride, value ?? new());
- L291 C77: new() :: set => SetField(ref _jobQueueWarningThresholdOverride, value ?? new());
- L304 C68: new() :: set => SetField(ref _outputVerbosityOverride, value ?? new());
- L317 C82: new() :: set => SetField(ref _enableGpuIndirectDebugLoggingOverride, value ?? new());
- L330 C81: new() :: set => SetField(ref _enableGpuIndirectCpuFallbackOverride, value ?? new());
- L343 C87: new() :: set => SetField(ref _enableGpuIndirectValidationLoggingOverride, value ?? new());
- L355 C92: new() :: set => SetField(ref _transformReplicationKeyframeIntervalSecOverride, value ?? new());
- L367 C76: new() :: set => SetField(ref _timeBetweenReplicationsOverride, value ?? new());
- L379 C62: new() :: set => SetField(ref _useGpuBvhOverride, value ?? new());
- L391 C68: new() :: set => SetField(ref _bvhLeafMaxPrimsOverride, value ?? new());
- L403 C60: new() :: set => SetField(ref _bvhModeOverride, value ?? new());
- L415 C75: new() :: set => SetField(ref _bvhRefitOnlyWhenStableOverride, value ?? new());
- L427 C70: new() :: set => SetField(ref _raycastBufferSizeOverride, value ?? new());
- L439 C78: new() :: set => SetField(ref _enableGpuBvhTimingQueriesOverride, value ?? new());
- L452 C69: new() :: set => SetField(ref _antiAliasingModeOverride, value ?? new());
- L465 C68: new() :: set => SetField(ref _msaaSampleCountOverride, value ?? new());
- L478 C58: new() :: set => SetField(ref _vSyncOverride, value ?? new());
- L491 C75: new() :: set => SetField(ref _globalIlluminationModeOverride, value ?? new());
- L504 C79: new() :: set => SetField(ref _tickGroupedItemsInParallelOverride, value ?? new());
- L517 C69: new() :: set => SetField(ref _enableNvidiaDlssOverride, value ?? new());
- L530 C64: new() :: set => SetField(ref _dlssQualityOverride, value ?? new());
- L543 C68: new() :: set => SetField(ref _enableIntelXessOverride, value ?? new());
- L556 C64: new() :: set => SetField(ref _xessQualityOverride, value ?? new());
- L569 C73: new() :: set => SetField(ref _allowShaderPipelinesOverride, value ?? new());
- L582 C75: new() :: set => SetField(ref _useIntegerWeightingIdsOverride, value ?? new());
- L595 C80: new() :: set => SetField(ref _recalcChildMatricesLoopTypeOverride, value ?? new());
- L608 C85: new() :: set => SetField(ref _calculateSkinningInComputeShaderOverride, value ?? new());
- L621 C88: new() :: set => SetField(ref _calculateBlendshapesInComputeShaderOverride, value ?? new());


## XRENGINE/Input/LocalPlayerController.cs
- L38 C70: new LocalInputInterface :: public LocalPlayerController(ELocalPlayerIndex index) : base(new LocalInputInterface((int)index))
- L43 C47: new LocalInputInterface :: public LocalPlayerController() : base(new LocalInputInterface(0))


## XRENGINE/Input/PawnController.cs
- L13 C72: new() :: protected readonly Queue<PawnComponent> _pawnPossessionQueue = new();


## XRENGINE/Input/PlayerControllerBase.cs
- L9 C42: new() :: private PlayerInfo _playerInfo = new();


## XRENGINE/Input/RemotePlayerController.cs
- L15 C69: new ServerInputInterface :: public RemotePlayerController(int serverPlayerIndex) : base(new ServerInputInterface(serverPlayerIndex))


## XRENGINE/Jobs/ActionJob.cs
- L16 C39: new ArgumentNullException :: _action = action ?? throw new ArgumentNullException(nameof(action));


## XRENGINE/Jobs/CoroutineJob.cs
- L15 C35: new ArgumentNullException :: _tick = tick ?? throw new ArgumentNullException(nameof(tick));


## XRENGINE/Jobs/EnumeratorJob.cs
- L17 C55: new ArgumentNullException :: _routineFactory = routine is null ? throw new ArgumentNullException(nameof(routine)) : () => routine;
- L29 C55: new ArgumentNullException :: _routineFactory = routineFactory ?? throw new ArgumentNullException(nameof(routineFactory));


## XRENGINE/Jobs/Job.cs
- L32 C50: new() :: private readonly object _lifecycleLock = new();
- L101 C24: new CancellationTokenSource :: _cts = new CancellationTokenSource();
- L108 C35: new Stack :: _executionStack = new Stack<IEnumerator>();
- L109 C50: new InvalidOperationException :: var routine = Process() ?? throw new InvalidOperationException("Job routine cannot be null.");
- L168 C22: new JobHandle :: Handle = new JobHandle(_id, completionSource.Task, this);
- L275 C53: new InvalidOperationException :: return AttachTask(task ?? throw new InvalidOperationException("Task factory returned null."));
- L307 C72: new InvalidOperationException :: var ex = aggregate?.GetBaseException() ?? aggregate ?? new InvalidOperationException("Job task faulted without an exception.");


## XRENGINE/Jobs/JobManager.cs
- L33 C13: new() :: new(), // Lowest
- L34 C13: new() :: new(), // Low
- L35 C13: new() :: new(), // Normal
- L36 C13: new() :: new(), // High
- L37 C13: new() :: new(), // Highest
- L41 C13: new() :: new(), // Lowest
- L42 C13: new() :: new(), // Low
- L43 C13: new() :: new(), // Normal
- L44 C13: new() :: new(), // High
- L45 C13: new() :: new(), // Highest
- L49 C13: new() :: new(), // Lowest
- L50 C13: new() :: new(), // Low
- L51 C13: new() :: new(), // Normal
- L52 C13: new() :: new(), // High
- L53 C13: new() :: new(), // Highest
- L57 C13: new() :: new(), // Lowest
- L58 C13: new() :: new(), // Low
- L59 C13: new() :: new(), // Normal
- L60 C13: new() :: new(), // High
- L61 C13: new() :: new(), // Highest
- L63 C46: new() :: private readonly List<Job> _active = new();
- L64 C47: new() :: private readonly object _activeLock = new();
- L66 C49: new int :: private readonly int[] _pendingCounts = new int[PriorityLevels];
- L67 C59: new int :: private readonly int[] _pendingMainThreadCounts = new int[PriorityLevels];
- L68 C56: new int :: private readonly int[] _pendingCollectCounts = new int[PriorityLevels];
- L69 C55: new int :: private readonly int[] _pendingRemoteCounts = new int[PriorityLevels];
- L70 C51: new long :: private readonly long[] _totalWaitTicks = new long[PriorityLevels];
- L71 C48: new long :: private readonly long[] _waitSamples = new long[PriorityLevels];
- L72 C58: new long :: private readonly long[] _lastQueueWarningTicks = new long[PriorityLevels];
- L80 C65: new() :: private readonly ConcurrentQueue<Job> _deferredBySlot = new();
- L81 C57: new() :: private readonly CancellationTokenSource _cts = new();
- L83 C53: new() :: private readonly object _remoteWorkerLock = new();
- L86 C55: new() :: private readonly object _deferredWorkerLock = new();
- L110 C31: new SemaphoreSlim :: _queueSlots = new SemaphoreSlim(_maxQueueSize, _maxQueueSize);
- L112 C24: new Thread :: _workers = new Thread[count];
- L116 C31: new Thread :: _workers[i] = new Thread(WorkerLoop)
- L191 C23: new InvalidOperationException :: throw new InvalidOperationException("Job has already been scheduled or completed.");
- L197 C36: new TaskCompletionSource :: var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
- L303 C23: new EnumeratorJob :: var job = new EnumeratorJob(routine, progress, completed, error, canceled, progressWithPayload);
- L318 C23: new EnumeratorJob :: var job = new EnumeratorJob(routineFactory, progress, completed, error, canceled, progressWithPayload);
- L327 C54: new InvalidOperationException :: var transport = RemoteTransport ?? throw new InvalidOperationException("Remote transport has not been configured.");
- L328 C23: new TaskCompletionSource :: var tcs = new TaskCompletionSource<RemoteJobResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
- L329 C23: new RemoteDispatchJob :: var job = new RemoteDispatchJob(request, transport, tcs);


## XRENGINE/Jobs/JobProgress.cs
- L17 C20: new JobProgress :: return new JobProgress(progress, payload);


## XRENGINE/Jobs/RemoteJobTypes.cs
- L49 C16: new() :: => new() { JobId = id, Success = false, Error = message };


## XRENGINE/Jobs/WaitForNextDispatch.cs
- L8 C63: new() :: public static readonly WaitForNextDispatch Instance = new();


## XRENGINE/ModelImporter.cs
- L45 C23: new AssimpContext :: _assimp = new AssimpContext();
- L77 C30: new JobProgress :: yield return new JobProgress(1f, result);
- L100 C88: new() :: private readonly ConcurrentDictionary<string, XRTexture2D> _texturePathCache = new();
- L113 C23: new XRMaterial :: var mat = new XRMaterial(new XRTexture?[textures.Count]);
- L113 C38: new XRTexture :: var mat = new XRMaterial(new XRTexture?[textures.Count]);
- L126 C25: new ShaderFloat :: new ShaderFloat(1.0f, "Opacity"),
- L127 C25: new ShaderFloat :: new ShaderFloat(1.0f, "Specular"),
- L128 C25: new ShaderFloat :: new ShaderFloat(0.9f, "Roughness"),
- L129 C25: new ShaderFloat :: new ShaderFloat(0.0f, "Metallic"),
- L130 C25: new ShaderFloat :: new ShaderFloat(1.0f, "IndexOfRefraction"),
- L140 C21: new ShaderVector3 :: new ShaderVector3(ColorF3.Magenta, "BaseColor"),
- L141 C21: new ShaderFloat :: new ShaderFloat(1.0f, "Opacity"),
- L142 C21: new ShaderFloat :: new ShaderFloat(1.0f, "Specular"),
- L143 C21: new ShaderFloat :: new ShaderFloat(1.0f, "Roughness"),
- L144 C21: new ShaderFloat :: new ShaderFloat(0.0f, "Metallic"),
- L145 C21: new ShaderFloat :: new ShaderFloat(1.0f, "IndexOfRefraction"),
- L151 C33: new RenderingParameters :: mat.RenderOptions = new RenderingParameters()
- L154 C29: new DepthTest :: DepthTest = new DepthTest()
- L192 C39: new XRTexture :: XRTexture[] textureList = new XRTexture[textures.Count];
- L225 C91: new() :: private readonly ConcurrentDictionary<string, bool> _missingTexturePathWarnings = new();
- L290 C39: new() :: XRTexture2D placeholder = new()
- L305 C40: new Mipmap2D :: placeholder.Mipmaps = [new Mipmap2D(new MagickImage(XRTexture2D.FillerImage))];
- L305 C53: new MagickImage :: placeholder.Mipmaps = [new Mipmap2D(new MagickImage(XRTexture2D.FillerImage))];
- L353 C85: new() :: private readonly ConcurrentDictionary<string, MagickImage?> _textureCache = new();
- L385 C34: new ModelImporter :: using var importer = new ModelImporter(path, onCompleted, materialFactory);
- L404 C23: new TaskCompletionSource :: var tcs = new TaskCompletionSource<(SceneNode?, IReadOnlyCollection<XRMaterial>, IReadOnlyCollection<XRMesh>)>(TaskCreationOptions.RunContinuationsAsynchronously);
- L443 C34: new ModelImporter :: using var importer = new ModelImporter(path, onCompleted: null, materialFactory);
- L471 C26: new ModelImporterResult :: var result = new ModelImporterResult(node, importer._materials, importer._meshes);
- L478 C129: new() :: private static readonly ConcurrentDictionary<(string path, string samplerName), XRTexture2D> _uberSamplerTextureCache = new();
- L484 C27: new XRTexture2D :: var tex = new XRTexture2D
- L500 C36: new Mipmap2D :: tex.Mipmaps = [new Mipmap2D(new MagickImage(XRTexture2D.FillerImage))];
- L500 C49: new MagickImage :: tex.Mipmaps = [new Mipmap2D(new MagickImage(XRTexture2D.FillerImage))];
- L585 C25: new ShaderFloat :: new ShaderFloat(1.0f, "Opacity"),
- L586 C25: new ShaderFloat :: new ShaderFloat(1.0f, "Specular"),
- L587 C25: new ShaderFloat :: new ShaderFloat(0.9f, "Roughness"),
- L588 C25: new ShaderFloat :: new ShaderFloat(0.0f, "Metallic"),
- L589 C25: new ShaderFloat :: new ShaderFloat(1.0f, "IndexOfRefraction"),
- L600 C21: new ShaderVector3 :: new ShaderVector3(ColorF3.Magenta, "BaseColor"),
- L601 C21: new ShaderFloat :: new ShaderFloat(1.0f, "Opacity"),
- L602 C21: new ShaderFloat :: new ShaderFloat(1.0f, "Specular"),
- L603 C21: new ShaderFloat :: new ShaderFloat(1.0f, "Roughness"),
- L604 C21: new ShaderFloat :: new ShaderFloat(0.0f, "Metallic"),
- L605 C21: new ShaderFloat :: new ShaderFloat(1.0f, "IndexOfRefraction"),
- L611 C33: new RenderingParameters :: mat.RenderOptions = new RenderingParameters()
- L614 C29: new DepthTest :: DepthTest = new DepthTest()
- L677 C21: new ShaderFloat :: new ShaderFloat(1.0f, "MatSpecularIntensity"),
- L678 C21: new ShaderFloat :: new ShaderFloat(32.0f, "MatShininess"),
- L679 C21: new ShaderFloat :: new ShaderFloat(0.5f, "AlphaCutoff"), // Default alpha cutoff threshold
- L697 C21: new ShaderVector4 :: new ShaderVector4(new Vector4(1, 0, 1, 1), "MatColor"),
- L697 C39: new Vector4 :: new ShaderVector4(new Vector4(1, 0, 1, 1), "MatColor"),
- L698 C21: new ShaderFloat :: new ShaderFloat(1.0f, "MatSpecularIntensity"),
- L699 C21: new ShaderFloat :: new ShaderFloat(32.0f, "MatShininess"),
- L700 C21: new ShaderFloat :: new ShaderFloat(0.5f, "AlphaCutoff"),
- L706 C33: new RenderingParameters :: mat.RenderOptions = new RenderingParameters()
- L709 C29: new DepthTest :: DepthTest = new DepthTest()
- L761 C17: new ShaderVector4 :: new ShaderVector4(new Vector4(1, 1, 1, 1), "_Color"),
- L761 C35: new Vector4 :: new ShaderVector4(new Vector4(1, 1, 1, 1), "_Color"),
- L762 C17: new ShaderVector4 :: new ShaderVector4(new Vector4(1, 1, 0, 0), "_MainTex_ST"),
- L762 C35: new Vector4 :: new ShaderVector4(new Vector4(1, 1, 0, 0), "_MainTex_ST"),
- L763 C17: new ShaderVector2 :: new ShaderVector2(Vector2.Zero, "_MainTexPan"),
- L764 C17: new ShaderInt :: new ShaderInt(0, "_MainTexUV"),
- L766 C17: new ShaderVector4 :: new ShaderVector4(new Vector4(1, 1, 0, 0), "_BumpMap_ST"),
- L766 C35: new Vector4 :: new ShaderVector4(new Vector4(1, 1, 0, 0), "_BumpMap_ST"),
- L767 C17: new ShaderVector2 :: new ShaderVector2(Vector2.Zero, "_BumpMapPan"),
- L768 C17: new ShaderInt :: new ShaderInt(0, "_BumpMapUV"),
- L769 C17: new ShaderFloat :: new ShaderFloat(bumpScale, "_BumpScale"),
- L771 C17: new ShaderFloat :: new ShaderFloat(1.0f, "_ShadingEnabled"),
- L772 C17: new ShaderInt :: new ShaderInt(6, "_LightingMode"),
- L773 C17: new ShaderVector3 :: new ShaderVector3(new Vector3(1, 1, 1), "_LightingShadowColor"),
- L773 C35: new Vector3 :: new ShaderVector3(new Vector3(1, 1, 1), "_LightingShadowColor"),
- L774 C17: new ShaderFloat :: new ShaderFloat(1.0f, "_ShadowStrength"),
- L775 C17: new ShaderFloat :: new ShaderFloat(0.0f, "_LightingMinLightBrightness"),
- L776 C17: new ShaderFloat :: new ShaderFloat(0.0f, "_LightingMonochromatic"),
- L777 C17: new ShaderFloat :: new ShaderFloat(0.0f, "_LightingCapEnabled"),
- L778 C17: new ShaderFloat :: new ShaderFloat(10.0f, "_LightingCap"),
- L780 C17: new ShaderInt :: new ShaderInt(0, "_MainAlphaMaskMode"),
- L781 C17: new ShaderFloat :: new ShaderFloat(0.0f, "_AlphaMod"),
- L782 C17: new ShaderFloat :: new ShaderFloat(1.0f, "_AlphaForceOpaque"),
- L783 C17: new ShaderFloat :: new ShaderFloat(0.5f, "_Cutoff"),
- L784 C17: new ShaderInt :: new ShaderInt(0, "_Mode"),
- L789 C33: new RenderingParameters :: mat.RenderOptions = new RenderingParameters()
- L792 C29: new DepthTest :: DepthTest = new DepthTest()
- L868 C31: new BooleanPropertyConfig :: _assimp.SetConfig(new BooleanPropertyConfig(AiConfigs.AI_CONFIG_IMPORT_FBX_PRESERVE_PIVOTS, preservePivots));
- L869 C31: new BooleanPropertyConfig :: _assimp.SetConfig(new BooleanPropertyConfig(AiConfigs.AI_CONFIG_IMPORT_FBX_READ_MATERIALS, true));
- L870 C31: new BooleanPropertyConfig :: _assimp.SetConfig(new BooleanPropertyConfig(AiConfigs.AI_CONFIG_IMPORT_FBX_READ_TEXTURES, true));
- L871 C31: new BooleanPropertyConfig :: _assimp.SetConfig(new BooleanPropertyConfig(AiConfigs.AI_CONFIG_GLOB_MULTITHREADING, multiThread));
- L1066 C33: new NodeTransformInfo :: _nodeTransforms.Add(new NodeTransformInfo(
- L1162 C27: new() :: Model model = new();
- L1170 C19: new ConcurrentDictionary :: ? new ConcurrentDictionary<int, SubMesh>()
- L1175 C33: new int :: int[] ordered = new int[count];
- L1236 C26: new XRMesh :: var xrMesh = new XRMesh(mesh, _assimp, _nodeCache, dataTransform);


## XRENGINE/Models/Gaussian/GaussianSplatCloud.cs
- L71 C23: new AABB :: _bounds = new AABB(min, max);
- L75 C43: new AABB :: _bounds = AABB.Union(_bounds, new AABB(min, max));
- L96 C19: new AABB :: _bounds = new AABB(min, max);
- L114 C20: new GaussianSplatCloud :: return new GaussianSplatCloud();
- L142 C56: new Quaternion :: Quaternion rotation = Quaternion.Normalize(new Quaternion(rotationRaw.X, rotationRaw.Y, rotationRaw.Z, rotationRaw.W));
- L150 C24: new GaussianSplat :: splats.Add(new GaussianSplat(position, scale, rotation, color, opacity));
- L153 C21: new GaussianSplatCloud :: var cloud = new GaussianSplatCloud(splats);


## XRENGINE/Models/Materials/Options/BlendMode.cs
- L56 C57: new() :: public static BlendMode EnabledTransparent() => new()
- L66 C52: new() :: public static BlendMode EnabledOpaque() => new()
- L76 C48: new() :: public static BlendMode Unchanged() => new()
- L80 C47: new() :: public static BlendMode Disabled() => new()


## XRENGINE/Models/Materials/Options/RenderingParameters.cs
- L14 C40: new() :: private DepthTest _depthTest = new();
- L15 C44: new() :: private StencilTest _stencilTest = new();


## XRENGINE/Models/Materials/Options/StencilTest.cs
- L10 C26: new() :: _frontFace = new(),
- L11 C25: new() :: _backFace = new();
- L26 C42: new StencilTestFace :: set => _frontFace = value ?? new StencilTestFace();
- L31 C41: new StencilTestFace :: set => _backFace = value ?? new StencilTestFace();


## XRENGINE/Models/Materials/Parameters/ShaderArray.cs
- L37 C44: new ShaderArrayValueHandler :: : base(name, owner) { _value = new ShaderArrayValueHandler<T>(); }
- L52 C22: new T :: Values = new T[count];


## XRENGINE/Models/Materials/Parameters/ShaderBVec2.cs
- L21 C40: new BoolVector2 :: public ShaderBVector2() : this(new BoolVector2(), NoName) { }
- L28 C31: new ShaderBool :: _fields.Add(".x", new ShaderBool(defaultValue.X, "X", this));
- L29 C31: new ShaderBool :: _fields.Add(".y", new ShaderBool(defaultValue.Y, "Y", this));


## XRENGINE/Models/Materials/Parameters/ShaderBVec4.cs
- L25 C40: new BoolVector4 :: public ShaderBVector4() : this(new BoolVector4(), NoName) { }
- L32 C31: new ShaderBool :: _fields.Add(".x", new ShaderBool(defaultValue.X, "X", this));
- L33 C31: new ShaderBool :: _fields.Add(".y", new ShaderBool(defaultValue.Y, "Y", this));
- L34 C31: new ShaderBool :: _fields.Add(".z", new ShaderBool(defaultValue.Z, "Z", this));
- L35 C31: new ShaderBool :: _fields.Add(".w", new ShaderBool(defaultValue.W, "W", this));


## XRENGINE/Models/Materials/Parameters/ShaderBVector3.cs
- L24 C40: new BoolVector3 :: public ShaderBVector3() : this(new BoolVector3(), NoName) { }
- L31 C31: new ShaderBool :: _fields.Add(".x", new ShaderBool(defaultValue.X, "X", this));
- L32 C31: new ShaderBool :: _fields.Add(".y", new ShaderBool(defaultValue.Y, "Y", this));
- L33 C31: new ShaderBool :: _fields.Add(".z", new ShaderBool(defaultValue.Z, "Z", this));


## XRENGINE/Models/Materials/Parameters/ShaderDVector2.cs
- L21 C40: new DVector2 :: public ShaderDVector2() : this(new DVector2(), NoName) { }
- L28 C31: new ShaderDouble :: _fields.Add(".x", new ShaderDouble(defaultValue.X, "X", this));
- L29 C31: new ShaderDouble :: _fields.Add(".y", new ShaderDouble(defaultValue.Y, "Y", this));


## XRENGINE/Models/Materials/Parameters/ShaderDVector3.cs
- L21 C40: new DVector3 :: public ShaderDVector3() : this(new DVector3(), NoName) { }
- L28 C31: new ShaderDouble :: _fields.Add(".x", new ShaderDouble(defaultValue.X, "X", this));
- L29 C31: new ShaderDouble :: _fields.Add(".y", new ShaderDouble(defaultValue.Y, "Y", this));
- L30 C31: new ShaderDouble :: _fields.Add(".z", new ShaderDouble(defaultValue.Z, "Z", this));


## XRENGINE/Models/Materials/Parameters/ShaderDVector4.cs
- L21 C40: new DVector4 :: public ShaderDVector4() : this(new DVector4(), NoName) { }
- L28 C31: new ShaderDouble :: _fields.Add(".x", new ShaderDouble(defaultValue.X, "X", this));
- L29 C31: new ShaderDouble :: _fields.Add(".y", new ShaderDouble(defaultValue.Y, "Y", this));
- L30 C31: new ShaderDouble :: _fields.Add(".z", new ShaderDouble(defaultValue.Z, "Z", this));
- L31 C31: new ShaderDouble :: _fields.Add(".w", new ShaderDouble(defaultValue.W, "W", this));


## XRENGINE/Models/Materials/Parameters/ShaderIVec2.cs
- L21 C40: new IVector2 :: public ShaderIVector2() : this(new IVector2(), NoName) { }
- L28 C31: new ShaderInt :: _fields.Add(".x", new ShaderInt(defaultValue.X, "X", this));
- L29 C31: new ShaderInt :: _fields.Add(".y", new ShaderInt(defaultValue.Y, "Y", this));


## XRENGINE/Models/Materials/Parameters/ShaderIVec4.cs
- L25 C40: new IVector4 :: public ShaderIVector4() : this(new IVector4(), NoName) { }
- L32 C31: new ShaderInt :: _fields.Add(".x", new ShaderInt(defaultValue.X, "X", this));
- L33 C31: new ShaderInt :: _fields.Add(".y", new ShaderInt(defaultValue.Y, "Y", this));
- L34 C31: new ShaderInt :: _fields.Add(".z", new ShaderInt(defaultValue.Z, "Z", this));
- L35 C31: new ShaderInt :: _fields.Add(".w", new ShaderInt(defaultValue.W, "W", this));


## XRENGINE/Models/Materials/Parameters/ShaderIVector3.cs
- L21 C20: new IVector3 :: : this(new IVector3(), NoName) { }
- L28 C31: new ShaderDouble :: _fields.Add(".x", new ShaderDouble(defaultValue.X, "X", this));
- L29 C31: new ShaderDouble :: _fields.Add(".y", new ShaderDouble(defaultValue.Y, "Y", this));
- L30 C31: new ShaderDouble :: _fields.Add(".z", new ShaderDouble(defaultValue.Z, "Z", this));


## XRENGINE/Models/Materials/Parameters/ShaderUVec2.cs
- L21 C40: new UVector2 :: public ShaderUVector2() : this(new UVector2(), NoName) { }
- L28 C31: new ShaderUInt :: _fields.Add(".x", new ShaderUInt(defaultValue.X, "X", this));
- L29 C31: new ShaderUInt :: _fields.Add(".y", new ShaderUInt(defaultValue.Y, "Y", this));


## XRENGINE/Models/Materials/Parameters/ShaderUVec4.cs
- L25 C40: new UVector4 :: public ShaderUVector4() : this(new UVector4(), NoName) { }
- L32 C31: new ShaderUInt :: _fields.Add(".x", new ShaderUInt(defaultValue.X, "X", this));
- L33 C31: new ShaderUInt :: _fields.Add(".y", new ShaderUInt(defaultValue.Y, "Y", this));
- L34 C31: new ShaderUInt :: _fields.Add(".z", new ShaderUInt(defaultValue.Z, "Z", this));
- L35 C31: new ShaderUInt :: _fields.Add(".w", new ShaderUInt(defaultValue.W, "W", this));


## XRENGINE/Models/Materials/Parameters/ShaderUVector3.cs
- L24 C40: new UVector3 :: public ShaderUVector3() : this(new UVector3(), NoName) { }
- L31 C31: new ShaderUInt :: _fields.Add(".x", new ShaderUInt(defaultValue.X, "X", this));
- L32 C31: new ShaderUInt :: _fields.Add(".y", new ShaderUInt(defaultValue.Y, "Y", this));
- L33 C31: new ShaderUInt :: _fields.Add(".z", new ShaderUInt(defaultValue.Z, "Z", this));


## XRENGINE/Models/Materials/Parameters/ShaderVar.cs
- L175 C84: new() :: public static readonly Dictionary<Type, EShaderVarType> TypeAssociations = new()
- L200 C90: new() :: public static readonly Dictionary<EShaderVarType, Type> ShaderTypeAssociations = new()
- L225 C92: new() :: public static readonly Dictionary<EShaderVarType, Type> AssemblyTypeAssociations = new()


## XRENGINE/Models/Materials/Parameters/ShaderVec2.cs
- L18 C20: new Vector2 :: : this(new Vector2(), NoName) { }
- L24 C31: new ShaderFloat :: _fields.Add(".x", new ShaderFloat(defaultValue.X, "X", this));
- L25 C31: new ShaderFloat :: _fields.Add(".y", new ShaderFloat(defaultValue.Y, "Y", this));


## XRENGINE/Models/Materials/Parameters/ShaderVector3.cs
- L21 C20: new Vector3 :: : this(new Vector3(), NoName) { }
- L27 C31: new ShaderFloat :: _fields.Add(".x", new ShaderFloat(defaultValue.X, "X", this));
- L28 C31: new ShaderFloat :: _fields.Add(".y", new ShaderFloat(defaultValue.Y, "Y", this));
- L29 C31: new ShaderFloat :: _fields.Add(".z", new ShaderFloat(defaultValue.Z, "Z", this));
- L43 C20: new Vector3 :: : this(new Vector3(x, y, z), name, owner) { }


## XRENGINE/Models/Materials/Parameters/ShaderVector4.cs
- L21 C20: new Vector4 :: : this(new Vector4(), NoName) { }
- L27 C31: new ShaderFloat :: _fields.Add(".x", new ShaderFloat(defaultValue.X, "X", this));
- L28 C31: new ShaderFloat :: _fields.Add(".y", new ShaderFloat(defaultValue.Y, "Y", this));
- L29 C31: new ShaderFloat :: _fields.Add(".z", new ShaderFloat(defaultValue.Z, "Z", this));
- L30 C31: new ShaderFloat :: _fields.Add(".w", new ShaderFloat(defaultValue.W, "W", this));
- L44 C20: new Vector4 :: : this(new Vector4(x, y, z, w), name, owner) { }


## XRENGINE/Models/Materials/Textures/TextureData.cs
- L32 C44: new Rectangle :: BitmapData data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, bmp.PixelFormat);
- L34 C21: new DataSource :: _data = new DataSource(length);
- L41 C21: new DataSource :: _data = new DataSource(data, length, true);
- L46 C21: new DataSource :: _data = new DataSource(data, GetLength(width, height, pixelFormat, pixelType), true);


## XRENGINE/Models/Meshes/SubMesh.cs
- L14 C51: new LODSorter :: private SortedSet<SubMeshLOD> _lods = new(new LODSorter());
- L21 C25: new SortedSet :: _lods = new SortedSet<SubMeshLOD>(new LODSorter());
- L21 C51: new LODSorter :: _lods = new SortedSet<SubMeshLOD>(new LODSorter());
- L71 C20: new SubMeshLOD :: : this(new SubMeshLOD(material, primitives, 0.0f)) { }
- L89 C27: new() :: AABB bounds = new();


## XRENGINE/Rendering/API/Rendering/Generic/AbstractRenderer.cs
- L43 C46: new() :: private readonly Lock _roCacheLock = new();
- L47 C70: new() :: private readonly Stack<BoundingRectangle> _renderAreaStack = new();
- L51 C15: new BoundingRectangle :: : new BoundingRectangle(0, 0, Window.Size.X, Window.Size.Y);
- L110 C40: new Vector2 :: framebufferScale = new Vector2(
- L118 C31: new Vector2 :: displaySize = new Vector2(region.Width, region.Height);
- L133 C40: new Vector2 :: framebufferScale = new Vector2(
- L137 C35: new Vector2 :: displaySize = new Vector2(
- L144 C31: new Vector2 :: displaySize = new Vector2(ortho.Width, ortho.Height);
- L307 C22: new NotSupportedException :: => throw new NotSupportedException();


## XRENGINE/Rendering/API/Rendering/Objects/Buffers/XRDataBuffer.cs
- L429 C27: new InvalidOperationException :: throw new InvalidOperationException("Not a proper numeric data type.");
- L449 C23: new InvalidOperationException :: throw new InvalidOperationException($"Cannot set data at index {index}: client-side buffer has not been allocated.");
- L456 C23: new InvalidOperationException :: throw new InvalidOperationException($"Cannot get data at index {index}: client-side buffer has not been allocated.");
- L458 C23: new ArgumentOutOfRangeException :: throw new ArgumentOutOfRangeException(nameof(index), $"Index {index} is out of range. Element count: {_elementCount}");
- L479 C23: new T :: T[] arr = new T[count];
- L573 C27: new InvalidOperationException :: throw new InvalidOperationException("Not a proper numeric data type.");
- L579 C37: new() :: Remapper remapper = new();
- L667 C37: new() :: Remapper remapper = new();
- L703 C37: new() :: Remapper remapper = new();
- L737 C23: new InvalidOperationException :: throw new InvalidOperationException("Data type mismatch.");
- L740 C21: new T :: array = new T[_elementCount];
- L751 C33: new() :: Remapper remapper = new();
- L814 C23: new InvalidOperationException :: throw new InvalidOperationException("Data type mismatch.");
- L817 C21: new T :: array = new T[_elementCount];
- L824 C33: new() :: Remapper remapper = new();
- L942 C32: new() :: StringBuilder sb = new();


## XRENGINE/Rendering/API/Rendering/Objects/Buffers/XRDataBufferView.cs
- L26 C39: new ArgumentNullException :: _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
- L38 C57: new ArgumentNullException :: set => SetField(ref _buffer, value ?? throw new ArgumentNullException(nameof(value)));


## XRENGINE/Rendering/API/Rendering/Objects/Materials/XRMaterial.cs
- L218 C19: new XRRenderProgram :: ? new XRRenderProgram(true, true, Shaders.Where(x => x.Type != EShaderType.Vertex))
- L256 C17: new ShaderFloat :: new ShaderFloat(parallaxScale, "ParallaxScale"),
- L257 C17: new ShaderInt :: new ShaderInt(parallaxMinSteps, "ParallaxMinSteps"),
- L258 C17: new ShaderInt :: new ShaderInt(parallaxMaxSteps, "ParallaxMaxSteps"),
- L259 C17: new ShaderInt :: new ShaderInt(parallaxRefineSteps, "ParallaxRefineSteps"),
- L260 C17: new ShaderFloat :: new ShaderFloat(parallaxHeightBias, "ParallaxHeightBias"),
- L261 C17: new ShaderFloat :: new ShaderFloat(parallaxSilhouette ? 1.0f : 0.0f, "ParallaxSilhouette"),
- L264 C17: new ShaderFloat :: new ShaderFloat(forwardSpecularIntensity, "MatSpecularIntensity"),
- L265 C17: new ShaderFloat :: new ShaderFloat(forwardShininess, "MatShininess"),
- L287 C17: new ShaderVector3 :: new ShaderVector3((ColorF3)color, "BaseColor"),
- L288 C17: new ShaderFloat :: new ShaderFloat(color.A, "Opacity"),
- L300 C21: new ShaderVector4 :: => new([new ShaderVector4(color, "MatColor")], ShaderHelper.UnlitColorFragForward()) { RenderPass = (int)EDefaultRenderPass.OpaqueForward };
- L323 C17: new ShaderVector3 :: new ShaderVector3((ColorF3)color, "BaseColor"),
- L324 C17: new ShaderFloat :: new ShaderFloat(color.A, "Opacity"),
- L325 C17: new ShaderFloat :: new ShaderFloat(1.0f, "Specular"),
- L326 C17: new ShaderFloat :: new ShaderFloat(1.0f, "Roughness"),
- L327 C17: new ShaderFloat :: new ShaderFloat(0.0f, "Metallic"),
- L328 C17: new ShaderFloat :: new ShaderFloat(1.0f, "IndexOfRefraction"),
- L393 C38: new ShaderVar :: ShaderVar[] parameters = new ShaderVar[count + 1];
- L401 C39: new ShaderVector3 :: parameters[count++] = new ShaderVector3(emission.Value, "Emission");
- L409 C39: new ShaderVector3 :: parameters[count++] = new ShaderVector3(ambient.Value, "Ambient");
- L417 C39: new ShaderVector3 :: parameters[count++] = new ShaderVector3(diffuse.Value, "Diffuse");
- L425 C39: new ShaderVector3 :: parameters[count++] = new ShaderVector3(specular.Value, "Specular");
- L431 C35: new ShaderFloat :: parameters[count++] = new ShaderFloat(shininess, "Shininess");
- L487 C36: new XRShader :: return new(parameters, new XRShader(EShaderType.Fragment, source));


## XRENGINE/Rendering/API/Rendering/Objects/Materials/XRMaterialBase.cs
- L58 C54: new() :: private RenderingParameters _renderOptions = new();
- L64 C39: new() :: get => _renderOptions ??= new();
- L65 C46: new() :: set => _renderOptions = value ?? new();


## XRENGINE/Rendering/API/Rendering/Objects/Meshes/TriangleAdapter.cs
- L22 C23: new Exception :: throw new Exception("missing map for a shuffled child");


## XRENGINE/Rendering/API/Rendering/Objects/Meshes/XRMesh.Assimp.cs
- L34 C27: new ConcurrentDictionary :: var vertexCache = new ConcurrentDictionary<int, Vertex>();
- L97 C26: new string :: string[] names = new string[mesh.MeshAnimationAttachmentCount];
- L123 C30: new int :: int[] offsetPoints = new int[faceCount];
- L124 C29: new int :: int[] offsetLines = new int[faceCount];
- L125 C33: new int :: int[] offsetTriangles = new int[faceCount];
- L149 C32: new Vertex :: Vertex[] pointsArray = new Vertex[totalPoints];
- L150 C31: new Vertex :: Vertex[] linesArray = new Vertex[totalLines];
- L151 C35: new Vertex :: Vertex[] trianglesArray = new Vertex[totalTriangles];
- L152 C35: new ConcurrentDictionary :: var concurrentFaceRemap = new ConcurrentDictionary<int, List<int>>();
- L231 C34: new TaskCompletionSource :: var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
- L249 C21: new Dictionary :: faceRemap = new Dictionary<int, List<int>>(concurrentFaceRemap);


## XRENGINE/Rendering/API/Rendering/Objects/Meshes/XRMesh.Blendshapes.cs
- L15 C28: new XRDataBuffer :: BlendshapeCounts = new XRDataBuffer(ECommonBufferType.BlendshapeCount.ToString(), EBufferTarget.ArrayBuffer, (uint)sourceList.Length,
- L90 C43: new IVector4 :: blendshapeIndices.Add(new IVector4(bsInd, posInd, nrmInd, tanInd));
- L107 C29: new XRDataBuffer :: BlendshapeIndices = new XRDataBuffer($"{ECommonBufferType.BlendshapeIndices}Buffer", EBufferTarget.ShaderStorageBuffer,
- L121 C28: new XRDataBuffer :: BlendshapeDeltas = new XRDataBuffer($"{ECommonBufferType.BlendshapeDeltas}Buffer", EBufferTarget.ShaderStorageBuffer,
- L164 C31: new() :: Remapper deltaRemap = new();
- L166 C28: new XRDataBuffer :: BlendshapeDeltas = new XRDataBuffer($"{ECommonBufferType.BlendshapeDeltas}Buffer", EBufferTarget.ShaderStorageBuffer,


## XRENGINE/Rendering/API/Rendering/Objects/Meshes/XRMesh.BufferCollection.cs
- L44 C43: new NotImplementedException :: EPrimitiveType.Quads => throw new NotImplementedException(),
- L45 C47: new NotImplementedException :: EPrimitiveType.QuadStrip => throw new NotImplementedException(),
- L46 C45: new NotImplementedException :: EPrimitiveType.Polygon => throw new NotImplementedException(),
- L47 C52: new NotImplementedException :: EPrimitiveType.LinesAdjacency => throw new NotImplementedException(),
- L48 C56: new NotImplementedException :: EPrimitiveType.LineStripAdjacency => throw new NotImplementedException(),
- L49 C56: new NotImplementedException :: EPrimitiveType.TrianglesAdjacency => throw new NotImplementedException(),
- L50 C60: new NotImplementedException :: EPrimitiveType.TriangleStripAdjacency => throw new NotImplementedException(),
- L51 C45: new NotImplementedException :: EPrimitiveType.Patches => throw new NotImplementedException(),
- L52 C24: new NotImplementedException :: _ => throw new NotImplementedException(),


## XRENGINE/Rendering/API/Rendering/Objects/Meshes/XRMesh.BufferInit.cs
- L18 C39: new XRDataBuffer :: InterleavedVertexBuffer = new XRDataBuffer(ECommonBufferType.InterleavedVertex.ToString(), EBufferTarget.ArrayBuffer, false)
- L69 C31: new XRDataBuffer :: PositionsBuffer = new XRDataBuffer(ECommonBufferType.Position.ToString(), EBufferTarget.ArrayBuffer, false);
- L75 C33: new XRDataBuffer :: NormalsBuffer = new XRDataBuffer(ECommonBufferType.Normal.ToString(), EBufferTarget.ArrayBuffer, false);
- L81 C34: new XRDataBuffer :: TangentsBuffer = new XRDataBuffer(ECommonBufferType.Tangent.ToString(), EBufferTarget.ArrayBuffer, false);
- L87 C32: new XRDataBuffer :: ColorBuffers = new XRDataBuffer[colorCount];
- L91 C31: new XRDataBuffer :: var buf = new XRDataBuffer(binding, EBufferTarget.ArrayBuffer, false);
- L99 C35: new XRDataBuffer :: TexCoordBuffers = new XRDataBuffer[texCoordCount];
- L103 C31: new XRDataBuffer :: var buf = new XRDataBuffer(binding, EBufferTarget.ArrayBuffer, false);


## XRENGINE/Rendering/API/Rendering/Objects/Meshes/XRMesh.Clone.cs
- L12 C24: new() :: XRMesh clone = new()
- L28 C25: new Vertex :: _vertices = new Vertex[Vertices.Length]
- L46 C34: new XRDataBuffer :: clone.ColorBuffers = new XRDataBuffer[ColorBuffers.Length];
- L53 C37: new XRDataBuffer :: clone.TexCoordBuffers = new XRDataBuffer[TexCoordBuffers.Length];


## XRENGINE/Rendering/API/Rendering/Objects/Meshes/XRMesh.Constructors.cs
- L16 C46: new VertexTriangle :: => new(positions.SelectEvery(3, x => new VertexTriangle(x[0], x[1], x[2])));
- L18 C46: new VertexTriangle :: => new(positions.SelectEvery(3, x => new VertexTriangle(x[0], x[1], x[2])));
- L20 C46: new VertexLine :: => new(positions.SelectEvery(2, x => new VertexLine(x[0], x[1])));
- L22 C16: new VertexLineStrip :: => new(new VertexLineStrip(closed, [.. positions.Select(x => new Vertex(x))]));
- L22 C70: new Vertex :: => new(new VertexLineStrip(closed, [.. positions.Select(x => new Vertex(x))]));
- L24 C46: new VertexLine :: => new(positions.SelectEvery(2, x => new VertexLine(x[0], x[1])));
- L26 C38: new Vertex :: => new(positions.Select(x => new Vertex(x)));
- L28 C38: new Vertex :: => new(positions.Select(x => new Vertex(x)));
- L46 C63: new AABB :: bounds = bounds?.ExpandedToInclude(v.Position) ?? new AABB(v.Position, v.Position);
- L52 C29: new AABB :: _bounds = bounds ?? new AABB(Vector3.Zero, Vector3.Zero);
- L53 C62: new IndexTriangle :: _triangles = [.. triangleIndices.SelectEvery(3, x => new IndexTriangle(x[0], x[1], x[2]))];
- L85 C71: new AABB :: bounds = bounds?.ExpandedToInclude(v.Position) ?? new AABB(v.Position, v.Position);
- L93 C81: new AABB :: bounds = bounds?.ExpandedToInclude(vtx.Position) ?? new AABB(vtx.Position, vtx.Position);
- L101 C77: new AABB :: bounds = bounds?.ExpandedToInclude(vtx.Position) ?? new AABB(vtx.Position, vtx.Position);
- L110 C81: new AABB :: bounds = bounds?.ExpandedToInclude(vtx.Position) ?? new AABB(vtx.Position, vtx.Position);
- L118 C29: new AABB :: _bounds = bounds ?? new AABB(Vector3.Zero, Vector3.Zero);
- L148 C36: new int :: firstAppearanceArray = new int[count];


## XRENGINE/Rendering/API/Rendering/Objects/Meshes/XRMesh.CookedBinary.cs
- L27 C19: new ArgumentException :: throw new ArgumentException("Stream key cannot be null or whitespace.", nameof(streamKey));
- L69 C33: new() :: MeshMetadata metadata = new()
- L88 C37: new() :: MeshPayloadWritePlan plan = new()
- L212 C18: new AABB :: Bounds = new AABB(metadata.BoundsMin, metadata.BoundsMax);
- L227 C33: new() :: MeshMetadata metadata = new()
- L378 C27: new IndexTriangle :: triangles.Add(new IndexTriangle(p0, p1, p2));
- L393 C23: new IndexLine :: lines.Add(new IndexLine(p0, p1));
- L413 C20: new BufferPlan :: return new BufferPlan
- L427 C19: new InvalidOperationException :: throw new InvalidOperationException($"Buffer '{streamKey}' does not have CPU-side data available.");
- L447 C23: new NotSupportedException :: throw new NotSupportedException($"Unsupported mesh buffer encoding '{encoding}'.");
- L450 C16: new BufferPlan :: return new BufferPlan
- L466 C15: new BufferMetadata :: : new BufferMetadata(buffer.AttributeName ?? string.Empty, buffer.Target, buffer.ComponentType, buffer.ComponentCount, buffer.ElementCount, buffer.Normalize, buffer.Integral, buffer.PadEndingToVec4);
- L532 C27: new InvalidOperationException :: throw new InvalidOperationException($"Raw buffer '{plan.StreamKey}' is missing source data.");
- L551 C23: new InvalidOperationException :: throw new InvalidOperationException("Unexpected buffer metadata for fixed stream.");
- L553 C22: new XRDataBuffer :: buffer = new XRDataBuffer(metadata.AttributeName, metadata.Target, metadata.ElementCount, metadata.ComponentType, metadata.ComponentCount, metadata.Normalize, metadata.Integral)
- L568 C19: new InvalidOperationException :: throw new InvalidOperationException("Dynamic buffer missing metadata in stream.");
- L595 C23: new NotSupportedException :: throw new NotSupportedException($"Unsupported mesh buffer encoding '{encoding}'.");
- L639 C28: new BoneInfo :: BoneInfo[] infos = new BoneInfo[boneCount];
- L647 C24: new BoneInfo :: infos[i] = new BoneInfo { BoneId = id, Name = name, ParentIndex = parentIndex, BindMatrix = bind, InverseBindMatrix = inverse };
- L692 C26: new string :: string[] names = new string[nameCount];
- L708 C16: new SkinningPlan :: return new SkinningPlan
- L729 C28: new BoneInfo :: BoneInfo[] bones = new BoneInfo[utilized.Length];
- L736 C24: new BoneInfo :: bones[i] = new BoneInfo
- L754 C16: new BlendshapePlan :: return new BlendshapePlan
- L799 C16: new Guid :: return new Guid(bytes);
- L824 C16: new BufferMetadata :: return new BufferMetadata(name, target, componentType, componentCount, elementCount, normalize, integral, padEnding);
- L857 C19: new InvalidOperationException :: throw new InvalidOperationException($"Buffer '{buffer.AttributeName}' does not contain {byteLength} bytes of CPU data.");
- L858 C27: new ReadOnlySpan :: writer.WriteBytes(new ReadOnlySpan<byte>(source.Address, (int)byteLength));
- L871 C19: new InvalidOperationException :: throw new InvalidOperationException("Snorm16 encoding requires float3 data.");
- L874 C19: new InvalidOperationException :: throw new InvalidOperationException($"Buffer '{buffer.AttributeName}' does not have CPU data available.");
- L877 C26: new byte :: byte[] encoded = new byte[vertexCount * 3 * sizeof(short)];
- L936 C19: new InvalidOperationException :: throw new InvalidOperationException("Decoded buffer length mismatch.");
- L943 C19: new InvalidOperationException :: throw new InvalidOperationException($"Buffer '{buffer.AttributeName}' does not contain {length} bytes.");
- L944 C16: new ReadOnlySpan :: return new ReadOnlySpan<byte>((void*)source.Address, (int)length);
- L951 C21: new Span :: data.CopyTo(new Span<byte>((void*)buffer.ClientSideSource!.Address, data.Length));
- L961 C57: new() :: public List<BufferPlan> ColorStreams { get; } = new();
- L962 C60: new() :: public List<BufferPlan> TexCoordStreams { get; } = new();
- L1037 C53: new() :: public static readonly SkinningPlan Empty = new() { HasSkinning = false };
- L1073 C55: new() :: public static readonly BlendshapePlan Empty = new() { HasBlendshapes = false, Names = Array.Empty<string>() };
- L1157 C29: new Transform :: Transform[] bones = new Transform[infos.Length];
- L1161 C30: new() :: Transform bone = new();
- L1190 C16: new BufferBlob :: return new BufferBlob
- L1216 C35: new DataSource :: buffer.ClientSideSource = new DataSource(blob.Data);
- L1219 C19: new InvalidOperationException :: throw new InvalidOperationException($"Cooked buffer '{blob.AttributeName ?? "<unnamed>"}' length mismatch.");
- L1229 C21: new BufferCollection :: Buffers ??= new BufferCollection();
- L1274 C16: new MeshCookedPayload :: return new MeshCookedPayload


## XRENGINE/Rendering/API/Rendering/Objects/Meshes/XRMesh.Core.cs
- L196 C41: new() :: private readonly Lock _boundsLock = new();
- L232 C28: new XRDataBuffer :: ColorBuffers = new XRDataBuffer[ColorCount];
- L243 C31: new XRDataBuffer :: TexCoordBuffers = new XRDataBuffer[TexCoordCount];
- L281 C32: new Vertex :: Vertex[] rebuilt = new Vertex[VertexCount];
- L284 C28: new() :: Vertex v = new()
- L296 C47: new List :: v.TextureCoordinateSets = new List<Vector2>((int)TexCoordCount);
- L303 C35: new List :: v.ColorSets = new List<Vector4>((int)ColorCount);
- L321 C30: new List :: _triangles = new List<IndexTriangle>(triangleCount);
- L325 C36: new IndexTriangle :: _triangles.Add(new IndexTriangle(idx++, idx++, idx++));


## XRENGINE/Rendering/API/Rendering/Objects/Meshes/XRMesh.Geometry.cs
- L23 C65: new[] :: EPrimitiveType.Triangles => _triangles?.SelectMany(x => new[] { x.Point0, x.Point1, x.Point2 }).ToArray(),
- L24 C57: new[] :: EPrimitiveType.Lines => _lines?.SelectMany(x => new[] { x.Point0, x.Point1 }).ToArray(),
- L34 C33: new() :: Remapper remapper = new();
- L37 C32: new IndexTriangle :: _triangles.Add(new IndexTriangle(remapper.RemapTable[i++], remapper.RemapTable[i++], remapper.RemapTable[i++]));
- L41 C28: new IndexTriangle :: _triangles.Add(new IndexTriangle(i++, i++, i++));
- L50 C33: new() :: Remapper remapper = new();
- L53 C28: new IndexLine :: _lines.Add(new IndexLine(remapper.RemapTable[i++], remapper.RemapTable[i++]));
- L57 C24: new IndexLine :: _lines.Add(new IndexLine(i++, i++));
- L66 C33: new() :: Remapper remapper = new();
- L123 C28: new TriangleAdapter :: _bvhTree = new(new TriangleAdapter(), triangles);
- L140 C16: new Triangle :: return new Triangle(p0, p1, p2);
- L170 C31: new() :: SignedDistanceField = new();
- L172 C23: new XRRenderProgram :: var program = new XRRenderProgram(true, true, shader);
- L201 C19: new XRDataBuffer :: var buf = new XRDataBuffer(target, true)


## XRENGINE/Rendering/API/Rendering/Objects/Meshes/XRMesh.Shapes.cs
- L17 C24: new VertexLineStrip :: return new VertexLineStrip(true, points);
- L22 C27: new Exception :: throw new Exception("A (very low res) circle needs at least 3 sides.");
- L28 C35: new Vertex :: Vertex[] points = new Vertex[sides];
- L38 C40: new Vector2 :: coord * 0.5f + new Vector2(0.5f));
- L58 C46: new Vertex :: Vertex[] stripVertices = new Vertex[((int)precision + 1) * 2];
- L71 C46: new Vertex :: stripVertices[x++] = new Vertex(pos, norm, uv);
- L80 C46: new Vertex :: stripVertices[x++] = new Vertex(pos, norm, uv);
- L82 C32: new VertexTriangleStrip :: strips.Add(new VertexTriangleStrip(stripVertices));
- L115 C31: new Vertex :: v.Add(new Vertex(center + normal * radius, normal, new Vector2(U, V)));
- L115 C76: new Vector2 :: v.Add(new Vertex(center + normal * radius, normal, new Vector2(U, V)));
- L121 C35: new VertexTriangle :: triangles.Add(new VertexTriangle(v[i], v[i + slices + 1], v[i + slices]));
- L122 C35: new VertexTriangle :: triangles.Add(new VertexTriangle(v[i + slices + 1], v[i], v[i + 1]));
- L130 C38: new VertexLine :: VertexLine[] lines = new VertexLine[sides * 3];
- L140 C34: new VertexLine :: lines[x++] = new VertexLine(bottomPoint, sidePoint.Position);
- L141 C34: new VertexLine :: lines[x++] = new VertexLine(topPoint, sidePoint.Position);
- L142 C34: new VertexLine :: lines[x++] = new VertexLine(sidePoints[i + 1 == sides ? 0 : i + 1], sidePoint);
- L167 C33: new Vertex :: topVertex = new Vertex(topPoint, up, new Vector2(0.5f));
- L167 C58: new Vector2 :: topVertex = new Vertex(topPoint, up, new Vector2(0.5f));
- L168 C30: new VertexTriangle :: tris.Add(new VertexTriangle(
- L185 C47: new Vector2 :: new(bottomPoint, -up, new Vector2(0.5f))
- L196 C35: new VertexTriangleFan :: tris.AddRange(new VertexTriangleFan(list).ToTriangles());
- L236 C39: new Vertex :: Vertex[] topPoints1 = new Vertex[pts], topPoints2 = new Vertex[pts];
- L236 C69: new Vertex :: Vertex[] topPoints1 = new Vertex[pts], topPoints2 = new Vertex[pts];
- L237 C39: new Vertex :: Vertex[] botPoints1 = new Vertex[pts], botPoints2 = new Vertex[pts];
- L237 C69: new Vertex :: Vertex[] botPoints1 = new Vertex[pts], botPoints2 = new Vertex[pts];
- L245 C37: new Vertex :: topPoints1[i] = new Vertex(topPoint + radius * v1);
- L246 C37: new Vertex :: topPoints2[i] = new Vertex(topPoint + radius * v2);
- L247 C37: new Vertex :: botPoints1[i] = new Vertex(bottomPoint - radius * v1);
- L248 C37: new Vertex :: botPoints2[i] = new Vertex(bottomPoint - radius * v2);
- L260 C21: new Vertex :: new Vertex(bottomPoint + rightNormal * radius),
- L261 C21: new Vertex :: new Vertex(topPoint + rightNormal * radius));
- L263 C21: new Vertex :: new Vertex(bottomPoint - rightNormal * radius),
- L264 C21: new Vertex :: new Vertex(topPoint - rightNormal * radius));
- L266 C21: new Vertex :: new Vertex(bottomPoint + forwardNormal * radius),
- L267 C21: new Vertex :: new Vertex(topPoint + forwardNormal * radius));
- L269 C21: new Vertex :: new Vertex(bottomPoint - forwardNormal * radius),
- L270 C21: new Vertex :: new Vertex(topPoint - forwardNormal * radius));
- L286 C54: new VertexTriangle :: VertexTriangle[] cylinderTriangles = new VertexTriangle[pointCountHalfCircle * 2];
- L287 C55: new VertexTriangle :: VertexTriangle[] topSphereTriangles = new VertexTriangle[pointCountHalfCircle * pointCountHalfCircle];
- L288 C58: new VertexTriangle :: VertexTriangle[] bottomSphereTriangles = new VertexTriangle[pointCountHalfCircle * pointCountHalfCircle];
- L305 C46: new VertexTriangle :: cylinderTriangles[x++] = new VertexTriangle(top1, top2, bot1);
- L306 C46: new VertexTriangle :: cylinderTriangles[x++] = new VertexTriangle(top2, bot2, bot1);
- L322 C51: new VertexTriangle :: topSphereTriangles[x++] = new VertexTriangle(top1, top2, v);
- L341 C54: new VertexTriangle :: bottomSphereTriangles[x++] = new VertexTriangle(bot1, bot2, v);
- L389 C39: new Vertex :: Vertex[] topPoints1 = new Vertex[pts], topPoints2 = new Vertex[pts];
- L389 C69: new Vertex :: Vertex[] topPoints1 = new Vertex[pts], topPoints2 = new Vertex[pts];
- L390 C39: new Vertex :: Vertex[] botPoints1 = new Vertex[pts], botPoints2 = new Vertex[pts];
- L390 C69: new Vertex :: Vertex[] botPoints1 = new Vertex[pts], botPoints2 = new Vertex[pts];
- L400 C37: new Vertex :: topPoints1[i] = new Vertex(Vector3.Transform(radius * v1, offset));
- L401 C37: new Vertex :: topPoints2[i] = new Vertex(Vector3.Transform(radius * v2, offset));
- L402 C37: new Vertex :: botPoints1[i] = new Vertex(-Vector3.Transform(radius * v1, offset));
- L403 C37: new Vertex :: botPoints2[i] = new Vertex(-Vector3.Transform(radius * v2, offset));
- L415 C21: new Vertex :: new Vertex(bottomPoint + rightNormal * radius),
- L416 C21: new Vertex :: new Vertex(topPoint + rightNormal * radius));
- L418 C21: new Vertex :: new Vertex(bottomPoint - rightNormal * radius),
- L419 C21: new Vertex :: new Vertex(topPoint - rightNormal * radius));
- L421 C21: new Vertex :: new Vertex(bottomPoint + forwardNormal * radius),
- L422 C21: new Vertex :: new Vertex(topPoint + forwardNormal * radius));
- L424 C21: new Vertex :: new Vertex(bottomPoint - forwardNormal * radius),
- L425 C21: new Vertex :: new Vertex(topPoint - forwardNormal * radius));
- L454 C28: new VertexLine :: topFront = new VertexLine(new Vertex(TFL), new Vertex(TFR));
- L454 C43: new Vertex :: topFront = new VertexLine(new Vertex(TFL), new Vertex(TFR));
- L454 C60: new Vertex :: topFront = new VertexLine(new Vertex(TFL), new Vertex(TFR));
- L455 C27: new VertexLine :: topBack = new VertexLine(new Vertex(TBL), new Vertex(TBR));
- L455 C42: new Vertex :: topBack = new VertexLine(new Vertex(TBL), new Vertex(TBR));
- L455 C59: new Vertex :: topBack = new VertexLine(new Vertex(TBL), new Vertex(TBR));
- L456 C27: new VertexLine :: topLeft = new VertexLine(new Vertex(TFL), new Vertex(TBL));
- L456 C42: new Vertex :: topLeft = new VertexLine(new Vertex(TFL), new Vertex(TBL));
- L456 C59: new Vertex :: topLeft = new VertexLine(new Vertex(TFL), new Vertex(TBL));
- L457 C28: new VertexLine :: topRight = new VertexLine(new Vertex(TFR), new Vertex(TBR));
- L457 C43: new Vertex :: topRight = new VertexLine(new Vertex(TFR), new Vertex(TBR));
- L457 C60: new Vertex :: topRight = new VertexLine(new Vertex(TFR), new Vertex(TBR));
- L459 C31: new VertexLine :: bottomFront = new VertexLine(new Vertex(BFL), new Vertex(BFR));
- L459 C46: new Vertex :: bottomFront = new VertexLine(new Vertex(BFL), new Vertex(BFR));
- L459 C63: new Vertex :: bottomFront = new VertexLine(new Vertex(BFL), new Vertex(BFR));
- L460 C30: new VertexLine :: bottomBack = new VertexLine(new Vertex(BBL), new Vertex(BBR));
- L460 C45: new Vertex :: bottomBack = new VertexLine(new Vertex(BBL), new Vertex(BBR));
- L460 C62: new Vertex :: bottomBack = new VertexLine(new Vertex(BBL), new Vertex(BBR));
- L461 C30: new VertexLine :: bottomLeft = new VertexLine(new Vertex(BFL), new Vertex(BBL));
- L461 C45: new Vertex :: bottomLeft = new VertexLine(new Vertex(BFL), new Vertex(BBL));
- L461 C62: new Vertex :: bottomLeft = new VertexLine(new Vertex(BFL), new Vertex(BBL));
- L462 C31: new VertexLine :: bottomRight = new VertexLine(new Vertex(BFR), new Vertex(BBR));
- L462 C46: new Vertex :: bottomRight = new VertexLine(new Vertex(BFR), new Vertex(BBR));
- L462 C63: new Vertex :: bottomRight = new VertexLine(new Vertex(BFR), new Vertex(BBR));
- L464 C29: new VertexLine :: frontLeft = new VertexLine(new Vertex(TFL), new Vertex(BFL));
- L464 C44: new Vertex :: frontLeft = new VertexLine(new Vertex(TFL), new Vertex(BFL));
- L464 C61: new Vertex :: frontLeft = new VertexLine(new Vertex(TFL), new Vertex(BFL));
- L465 C30: new VertexLine :: frontRight = new VertexLine(new Vertex(TFR), new Vertex(BFR));
- L465 C45: new Vertex :: frontRight = new VertexLine(new Vertex(TFR), new Vertex(BFR));
- L465 C62: new Vertex :: frontRight = new VertexLine(new Vertex(TFR), new Vertex(BFR));
- L466 C28: new VertexLine :: backLeft = new VertexLine(new Vertex(TBL), new Vertex(BBL));
- L466 C43: new Vertex :: backLeft = new VertexLine(new Vertex(TBL), new Vertex(BBL));
- L466 C60: new Vertex :: backLeft = new VertexLine(new Vertex(TBL), new Vertex(BBL));
- L467 C29: new VertexLine :: backRight = new VertexLine(new Vertex(TBR), new Vertex(BBR));
- L467 C44: new Vertex :: backRight = new VertexLine(new Vertex(TBR), new Vertex(BBR));
- L467 C61: new Vertex :: backRight = new VertexLine(new Vertex(TBR), new Vertex(BBR));
- L596 C31: new VertexLineStrip :: return Create(new VertexLineStrip(true, bottomLeft, bottomRight, topRight, topLeft));
- L611 C27: new Exception :: throw new Exception("A (very low res) circle needs at least 3 sides.");
- L616 C34: new Vertex :: points.Insert(0, new Vertex(center, normal, new Vector2(0.5f)));
- L616 C61: new Vector2 :: points.Insert(0, new Vertex(center, normal, new Vector2(0.5f)));


## XRENGINE/Rendering/API/Rendering/Objects/Meshes/XRMesh.Skinning.cs
- L46 C33: new Dictionary :: var weightsPerVertex2 = new Dictionary<TransformBase, (float weight, Matrix4x4 invBindMatrix)>?[vertexCount];
- L48 C41: new ConcurrentDictionary :: var concurrentInvBindMatrices = new ConcurrentDictionary<TransformBase, Matrix4x4>();
- L49 C42: new ConcurrentDictionary :: var concurrentBoneToIndexTable = new ConcurrentDictionary<TransformBase, int>();
- L53 C32: new object :: object[] vertexLocks = new object[vertexCount];
- L55 C30: new object :: vertexLocks[i] = new object();
- L140 C33: new XRDataBuffer :: BoneWeightOffsets = new XRDataBuffer(ECommonBufferType.BoneMatrixOffset.ToString(), EBufferTarget.ArrayBuffer, vertCount, indexVarType, 4, false, intVarType);
- L141 C32: new XRDataBuffer :: BoneWeightCounts = new XRDataBuffer(ECommonBufferType.BoneMatrixCount.ToString(), EBufferTarget.ArrayBuffer, vertCount, EComponentType.Float, 4, false, false);
- L145 C33: new XRDataBuffer :: BoneWeightOffsets = new XRDataBuffer(ECommonBufferType.BoneMatrixOffset.ToString(), EBufferTarget.ArrayBuffer, vertCount, indexVarType, 1, false, intVarType);
- L146 C32: new XRDataBuffer :: BoneWeightCounts = new XRDataBuffer(ECommonBufferType.BoneMatrixCount.ToString(), EBufferTarget.ArrayBuffer, vertCount, indexVarType, 1, false, intVarType);
- L148 C33: new XRDataBuffer :: BoneWeightIndices = new XRDataBuffer($"{ECommonBufferType.BoneMatrixIndices}Buffer", EBufferTarget.ShaderStorageBuffer, true);
- L150 C32: new XRDataBuffer :: BoneWeightValues = new XRDataBuffer($"{ECommonBufferType.BoneMatrixWeights}Buffer", EBufferTarget.ShaderStorageBuffer, false);
- L179 C25: new uint :: uint[] counts = new uint[vertexCount];
- L180 C40: new List :: List<int>[] localBoneIndices = new List<int>[vertexCount];
- L181 C42: new List :: List<float>[] localBoneWeights = new List<float>[vertexCount];
- L198 C31: new List :: var indicesList = new List<int>(count);
- L199 C31: new List :: var weightsList = new List<float>(count);


## XRENGINE/Rendering/API/Rendering/Objects/Render Targets/XRCubeFrameBuffer.cs
- L24 C34: new XRMeshRenderer :: FullScreenCubeMesh = new XRMeshRenderer(XRMesh.Shapes.SolidBox(new Vector3(-0.5f), new Vector3(0.5f), true), mat);
- L24 C76: new Vector3 :: FullScreenCubeMesh = new XRMeshRenderer(XRMesh.Shapes.SolidBox(new Vector3(-0.5f), new Vector3(0.5f), true), mat);
- L24 C96: new Vector3 :: FullScreenCubeMesh = new XRMeshRenderer(XRMesh.Shapes.SolidBox(new Vector3(-0.5f), new Vector3(0.5f), true), mat);
- L61 C34: new XRCamera :: XRCamera[] cameras = new XRCamera[6];
- L74 C21: new XRPerspectiveCameraParameters :: p = new XRPerspectiveCameraParameters(90.0f, 1.0f, nearZ, farZ);
- L77 C29: new XROrthographicCameraParameters :: var ortho = new XROrthographicCameraParameters(1.0f, 1.0f, nearZ, farZ);
- L84 C27: new Transform :: var tfm = new Transform()


## XRENGINE/Rendering/API/Rendering/Objects/Render Targets/XRFrameBuffer.cs
- L20 C67: new() :: private static readonly Stack<XRFrameBuffer> _readStack = new();
- L21 C68: new() :: private static readonly Stack<XRFrameBuffer> _writeStack = new();
- L22 C67: new() :: private static readonly Stack<XRFrameBuffer> _bindStack = new();


## XRENGINE/Rendering/API/Rendering/Objects/Render Targets/XRQuadFrameBuffer.cs
- L24 C21: new Vector3 :: new Vector3(-1, -1, 0),
- L25 C21: new Vector3 :: new Vector3( 3, -1, 0),
- L26 C21: new Vector3 :: new Vector3(-1,  3, 0));
- L37 C21: new Vector3 :: new Vector3(-1, -1, 0),
- L38 C21: new Vector3 :: new Vector3( 1, -1, 0),
- L39 C21: new Vector3 :: new Vector3( 1,  1, 0));
- L46 C21: new Vector3 :: new Vector3(-1, -1, 0),
- L47 C21: new Vector3 :: new Vector3( 1,  1, 0),
- L48 C21: new Vector3 :: new Vector3(-1,  1, 0));
- L61 C30: new XRMeshRenderer :: FullScreenMesh = new XRMeshRenderer(Mesh(useTriangle), mat);


## XRENGINE/Rendering/API/Rendering/Objects/Textures/1D/Mipmap1D.cs
- L86 C16: new() :: => new()
- L109 C20: new DataSource :: Data = new DataSource(data);


## XRENGINE/Rendering/API/Rendering/Objects/Textures/1D/XRTexture1D.cs
- L36 C31: new Mipmap1D :: Mipmap1D[] mips = new Mipmap1D[mipCount];
- L40 C27: new Mipmap1D :: mips[i] = new Mipmap1D(Math.Max(1u, currentWidth), internalFormat, format, type, allocateData);
- L144 C37: new Mipmap1D :: Mipmap1D[] newMipmaps = new Mipmap1D[desiredLevels];
- L151 C33: new Mipmap1D :: newMipmaps[i] = new Mipmap1D(currentWidth, baseMip.InternalFormat, baseMip.PixelFormat, baseMip.PixelType, false);


## XRENGINE/Rendering/API/Rendering/Objects/Textures/1D/XRTexture1DArray.cs
- L32 C38: new XRTexture1D :: XRTexture1D[] textures = new XRTexture1D[layerCount];
- L34 C31: new XRTexture1D :: textures[i] = new XRTexture1D(width, internalFormat, format, type, allocateData);


## XRENGINE/Rendering/API/Rendering/Objects/Textures/2D/Mipmap2D.cs
- L54 C39: new DataSource :: Data = allocateData ? new DataSource(XRTexture.AllocateBytes(width, height, pixelFormat, pixelType)) : null;
- L89 C52: new DataSource :: set => _bytes = value is null ? null : new DataSource(value);
- L116 C28: new() :: Mipmap2D mip = new();
- L132 C35: new Rgba32 :: Rgba32[] pixels = new Rgba32[image.Width * image.Height];
- L223 C28: new DataSource :: Data = new DataSource(XRTexture.AllocateBytes(width, height, PixelFormat, PixelType));
- L237 C32: new DataSource :: Data = new DataSource(XRTexture.AllocateBytes(width, height, PixelFormat, PixelType));
- L289 C16: new() :: => new()


## XRENGINE/Rendering/API/Rendering/Objects/Textures/2D/XRTexture2D.cs
- L52 C35: new MagickImage :: var sourceImage = new MagickImage(filePath);
- L90 C23: new ArgumentException :: throw new ArgumentException("File path must be provided.", nameof(filePath));
- L92 C45: new XRTexture2D :: XRTexture2D target = texture ?? new XRTexture2D();
- L171 C23: new ArgumentException :: throw new ArgumentException("File path must be provided.", nameof(filePath));
- L173 C45: new XRTexture2D :: XRTexture2D target = texture ?? new XRTexture2D();
- L318 C24: new Mipmap2D :: Mipmaps = [new Mipmap2D(new MagickImage(FillerImage))];
- L318 C37: new MagickImage :: Mipmaps = [new Mipmap2D(new MagickImage(FillerImage))];
- L331 C28: new YamlStream :: var yaml = new YamlStream();
- L438 C35: new Mipmap2D :: target.Mipmaps = [new Mipmap2D(previewImage)];
- L490 C35: new Mipmap2D :: target.Mipmaps = [new Mipmap2D(baseImage)];
- L508 C37: new Mipmap2D :: Mipmap2D[] copies = new Mipmap2D[sourceMipmaps.Length];
- L512 C47: new Mipmap2D :: copies[i] = mip is null ? new Mipmap2D() : mip.Clone(cloneImage: deepCopy);
- L523 C31: new Mipmap2D :: target.Mipmaps = [new Mipmap2D(filler)];
- L563 C31: new Mipmap2D :: Mipmap2D[] mips = new Mipmap2D[GetSmallestMipmapLevel(image.Width, image.Height)];
- L564 C23: new Mipmap2D :: mips[0] = new Mipmap2D(image);
- L571 C27: new Mipmap2D :: mips[i] = new Mipmap2D(clone as MagickImage);
- L583 C24: new MagickImage :: return new MagickImage(path);
- L591 C26: new Drawables :: img.Draw(new Drawables()
- L626 C24: new Mipmap2D :: Mipmaps = [new Mipmap2D(loadTask)];
- L630 C24: new Mipmap2D :: Mipmaps = [new Mipmap2D(width, height, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, true)
- L632 C24: new DataSource :: Data = new DataSource(ColorToBytes(width, height, color))
- L638 C27: new byte :: byte[] data = new byte[width * height * 4];
- L653 C31: new Mipmap2D :: Mipmap2D[] mips = new Mipmap2D[mipmapCount];
- L656 C35: new() :: Mipmap2D mipmap = new()
- L663 C28: new DataSource :: Data = new DataSource(AllocateBytes(width, height, format, type))
- L674 C31: new Mipmap2D :: Mipmap2D[] mips = new Mipmap2D[mipMapPaths.Length];
- L682 C31: new Mipmap2D :: mips[i] = new Mipmap2D(new MagickImage(path));
- L682 C44: new MagickImage :: mips[i] = new Mipmap2D(new MagickImage(path));
- L693 C24: new Mipmap2D :: Mipmaps = [new Mipmap2D()
- L700 C39: new DataSource :: Data = allocateData ? new DataSource(AllocateBytes(width, height, format, type)) : null
- L705 C31: new Mipmap2D :: Mipmap2D[] mips = new Mipmap2D[mipmaps.Length];
- L710 C27: new Mipmap2D :: mips[i] = new Mipmap2D(image);
- L716 C24: new Mipmap2D :: Mipmaps = [new Mipmap2D(image)];
- L720 C24: new Mipmap2D :: Mipmaps = [new Mipmap2D(image)];
- L906 C32: new XRDataBuffer :: _pbo = new XRDataBuffer(EBufferTarget.PixelUnpackBuffer, true)
- L988 C26: new GrabPassInfo :: t.GrabPass = new GrabPassInfo(t, readBuffer, colorBit, depthBit, stencilBit, linearFilter, true, resizeScale);
- L1018 C26: new GrabPassInfo :: t.GrabPass = new GrabPassInfo(t, readBuffer, colorBit, depthBit, stencilBit, linearFilter, false, 1.0f);
- L1147 C34: new Mipmap2D :: Mipmap2D[] mipmaps = new Mipmap2D[mipCount];
- L1157 C32: new() :: Mipmap2D mip = new()
- L1164 C51: new DataSource :: Data = bytes is null ? null : new DataSource(bytes)
- L1205 C20: new GrabPassInfo :: return new GrabPassInfo(owner, readBuffer, colorBit, depthBit, stencilBit, linearFilter, resizeToFit, resizeScale);


## XRENGINE/Rendering/API/Rendering/Objects/Textures/2D/XRTexture2DArray.cs
- L32 C28: new XRTexture2D :: var textures = new XRTexture2D[count];
- L34 C31: new XRTexture2D :: textures[i] = new XRTexture2D(width, height, internalFormat, format, type, allocateData);
- L212 C24: new XRTexture2D :: Textures = new XRTexture2D[collection.Count];


## XRENGINE/Rendering/API/Rendering/Objects/Textures/3D/Mipmap3D.cs
- L106 C16: new() :: => new()
- L131 C20: new DataSource :: Data = new DataSource(data);


## XRENGINE/Rendering/API/Rendering/Objects/Textures/3D/XRTexture3D.cs
- L135 C31: new Mipmap3D :: Mipmap3D[] mips = new Mipmap3D[mipmapCount];
- L141 C27: new Mipmap3D :: mips[i] = new Mipmap3D(Math.Max(1u, w), Math.Max(1u, h), Math.Max(1u, d), internalFormat, format, type, allocateData);
- L187 C37: new Mipmap3D :: Mipmap3D[] newMipmaps = new Mipmap3D[desiredLevels];
- L201 C33: new Mipmap3D :: newMipmaps[i] = new Mipmap3D(w, h, d, baseMip.InternalFormat, baseMip.PixelFormat, baseMip.PixelType, false);
- L323 C28: new DataSource :: baseMip.Data = new DataSource(data);
- L331 C28: new byte :: byte[] bytes = new byte[texels * components * componentSize];


## XRENGINE/Rendering/API/Rendering/Objects/Textures/Cube/XRTextureCube.cs
- L28 C33: new CubeMipmap :: CubeMipmap[] mips = new CubeMipmap[mipCount];
- L31 C27: new CubeMipmap :: mips[i] = new CubeMipmap(sDim);
- L46 C33: new CubeMipmap :: CubeMipmap[] mips = new CubeMipmap[mipCount];
- L49 C27: new CubeMipmap :: mips[i] = new CubeMipmap(sDim, internalFormat, pixelFormat, pixelType, allocateData);


## XRENGINE/Rendering/API/Rendering/Objects/Textures/Cube/XRTextureCubeArray.cs
- L34 C37: new XRTextureCube :: XRTextureCube[] cubes = new XRTextureCube[layerCount];
- L36 C28: new XRTextureCube :: cubes[i] = new XRTextureCube(extent, internalFormat, format, type, allocateData, mipCount);


## XRENGINE/Rendering/API/Rendering/Objects/Textures/XRTexture.cs
- L31 C43: new() :: MagickReadSettings settings = new()
- L62 C43: new() :: MagickReadSettings settings = new()
- L93 C43: new() :: MagickReadSettings settings = new()
- L118 C16: new byte :: => new byte[width * height * depth * ComponentSize(type) * GetComponentCount(format)];
- L148 C32: new NotSupportedException :: _ => throw new NotSupportedException($"Unsupported pixel depth: {depth}"),


## XRENGINE/Rendering/API/Rendering/Objects/Textures/XRTextureView.cs
- L36 C63: new NotSupportedException :: public override ETextureTarget TextureTarget => throw new NotSupportedException("Texture view requires a concrete subtype for target.");


## XRENGINE/Rendering/API/Rendering/Objects/XRRenderProgram.cs
- L108 C13: new Dictionary :: new Dictionary<string, ShaderUniformBinding>(UniformComparer);
- L112 C13: new Dictionary :: new Dictionary<string, ShaderTextureBinding>(UniformComparer);
- L117 C96: new() :: private readonly Dictionary<XRShader, ShaderSubscription> _shaderSourceSubscriptions = new();
- L191 C47: new() :: ShaderSubscription subscription = new()
- L292 C27: new ShaderInterfaceBuilder :: var builder = new ShaderInterfaceBuilder();
- L846 C23: new ArgumentNullException :: throw new ArgumentNullException(nameof(buffer), "Cannot bind a null buffer to the shader program.");
- L893 C34: new() :: List<Token> tokens = new();
- L907 C28: new Token :: tokens.Add(new Token(statement[start..index], start));
- L1065 C30: new UniformDeclaration :: yield return new UniformDeclaration(glslType, name, isArray, arrayLength, arrayExpr);
- L1128 C43: new StructDefinition :: definitions[structName] = new StructDefinition(structName, fields);
- L1231 C34: new HashSet :: var recursionGuard = new HashSet<string>(StringComparer.Ordinal);
- L1321 C37: new Dictionary :: var builtUniforms = new Dictionary<string, ShaderUniformBinding>(_uniforms.Count, UniformComparer);
- L1324 C43: new ShaderUniformBinding :: builtUniforms[name] = new ShaderUniformBinding(
- L1334 C37: new Dictionary :: var builtTextures = new Dictionary<string, ShaderTextureBinding>(_textures.Count, UniformComparer);
- L1337 C43: new ShaderTextureBinding :: builtTextures[name] = new ShaderTextureBinding(
- L1353 C35: new UniformAccumulator :: accumulator = new UniformAccumulator(
- L1373 C35: new TextureAccumulator :: accumulator = new TextureAccumulator(
- L1404 C65: new() :: private readonly HashSet<EShaderType> _stages = new();
- L1442 C65: new() :: private readonly HashSet<EShaderType> _stages = new();
- L1486 C23: new ArgumentOutOfRangeException :: throw new ArgumentOutOfRangeException(nameof(commandCount),


## XRENGINE/Rendering/API/Rendering/Objects/XRShader.cs
- L111 C29: new() :: TextFile file = new();
- L131 C29: new() :: TextFile file = new();
- L226 C71: new() :: public ConcurrentDictionary<string, bool> _existingUniforms = new();


## XRENGINE/Rendering/API/Rendering/OpenGL/OpenGLRenderer.cs
- L116 C26: new ImGuiController :: controller = new ImGuiController(Api, XRWindow.Window, input);
- L132 C38: new OpenGLImGuiBackend :: return _imguiBackend ??= new OpenGLImGuiBackend(controller);
- L164 C36: new string :: var list = new string[extCount];
- L376 C36: new GLMaterial :: XRMaterial data => new GLMaterial(this, data),
- L377 C31: new GLShader :: XRShader s => new GLShader(this, s),
- L381 C52: new GLMeshRenderer :: XRMeshRenderer.BaseVersion data => new GLMeshRenderer(this, data),
- L384 C49: new GLRenderProgramPipeline :: XRRenderProgramPipeline data => new GLRenderProgramPipeline(this, data),
- L385 C41: new GLRenderProgram :: XRRenderProgram data => new GLRenderProgram(this, data),
- L388 C38: new GLDataBuffer :: XRDataBuffer data => new GLDataBuffer(this, data),
- L389 C42: new GLDataBufferView :: XRDataBufferView data => new GLDataBufferView(this, data),
- L392 C40: new GLRenderBuffer :: XRRenderBuffer data => new GLRenderBuffer(this, data),
- L393 C39: new GLFrameBuffer :: XRFrameBuffer data => new GLFrameBuffer(this, data),
- L396 C37: new GLTexture1D :: XRTexture1D data => new GLTexture1D(this, data),
- L397 C42: new GLTexture1DArray :: XRTexture1DArray data => new GLTexture1DArray(this, data),
- L398 C43: new GLTextureView :: XRTextureViewBase data => new GLTextureView(this, data),
- L401 C37: new GLTexture2D :: XRTexture2D data => new GLTexture2D(this, data),
- L402 C42: new GLTexture2DArray :: XRTexture2DArray data => new GLTexture2DArray(this, data),
- L403 C44: new GLTextureRectangle :: XRTextureRectangle data => new GLTextureRectangle(this, data),
- L406 C37: new GLTexture3D :: XRTexture3D data => new GLTexture3D(this, data),
- L409 C39: new GLTextureCube :: XRTextureCube data => new GLTextureCube(this, data),
- L410 C44: new GLTextureCubeArray :: XRTextureCubeArray data => new GLTextureCubeArray(this, data),
- L413 C41: new GLTextureBuffer :: XRTextureBuffer data => new GLTextureBuffer(this, data),
- L416 C32: new GLSampler :: XRSampler s => new GLSampler(this, s),
- L419 C39: new GLRenderQuery :: XRRenderQuery data => new GLRenderQuery(this, data),
- L420 C45: new GLTransformFeedback :: XRTransformFeedback data => new GLTransformFeedback(this, data),
- L422 C28: new InvalidOperationException :: _ => throw new InvalidOperationException($"Render object type {renderObject.GetType()} is not supported.")
- L558 C28: new ArgumentOutOfRangeException :: _ => throw new ArgumentOutOfRangeException(nameof(comparison), comparison, null),
- L629 C28: new ArgumentOutOfRangeException :: _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
- L1323 C30: new XRShader :: var shader = new XRShader(EShaderType.Compute, LuminanceComputeShaderSource);
- L1324 C44: new XRRenderProgram :: _luminanceComputeProgram = new XRRenderProgram(true, false, shader);
- L1350 C32: new XRShader :: var shader2D = new XRShader(EShaderType.Compute, AutoExposureComputeShaderSource2D);
- L1351 C49: new XRRenderProgram :: _autoExposureComputeProgram2D = new XRRenderProgram(true, false, shader2D);
- L1353 C37: new XRShader :: var shader2DArray = new XRShader(EShaderType.Compute, AutoExposureComputeShaderSource2DArray);
- L1354 C54: new XRRenderProgram :: _autoExposureComputeProgram2DArray = new XRRenderProgram(true, false, shader2DArray);
- L1554 C19: new Vector4 :: : new Vector4[layerCount];
- L1719 C32: new Vector3 :: callback(true, new Vector3(r, g, b).Dot(luminance));
- L1801 C46: new Data.Vectors.IVector2 :: glProgram.Uniform("textureSize", new Data.Vectors.IVector2((int)w, (int)h));
- L1838 C32: new Vector3 :: callback(true, new Vector3(avg.X, avg.Y, avg.Z).Dot(luminance));
- L2134 C30: new byte :: byte[] newData = new byte[pixelCount * 3];
- L2154 C30: new byte :: byte[] newData = new byte[pixelCount * 3];
- L2189 C37: new BoundingRectangle :: uint pbo = ReadFBOToPBO(new BoundingRectangle(x, y, 1, 1), format, pixelType, size, out IntPtr sync);
- L2222 C37: new BoundingRectangle :: uint pbo = ReadFBOToPBO(new BoundingRectangle(x, y, 1, 1), format, pixelType, size, out IntPtr sync);
- L2293 C33: new uint :: uint[] bindingIds = new uint[objs.Length];
- L2360 C26: new uint :: uint[] ids = new uint[count];
- L2407 C75: new() :: private static EGLObjectType TypeFor<T>() where T : GLObjectBase, new()
- L2445 C28: new InvalidOperationException :: _ => throw new InvalidOperationException($"Type {typeof(T)} is not a valid GLObjectBase type."),
- L3031 C79: new() :: private readonly Dictionary<int, IGLTexture?> _boundTexturesPerUnit = new();


## XRENGINE/Rendering/API/Rendering/OpenGL/OpenGLRenderer.DebugTracking.cs
- L32 C58: new() :: private static readonly object _glErrorTrackerLock = new();
- L33 C90: new() :: private static readonly Dictionary<int, OpenGLDebugErrorAggregate> _glErrorTracker = new();
- L45 C29: new OpenGLDebugErrorAggregate :: aggregate = new OpenGLDebugErrorAggregate
- L69 C28: new OpenGLDebugErrorInfo :: var snapshot = new OpenGLDebugErrorInfo[_glErrorTracker.Count];
- L73 C37: new OpenGLDebugErrorInfo :: snapshot[index++] = new OpenGLDebugErrorInfo


## XRENGINE/Rendering/API/Rendering/OpenGL/Types/Buffers/GLDataBuffer.cs
- L15 C98: new() :: private static readonly ConcurrentDictionary<string, byte> _missingInterleavedLogs = new();
- L275 C28: new ArgumentOutOfRangeException :: _ => throw new ArgumentOutOfRangeException(nameof(usage), usage, null),
- L394 C33: new DataSource :: GPUSideSource = new DataSource(addr, length);


## XRENGINE/Rendering/API/Rendering/OpenGL/Types/GLObjectBase.cs
- L157 C32: new ArgumentOutOfRangeException :: _ => throw new ArgumentOutOfRangeException(nameof(wrap), wrap, null),
- L169 C32: new ArgumentOutOfRangeException :: _ => throw new ArgumentOutOfRangeException(nameof(minFilter), minFilter, null),
- L177 C32: new ArgumentOutOfRangeException :: _ => throw new ArgumentOutOfRangeException(nameof(magFilter), magFilter, null),
- L202 C32: new ArgumentOutOfRangeException :: _ => throw new ArgumentOutOfRangeException(nameof(pixelType), pixelType, null),
- L230 C32: new ArgumentOutOfRangeException :: _ => throw new ArgumentOutOfRangeException(nameof(pixelFormat), pixelFormat, null),
- L289 C32: new ArgumentOutOfRangeException :: _ => throw new ArgumentOutOfRangeException(nameof(internalFormat), internalFormat, null),
- L376 C32: new ArgumentOutOfRangeException :: _ => throw new ArgumentOutOfRangeException(nameof(sizedInternalFormat), sizedInternalFormat, null),
- L397 C32: new ArgumentOutOfRangeException :: _ => throw new ArgumentOutOfRangeException(nameof(target), target, null),
- L532 C32: new NotImplementedException :: _ => throw new NotImplementedException()
- L541 C32: new NotImplementedException :: _ => throw new NotImplementedException()
- L623 C32: new ArgumentOutOfRangeException :: _ => throw new ArgumentOutOfRangeException(nameof(sizedInternalFormat), sizedInternalFormat, null),
- L738 C32: new ArgumentOutOfRangeException :: _ => throw new ArgumentOutOfRangeException(nameof(attachment), attachment, null),


## XRENGINE/Rendering/API/Rendering/OpenGL/Types/Mesh Renderer/GLMeshRenderer.Shaders.cs
- L113 C78: new XRRenderProgramPipeline :: _pipeline ??= Renderer.GenericToAPI<GLRenderProgramPipeline>(new XRRenderProgramPipeline())!;
- L193 C66: new XRRenderProgram :: program = Renderer.GenericToAPI<GLRenderProgram>(new XRRenderProgram(false, false, shaders))!;
- L218 C72: new XRRenderProgram :: vertexProgram = Renderer.GenericToAPI<GLRenderProgram>(new XRRenderProgram(false, true, vertexShader))!;


## XRENGINE/Rendering/API/Rendering/OpenGL/Types/Meshes/GLRenderProgram.cs
- L30 C33: new() :: _uniformCache = new(),
- L31 C32: new() :: _attribCache = new();
- L33 C85: new() :: private readonly ConcurrentDictionary<int, string> _locationNameCache = new();
- L34 C91: new() :: private readonly ConcurrentDictionary<string, UniformInfo> _uniformMetadata = new();
- L35 C92: new() :: private readonly ConcurrentDictionary<string, byte> _loggedUniformMismatches = new();
- L41 C94: new() :: private readonly ConcurrentDictionary<string, byte> _loggedEmptyBindingBatches = new();
- L452 C37: new byte :: byte[] nameBuffer = new byte[maxLength];
- L467 C50: new UniformInfo :: _uniformMetadata[name] = new UniformInfo(type, size);
- L471 C58: new UniformInfo :: _uniformMetadata[baseName] = new UniformInfo(type, size);
- L567 C31: new() :: BinaryCache = new();
- L741 C48: new GLShader :: GLShader?[] attached = new GLShader?[shaderCache.Count];
- L848 C33: new byte :: byte[] binary = new byte[len];
- L1203 C30: new int :: int[] conv = new int[p.Length];
- L1232 C30: new int :: int[] conv = new int[p.Length * 2];
- L1246 C30: new int :: int[] conv = new int[p.Length * 3];
- L1261 C30: new int :: int[] conv = new int[p.Length * 4];


## XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/CubeMipmap.cs
- L14 C57: new Mipmap2D :: public Mipmap2D[] Sides { get; private set; } = new Mipmap2D[6];
- L22 C23: new InvalidOperationException :: throw new InvalidOperationException("Cubemap cross dimensions are invalid; width/height be a 4:3 or 3:4 ratio.");
- L34 C32: new Mipmap2D :: => Sides.Fill(i => new Mipmap2D(dim, dim, internalFormat, format, type, allocateData));
- L149 C51: new float :: outPixels.SetPixel((int)i, j, new float[] { (ushort)ri, (ushort)gi, (ushort)bi });
- L262 C24: new Mipmap2D :: return new Mipmap2D((MagickImage)clone);
- L277 C28: new Mipmap2D :: Sides[i] = new Mipmap2D(bmp);
- L281 C32: new Mipmap2D :: => Sides.Fill(i => new Mipmap2D(new MagickImage(color ??= new MagickColor(0, 0, 0, 0), dim, dim)));
- L281 C45: new MagickImage :: => Sides.Fill(i => new Mipmap2D(new MagickImage(color ??= new MagickColor(0, 0, 0, 0), dim, dim)));
- L281 C71: new MagickColor :: => Sides.Fill(i => new Mipmap2D(new MagickImage(color ??= new MagickColor(0, 0, 0, 0), dim, dim)));
- L284 C32: new Mipmap2D :: => Sides.Fill(i => new Mipmap2D(dim, dim, internalFormat, format, type, allocateData));


## XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture.cs
- L128 C40: new() :: PreBindCallback callback = new();
- L135 C44: new() :: PrePushDataCallback callback = new();


## XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture1D.cs
- L13 C59: new() :: private readonly List<Mipmap1D> _trackedMipmaps = new();
- L208 C27: new ArgumentException :: throw new ArgumentException("PBO must be bound to PixelUnpackBuffer for texture uploads.");


## XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture1DArray.cs
- L14 C62: new() :: private readonly List<LayerBinding> _layerBindings = new();
- L61 C36: new LayerBinding :: _layerBindings.Add(new LayerBinding(this, texture));
- L227 C27: new ArgumentException :: throw new ArgumentException("PBO must be bound to PixelUnpackBuffer for texture uploads.");
- L263 C63: new() :: private readonly List<Mipmap1D> _trackedMipmaps = new();


## XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture2D.cs
- L46 C23: new MipmapInfo :: Mipmaps = new MipmapInfo[Data.Mipmaps.Length];
- L48 C30: new MipmapInfo :: Mipmaps[i] = new MipmapInfo(this, Data.Mipmaps[i]);
- L264 C23: new ArgumentException :: throw new ArgumentException("PBO must be of type PixelUnpackBuffer.");


## XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTexture2DArray.cs
- L103 C23: new MipmapInfo :: Mipmaps = new MipmapInfo[Data.Mipmaps.Length];
- L105 C30: new MipmapInfo :: Mipmaps[i] = new MipmapInfo(this, Data.Mipmaps[i]);


## XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTextureCube.cs
- L88 C23: new MipmapInfo :: Mipmaps = new MipmapInfo[Data.Mipmaps.Length];
- L90 C30: new MipmapInfo :: Mipmaps[i] = new MipmapInfo(this, Data.Mipmaps[i]);


## XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTextureCubeArray.cs
- L15 C60: new() :: private readonly List<CubeLayerInfo> _cubeLayers = new();
- L62 C33: new CubeLayerInfo :: _cubeLayers.Add(new CubeLayerInfo(this, cube));
- L257 C27: new ArgumentException :: throw new ArgumentException("PBO must be bound to PixelUnpackBuffer for texture uploads.");
- L296 C58: new() :: private readonly List<MipmapInfo> _mipmaps = new();
- L352 C34: new MipmapInfo :: _mipmaps.Add(new MipmapInfo(_owner, mip));


## XRENGINE/Rendering/API/Rendering/OpenGL/Types/Textures/GLTextureRectangle.cs
- L71 C27: new ArgumentException :: throw new ArgumentException("StreamingPBO must target PixelUnpackBuffer for rectangle uploads.");


## XRENGINE/Rendering/API/Rendering/OpenXR/Extensions.cs
- L40 C23: new ArgumentOutOfRangeException :: throw new ArgumentOutOfRangeException(nameof(renderer), renderer, null);


## XRENGINE/Rendering/API/Rendering/OpenXR/Init.cs
- L199 C29: new View :: private View[] _views = new View[2];
- L307 C19: new Exception :: throw new Exception("Window is null");
- L309 C28: new GraphicsRequirementsVulkanKHR :: var requirements = new GraphicsRequirementsVulkanKHR
- L315 C19: new Exception :: throw new Exception("Failed to get Vulkan extension");
- L318 C19: new Exception :: throw new Exception("Failed to get Vulkan graphics requirements");
- L323 C19: new Exception :: throw new Exception("Renderer is not a VulkanRenderer.");
- L345 C25: new GraphicsBindingVulkanKHR :: var vkBinding = new GraphicsBindingVulkanKHR
- L354 C26: new SessionCreateInfo :: var createInfo = new SessionCreateInfo
- L362 C19: new Exception :: throw new Exception($"Failed to create session: {result}");
- L391 C40: new FrameEndInfo :: var frameEndInfoNoLayers = new FrameEndInfo
- L406 C21: new CompositionLayerProjection :: var layer = new CompositionLayerProjection
- L418 C28: new FrameEndInfo :: var frameEndInfo = new FrameEndInfo
- L451 C27: new SwapchainImageAcquireInfo :: var acquireInfo = new SwapchainImageAcquireInfo
- L460 C24: new SwapchainImageWaitInfo :: var waitInfo = new SwapchainImageWaitInfo
- L487 C27: new SwapchainImageReleaseInfo :: var releaseInfo = new SwapchainImageReleaseInfo
- L502 C57: new Rect2Di :: projectionViews[viewIndex].SubImage.ImageRect = new Rect2Di
- L504 C22: new Offset2Di :: Offset = new Offset2Di
- L509 C22: new Extent2Di :: Extent = new Extent2Di
- L542 C19: new Exception :: throw new Exception($"Expected 2 views, got {_viewCount}");
- L545 C18: new View :: _views = new View[_viewCount];
- L555 C39: new SwapchainCreateInfo :: var swapchainCreateInfo = new SwapchainCreateInfo
- L572 C27: new Exception :: throw new Exception($"Failed to create swapchain for view {i}");
- L634 C23: new Exception :: throw new Exception("Unsupported renderer");
- L789 C29: new SystemGetInfo :: var systemGetInfo = new SystemGetInfo
- L797 C19: new Exception :: throw new Exception($"Failed to get system: {result}");
- L803 C31: new ReferenceSpaceCreateInfo :: var spaceCreateInfo = new ReferenceSpaceCreateInfo
- L807 C36: new Posef :: PoseInReferenceSpace = new Posef
- L809 C31: new Quaternionf :: Orientation = new Quaternionf { X = 0, Y = 0, Z = 0, W = 1 },
- L810 C28: new Vector3f :: Position = new Vector3f { X = 0, Y = 0, Z = 0 }
- L816 C19: new Exception :: throw new Exception("Failed to create reference space");
- L875 C19: new Exception :: throw new Exception("Window is null");
- L906 C28: new GraphicsRequirementsOpenGLKHR :: var requirements = new GraphicsRequirementsOpenGLKHR
- L912 C19: new Exception :: throw new Exception("Failed to get OpenGL extension");
- L915 C19: new Exception :: throw new Exception("Failed to get OpenGL graphics requirements");
- L940 C19: new Exception :: throw new Exception("Cannot create OpenXR session: no valid OpenGL handles available (both current and window handles are null). Ensure OpenXR OpenGL session creation runs on the window render thread and the GL context is created.");
- L950 C30: new List :: var attemptResults = new List<string>(2);
- L980 C27: new Exception :: throw new Exception(
- L988 C19: new Exception :: throw new Exception($"OpenXR OpenGL preflight failed: {ex.Message}");
- L1002 C29: new GraphicsBindingOpenGLWin32KHR :: var glBinding = new GraphicsBindingOpenGLWin32KHR
- L1008 C30: new SessionCreateInfo :: var createInfo = new SessionCreateInfo
- L1030 C19: new Exception :: throw new Exception(
- L1050 C65: new ViewConfigurationView :: private readonly ViewConfigurationView[] _viewConfigViews = new ViewConfigurationView[2];
- L1055 C48: new Swapchain :: private readonly Swapchain[] _swapchains = new Swapchain[2];
- L1060 C70: new SwapchainImageOpenGLKHR :: private readonly SwapchainImageOpenGLKHR*[] _swapchainImagesGL = new SwapchainImageOpenGLKHR*[2];
- L1065 C56: new uint :: private readonly uint[][] _swapchainFramebuffers = new uint[2][];
- L1070 C53: new uint :: private readonly uint[] _swapchainImageCounts = new uint[2];
- L1075 C71: new SwapchainImageVulkan2KHR :: private readonly SwapchainImageVulkan2KHR*[] _swapchainImagesVK = new SwapchainImageVulkan2KHR*[2];
- L1080 C69: new SwapchainImageD3D12KHR :: private readonly SwapchainImageD3D12KHR*[] _swapchainImagesDX = new SwapchainImageD3D12KHR*[2];
- L1090 C19: new Exception :: throw new Exception("OpenGL context not initialized for OpenXR");
- L1096 C19: new Exception :: throw new Exception($"Failed to enumerate OpenXR swapchain formats for OpenGL. Result={formatResult}, Count={formatCount}");
- L1098 C23: new long :: var formats = new long[formatCount];
- L1104 C19: new Exception :: throw new Exception($"Failed to enumerate OpenXR swapchain formats for OpenGL. Result={formatResult}, Count={formatCount}");
- L1133 C19: new Exception :: throw new Exception($"Expected 2 views, got {_viewCount}");
- L1135 C18: new View :: _views = new View[_viewCount];
- L1155 C23: new Exception :: throw new Exception($"OpenXR runtime reported an invalid recommended image rect size for view {i}: {rw}x{rh}. Cannot create swapchains.");
- L1170 C39: new[] :: foreach (var usage in new[] { SwapchainUsageFlags.ColorAttachmentBit | SwapchainUsageFlags.SampledBit, SwapchainUsageFlags.ColorAttachmentBit })
- L1172 C97: new[] :: foreach (var samples in recommendedSamples > 1 ? [recommendedSamples, 1u] : new[] { 1u })
- L1174 C51: new SwapchainCreateInfo :: var swapchainCreateInfo = new SwapchainCreateInfo
- L1209 C23: new Exception :: throw new Exception($"Failed to create swapchain for view {i}. LastResult={lastResult}, RecommendedSamples={recommendedSamples}, Size={width}x{height}, SupportedFormats={supportedFormatsLog}");
- L1218 C41: new uint :: _swapchainFramebuffers[i] = new uint[imageCount];
- L1366 C33: new XRViewport :: _openXrLeftViewport ??= new XRViewport(Window)
- L1373 C34: new XRViewport :: _openXrRightViewport ??= new XRViewport(Window)
- L1417 C34: new XRCamera :: _openXrLeftEyeCamera ??= new XRCamera(new Transform());
- L1417 C47: new Transform :: _openXrLeftEyeCamera ??= new XRCamera(new Transform());
- L1418 C35: new XRCamera :: _openXrRightEyeCamera ??= new XRCamera(new Transform());
- L1418 C48: new Transform :: _openXrRightEyeCamera ??= new XRCamera(new Transform());
- L1435 C28: new XROpenXRFovCameraParameters :: openxrParams = new XROpenXRFovCameraParameters(nearZ, farZ);
- L1455 C50: new Quaternion :: Quaternion eyeRot = Quaternion.Normalize(new Quaternion(
- L1510 C32: new XRRenderBuffer :: _viewportMirrorDepth = new XRRenderBuffer(width, height, ERenderBufferStorage.Depth24Stencil8, EFrameBufferAttachment.DepthStencilAttachment)
- L1515 C30: new XRFrameBuffer :: _viewportMirrorFbo = new XRFrameBuffer(
- L1638 C30: new FrameBeginInfo :: var frameBeginInfo = new FrameBeginInfo { Type = StructureType.FrameBeginInfo };
- L1654 C29: new FrameWaitInfo :: var frameWaitInfo = new FrameWaitInfo { Type = StructureType.FrameWaitInfo };
- L1655 C22: new FrameState :: frameState = new FrameState { Type = StructureType.FrameState };
- L1667 C30: new ViewLocateInfo :: var viewLocateInfo = new ViewLocateInfo
- L1675 C25: new ViewState :: var viewState = new ViewState { Type = StructureType.ViewState };
- L1679 C29: new Span :: var viewsSpan = new Span<View>(viewsPtr, (int)_viewCount);
- L1697 C25: new EventDataBuffer :: var eventData = new EventDataBuffer
- L1726 C45: new SessionBeginInfo :: var beginInfo = new SessionBeginInfo


## XRENGINE/Rendering/API/Rendering/OpenXR/Instance.cs
- L46 C15: new[] :: ? new[] { "XR_KHR_vulkan_enable", "XR_KHR_vulkan_enable2" }
- L47 C15: new[] :: : new[] { "XR_KHR_opengl_enable" };
- L50 C19: new Exception :: throw new Exception($"OpenXR runtime does not support required renderer extension(s): {string.Join(", ", requiredForRenderer)}");
- L140 C27: new ProcessStartInfo :: Process.Start(new ProcessStartInfo
- L159 C27: new ProcessStartInfo :: Process.Start(new ProcessStartInfo
- L176 C19: new HashSet :: var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
- L180 C21: new ExtensionProperties :: var props = new ExtensionProperties[count];
- L209 C21: new ExtensionProperties :: var props = new ExtensionProperties[count];
- L236 C19: new Exception :: throw new Exception(BuildCreateInstanceFailureMessage(result, createInfo));
- L242 C18: new StringBuilder :: var sb = new StringBuilder();
- L293 C20: new List :: var list = new List<string>((int)count);
- L342 C41: new() :: InstanceCreateInfo createInfo = new()
- L367 C30: new Version64 :: appInfo.ApiVersion = new Version64(1, 0, 0);
- L368 C38: new Version32 :: appInfo.ApplicationVersion = new Version32(1, 0, 0);
- L369 C33: new Version32 :: appInfo.EngineVersion = new Version32(1, 0, 0);


## XRENGINE/Rendering/API/Rendering/OpenXR/Validation.cs
- L54 C55: new() :: DebugUtilsMessengerCreateInfoEXT createInfo = new();
- L57 C17: new DebugUtilsMessengerEXT :: var d = new DebugUtilsMessengerEXT();
- L59 C19: new Exception :: throw new Exception("Failed to set up OpenXR debug messenger.");
- L66 C31: new ApiLayerProperties :: var availableLayers = new ApiLayerProperties[layerCount];


## XRENGINE/Rendering/API/Rendering/Vulkan/Drawing.cs
- L18 C19: new NotImplementedException :: throw new NotImplementedException();
- L22 C19: new NotImplementedException :: throw new NotImplementedException();
- L26 C19: new NotImplementedException :: throw new NotImplementedException();
- L65 C40: new() :: BufferImageCopy copy = new()
- L70 C40: new ImageSubresourceLayers :: ImageSubresource = new ImageSubresourceLayers
- L77 C35: new Offset3D :: ImageOffset = new Offset3D { X = x, Y = y, Z = 0 },
- L78 C35: new Extent3D :: ImageExtent = new Extent3D { Width = (uint)w, Height = (uint)h, Depth = 1 }
- L132 C19: new NotImplementedException :: throw new NotImplementedException();
- L136 C19: new NotImplementedException :: throw new NotImplementedException();
- L140 C19: new NotImplementedException :: throw new NotImplementedException();
- L243 C19: new NotImplementedException :: throw new NotImplementedException();
- L252 C19: new NotImplementedException :: throw new NotImplementedException();
- L256 C19: new NotImplementedException :: throw new NotImplementedException();
- L262 C36: new VkMaterial :: XRMaterial data => new VkMaterial(this, data),
- L263 C52: new VkMeshRenderer :: XRMeshRenderer.BaseVersion data => new VkMeshRenderer(this, data),
- L264 C49: new VkRenderProgramPipeline :: XRRenderProgramPipeline data => new VkRenderProgramPipeline(this, data),
- L265 C41: new VkRenderProgram :: XRRenderProgram data => new VkRenderProgram(this, data),
- L266 C38: new VkDataBuffer :: XRDataBuffer data => new VkDataBuffer(this, data),
- L267 C32: new VkSampler :: XRSampler s => new VkSampler(this, s),
- L268 C31: new VkShader :: XRShader s => new VkShader(this, s),
- L271 C40: new VkRenderBuffer :: XRRenderBuffer data => new VkRenderBuffer(this, data),
- L272 C39: new VkFrameBuffer :: XRFrameBuffer data => new VkFrameBuffer(this, data),
- L277 C43: new VkTextureView :: XRTextureViewBase data => new VkTextureView(this, data),
- L281 C37: new VkTexture2D :: XRTexture2D data => new VkTexture2D(this, data),
- L282 C42: new VkTexture2DArray :: XRTexture2DArray data => new VkTexture2DArray(this, data),
- L287 C37: new VkTexture3D :: XRTexture3D data => new VkTexture3D(this, data),
- L292 C39: new VkTextureCube :: XRTextureCube data => new VkTextureCube(this, data),
- L297 C39: new VkRenderQuery :: XRRenderQuery data => new VkRenderQuery(this, data),
- L298 C45: new VkTransformFeedback :: XRTransformFeedback data => new VkTransformFeedback(this, data),
- L300 C28: new InvalidOperationException :: _ => throw new InvalidOperationException($"Render object type {renderObject.GetType()} is not supported.")
- L332 C23: new Exception :: throw new Exception("Failed to acquire swap chain image.");
- L347 C37: new() :: SubmitInfo submitInfo = new()
- L375 C23: new Exception :: throw new Exception("Failed to submit draw command buffer.");
- L379 C42: new() :: PresentInfoKHR presentInfo = new()
- L402 C23: new Exception :: throw new Exception("Failed to present swap chain image.");
- L442 C19: new NotImplementedException :: throw new NotImplementedException();
- L448 C19: new NotImplementedException :: throw new NotImplementedException();
- L454 C19: new NotImplementedException :: throw new NotImplementedException();
- L460 C19: new NotImplementedException :: throw new NotImplementedException();
- L580 C28: new BlitImageInfo :: info = new BlitImageInfo(
- L593 C28: new BlitImageInfo :: info = new BlitImageInfo(
- L606 C28: new BlitImageInfo :: info = new BlitImageInfo(
- L620 C28: new BlitImageInfo :: info = new BlitImageInfo(
- L647 C32: new() :: ImageBlit region = new()
- L649 C34: new ImageSubresourceLayers :: SrcSubresource = new ImageSubresourceLayers
- L656 C34: new ImageSubresourceLayers :: DstSubresource = new ImageSubresourceLayers
- L665 C42: new Offset3D :: region.SrcOffsets.Element0 = new Offset3D { X = inX, Y = inY, Z = 0 };
- L666 C42: new Offset3D :: region.SrcOffsets.Element1 = new Offset3D { X = inX + (int)inW, Y = inY + (int)inH, Z = 1 };
- L667 C42: new Offset3D :: region.DstOffsets.Element0 = new Offset3D { X = outX, Y = outY, Z = 0 };
- L668 C42: new Offset3D :: region.DstOffsets.Element1 = new Offset3D { X = outX + (int)outW, Y = outY + (int)outH, Z = 1 };
- L683 C42: new() :: ImageMemoryBarrier barrier = new()
- L693 C36: new ImageSubresourceRange :: SubresourceRange = new ImageSubresourceRange
- L721 C42: new() :: ImageMemoryBarrier barrier = new()
- L731 C36: new ImageSubresourceRange :: SubresourceRange = new ImageSubresourceRange


## XRENGINE/Rendering/API/Rendering/Vulkan/FrameBufferRenderPasses.cs
- L9 C87: new() :: private readonly Dictionary<RenderPassKey, RenderPass> _frameBufferRenderPasses = new();
- L26 C48: new AttachmentDescription :: AttachmentDescription[] descriptions = new AttachmentDescription[signature.Length];
- L42 C15: new AttachmentReference :: ? new AttachmentReference[colorCount]
- L69 C42: new() :: SubpassDescription subpass = new()
- L77 C47: new() :: RenderPassCreateInfo createInfo = new()
- L87 C23: new Exception :: throw new Exception("Failed to create framebuffer render pass.");
- L122 C29: new() :: HashCode hash = new();
- L170 C16: new() :: => new()
- L188 C20: new AttachmentReference :: return new AttachmentReference
- L212 C29: new() :: HashCode hash = new();


## XRENGINE/Rendering/API/Rendering/Vulkan/Init.cs
- L18 C23: new Exception :: throw new Exception("Windowing platform doesn't support Vulkan.");
- L58 C45: new() :: AllocationCallbacks callbacks = new()
- L60 C33: new PfnAllocationFunction :: PfnAllocation = new PfnAllocationFunction(Allocated),
- L61 C35: new PfnReallocationFunction :: PfnReallocation = new PfnReallocationFunction(Reallocated),
- L62 C27: new PfnFreeFunction :: PfnFree = new PfnFreeFunction(Freed),
- L63 C41: new PfnInternalAllocationNotification :: PfnInternalAllocation = new PfnInternalAllocationNotification(InternalAllocated),
- L64 C35: new PfnInternalFreeNotification :: PfnInternalFree = new PfnInternalFreeNotification(InternalFreed)
- L67 C23: new Exception :: throw new Exception("Failed to allocate memory.");
- L127 C19: new NotImplementedException :: throw new NotImplementedException();
- L135 C19: new NotImplementedException :: throw new NotImplementedException();
- L139 C19: new NotImplementedException :: throw new NotImplementedException();
- L177 C19: new NotImplementedException :: throw new NotImplementedException();


## XRENGINE/Rendering/API/Rendering/Vulkan/Objects/CommandBuffers.cs
- L29 C23: new InvalidOperationException :: throw new InvalidOperationException("Framebuffers must be created before allocating command buffers.");
- L31 C31: new CommandBuffer :: _commandBuffers = new CommandBuffer[swapChainFramebuffers.Length];
- L33 C51: new() :: CommandBufferAllocateInfo allocInfo = new()
- L44 C27: new Exception :: throw new Exception("Failed to allocate command buffers.");
- L53 C23: new InvalidOperationException :: throw new InvalidOperationException("Command buffers have not been allocated yet.");
- L56 C23: new InvalidOperationException :: throw new InvalidOperationException("Command buffer dirty flags are not initialised correctly.");
- L72 C48: new() :: CommandBufferBeginInfo beginInfo = new()
- L78 C23: new Exception :: throw new Exception("Failed to begin recording command buffer.");
- L88 C50: new() :: RenderPassBeginInfo renderPassInfo = new()
- L93 C30: new Rect2D :: RenderArea = new Rect2D
- L95 C30: new Offset2D :: Offset = new Offset2D(0, 0),
- L116 C23: new Exception :: throw new Exception("Failed to record command buffer.");
- L136 C43: new() :: MemoryBarrier memoryBarrier = new()
- L271 C47: new() :: ImageSubresourceRange range = new()
- L280 C46: new() :: ImageMemoryBarrier barrier = new()
- L331 C54: new() :: CommandBufferAllocateInfo allocateInfo = new()
- L341 C48: new() :: CommandBufferBeginInfo beginInfo = new()
- L356 C37: new() :: SubmitInfo submitInfo = new()
- L377 C40: new bool :: _commandBufferDirtyFlags = new bool[_commandBuffers.Length];


## XRENGINE/Rendering/API/Rendering/Vulkan/Objects/CommandPool.cs
- L16 C46: new() :: CommandPoolCreateInfo poolInfo = new()
- L23 C23: new Exception :: throw new Exception("failed to create command pool!");


## XRENGINE/Rendering/API/Rendering/Vulkan/Objects/DescriptorPool.cs
- L14 C29: new DescriptorPoolSize :: var poolSizes = new DescriptorPoolSize[]
- L16 C13: new() :: new()
- L21 C13: new() :: new()
- L32 C53: new() :: DescriptorPoolCreateInfo poolInfo = new()
- L41 C27: new Exception :: throw new Exception("Failed to create descriptor pool.");


## XRENGINE/Rendering/API/Rendering/Vulkan/Objects/DescriptorSetLayout.cs
- L14 C59: new() :: DescriptorSetLayoutBinding uboLayoutBinding = new()
- L23 C56: new() :: DescriptorSetLayoutCreateInfo layoutInfo = new()
- L33 C27: new Exception :: throw new Exception("failed to create descriptor set layout!");


## XRENGINE/Rendering/API/Rendering/Vulkan/Objects/DescriptorSets.cs
- L11 C23: new DescriptorSetLayout :: var layouts = new DescriptorSetLayout[swapChainImages!.Length];
- L16 C54: new() :: DescriptorSetAllocateInfo allocateInfo = new()
- L24 C30: new DescriptorSet :: descriptorSets = new DescriptorSet[swapChainImages.Length];
- L28 C27: new Exception :: throw new Exception("Failed to allocate descriptor sets.");


## XRENGINE/Rendering/API/Rendering/Vulkan/Objects/FrameBuffers.cs
- L26 C19: new InvalidOperationException :: throw new InvalidOperationException("Swapchain image views must be created before framebuffers.");
- L28 C33: new Framebuffer :: swapChainFramebuffers = new Framebuffer[swapChainImageViews.Length];
- L35 C53: new() :: FramebufferCreateInfo framebufferInfo = new()
- L47 C23: new Exception :: throw new Exception("Failed to create framebuffer.");


## XRENGINE/Rendering/API/Rendering/Vulkan/Objects/ImageViews.cs
- L17 C31: new ImageView :: swapChainImageViews = new ImageView[swapChainImages!.Length];
- L21 C46: new() :: ImageViewCreateInfo createInfo = new()
- L46 C23: new Exception :: throw new Exception("Failed to create image views.");


## XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Instance.cs
- L23 C35: new() :: ApplicationInfo appInfo = new()
- L27 C34: new Version32 :: ApplicationVersion = new Version32(1, 0, 0),
- L29 C29: new Version32 :: EngineVersion = new Version32(1, 0, 0),
- L33 C41: new() :: InstanceCreateInfo createInfo = new()
- L48 C64: new() :: DebugUtilsMessengerCreateInfoEXT debugCreateInfo = new();
- L59 C19: new Exception :: throw new Exception("failed to create instance!");


## XRENGINE/Rendering/API/Rendering/Vulkan/Objects/LogicalDevice.cs
- L40 C35: new[] :: var uniqueQueueFamilies = new[] { indices.GraphicsFamilyIndex!.Value, indices.PresentFamilyIndex!.Value };
- L52 C35: new() :: queueCreateInfos[i] = new()
- L61 C52: new() :: PhysicalDeviceFeatures supportedFeatures = new();
- L64 C49: new() :: PhysicalDeviceFeatures deviceFeatures = new();
- L72 C39: new() :: DeviceCreateInfo createInfo = new()
- L96 C19: new Exception :: throw new Exception("Failed to create logical device.");


## XRENGINE/Rendering/API/Rendering/Vulkan/Objects/RenderPasses.cs
- L19 C49: new() :: AttachmentDescription colorAttachment = new()
- L30 C50: new() :: AttachmentReference colorAttachmentRef = new()
- L36 C38: new() :: SubpassDescription subpass = new()
- L43 C47: new() :: RenderPassCreateInfo renderPassInfo = new()
- L53 C19: new Exception :: throw new Exception("Failed to create render pass.");


## XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Surface.cs
- L16 C19: new NotSupportedException :: throw new NotSupportedException("KHR_surface extension not found.");


## XRENGINE/Rendering/API/Rendering/Vulkan/Objects/SyncObjects.cs
- L24 C36: new Semaphore :: imageAvailableSemaphores = new Semaphore[MAX_FRAMES_IN_FLIGHT];
- L25 C36: new Semaphore :: renderFinishedSemaphores = new Semaphore[MAX_FRAMES_IN_FLIGHT];
- L26 C26: new Fence :: inFlightFences = new Fence[MAX_FRAMES_IN_FLIGHT];
- L27 C26: new Fence :: imagesInFlight = new Fence[swapChainImages!.Length];
- L29 C45: new() :: SemaphoreCreateInfo semaphoreInfo = new()
- L34 C37: new() :: FenceCreateInfo fenceInfo = new()
- L44 C23: new Exception :: throw new Exception("failed to create synchronization objects for a frame!");


## XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkFrameBuffer.cs
- L27 C33: new ImageView :: ImageView[] views = new ImageView[attachments.Length];
- L28 C59: new FrameBufferAttachmentSignature :: FrameBufferAttachmentSignature[] signatures = new FrameBufferAttachmentSignature[attachments.Length];
- L40 C57: new() :: FramebufferCreateInfo framebufferInfo = new()
- L54 C31: new Exception :: throw new Exception("Failed to create framebuffer.");
- L65 C23: new InvalidOperationException :: throw new InvalidOperationException("Framebuffer must have at least one attachment.");
- L75 C27: new InvalidOperationException :: throw new InvalidOperationException("Framebuffer attachment target cannot be null.");
- L86 C42: new AttachmentBuildInfo :: colorAttachments.Add(new AttachmentBuildInfo(source.View, signature, slot));
- L91 C27: new InvalidOperationException :: throw new InvalidOperationException($"Framebuffer '{Data.Name ?? "<unnamed>"}' defines multiple depth/stencil attachments which is not supported in Vulkan subpasses.");
- L94 C35: new AttachmentBuildInfo :: depthAttachment = new AttachmentBuildInfo(source.View, depthSignature, 0);
- L105 C23: new InvalidOperationException :: throw new InvalidOperationException($"Framebuffer '{Data.Name ?? "<unnamed>"}' does not define any attachments.");
- L117 C19: new InvalidOperationException :: throw new InvalidOperationException(
- L137 C28: new NotSupportedException :: _ => throw new NotSupportedException($"Framebuffer attachment type '{target.GetType().Name}' is not supported yet.")
- L143 C23: new InvalidOperationException :: throw new InvalidOperationException("Render buffer is not backed by a Vulkan object.");
- L146 C20: new AttachmentSource :: return new AttachmentSource(vkRenderBuffer.View, vkRenderBuffer.Format, vkRenderBuffer.Samples, vkRenderBuffer.Aspect);
- L153 C23: new InvalidOperationException :: throw new InvalidOperationException($"Texture '{texture.Name ?? texture.GetDescribingName()}' is not backed by a Vulkan texture.");
- L157 C20: new AttachmentSource :: return new AttachmentSource(view, vkTexture.ResolvedFormat, vkTexture.SampleCount, vkTexture.AspectFlags);
- L177 C27: new InvalidOperationException :: throw new InvalidOperationException($"Color attachment slot {explicitSlot} is already bound for this framebuffer.");
- L209 C20: new FrameBufferAttachmentSignature :: return new FrameBufferAttachmentSignature(


## XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkMeshRenderer.cs
- L69 C73: new() :: private readonly Dictionary<PrimitiveTopology, Pipeline> _pipelines = new();
- L173 C15: new PendingMeshDraw :: var draw = new PendingMeshDraw(this, modelMatrix, prevModelMatrix, materialOverride, instances, billboardMode);
- L243 C11: new XRMaterial :: ?? new XRMaterial();
- L251 C18: new List :: var shaders = new List<XRShader>();
- L268 C17: new XRShader :: shaders.Add(new XRShader(EShaderType.Vertex, vsSource));
- L272 C24: new XRRenderProgram :: _generatedProgram = new XRRenderProgram(linkNow: false, separable: false, shaders);
- L296 C18: new VertexInputBindingDescription :: bindings.Add(new VertexInputBindingDescription
- L308 C22: new VertexInputAttributeDescription :: attributes.Add(new VertexInputAttributeDescription
- L320 C21: new VertexInputAttributeDescription :: attributes.Add(new VertexInputAttributeDescription
- L351 C22: new PipelineVertexInputStateCreateInfo :: var vertexInput = new PipelineVertexInputStateCreateInfo
- L366 C58: new() :: PipelineInputAssemblyStateCreateInfo inputAssembly = new()
- L373 C53: new() :: PipelineViewportStateCreateInfo viewportState = new()
- L380 C55: new() :: PipelineRasterizationStateCreateInfo rasterizer = new()
- L392 C56: new() :: PipelineMultisampleStateCreateInfo multisampling = new()
- L399 C56: new() :: PipelineDepthStencilStateCreateInfo depthStencil = new()
- L409 C62: new() :: PipelineColorBlendAttachmentState colorBlendAttachment = new()
- L417 C55: new() :: PipelineColorBlendStateCreateInfo colorBlending = new()
- L437 C53: new() :: PipelineDynamicStateCreateInfo dynamicState = new()
- L444 C49: new() :: GraphicsPipelineCreateInfo pipelineInfo = new()
- L499 C32: new VkBufferHandle :: VkBufferHandle[] buffers = new VkBufferHandle[sortedBindings.Length];
- L500 C23: new ulong :: ulong[] offsets = new ulong[sortedBindings.Length];
- L643 C41: new() :: DescriptorPoolCreateInfo poolInfo = new()
- L659 C22: new DescriptorSet :: _descriptorSets = new DescriptorSet[frameCount][];
- L663 C33: new DescriptorSet :: DescriptorSet[] frameSets = new DescriptorSet[layoutArray.Length];
- L668 C44: new() :: DescriptorSetAllocateInfo allocInfo = new()
- L731 C37: new DescriptorPoolSize :: DescriptorPoolSize[] poolSizes = new DescriptorPoolSize[counts.Count];
- L734 C22: new DescriptorPoolSize :: poolSizes[i++] = new DescriptorPoolSize { Type = pair.Key, DescriptorCount = pair.Value };
- L765 C18: new WriteDescriptorSet :: writes.Add(new WriteDescriptorSet
- L782 C18: new WriteDescriptorSet :: writes.Add(new WriteDescriptorSet
- L841 C17: new DescriptorBufferInfo :: bufferInfo = new DescriptorBufferInfo
- L886 C9: new DescriptorImageInfo :: : new DescriptorImageInfo
- L895 C18: new DescriptorImageInfo :: imageInfo = new DescriptorImageInfo
- L904 C18: new DescriptorImageInfo :: imageInfo = new DescriptorImageInfo
- L913 C18: new DescriptorImageInfo :: imageInfo = new DescriptorImageInfo
- L948 C18: new DescriptorBufferInfo :: bufferInfo = new DescriptorBufferInfo
- L970 C37: new EngineUniformBuffer :: EngineUniformBuffer[] buffers = new EngineUniformBuffer[frames];
- L976 C19: new EngineUniformBuffer :: buffers[i] = new EngineUniformBuffer(buffer, memory, size);
- L988 C35: new() :: BufferCreateInfo bufferInfo = new()
- L1004 C36: new() :: MemoryAllocateInfo allocInfo = new()
- L1099 C36: new Vector2 :: return UploadUniform(buffer, new Vector2(0f, 0f));


## XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkObjectBase.cs
- L65 C27: new Exception :: throw new Exception($"Failed to generate object of type {Type}.");


## XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkRenderBuffer.cs
- L99 C36: new() :: ImageCreateInfo info = new()
- L103 C26: new Extent3D :: Extent = new Extent3D(Math.Max(Data.Width, 1u), Math.Max(Data.Height, 1u), 1),
- L117 C27: new Exception :: throw new Exception("Failed to create Vulkan render buffer image.");
- L122 C44: new() :: MemoryAllocateInfo allocInfo = new()
- L135 C23: new Exception :: throw new Exception("Failed to bind memory for render buffer image.");
- L150 C44: new() :: ImageViewCreateInfo viewInfo = new()
- L156 C36: new ImageSubresourceRange :: SubresourceRange = new ImageSubresourceRange
- L167 C23: new Exception :: throw new Exception("Failed to create render buffer image view.");


## XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkRenderProgram.cs
- L24 C72: new() :: private readonly Dictionary<XRShader, VkShader> _shaderCache = new();
- L25 C81: new() :: private readonly Dictionary<EProgramStageMask, VkShader> _stageLookup = new();
- L28 C83: new() :: private readonly List<DescriptorBindingInfo> _programDescriptorBindings = new();
- L169 C49: new() :: PipelineLayoutCreateInfo info = new() { SType = StructureType.PipelineLayoutCreateInfo };
- L171 C27: new InvalidOperationException :: throw new InvalidOperationException($"Failed to create pipeline layout for program '{Data.Name ?? "UnnamedProgram"}'.");
- L178 C49: new() :: PipelineLayoutCreateInfo info = new()
- L186 C27: new InvalidOperationException :: throw new InvalidOperationException($"Failed to create pipeline layout for program '{Data.Name ?? "UnnamedProgram"}'.");
- L234 C23: new InvalidOperationException :: throw new InvalidOperationException($"Program '{Data.Name ?? "UnnamedProgram"}' is not linkable.");
- L238 C23: new InvalidOperationException :: throw new InvalidOperationException("Graphics pipeline creation requires at least one graphics shader stage.");
- L248 C27: new InvalidOperationException :: throw new InvalidOperationException($"Failed to create graphics pipeline ({result}).");
- L257 C23: new InvalidOperationException :: throw new InvalidOperationException($"Program '{Data.Name ?? "UnnamedProgram"}' is not linkable.");
- L261 C23: new InvalidOperationException :: throw new InvalidOperationException("Compute pipeline creation requires a compute shader stage.");
- L268 C23: new InvalidOperationException :: throw new InvalidOperationException($"Failed to create compute pipeline ({result}).");
- L291 C96: new() :: Dictionary<(uint set, uint binding), DescriptorSetLayoutBindingBuilder> builders = new();
- L297 C31: new DescriptorSetLayoutBindingBuilder :: builder = new DescriptorSetLayoutBindingBuilder(binding);
- L307 C24: new DescriptorLayoutBuildResult :: return new DescriptorLayoutBuildResult(Array.Empty<DescriptorSetLayout>(), new List<DescriptorBindingInfo>());
- L307 C92: new List :: return new DescriptorLayoutBuildResult(Array.Empty<DescriptorSetLayout>(), new List<DescriptorBindingInfo>());
- L309 C49: new() :: List<DescriptorSetLayout> layouts = new();
- L319 C64: new() :: DescriptorSetLayoutCreateInfo layoutInfo = new()
- L327 C31: new InvalidOperationException :: throw new InvalidOperationException($"Failed to create descriptor set layout for program '{programName}'.");
- L339 C20: new DescriptorLayoutBuildResult :: return new DescriptorLayoutBuildResult(layouts.ToArray(), mergedBindings);
- L362 C27: new InvalidOperationException :: throw new InvalidOperationException($"Conflicting descriptor definitions detected for set {Set}, binding {Binding}.");
- L368 C20: new() :: => new()


## XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkRenderProgramPipeline.cs
- L13 C90: new() :: private readonly Dictionary<EProgramStageMask, VkRenderProgram> _stagePrograms = new();
- L16 C76: new() :: private readonly List<DescriptorBindingInfo> _descriptorBindings = new();
- L64 C23: new InvalidOperationException :: throw new InvalidOperationException($"Program '{program.Data.Name ?? "UnnamedProgram"}' is not linkable.");
- L93 C23: new InvalidOperationException :: throw new InvalidOperationException("Graphics pipeline creation requires configured shader stages.");
- L103 C27: new InvalidOperationException :: throw new InvalidOperationException($"Failed to create graphics pipeline ({result}).");
- L115 C23: new InvalidOperationException :: throw new InvalidOperationException("Compute pipeline creation requires a compute shader stage.");
- L122 C23: new InvalidOperationException :: throw new InvalidOperationException($"Failed to create compute pipeline ({result}).");
- L183 C49: new() :: PipelineLayoutCreateInfo info = new() { SType = StructureType.PipelineLayoutCreateInfo };
- L185 C27: new InvalidOperationException :: throw new InvalidOperationException("Failed to create pipeline layout for pipeline object.");
- L192 C49: new() :: PipelineLayoutCreateInfo info = new()
- L200 C27: new InvalidOperationException :: throw new InvalidOperationException("Failed to create pipeline layout for pipeline object.");


## XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VKSampler.cs
- L76 C49: new() :: SamplerCreateInfo samplerInfo = new()
- L103 C27: new Exception :: throw new Exception("Failed to create Vulkan sampler.");


## XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkShader.cs
- L16 C76: new() :: private readonly List<DescriptorBindingInfo> _descriptorBindings = new();
- L44 C53: new() :: ShaderModuleCreateInfo createInfo = new()
- L54 C31: new InvalidOperationException :: throw new InvalidOperationException($"Failed to create shader module for '{Data.Name ?? "UnnamedShader"}'.");
- L57 C42: new() :: _shaderStageCreateInfo = new()
- L83 C28: new ArgumentOutOfRangeException :: _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)


## XRENGINE/Rendering/API/Rendering/Vulkan/Objects/Types/VkTexture2D.cs
- L19 C86: new() :: private readonly Dictionary<AttachmentViewKey, ImageView> _attachmentViews = new();
- L145 C20: new TextureLayout :: return new TextureLayout(extent, layers, mips);
- L179 C41: new() :: ImageCreateInfo imageInfo = new()
- L199 C27: new Exception :: throw new Exception($"Failed to create Vulkan image for texture '{ResolveLogicalResourceName() ?? Data.Name ?? "<unnamed>"}'. Result={result}.");
- L204 C44: new() :: MemoryAllocateInfo allocInfo = new()
- L217 C23: new Exception :: throw new Exception("Failed to bind memory for texture image.");
- L229 C19: new AttachmentViewKey :: ? new AttachmentViewKey(0, ResolvedMipLevels, 0, ResolvedArrayLayers, DefaultViewType, AspectFlags)
- L237 C44: new() :: ImageViewCreateInfo viewInfo = new()
- L243 C30: new ComponentMapping :: Components = new ComponentMapping(ComponentSwizzle.Identity, ComponentSwizzle.Identity, ComponentSwizzle.Identity, ComponentSwizzle.Identity),
- L244 C36: new ImageSubresourceRange :: SubresourceRange = new ImageSubresourceRange
- L255 C23: new Exception :: throw new Exception("Failed to create image view.");
- L305 C45: new() :: SamplerCreateInfo samplerInfo = new()
- L326 C23: new Exception :: throw new Exception("Failed to create sampler.");
- L350 C20: new AttachmentViewKey :: return new AttachmentViewKey(baseMip, 1, 0, 1, ImageViewType.Type2D, AspectFlags);
- L368 C23: new ImageMemoryBarrier :: barrier = new ImageMemoryBarrier
- L376 C36: new ImageSubresourceRange :: SubresourceRange = new ImageSubresourceRange
- L411 C38: new() :: BufferImageCopy region = new()
- L416 C36: new ImageSubresourceLayers :: ImageSubresource = new ImageSubresourceLayers
- L423 C31: new Offset3D :: ImageOffset = new Offset3D(0, 0, 0),
- L431 C57: new() :: public DescriptorImageInfo CreateImageInfo() => new()
- L564 C42: new() :: ImageMemoryBarrier barrier = new()
- L570 C36: new ImageSubresourceRange :: SubresourceRange = new ImageSubresourceRange
- L624 C30: new() :: ImageBlit blit = new()
- L626 C34: new ImageSubresourceLayers :: SrcSubresource = new ImageSubresourceLayers
- L633 C34: new ImageSubresourceLayers :: DstSubresource = new ImageSubresourceLayers
- L642 C40: new Offset3D :: blit.SrcOffsets.Element0 = new Offset3D(0, 0, 0);
- L643 C40: new Offset3D :: blit.SrcOffsets.Element1 = new Offset3D(mipWidth, mipHeight, 1);
- L644 C40: new Offset3D :: blit.DstOffsets.Element0 = new Offset3D(0, 0, 0);
- L645 C40: new Offset3D :: blit.DstOffsets.Element1 = new Offset3D(dstWidth, dstHeight, 1);
- L658 C20: new TextureLayout :: return new TextureLayout(new Extent3D(width, height, 1), 1, mipLevels);
- L658 C38: new Extent3D :: return new TextureLayout(new Extent3D(width, height, 1), 1, mipLevels);
- L711 C20: new TextureLayout :: return new TextureLayout(new Extent3D(width, height, 1), layers, 1);
- L711 C38: new Extent3D :: return new TextureLayout(new Extent3D(width, height, 1), layers, 1);
- L720 C24: new AttachmentViewKey :: return new AttachmentViewKey(baseMip, 1, baseLayer, 1, ImageViewType.Type2D, AspectFlags);
- L786 C20: new TextureLayout :: return new TextureLayout(new Extent3D(width, height, depth), 1, 1);
- L786 C38: new Extent3D :: return new TextureLayout(new Extent3D(width, height, depth), 1, 1);
- L801 C20: new TextureLayout :: return new TextureLayout(new Extent3D(extent, extent, 1), 6, mipLevels);
- L801 C38: new Extent3D :: return new TextureLayout(new Extent3D(extent, extent, 1), 6, mipLevels);
- L809 C24: new AttachmentViewKey :: return new AttachmentViewKey(baseMip, 1, (uint)layerIndex, 1, ImageViewType.Type2D, AspectFlags);


## XRENGINE/Rendering/API/Rendering/Vulkan/PhysicalDevice.cs
- L17 C19: new Exception :: throw new Exception("Failed to find GPUs with Vulkan support.");
- L19 C23: new PhysicalDevice :: var devices = new PhysicalDevice[devicedCount];
- L34 C19: new Exception :: throw new Exception("Failed to find a suitable GPU for Vulkan.");
- L67 C35: new ExtensionProperties :: var availableExtensions = new ExtensionProperties[extentionsCount];
- L87 C15: new Exception :: throw new Exception("Failed to find suitable memory type.");


## XRENGINE/Rendering/API/Rendering/Vulkan/SwapChain.cs
- L68 C26: new Fence :: imagesInFlight = new Fence[swapChainImages!.Length];
- L126 C15: new Exception :: throw new Exception("failed to find supported format!");
- L146 C45: new() :: SwapchainCreateInfoKHR createInfo = new()
- L185 C19: new NotSupportedException :: throw new NotSupportedException("VK_KHR_swapchain extension not found.");
- L188 C19: new Exception :: throw new Exception("Failed to create swap chain.");
- L191 C27: new Image :: swapChainImages = new Image[imageCount];
- L258 C37: new() :: Extent2D actualExtent = new()
- L273 C23: new SwapChainSupportDetails :: var details = new SwapChainSupportDetails();
- L282 C31: new SurfaceFormatKHR :: details.Formats = new SurfaceFormatKHR[formatCount];
- L298 C36: new PresentModeKHR :: details.PresentModes = new PresentModeKHR[presentModeCount];


## XRENGINE/Rendering/API/Rendering/Vulkan/Types/QueueFamilyIndices.cs
- L20 C23: new QueueFamilyIndices :: var indices = new QueueFamilyIndices();
- L25 C29: new QueueFamilyProperties :: var queueFamilies = new QueueFamilyProperties[queueFamilityCount];


## XRENGINE/Rendering/API/Rendering/Vulkan/Types/VkDataBuffer.cs
- L244 C33: new DataSource :: GPUSideSource = new DataSource(_persistentMappedPtr, (uint)_bufferSize);
- L256 C33: new DataSource :: GPUSideSource = new DataSource(_persistentMappedPtr, length);
- L373 C23: new ArgumentNullException :: throw new ArgumentNullException(nameof(vkMemory), "Cannot map null Vulkan memory.");
- L377 C23: new Exception :: throw new Exception("Failed to map Vulkan buffer memory.");
- L385 C23: new ArgumentNullException :: throw new ArgumentNullException("Buffers cannot be null for copy operation.");
- L390 C55: new() :: CommandBufferAllocateInfo allocInfo = new()
- L402 C30: new BufferCopy :: var copyRegion = new BufferCopy
- L415 C23: new ArgumentNullException :: throw new ArgumentNullException("Buffer, memory, or address cannot be null for update operation.");
- L419 C23: new Exception :: throw new Exception("Failed to map Vulkan buffer memory.");
- L428 C23: new ArgumentNullException :: throw new ArgumentNullException(nameof(vkMemory), "Cannot unmap null Vulkan memory.");
- L443 C23: new ArgumentNullException :: throw new ArgumentNullException("Buffers cannot be null for copy operation.");
- L448 C55: new() :: CommandBufferAllocateInfo allocInfo = new()
- L460 C30: new BufferCopy :: var copyRegion = new BufferCopy
- L488 C23: new ArgumentException :: throw new ArgumentException("Buffer size must be greater than zero.", nameof(bufferSize));
- L490 C30: new BufferCreateInfo :: var bufferInfo = new BufferCreateInfo
- L499 C23: new Exception :: throw new Exception("Failed to create Vulkan staging buffer.");
- L502 C30: new MemoryAllocateInfo :: var memoryInfo = new MemoryAllocateInfo
- L510 C23: new Exception :: throw new Exception("Failed to allocate Vulkan staging memory.");
- L519 C27: new Exception :: throw new Exception("Failed to map Vulkan staging memory.");
- L533 C23: new ArgumentNullException :: throw new ArgumentNullException(nameof(vkMemory), "Cannot flush null Vulkan memory.");
- L535 C21: new MappedMemoryRange :: var v = new MappedMemoryRange
- L544 C23: new Exception :: throw new Exception("Failed to flush Vulkan buffer memory.");


## XRENGINE/Rendering/API/Rendering/Vulkan/Validation.cs
- L44 C55: new() :: DebugUtilsMessengerCreateInfoEXT createInfo = new();
- L48 C19: new Exception :: throw new Exception("failed to set up debug messenger!");
- L54 C31: new LayerProperties :: var availableLayers = new LayerProperties[layerCount];


## XRENGINE/Rendering/API/Rendering/Vulkan/VulkanBarrierPlanner.cs
- L56 C46: new PlannedImageBarrier :: plannedBarrier = new PlannedImageBarrier(pass.PassIndex, logicalResource, group, previousState, desiredState);
- L60 C42: new PlannedImageBarrier :: plannedBarrier = new PlannedImageBarrier(


## XRENGINE/Rendering/API/Rendering/Vulkan/VulkanRenderer.ImGui.cs
- L14 C58: new() :: private readonly ImGuiDrawDataCache _imguiDrawData = new();
- L42 C30: new Vector2 :: io.DisplaySize = new Vector2(width, height);
- L66 C30: new VulkanImGuiBackend :: => _imguiBackend ??= new VulkanImGuiBackend(this);
- L98 C28: new ImDrawDataPtr :: => _drawData = new ImDrawDataPtr(null);


## XRENGINE/Rendering/API/Rendering/Vulkan/VulkanRenderer.State.cs
- L14 C50: new() :: private readonly VulkanStateTracker _state = new();
- L15 C63: new() :: private readonly VulkanResourcePlanner _resourcePlanner = new();
- L16 C67: new() :: private readonly VulkanResourceAllocator _resourceAllocator = new();
- L17 C61: new() :: private readonly VulkanBarrierPlanner _barrierPlanner = new();
- L34 C26: new ColorF4 :: ClearColor = new ColorF4(0, 0, 0, 1);
- L84 C25: new Viewport :: _viewport = new Viewport
- L101 C24: new Rect2D :: _scissor = new Rect2D
- L103 C26: new Offset2D :: Offset = new Offset2D(region.X, region.Y),
- L104 C26: new Extent2D :: Extent = new Extent2D((uint)Math.Max(region.Width, 0), (uint)Math.Max(region.Height, 0))
- L116 C16: new() :: => new()
- L127 C16: new() :: => new()
- L129 C26: new Offset2D :: Offset = new Offset2D(0, 0),
- L130 C26: new Extent2D :: Extent = new Extent2D(extent.Width, extent.Height)
- L179 C30: new ClearValue :: destination[0] = new ClearValue
- L181 C25: new ClearColorValue :: Color = new ClearColorValue
- L192 C34: new ClearValue :: destination[1] = new ClearValue
- L194 C36: new ClearDepthStencilValue :: DepthStencil = new ClearDepthStencilValue
- L226 C37: new() :: ImageCreateInfo imageInfo = new()
- L246 C23: new Exception :: throw new Exception($"Failed to create Vulkan image for resource group '{group.Key}'. Result={result}.");
- L251 C40: new() :: MemoryAllocateInfo allocInfo = new()
- L265 C19: new Exception :: throw new Exception($"Failed to bind device memory for Vulkan image group '{group.Key}'. Result={bindResult}.");


## XRENGINE/Rendering/API/Rendering/Vulkan/VulkanResourceAllocator.cs
- L15 C92: new() :: private readonly Dictionary<VulkanAliasGroupKey, VulkanImageAliasGroup> _aliasGroups = new();
- L16 C98: new() :: private readonly Dictionary<VulkanAliasGroupKey, VulkanPhysicalImageGroup> _physicalGroups = new();
- L42 C25: new VulkanImageAliasGroup :: group = new VulkanImageAliasGroup(key);
- L155 C16: new Extent3D :: return new Extent3D(width, height, 1);
- L201 C65: new() :: private readonly List<VulkanImageAllocation> _allocations = new();
- L251 C70: new() :: private readonly List<VulkanImageAllocation> _logicalResources = new();


## XRENGINE/Rendering/API/Rendering/Vulkan/VulkanResourcePlanner.cs
- L53 C52: new() :: List<VulkanAllocationRequest> persistent = new();
- L54 C51: new() :: List<VulkanAllocationRequest> transient = new();
- L55 C50: new() :: List<VulkanAllocationRequest> external = new();
- L59 C27: new VulkanAllocationRequest :: var request = new VulkanAllocationRequest(descriptor);
- L74 C24: new Dictionary :: var fboPlans = new Dictionary<string, VulkanFrameBufferPlan>(_frameBuffers.Count, StringComparer.OrdinalIgnoreCase);
- L76 C30: new VulkanFrameBufferPlan :: fboPlans[name] = new VulkanFrameBufferPlan(descriptor);
- L78 C17: new VulkanResourcePlan :: _plan = new VulkanResourcePlan(
- L112 C9: new Dictionary :: new Dictionary<string, VulkanFrameBufferPlan>(StringComparer.OrdinalIgnoreCase));


## XRENGINE/Rendering/API/Rendering/Vulkan/VulkanShaderTools.cs
- L31 C19: new InvalidOperationException :: throw new InvalidOperationException($"Shader '{shader.Name ?? "UnnamedShader"}' does not contain GLSL source code.");
- L35 C19: new InvalidOperationException :: throw new InvalidOperationException("Failed to initialize the shaderc compiler instance.");
- L41 C19: new InvalidOperationException :: throw new InvalidOperationException("Failed to allocate shaderc compile options.");
- L69 C23: new InvalidOperationException :: throw new InvalidOperationException($"Shader '{shader.Name ?? "UnnamedShader"}' failed to compile due to an unknown error.");
- L75 C23: new InvalidOperationException :: throw new InvalidOperationException($"Shader '{shader.Name ?? "UnnamedShader"}' failed to compile: {message}");
- L80 C23: new InvalidOperationException :: throw new InvalidOperationException($"Shader '{shader.Name ?? "UnnamedShader"}' produced an empty SPIR-V module.");
- L82 C28: new byte :: byte[] spirv = new byte[(int)length];
- L119 C24: new ArgumentOutOfRangeException :: _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
- L156 C48: new() :: List<DescriptorBindingInfo> bindings = new();
- L178 C26: new DescriptorBindingInfo :: bindings.Add(new DescriptorBindingInfo(set, binding, descriptorType, stage, arraySize == 0 ? 1u : arraySize, name));
- L256 C43: new[] :: string[] tokens = sanitized.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
- L264 C63: new() :: private readonly Dictionary<uint, SpirvType> _types = new();
- L265 C71: new() :: private readonly Dictionary<uint, SpirvVariable> _variables = new();
- L266 C76: new() :: private readonly Dictionary<uint, SpirvDecorations> _decorations = new();
- L267 C60: new() :: private readonly Dictionary<uint, string> _names = new();
- L268 C63: new() :: private readonly Dictionary<uint, ulong> _constants = new();
- L275 C23: new InvalidOperationException :: throw new InvalidOperationException("SPIR-V bytecode length must be divisible by 4.");
- L284 C52: new() :: List<DescriptorBindingInfo> bindings = new();
- L302 C30: new DescriptorBindingInfo :: bindings.Add(new DescriptorBindingInfo(set, binding, descriptorType, _stage, descriptorCount == 0 ? 1u : descriptorCount, name));
- L311 C23: new InvalidOperationException :: throw new InvalidOperationException("SPIR-V module header is incomplete.");
- L321 C27: new InvalidOperationException :: throw new InvalidOperationException($"Invalid SPIR-V word count for opcode {opCode}.");
- L324 C27: new InvalidOperationException :: throw new InvalidOperationException("SPIR-V instruction extends beyond buffer.");
- L430 C36: new SpirvVariable :: _variables[resultId] = new SpirvVariable(resultId, resultTypeId, storageClass);
- L504 C29: new SpirvImageInfo :: ImageType = new SpirvImageInfo
- L541 C32: new SpirvType :: _types[resultId] = new SpirvType(resultId) { Kind = SpirvTypeKind.Sampler };
- L630 C25: new SpirvDecorations :: decor = new SpirvDecorations();


## XRENGINE/Rendering/API/XRWindow.cs
- L54 C29: new Engine.StateChangeInfo :: new Engine.StateChangeInfo(
- L87 C20: new WorldHierarchy :: return new WorldHierarchy
- L99 C20: new NodeRepresentation :: return new NodeRepresentation
- L285 C37: new XRFrameBuffer :: _viewportPanelFBO = new XRFrameBuffer((_viewportPanelTexture, EFrameBufferAttachment.ColorAttachment0, 0, -1))
- L580 C38: new OpenGLRenderer :: ContextAPI.OpenGL => new OpenGLRenderer(this, true),
- L581 C38: new VulkanRenderer :: ContextAPI.Vulkan => new VulkanRenderer(this, true),
- L582 C28: new Exception :: _ => throw new Exception($"Unsupported API: {Window.API.API}"),


## XRENGINE/Rendering/Camera/ColorGradingSettings.cs
- L500 C17: new Vector3 :: w = new Vector3(Sanitize(w.X), Sanitize(w.Y), Sanitize(w.Z));


## XRENGINE/Rendering/Camera/LensDistortionSettings.cs
- L153 C54: new Vector2 :: program.Uniform("PaniniViewExtents", new Vector2(1.0f, 1.0f));
- L159 C55: new Vector3 :: program.Uniform("BrownConradyRadial", new Vector3(_brownConradyK1, _brownConradyK2, _brownConradyK3));
- L160 C59: new Vector2 :: program.Uniform("BrownConradyTangential", new Vector2(_brownConradyP1, _brownConradyP2));
- L176 C20: new Vector2 :: return new Vector2(viewExtX, viewExtY);


## XRENGINE/Rendering/Camera/VignetteSettings.cs
- L13 C46: new ColorF3 :: public ColorF3 Color { get; set; } = new ColorF3();


## XRENGINE/Rendering/Camera/XRCamera.cs
- L70 C20: new Vector2 :: return new Vector2(
- L111 C71: new() :: private CameraPostProcessStateCollection _postProcessStates = new();
- L116 C66: new() :: private readonly Stack<Vector2> _projectionJitterStack = new();
- L162 C65: new Transform :: get => _transform ?? SetFieldReturn(ref _transform, new Transform())!;
- L193 C62: new CameraPostProcessStateCollection :: set => SetField(ref _postProcessStates, value ?? new CameraPostProcessStateCollection());
- L528 C27: new Vector2 :: screenPoint = new Vector2(xyd.X, xyd.Y);
- L549 C145: new Vector3 :: Vector3 clip01 = (Vector3.Transform(Vector3.Transform(worldPoint, Transform.InverseWorldMatrix), ProjectionMatrix) + Vector3.One) * new Vector3(0.5f);
- L554 C26: new Vector3 :: clip01 = new Vector3(ApplyLensDistortionInverse(clip01.XY(), lens), clip01.Z);
- L568 C52: new Vector3 :: => NormalizedViewportToWorldCoordinate(new Vector3(normalizedX, normalizedY, depth));
- L628 C24: new LensParams :: return new LensParams(ELensDistortionMode.None, 0.0f, 0.0f, 1.0f, Vector2.One);
- L632 C24: new LensParams :: return new LensParams(ELensDistortionMode.None, 0.0f, 0.0f, 1.0f, Vector2.One);
- L636 C24: new LensParams :: return new LensParams(ELensDistortionMode.None, 0.0f, 0.0f, 1.0f, Vector2.One);
- L663 C20: new LensParams :: return new LensParams(mode, intensity, paniniDistance, paniniCrop, paniniViewExtents);
- L691 C39: new Vector2 :: Vector2 centered = uv01 - new Vector2(0.5f, 0.5f);
- L699 C32: new Vector2 :: return distorted + new Vector2(0.5f, 0.5f);
- L707 C39: new Vector2 :: Vector2 centered = uv01 - new Vector2(0.5f, 0.5f);
- L710 C24: new Vector2 :: return new Vector2(0.5f, 0.5f);
- L724 C30: new Vector2 :: return dir * r + new Vector2(0.5f, 0.5f);
- L735 C37: new Vector2 :: return projNdc * 0.5f + new Vector2(0.5f, 0.5f);
- L757 C59: new Vector2 :: Vector2 uvDx = ApplyPaniniFromView(view + new Vector2(delta, 0.0f), lens);
- L758 C59: new Vector2 :: Vector2 uvDy = ApplyPaniniFromView(view + new Vector2(0.0f, delta), lens);
- L775 C98: new Vector2 :: Vector2 uvUndistorted = (view / (lens.PaniniViewExtents * lens.PaniniCrop)) * 0.5f + new Vector2(0.5f, 0.5f);
- L783 C37: new Vector2 :: return projNdc * 0.5f + new Vector2(0.5f, 0.5f);
- L808 C20: new Segment :: return new Segment(start, end);
- L814 C20: new Ray :: return new Ray(start, end - start);
- L955 C44: new Plane :: => _obliqueNearClippingPlane = new Plane(planeNormalWorld, planeDistance);


## XRENGINE/Rendering/Camera/XRCameraParameters.cs
- L85 C52: new() :: private object _untransformedFrustumLock = new();
- L104 C69: new Vector2 :: program.Uniform(EEngineUniform.ScreenOrigin.ToString(), new Vector2(0.0f, 0.0f));


## XRENGINE/Rendering/Camera/XROpenXRFovCameraParameters.cs
- L78 C20: new Frustum :: return new Frustum(invProj);
- L85 C20: new Vector2 :: return new Vector2(w, h);


## XRENGINE/Rendering/Camera/XROrthographicCameraParameters.cs
- L84 C23: new Vector2 :: _origin = new Vector2(_orthoLeft, _orthoBottom) + _originPercentages * new Vector2(Width, Height);
- L84 C84: new Vector2 :: _origin = new Vector2(_orthoLeft, _orthoBottom) + _originPercentages * new Vector2(Width, Height);


## XRENGINE/Rendering/Camera/XROVRCameraParameters.cs
- L29 C52: new Vector3 :: Vector3 bottomLeft = Vector3.Transform(new Vector3(-1, -1, normDist), invProj);
- L30 C53: new Vector3 :: Vector3 bottomRight = Vector3.Transform(new Vector3(1, -1, normDist), invProj);
- L31 C49: new Vector3 :: Vector3 topLeft = Vector3.Transform(new Vector3(-1, 1, normDist), invProj);
- L33 C20: new Vector2 :: return new Vector2((bottomRight - bottomLeft).Length(), (topLeft - bottomLeft).Length());


## XRENGINE/Rendering/Camera/XRPerspectiveCameraParameters.cs
- L98 C20: new Vector2 :: return new Vector2(width, height);


## XRENGINE/Rendering/Camera/XRPhysicalCameraParameters.cs
- L226 C20: new Frustum :: return new Frustum(invProj);
- L267 C20: new Vector2 :: return new Vector2(width, height);


## XRENGINE/Rendering/Commands/GPUIndirectRenderCommand.cs
- L49 C33: new Vector4 :: => BoundingSphere = new Vector4(center, radius);


## XRENGINE/Rendering/Commands/GPURenderPassCollection.Core.cs
- L115 C72: new() :: private static readonly IndirectDebugSettings _indirectDebug = new();
- L215 C39: new() :: private readonly Lock _lock = new();
- L333 C66: new() :: private readonly HybridRenderingManager _renderManager = new() { UseMeshletPipeline = false };


## XRENGINE/Rendering/Commands/GPURenderPassCollection.CullingAndSoA.cs
- L63 C23: new Exception :: throw new Exception("ReadUints failed - null pointer");
- L109 C23: new Exception :: throw new Exception("WriteUints failed - null pointer");
- L150 C27: new Exception :: throw new Exception("ReadUIntAt failed - null pointer");
- L189 C23: new Exception :: throw new Exception("WriteUIntAt failed - null pointer");
- L224 C27: new Exception :: throw new Exception("ReadUInt failed - null pointer");
- L259 C27: new Exception :: throw new Exception("WriteUInt failed - null pointer");
- L320 C42: new XRDataBuffer :: _passFilterDebugBuffer = new XRDataBuffer("PassFilterDebug", EBufferTarget.ShaderStorageBuffer, requiredElements, EComponentType.UInt, 1, false, true)
- L370 C26: new StringBuilder :: var sb = new StringBuilder();
- L785 C26: new StringBuilder :: var sb = new StringBuilder();
- L811 C26: new StringBuilder :: var sb = new StringBuilder();
- L883 C31: new SoftIssueInfo :: map[reason] = new SoftIssueInfo
- L916 C35: new List :: var invalidCommands = new List<(uint index, GPUIndirectRenderCommand command, string reason)>();
- L917 C30: new Dictionary :: var softIssues = new Dictionary<string, SoftIssueInfo>(StringComparer.OrdinalIgnoreCase);
- L918 C38: new HashSet :: var missingMaterialIds = new HashSet<uint>();
- L1034 C30: new List :: var signatures = new List<(uint MeshId, uint MaterialId, uint Pass)>(Math.Min((int)copyCount, ValidationSignatureLogLimit));
- L1052 C30: new List :: var signatures = new List<(uint MeshId, uint MaterialId, uint Pass)>(Math.Min((int)gpuVisibleCount, ValidationSignatureLogLimit));
- L1078 C26: new HashSet :: var cpuSet = new HashSet<(uint MeshId, uint MaterialId, uint Pass)>(cpu);
- L1079 C26: new HashSet :: var gpuSet = new HashSet<(uint MeshId, uint MaterialId, uint Pass)>(gpu);
- L1088 C26: new StringBuilder :: var sb = new StringBuilder();
- L1096 C26: new StringBuilder :: var sb = new StringBuilder();
- L1142 C22: new StringBuilder :: var sb = new StringBuilder();
- L1147 C36: new Dictionary :: var reasonCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
- L1177 C22: new StringBuilder :: var sb = new StringBuilder();
- L1189 C25: new List :: var parts = new List<string>(softIssues.Count);
- L1250 C22: new StringBuilder :: var sb = new StringBuilder();


## XRENGINE/Rendering/Commands/GPURenderPassCollection.IndirectAndMaterials.cs
- L59 C94: new HybridRenderingManager.DrawBatch :: List<HybridRenderingManager.DrawBatch> batches = BuildMaterialBatches(scene) ?? [new HybridRenderingManager.DrawBatch(0, VisibleCommandCount, 0)];
- L246 C27: new StringBuilder :: var message = new StringBuilder()
- L391 C33: new HybridRenderingManager.DrawBatch :: batches.Add(new HybridRenderingManager.DrawBatch(batchStart, batchCount, currentMaterial));
- L399 C29: new HybridRenderingManager.DrawBatch :: batches.Add(new HybridRenderingManager.DrawBatch(batchStart, batchCount, currentMaterial));
- L497 C22: new StringBuilder :: var sb = new StringBuilder();


## XRENGINE/Rendering/Commands/GPURenderPassCollection.ShadersAndInit.cs
- L133 C37: new XRRenderProgram :: _cullingComputeShader = new XRRenderProgram(true, false, ShaderHelper.LoadEngineShader("Compute/GPURenderCulling.comp", EShaderType.Compute));
- L136 C41: new XRRenderProgram :: _indirectRenderTaskShader = new XRRenderProgram(true, false, ShaderHelper.LoadEngineShader("Compute/GPURenderIndirect.comp", EShaderType.Compute));
- L137 C43: new XRRenderProgram :: _resetCountersComputeShader = new XRRenderProgram(true, false, ShaderHelper.LoadEngineShader("Compute/GPURenderResetCounters.comp", EShaderType.Compute));
- L138 C40: new XRRenderProgram :: _extractSoAComputeShader = new XRRenderProgram(true, false, ShaderHelper.LoadEngineShader("Compute/GPURenderExtractSoA.comp", EShaderType.Compute));
- L139 C40: new XRRenderProgram :: _soACullingComputeShader = new XRRenderProgram(true, false, ShaderHelper.LoadEngineShader("Compute/GPURenderCullingSoA.comp", EShaderType.Compute));
- L142 C36: new XRRenderProgram :: _copyCommandsProgram = new XRRenderProgram(true, false, ShaderHelper.LoadEngineShader("Compute/GPURenderCopyCommands.comp", EShaderType.Compute));
- L198 C59: new[] :: => scene.IndirectFaceIndices?.SelectMany(x => new[] { x.Point0, x.Point1, x.Point2 }).ToArray();
- L208 C23: new XRDataBuffer :: var buf = new XRDataBuffer(EBufferTarget.ElementArrayBuffer, true)
- L236 C33: new XRMeshRenderer :: _indirectRenderer = new XRMeshRenderer();
- L309 C40: new XRDataBuffer :: _sortedCommandBuffer = new XRDataBuffer("SortedCommands_Pass", EBufferTarget.ShaderStorageBuffer, capacity, EComponentType.Float, 32, false, false)
- L371 C39: new XRDataBuffer :: _indirectDrawBuffer = new XRDataBuffer("IndirectDraw_Pass", EBufferTarget.DrawIndirectBuffer, capacity, EComponentType.UInt, _indirectCommandComponentCount, false, true)
- L407 C26: new XRDataBuffer :: buffer = new XRDataBuffer(name, EBufferTarget.ParameterBuffer, elementCount, EComponentType.UInt, componentCount, false, true)
- L457 C26: new XRDataBuffer :: buffer = new XRDataBuffer(name, EBufferTarget.ShaderStorageBuffer, 1, EComponentType.UInt, 1, false, true)
- L507 C32: new XRDataBuffer :: _statsBuffer = new XRDataBuffer("RenderStats", EBufferTarget.ShaderStorageBuffer, requiredElements, EComponentType.UInt, 1, false, true)
- L517 C41: new uint :: _statsBuffer.SetDataRaw(new uint[requiredElements], (int)requiredElements);
- L630 C40: new XRDataBuffer :: _soaBoundingSpheresA = new XRDataBuffer("SoA_Spheres_A", EBufferTarget.ShaderStorageBuffer, capacity, EComponentType.Float, sphereStride, false, false)
- L640 C33: new XRDataBuffer :: _soaMetadataA = new XRDataBuffer("SoA_Metadata_A", EBufferTarget.ShaderStorageBuffer, capacity, EComponentType.UInt, metaStride, false, false)
- L650 C40: new XRDataBuffer :: _soaBoundingSpheresB = new XRDataBuffer("SoA_Spheres_B", EBufferTarget.ShaderStorageBuffer, capacity, EComponentType.Float, sphereStride, false, false)
- L660 C33: new XRDataBuffer :: _soaMetadataB = new XRDataBuffer("SoA_Metadata_B", EBufferTarget.ShaderStorageBuffer, capacity, EComponentType.UInt, metaStride, false, false)
- L676 C29: new XRDataBuffer :: _soaIndexList = new XRDataBuffer("SoA_IndexList", EBufferTarget.ShaderStorageBuffer, capacity + 1, EComponentType.UInt, 1, false, true)
- L691 C38: new XRDataBuffer :: _materialIDsBuffer = new XRDataBuffer("MaterialIDs", EBufferTarget.ShaderStorageBuffer, capacity, EComponentType.UInt, 1, false, true)


## XRENGINE/Rendering/Commands/GPUScene.cs
- L60 C82: new() :: private readonly ConcurrentDictionary<XRMaterial, uint> _materialIDMap = new();
- L61 C81: new() :: private readonly ConcurrentDictionary<uint, XRMaterial> _idToMaterial = new();
- L62 C73: new() :: private readonly ConcurrentDictionary<uint, XRMesh> _idToMesh = new();
- L131 C33: new XRDataBuffer :: _atlasPositions ??= new XRDataBuffer(ECommonBufferType.Position.ToString(), EBufferTarget.ArrayBuffer, 0, EComponentType.Float, 3, false, false)
- L138 C31: new XRDataBuffer :: _atlasNormals ??= new XRDataBuffer(ECommonBufferType.Normal.ToString(), EBufferTarget.ArrayBuffer, 0, EComponentType.Float, 3, false, false)
- L145 C32: new XRDataBuffer :: _atlasTangents ??= new XRDataBuffer(ECommonBufferType.Tangent.ToString(), EBufferTarget.ArrayBuffer, 0, EComponentType.Float, 4, false, false)
- L152 C27: new XRDataBuffer :: _atlasUV0 ??= new XRDataBuffer($"{ECommonBufferType.TexCoord}0", EBufferTarget.ArrayBuffer, 0, EComponentType.Float, 2, false, false)
- L159 C31: new XRDataBuffer :: _atlasIndices ??= new XRDataBuffer("MeshAtlas_Indices", EBufferTarget.ElementArrayBuffer, 0, EComponentType.UInt, 1, false, true)
- L193 C35: new Vector3 :: Vector3[] positions = new Vector3[vertexCount];
- L194 C33: new Vector3 :: Vector3[] normals = new Vector3[vertexCount];
- L195 C34: new Vector4 :: Vector4[] tangents = new Vector4[vertexCount];
- L196 C29: new Vector2 :: Vector2[] uv0 = new Vector2[vertexCount];
- L202 C31: new Vector4 :: tangents[v] = new Vector4(tan, 1.0f);
- L213 C46: new IndexTriangle :: _indirectFaceIndices.Add(new IndexTriangle(
- L234 C46: new IndexTriangle :: _indirectFaceIndices.Add(new IndexTriangle(
- L412 C39: new() :: MeshDataEntry entry = new()
- L446 C23: new AABB :: _bounds = new AABB();
- L455 C26: new XRDataBuffer :: var buffer = new XRDataBuffer(
- L475 C26: new XRDataBuffer :: var buffer = new XRDataBuffer(
- L501 C74: new() :: private readonly ConcurrentDictionary<XRMesh, uint> _meshIDMap = new();
- L503 C39: new() :: private readonly Lock _lock = new();
- L504 C82: new() :: private readonly ConcurrentDictionary<XRMesh, string> _meshDebugLabels = new();
- L505 C90: new() :: private readonly ConcurrentDictionary<XRMesh, string> _unsupportedMeshMessages = new();
- L529 C56: new() :: private readonly MeshletCollection _meshlets = new();
- L604 C35: new List :: indices = new List<uint>(subMeshes.Length);
- L837 C35: new() :: MeshDataEntry entry = new()
- L1050 C30: new GPUIndirectRenderCommand :: var gpuCommand = new GPUIndirectRenderCommand


## XRENGINE/Rendering/Commands/RenderCommandCollection.cs
- L30 C134: new SortedSet :: _updatingPasses = passIndicesAndSorters.ToDictionary(x => x.Key, x => x.Value is null ? [] : (ICollection<RenderCommand>)new SortedSet<RenderCommand>(x.Value));
- L34 C77: new Dictionary :: _passMetadata = passMetadata?.ToDictionary(m => m.PassIndex) ?? new Dictionary<int, RenderPassMetadata>();
- L38 C31: new GPURenderPassCollection :: var gpuPass = new GPURenderPassCollection(pass.Key);
- L43 C47: new RenderPassMetadata :: _passMetadata[pass.Key] = new RenderPassMetadata(pass.Key, $"Pass{pass.Key}", RenderGraphPassStage.Graphics);
- L66 C39: new() :: private readonly Lock _lock = new();


## XRENGINE/Rendering/Compute/BvhGpuProfiler.cs
- L14 C58: new() :: public static BvhGpuProfiler Instance { get; } = new();
- L135 C41: new() :: private readonly object _lock = new();
- L136 C60: new() :: private readonly Queue<XRRenderQuery> _queryPool = new();
- L199 C20: new TimingHandle :: return new TimingHandle(stage, workCount, start, gl);
- L212 C30: new PendingQuery :: _pending.Add(new PendingQuery(handle.Stage, handle.WorkCount, handle.StartQuery, end));
- L237 C71: new XRRenderQuery :: query = _queryPool.Count > 0 ? _queryPool.Dequeue() : new XRRenderQuery();
- L243 C26: new InvalidOperationException :: ?? throw new InvalidOperationException("Failed to acquire GLRenderQuery wrapper.");


## XRENGINE/Rendering/Compute/BvhRaycastDispatcher.cs
- L26 C76: new() :: private readonly ConcurrentQueue<BvhRaycastRequest> _pendingRequests = new();
- L27 C76: new() :: private readonly ConcurrentQueue<BvhRaycastResult> _completedResults = new();
- L28 C68: new() :: private readonly ConcurrentQueue<Action> _completedCallbacks = new();
- L120 C26: new BvhRaycastResult :: var result = new BvhRaycastResult(entry.Request, hits, raw);
- L236 C23: new InFlightRaycast :: _inFlight.Add(new InFlightRaycast(request, fence, clampedBytes));
- L243 C60: new XRRenderProgram :: BvhRaycastVariant.AnyHit => _anyHitProgram ??= new XRRenderProgram(true, false, _anyHitShader ??= ShaderHelper.LoadEngineShader("Compute/bvh_anyhit.comp", EShaderType.Compute)),
- L244 C68: new XRRenderProgram :: BvhRaycastVariant.ClosestHit => _closestHitProgram ??= new XRRenderProgram(true, false, _closestHitShader ??= ShaderHelper.LoadEngineShader("Compute/bvh_closesthit.comp", EShaderType.Compute)),
- L245 C38: new XRRenderProgram :: _ => _raycastProgram ??= new XRRenderProgram(true, false, _raycastShader ??= ShaderHelper.LoadEngineShader("Compute/bvh_raycast.comp", EShaderType.Compute))
- L274 C23: new byte :: byte[] data = new byte[byteLength];


## XRENGINE/Rendering/Compute/MeshSDFExample.cs
- L20 C38: new MeshSDFGenerator :: using var sdfGenerator = new MeshSDFGenerator();
- L27 C30: new IVector3 :: var resolution = new IVector3(128, 128, 128);
- L54 C38: new MeshSDFGenerator :: using var sdfGenerator = new MeshSDFGenerator();
- L60 C30: new IVector3 :: var resolution = new IVector3(256, 256, 256);
- L81 C38: new MeshSDFGenerator :: using var sdfGenerator = new MeshSDFGenerator();
- L87 C30: new IVector3 :: var resolution = new IVector3(512, 512, 512);
- L108 C31: new XRTexture3D :: var sdfTextures = new XRTexture3D[3];
- L111 C38: new MeshSDFGenerator :: using var sdfGenerator = new MeshSDFGenerator();
- L117 C31: new[] :: var resolutions = new[]
- L119 C17: new IVector3 :: new IVector3(64, 64, 64),   // Low resolution for distant objects
- L120 C17: new IVector3 :: new IVector3(128, 128, 128), // Medium resolution for normal distance
- L121 C17: new IVector3 :: new IVector3(256, 256, 256)  // High resolution for close objects
- L149 C32: new MeshSDFGenerator :: var sdfGenerator = new MeshSDFGenerator();


## XRENGINE/Rendering/Compute/MeshSDFGenerator.cs
- L105 C35: new XRRenderProgram :: _computeProgram = new XRRenderProgram(true, false, _meshSDFShader);
- L229 C30: new AABB :: var bounds = new AABB(minBounds, maxBounds);
- L252 C33: new Vector3 :: var paddingVector = new Vector3(padding);
- L253 C20: new AABB :: return new AABB(
- L265 C27: new XRTexture3D :: _sdfTexture = new XRTexture3D((uint)resolution.X, (uint)resolution.Y, (uint)resolution.Z) { Resizable = false };
- L274 C96: new InvalidOperationException :: var positionsBuffer = mesh.Buffers[ECommonBufferType.Position.ToString()] ?? throw new InvalidOperationException("Mesh does not have position buffer");
- L283 C128: new InvalidOperationException :: var indexBuffer = mesh.GetIndexBuffer(EPrimitiveType.Triangles, out _, EBufferTarget.ShaderStorageBuffer) ?? throw new InvalidOperationException("Mesh does not have index buffer");
- L311 C32: new List :: var spatialNodes = new List<Vector4>();
- L312 C34: new List :: var triangleToNode = new List<uint>();
- L330 C34: new Vector4 :: spatialNodes.Add(new Vector4(center, radius));
- L336 C35: new XRDataBuffer :: _spatialNodesBuffer = new XRDataBuffer("SpatialNodes", EBufferTarget.ShaderStorageBuffer, (uint)spatialNodes.Count, EComponentType.Float, 4, false, false);
- L342 C37: new XRDataBuffer :: _triangleToNodeBuffer = new XRDataBuffer("TriangleToNode", EBufferTarget.ShaderStorageBuffer, (uint)triangleToNode.Count, EComponentType.UInt, 1, false, false);
- L354 C23: new InvalidOperationException :: throw new InvalidOperationException("Compute program or SDF texture not initialized");


## XRENGINE/Rendering/Compute/OctreeGPU.cs
- L85 C39: new() :: private readonly object _syncRoot = new();
- L86 C37: new() :: private readonly List<T> _items = new();
- L89 C46: new() :: private readonly List<Vector4> _aabbMins = new();
- L90 C46: new() :: private readonly List<Vector4> _aabbMaxs = new();
- L91 C56: new() :: private readonly List<Matrix4x4> _transformScratch = new();
- L92 C50: new() :: private readonly List<uint> _objectIdScratch = new();
- L351 C13: new NotSupportedException :: => throw new NotSupportedException("OctreeGPU is GPU-only and does not expose CPU enumeration.");
- L354 C13: new NotSupportedException :: => throw new NotSupportedException("OctreeGPU is GPU-only and does not expose CPU enumeration.");
- L357 C13: new NotSupportedException :: => throw new NotSupportedException("OctreeGPU does not support CPU culling queries.");
- L360 C13: new NotSupportedException :: => throw new NotSupportedException("OctreeGPU does not support CPU culling queries.");
- L363 C13: new NotSupportedException :: => throw new NotSupportedException("OctreeGPU does not expose CPU-side nodes.");
- L366 C13: new NotSupportedException :: => throw new NotSupportedException("OctreeGPU does not support CPU debug rendering.");
- L515 C16: new Dictionary :: newStates = new Dictionary<T, ItemState>(ReferenceEqualityComparer<T>.Instance);
- L555 C19: new Vector4 :: _aabbMins.Add(new Vector4(bounds.Min, 1f));
- L556 C19: new Vector4 :: _aabbMaxs.Add(new Vector4(bounds.Max, 1f));
- L582 C23: new ItemState :: newStates[item] = new ItemState(objectIndex, bounds, item.CullingOffsetMatrix, shouldRender);
- L628 C25: new Vector4 :: Vector4[] combined = new Vector4[total];
- L648 C33: new[] :: _transformBuffer.SetDataRaw(new[] { IdentityMatrix }, 1);
- L663 C32: new uint :: _objectIdBuffer.SetDataRaw(new uint[] { 0u }, 1);
- L678 C27: new[] :: _aabbBuffer.SetDataRaw(new[] { ZeroVec4, ZeroVec4 }, 2);
- L683 C32: new[] :: _transformBuffer.SetDataRaw(new[] { IdentityMatrix }, 1);
- L688 C31: new uint :: _objectIdBuffer.SetDataRaw(new uint[] { 0u }, 1);
- L738 C17: new Vector3 :: sceneMin -= new Vector3(0.5f);
- L739 C17: new Vector3 :: sceneMax += new Vector3(0.5f);
- L1073 C30: new uint :: _mortonBuffer.SetDataRaw(new uint[required], (int)required);
- L1123 C38: new uint :: _bvhCounterBuffer.SetDataRaw(new uint[Math.Max(nodeCount, 1u)], (int)Math.Max(nodeCount, 1u));
- L1143 C28: new uint :: _nodeBuffer.SetDataRaw(new uint[required == 0 ? HeaderScalarCount : required], (int)(required == 0 ? HeaderScalarCount : required));
- L1159 C29: new uint :: _queueBuffer.SetDataRaw(new uint[length], (int)length);
- L1182 C11: new XRRenderProgram :: return new XRRenderProgram(true, false, shader);
- L1201 C11: new XRShader :: return new XRShader(EShaderType.Compute, cloneText);
- L1252 C35: new uint :: _overflowFlagBuffer.SetDataRaw(new uint[] { 0u }, 1);
- L1262 C36: new uint :: _overflowFlagBuffer.SetDataRaw(new uint[] { 0u }, 1);
- L1283 C27: new() :: List<string> reasons = new();
- L1508 C70: new() :: public static ReferenceEqualityComparer<TRef> Instance { get; } = new();


## XRENGINE/Rendering/Compute/SkinnedMeshBoundsCalculator.cs
- L18 C85: new SkinnedMeshBoundsCalculator :: private static readonly Lazy<SkinnedMeshBoundsCalculator> _instance = new(() => new SkinnedMeshBoundsCalculator());
- L22 C41: new() :: private readonly object _syncRoot = new();
- L121 C22: new Result :: result = new Result(worldPositions, bounds);
- L132 C20: new XRRenderProgram :: _program = new XRRenderProgram(true, false, _shader);
- L168 C16: new AABB :: return new AABB(min, max);
- L223 C81: new Vector4 :: private static readonly UInt4 PositiveInfinityPacked = UInt4.FromVector(new Vector4(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity, 1f));
- L224 C81: new Vector4 :: private static readonly UInt4 NegativeInfinityPacked = UInt4.FromVector(new Vector4(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity, -1f));
- L238 C32: new XRDataBuffer :: _outputPositions = new XRDataBuffer($"{meshName}_SkinnedBoundsOutput", EBufferTarget.ShaderStorageBuffer, (uint)Math.Max(1, mesh.VertexCount), EComponentType.Float, 4, false, false)
- L243 C23: new XRDataBuffer :: _bounds = new XRDataBuffer($"{meshName}_SkinnedBoundsReduction", EBufferTarget.ShaderStorageBuffer, 2, EComponentType.UInt, 4, false, false)
- L276 C25: new Vector3 :: positions = new Vector3[vertexCount];
- L289 C36: new Vector3 :: positions[i] = new Vector3(v.X, v.Y, v.Z);
- L323 C22: new AABB :: bounds = new AABB(min, max);
- L356 C20: new() :: => new()


## XRENGINE/Rendering/Compute/SkinnedMeshBvhScheduler.cs
- L21 C63: new() :: public static SkinnedMeshBvhScheduler Instance { get; } = new();
- L29 C19: new TaskCompletionSource :: var tcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
- L43 C19: new TaskCompletionSource :: var tcs = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
- L50 C52: new SkinnedMeshBoundsCalculator.Result :: var localized = mesh.EnsureLocalBounds(new SkinnedMeshBoundsCalculator.Result(positions, bounds, basis));
- L51 C30: new Result :: tcs.TrySetResult(new Result(version, null, localized));
- L55 C51: new SkinnedMeshBoundsCalculator.Result :: var boundsResult = mesh.EnsureLocalBounds(new SkinnedMeshBoundsCalculator.Result(positions, bounds, basis));
- L60 C30: new Result :: tcs.TrySetResult(new Result(version, null, boundsResult));
- L89 C34: new Result :: tcs.TrySetResult(new Result(version, null, boundsResult));
- L96 C34: new Result :: tcs.TrySetResult(new Result(version, null, boundsResult));
- L121 C26: new Result :: tcs.TrySetResult(new Result(version, task.Result, boundsResult));
- L126 C30: new List :: var worldTriangles = new List<Triangle>(triangles.Count);
- L135 C36: new Triangle :: worldTriangles.Add(new Triangle(
- L142 C19: new BVH :: ? new BVH<Triangle>(new TriangleAdapter(), worldTriangles)
- L142 C37: new TriangleAdapter :: ? new BVH<Triangle>(new TriangleAdapter(), worldTriangles)
- L151 C35: new SkinnedMeshBoundsCalculator.Result :: => new(version, null, new SkinnedMeshBoundsCalculator.Result(Array.Empty<Vector3>(), default, Matrix4x4.Identity));


## XRENGINE/Rendering/Compute/SkinningPrepassDispatcher.cs
- L19 C83: new SkinningPrepassDispatcher :: private static readonly Lazy<SkinningPrepassDispatcher> _instance = new(() => new SkinningPrepassDispatcher());
- L22 C41: new() :: private readonly object _syncRoot = new();
- L23 C91: new() :: private readonly ConditionalWeakTable<XRMeshRenderer, RendererResources> _resources = new();
- L114 C78: new RendererResources :: RendererResources resources = _resources.GetValue(renderer, r => new RendererResources(r));
- L153 C45: new XRRenderProgram :: _program = _shader is null ? null : new XRRenderProgram(true, false, _shader);
- L162 C67: new XRRenderProgram :: _interleavedProgram = _interleavedShader is null ? null : new XRRenderProgram(true, false, _interleavedShader);
- L253 C54: new XRDataBuffer :: _renderer.SkinnedInterleavedBuffer = new XRDataBuffer(
- L270 C52: new XRDataBuffer :: _renderer.SkinnedPositionsBuffer = new XRDataBuffer(
- L285 C54: new XRDataBuffer :: _renderer.SkinnedNormalsBuffer = new XRDataBuffer(
- L301 C55: new XRDataBuffer :: _renderer.SkinnedTangentsBuffer = new XRDataBuffer(


## XRENGINE/Rendering/DLSS/NvidiaDlssManager.cs
- L22 C94: new() :: private static readonly ConcurrentDictionary<XRViewport, float> _lastViewportScale = new();


## XRENGINE/Rendering/DLSS/StreamlineNative.cs
- L130 C45: new() :: StreamlineDlssOptions options = new()
- L160 C59: new() :: StreamlineViewportHandle viewportHandle = new()


## XRENGINE/Rendering/Generator/ShaderGenerator.cs
- L520 C28: new ArgumentOutOfRangeException :: _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)


## XRENGINE/Rendering/Generator/ShaderGraph.cs
- L99 C22: new GLSLManager :: var parser = new GLSLManager();
- L101 C16: new ShaderGraph :: return new ShaderGraph(parser);
- L106 C20: new ShaderGraphNode :: var node = new ShaderGraphNode(NewId(), method.Name, ShaderGraphNodeKind.MethodInvocation)
- L116 C29: new ShaderGraphInput :: node.Inputs.Add(new ShaderGraphInput(param.Name, param.Type, null));
- L139 C34: new ShaderGraphEdge :: yield return new ShaderGraphEdge(from.Id, node.Id, input.Name, input.SourceVariable!);
- L146 C19: new HashSet :: var set = new HashSet<string>(StringComparer.Ordinal);
- L236 C16: new ShaderGraphNode :: return new ShaderGraphNode(NewId(), variable.Name, kind)
- L248 C24: new ShaderGraphNode :: var node = new ShaderGraphNode(NewId(), method.Name, ShaderGraphNodeKind.MethodDefinition)
- L256 C33: new ShaderGraphInput :: node.Inputs.Add(new ShaderGraphInput(param.Name, param.Type, null));
- L266 C24: new ShaderGraphNode :: var node = new ShaderGraphNode(NewId(), invocation.MethodName, ShaderGraphNodeKind.MethodInvocation)
- L279 C33: new ShaderGraphInput :: node.Inputs.Add(new ShaderGraphInput(inputName, inputType, arguments[i]));


## XRENGINE/Rendering/Generator/ShaderGraphGenerator.cs
- L14 C24: new XRMesh :: : base(mesh ?? new XRMesh())
- L16 C32: new ArgumentNullException :: Graph = graph ?? throw new ArgumentNullException(nameof(graph));


## XRENGINE/Rendering/Generator/VarNameGen.cs
- L8 C23: New() :: public string New()


## XRENGINE/Rendering/GI/LightVolumeRegistry.cs
- L12 C102: new() :: private static readonly Dictionary<XRWorldInstance, List<LightVolumeComponent>> s_perWorld = new();
- L13 C49: new() :: private static readonly object s_lock = new();
- L21 C28: new List :: list = new List<LightVolumeComponent>();


## XRENGINE/Rendering/GI/RadianceCascadeRegistry.cs
- L12 C106: new() :: private static readonly Dictionary<XRWorldInstance, List<RadianceCascadeComponent>> s_perWorld = new();
- L13 C49: new() :: private static readonly object s_lock = new();
- L21 C28: new List :: list = new List<RadianceCascadeComponent>();


## XRENGINE/Rendering/GI/RestirGI.cs
- L93 C19: new InvalidOperationException :: throw new InvalidOperationException("GL_NV_ray_tracing is not available on the current device.");
- L107 C19: new InvalidOperationException :: throw new InvalidOperationException("Failed to bind the ReSTIR ray tracing pipeline.");
- L126 C19: new InvalidOperationException :: throw new InvalidOperationException("Failed to dispatch ReSTIR ray tracing rays.");
- L168 C20: new TraceParameters :: return new TraceParameters


## XRENGINE/Rendering/HybridRenderingManager.cs
- L71 C36: new XRRenderProgram :: _indirectCompProgram = new XRRenderProgram(
- L416 C26: new StringBuilder :: var sb = new StringBuilder();
- L552 C34: new StringBuilder :: var sb = new StringBuilder();
- L1148 C31: new DrawElementsIndirectCommand :: var drawCmd = new DrawElementsIndirectCommand
- L1221 C30: new List :: var shaderList = new List<XRShader>(material.Shaders.Where(shader => shader is not null));
- L1245 C27: new XRRenderProgram :: var program = new XRRenderProgram(linkNow: false, separable: false, shaderList);
- L1255 C60: new MaterialProgramCache :: _materialPrograms[(materialID, rendererKey)] = new MaterialProgramCache(program, generatedVertexShader);
- L1265 C22: new StringBuilder :: var sb = new StringBuilder();
- L1374 C20: new XRShader :: return new XRShader(EShaderType.Vertex, sb.ToString())
- L1387 C27: new DefaultVertexShaderGenerator :: var gen = new DefaultVertexShaderGenerator(mesh)
- L1393 C31: new XRShader :: generatedVS = new XRShader(EShaderType.Vertex, vertexShaderSource)
- L1403 C31: new XRShader :: generatedVS = new XRShader(EShaderType.Vertex, fallbackSource)
- L1415 C22: new StringBuilder :: var sb = new StringBuilder();


## XRENGINE/Rendering/Info/RenderInfo2D.cs
- L26 C70: new RenderInfo2D :: => ConstructorOverride?.Invoke(owner, renderCommands) ?? new RenderInfo2D(owner, renderCommands);


## XRENGINE/Rendering/Info/RenderInfo3D.cs
- L23 C58: new RenderInfo3D :: => ConstructorOverride?.Invoke(owner, []) ?? new RenderInfo3D(owner);
- L25 C70: new RenderInfo3D :: => ConstructorOverride?.Invoke(owner, renderCommands) ?? new RenderInfo3D(owner, renderCommands);
- L28 C27: new RenderCommandMethod3D :: => New(owner, new RenderCommandMethod3D(renderPass, renderMethod));
- L30 C27: new RenderCommandMethod3D :: => New(owner, new RenderCommandMethod3D((int)renderPass, renderMethod));
- L33 C52: new RenderCommandMethod3D :: => New(owner, methods.Select((x, y) => new RenderCommandMethod3D(x.renderPass, x.renderMethod)).ToArray());
- L35 C52: new RenderCommandMethod3D :: => New(owner, methods.Select((x, y) => new RenderCommandMethod3D((int)x.renderPass, x.renderMethod)).ToArray());
- L38 C27: new RenderCommandMesh3D :: => New(owner, new RenderCommandMesh3D(renderPass, manager, worldMatrix, materialOverride));
- L40 C27: new RenderCommandMesh3D :: => New(owner, new RenderCommandMesh3D((int)renderPass, manager, worldMatrix, materialOverride));
- L43 C51: new RenderCommandMesh3D :: => New(owner, meshes.Select((x, y) => new RenderCommandMesh3D(x.renderPass, x.manager, x.worldMatrix, x.materialOverride)).ToArray());
- L45 C51: new RenderCommandMesh3D :: => New(owner, meshes.Select((x, y) => new RenderCommandMesh3D((int)x.renderPass, x.manager, x.worldMatrix, x.materialOverride)).ToArray());


## XRENGINE/Rendering/Lightmapping/LightmapBakeManager.cs
- L27 C76: new() :: private readonly ConcurrentQueue<LightComponent> _manualBakeRequests = new();
- L144 C27: new LightmapBakeRequest :: var request = new LightmapBakeRequest(
- L145 C34: new LayerMask :: StaticLayerMask: new LayerMask(1 << DefaultLayers.StaticIndex),
- L164 C20: new LightmapBakeResult :: return new LightmapBakeResult(
- L174 C20: new LightmapBakeResult :: return new LightmapBakeResult(
- L188 C31: new Transform :: XRCamera camera = new(new Transform());
- L189 C24: new XRRenderPipelineInstance :: var pipeline = new XRRenderPipelineInstance();
- L207 C24: new List :: var bakeList = new List<(LightmapBakeTarget Target, XRMeshRenderer Renderer)>();
- L217 C20: new LightmapBakeResult :: return new LightmapBakeResult(light, targets, ELightmapBakeStatus.Failed, "No valid mesh renderers found for bake targets.");
- L244 C25: new XRRenderBuffer :: var depth = new XRRenderBuffer((uint)page.Size, (uint)page.Size, ERenderBufferStorage.Depth24Stencil8);
- L247 C23: new XRFrameBuffer :: var fbo = new XRFrameBuffer();
- L276 C33: new Vector2 :: var scale = new Vector2(innerW * inv, innerH * inv);
- L277 C34: new Vector2 :: var offset = new Vector2(innerX * inv, innerY * inv);
- L278 C32: new BakedLightmapInfo :: var info = new BakedLightmapInfo(atlasTex, scale, offset, request.LightmapUvChannel, pageIndex);
- L291 C16: new LightmapBakeResult :: return new LightmapBakeResult(light, targets, ELightmapBakeStatus.Completed, null);
- L367 C19: new InvalidOperationException :: throw new InvalidOperationException($"Atlas too small: atlas={atlasSize}, tile={tileSize}, padding={padding}.");
- L369 C21: new List :: var pages = new List<AtlasPage>();
- L370 C21: new List :: var tiles = new List<AtlasTile>();
- L380 C27: new AtlasPage :: pages.Add(new AtlasPage(atlasSize, tiles));
- L401 C23: new AtlasTile :: tiles.Add(new AtlasTile(i, cursorX, cursorY, tileSize, padding));
- L406 C23: new AtlasPage :: pages.Add(new AtlasPage(atlasSize, tiles));
- L408 C16: new AtlasPackResult :: return new AtlasPackResult(pages);
- L583 C13: new ShaderInt :: new ShaderInt(0, ParamLightKind),
- L584 C13: new ShaderVector3 :: new ShaderVector3(Vector3.One, ParamLightColor),
- L585 C13: new ShaderFloat :: new ShaderFloat(1.0f, ParamLightIntensity),
- L586 C13: new ShaderVector3 :: new ShaderVector3(Vector3.Zero, ParamLightPosition),
- L587 C13: new ShaderVector3 :: new ShaderVector3(Globals.Forward, ParamLightDirection),
- L588 C13: new ShaderFloat :: new ShaderFloat(10.0f, ParamLightRadius),
- L589 C13: new ShaderFloat :: new ShaderFloat(1.0f, ParamLightBrightness),
- L590 C13: new ShaderFloat :: new ShaderFloat(0.9f, ParamSpotInnerCutoff),
- L591 C13: new ShaderFloat :: new ShaderFloat(0.8f, ParamSpotOuterCutoff),
- L592 C13: new ShaderFloat :: new ShaderFloat(1.0f, ParamSpotExponent),
- L594 C13: new ShaderInt :: new ShaderInt(0, ParamShadowsEnabled),
- L595 C13: new ShaderMat4 :: new ShaderMat4(Matrix4x4.Identity, ParamWorldToLightInvViewMatrix),
- L596 C13: new ShaderMat4 :: new ShaderMat4(Matrix4x4.Identity, ParamWorldToLightProjMatrix),
- L597 C13: new ShaderFloat :: new ShaderFloat(1.0f, ParamShadowBase),
- L598 C13: new ShaderFloat :: new ShaderFloat(1.0f, ParamShadowMult),
- L599 C13: new ShaderFloat :: new ShaderFloat(0.00001f, ParamShadowBiasMin),
- L600 C13: new ShaderFloat :: new ShaderFloat(0.004f, ParamShadowBiasMax),
- L603 C15: new XRMaterial :: mat = new XRMaterial(
- L605 C13: new XRShader :: new XRShader(EShaderType.Geometry, geom),
- L606 C13: new XRShader :: new XRShader(EShaderType.Fragment, frag))
- L660 C13: new ShaderVector2 :: new ShaderVector2(Vector2.One, ParamLightmapScale),
- L661 C13: new ShaderVector2 :: new ShaderVector2(Vector2.Zero, ParamLightmapOffset),
- L664 C15: new XRMaterial :: mat = new XRMaterial(parameters, new XRShader(EShaderType.Fragment, frag))
- L664 C42: new XRShader :: mat = new XRMaterial(parameters, new XRShader(EShaderType.Fragment, frag))
- L766 C13: new ShaderVector2 :: new ShaderVector2(info.Scale, ParamLightmapScale),
- L767 C13: new ShaderVector2 :: new ShaderVector2(info.Offset, ParamLightmapOffset),
- L770 C23: new XRMaterial :: var preview = new XRMaterial(parameters, new XRTexture?[] { info.Atlas }, previewBase.Shaders)
- L770 C50: new XRTexture :: var preview = new XRMaterial(parameters, new XRTexture?[] { info.Atlas }, previewBase.Shaders)
- L807 C37: new LightmapBakeTarget :: targets.Add(new LightmapBakeTarget(node, renderable, link, mesh, request.LightmapUvChannel, hasUv));


## XRENGINE/Rendering/Lights3DCollection.cs
- L35 C69: new AABB :: public Octree<LightProbeCell> LightProbeTree { get; } = new(new AABB());
- L36 C62: new LightmapBakeManager :: public LightmapBakeManager LightmapBaking { get; } = new LightmapBakeManager(world);
- L45 C74: new XRTexture2D :: private static XRTexture2D DummyShadowMap => _dummyShadowMap ??= new XRTexture2D(1, 1, ColorF4.White);
- L50 C75: new() :: public EventList<SpotLightComponent> DynamicSpotLights { get; } = new() { ThreadSafe = true };
- L54 C77: new() :: public EventList<PointLightComponent> DynamicPointLights { get; } = new() { ThreadSafe = true };
- L58 C89: new() :: public EventList<DirectionalLightComponent> DynamicDirectionalLights { get; } = new() { ThreadSafe = true };
- L62 C70: new() :: public EventList<LightProbeComponent> LightProbes { get; } = new() { ThreadSafe = true };
- L68 C85: new() :: private readonly ConcurrentQueue<SceneCaptureComponentBase> _captureQueue = new();
- L71 C62: new() :: private readonly Stopwatch _captureBudgetStopwatch = new();
- L107 C46: new Vector3 :: program.Uniform("GlobalAmbient", new Vector3(0.1f, 0.1f, 0.1f));
- L387 C23: new() :: Box box = new()
- L482 C112: new ColorF4 :: UpdateDirectionalCameraLightIntersections(DynamicDirectionalLights, preparedCamera, cameraForward, new ColorF4(0.2f, 0.8f, 1.0f, 1.0f));
- L483 C79: new ColorF4 :: UpdateCameraLightIntersections(DynamicSpotLights, preparedCamera, new ColorF4(1.0f, 0.85f, 0.2f, 1.0f));
- L484 C80: new ColorF4 :: UpdateCameraLightIntersections(DynamicPointLights, preparedCamera, new ColorF4(1.0f, 0.2f, 0.8f, 1.0f));
- L596 C35: new AABB :: LightProbeTree.Remake(new AABB());
- L741 C26: new AABB :: var bounds = new AABB(firstPos, firstPos);
- L769 C28: new Dictionary :: var distinct = new Dictionary<(int, int, int), LightProbeComponent>();
- L828 C25: new SceneNode :: new SceneNode(parent, $"Probe[{x},{y},{z}]", new Transform(localMin + baseInc + new Vector3(x, y, z) * probeInc)).AddComponent<LightProbeComponent>();
- L828 C70: new Transform :: new SceneNode(parent, $"Probe[{x},{y},{z}]", new Transform(localMin + baseInc + new Vector3(x, y, z) * probeInc)).AddComponent<LightProbeComponent>();
- L828 C105: new Vector3 :: new SceneNode(parent, $"Probe[{x},{y},{z}]", new Transform(localMin + baseInc + new Vector3(x, y, z) * probeInc)).AddComponent<LightProbeComponent>();
- L859 C23: new NotImplementedException :: throw new NotImplementedException();
- L864 C23: new NotImplementedException :: throw new NotImplementedException();
- L869 C23: new NotImplementedException :: throw new NotImplementedException();
- L874 C23: new NotImplementedException :: throw new NotImplementedException();
- L879 C23: new NotImplementedException :: throw new NotImplementedException();
- L884 C23: new NotImplementedException :: throw new NotImplementedException();
- L889 C23: new NotImplementedException :: throw new NotImplementedException();
- L894 C23: new NotImplementedException :: throw new NotImplementedException();
- L899 C23: new NotImplementedException :: throw new NotImplementedException();
- L904 C23: new NotImplementedException :: throw new NotImplementedException();
- L909 C23: new NotImplementedException :: throw new NotImplementedException();
- L914 C23: new NotImplementedException :: throw new NotImplementedException();


## XRENGINE/Rendering/Materials/GPUMaterialTable.cs
- L34 C22: new XRDataBuffer :: Buffer = new XRDataBuffer(


## XRENGINE/Rendering/Meshlets/MeshletCollection.cs
- L53 C32: new XRRenderProgram :: _taskMeshProgram = new XRRenderProgram(false, true, taskShader, meshShader, fragmentShader);
- L146 C42: new uint :: var visibleMeshletData = new uint[1 + _meshlets.Count];
- L163 C38: new Matrix4x4 :: var transformArray = new Matrix4x4[maxMeshID];


## XRENGINE/Rendering/Meshlets/MeshletGenerator.cs
- L121 C41: new MeshoptMeshlet :: MeshoptMeshlet[] meshlets = new MeshoptMeshlet[maxMeshlets];
- L152 C36: new uint :: meshletVertexIndices = new uint[meshletCount * maxVerticesPerMeshlet];
- L153 C38: new byte :: meshletTriangleIndices = new byte[meshletCount * maxTrianglesPerMeshlet * 3];
- L160 C33: new Meshlet :: Meshlet[] results = new Meshlet[meshletCount];
- L167 C30: new Meshlet :: results[i] = new Meshlet
- L204 C20: new Vector4 :: return new Vector4(center, radius);


## XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_BloomPass.cs
- L125 C21: new ShaderFloat :: new ShaderFloat(0.0f, "Ping"),
- L126 C21: new ShaderInt :: new ShaderInt(0, "LOD"),
- L127 C21: new ShaderFloat :: new ShaderFloat(1.0f, "Radius"),
- L132 C33: new RenderingParameters :: RenderOptions = new RenderingParameters()
- L143 C25: new XRQuadFrameBuffer :: var blur1 = new XRQuadFrameBuffer(bloomBlurMat) { Name = BloomBlur1FBOName };
- L144 C25: new XRQuadFrameBuffer :: var blur2 = new XRQuadFrameBuffer(bloomBlurMat) { Name = BloomBlur2FBOName };
- L145 C25: new XRQuadFrameBuffer :: var blur4 = new XRQuadFrameBuffer(bloomBlurMat) { Name = BloomBlur4FBOName };
- L146 C25: new XRQuadFrameBuffer :: var blur8 = new XRQuadFrameBuffer(bloomBlurMat) { Name = BloomBlur8FBOName };
- L147 C26: new XRQuadFrameBuffer :: var blur16 = new XRQuadFrameBuffer(bloomBlurMat) { Name = BloomBlur16FBOName };
- L152 C23: new InvalidOperationException :: throw new InvalidOperationException("Output texture is not an IFrameBufferAttachement.");


## XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_ForwardPlusLightCullingPass.cs
- L53 C31: new XRRenderProgram :: _computeProgram = new XRRenderProgram(true, false, compute);
- L123 C52: new IVector2 :: _computeProgram!.Uniform("screenSize", new IVector2(width, height));
- L133 C60: new Vector2 :: Engine.Rendering.State.ForwardPlusScreenSize = new Vector2(width, height);
- L152 C28: new ForwardPlusLocalLight :: result.Add(new ForwardPlusLocalLight
- L154 C34: new Vector4 :: PositionWS = new Vector4(p.Transform.RenderTranslation, 1.0f),
- L155 C44: new Vector4 :: DirectionWS_Exponent = new Vector4(0, 0, 0, 0),
- L156 C34: new Vector4 :: Color_Type = new Vector4(p.Color, 0.0f),
- L157 C30: new Vector4 :: Params = new Vector4(p.Radius, p.Brightness, p.DiffuseIntensity, 0.0f),
- L167 C28: new ForwardPlusLocalLight :: result.Add(new ForwardPlusLocalLight
- L169 C34: new Vector4 :: PositionWS = new Vector4(s.Transform.RenderTranslation, 1.0f),
- L170 C44: new Vector4 :: DirectionWS_Exponent = new Vector4(Vector3.Normalize(s.Transform.RenderForward), s.Exponent),
- L171 C34: new Vector4 :: Color_Type = new Vector4(s.Color, 1.0f),
- L172 C30: new Vector4 :: Params = new Vector4(s.Distance, s.Brightness, s.DiffuseIntensity, 0.0f),
- L173 C34: new Vector4 :: SpotAngles = new Vector4(s.InnerCutoff, s.OuterCutoff, 0.0f, 0.0f),
- L186 C38: new XRDataBuffer :: _localLightsBuffer = new XRDataBuffer(
- L207 C41: new XRDataBuffer :: _visibleIndicesBuffer = new XRDataBuffer(


## XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_LightCombinePass.cs
- L42 C23: new Exception :: throw new Exception("One or more required textures are missing.");
- L139 C34: new XRMeshRenderer :: PointLightRenderer = new XRMeshRenderer(pointLightMesh, pointLightMat);
- L142 C33: new XRMeshRenderer :: SpotLightRenderer = new XRMeshRenderer(spotLightMesh, spotLightMat);
- L145 C40: new XRMeshRenderer :: DirectionalLightRenderer = new XRMeshRenderer(dirLightMesh, dirLightMat);
- L151 C56: new() :: RenderingParameters additiveRenderParams = new()
- L170 C43: new() :: BlendModeAllDrawBuffers = new()
- L181 C29: new() :: DepthTest = new()


## XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_LightVolumesPass.cs
- L123 C41: new XRRenderProgram :: _computeProgramStereo = new XRRenderProgram(true, false, shader);
- L132 C35: new XRRenderProgram :: _computeProgram = new XRRenderProgram(true, false, shader);
- L157 C51: new IVector2 :: _computeProgram.Uniform("resolution", new IVector2((int)width, (int)height));
- L207 C57: new IVector2 :: _computeProgramStereo.Uniform("resolution", new IVector2((int)width, (int)height));


## XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_MSVO.cs
- L99 C48: new() :: RenderingParameters renderParams = new()
- L120 C23: new ArgumentException :: throw new ArgumentException("Albedo texture must be an IFrameBufferAttachement");
- L123 C23: new ArgumentException :: throw new ArgumentException("Normal texture must be an IFrameBufferAttachement");
- L126 C23: new ArgumentException :: throw new ArgumentException("RMSE texture must be an IFrameBufferAttachement");
- L129 C23: new ArgumentException :: throw new ArgumentException("DepthStencil texture must be an IFrameBufferAttachement");


## XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_MVAOPass.cs
- L72 C113: new() :: private static readonly ConditionalWeakTable<XRRenderPipelineInstance, InstanceState> _instanceStates = new();
- L75 C56: new InstanceState :: => _instanceStates.GetValue(instance, _ => new InstanceState());
- L201 C32: new Vector2 :: state.NoiseScale = new Vector2(
- L248 C48: new() :: RenderingParameters renderParams = new()
- L279 C23: new ArgumentException :: throw new ArgumentException("Albedo texture must be an IFrameBufferAttachement");
- L282 C23: new ArgumentException :: throw new ArgumentException("Normal texture must be an IFrameBufferAttachement");
- L285 C23: new ArgumentException :: throw new ArgumentException("RMSE texture must be an IFrameBufferAttachement");
- L288 C23: new ArgumentException :: throw new ArgumentException("TransformId texture must be an IFrameBufferAttachement");
- L291 C23: new ArgumentException :: throw new ArgumentException("DepthStencil texture must be an IFrameBufferAttachement");
- L305 C23: new ArgumentException :: throw new ArgumentException("Ambient occlusion texture must be an IFrameBufferAttachement");
- L326 C29: new() :: Random random = new();
- L328 C22: new Vector3 :: Kernel = new Vector3[MaxKernelSize];
- L329 C21: new Vector2 :: Noise = new Vector2[NoiseWidth * NoiseHeight];
- L401 C40: new() :: XRTexture2D noiseTexture = new()
- L413 C21: new() :: new()
- L415 C76: new float :: Data = DataSource.FromArray(Noise!.SelectMany(v => new float[] { v.X, v.Y }).ToArray()),


## XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_RadianceCascadesPass.cs
- L140 C32: new XRTexture2D :: _historyTextureA = new XRTexture2D((uint)width, (uint)height, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat)
- L149 C32: new XRTexture2D :: _historyTextureB = new XRTexture2D((uint)width, (uint)height, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat)
- L170 C38: new XRTexture2DArray :: _historyTextureStereoA = new XRTexture2DArray((uint)width, (uint)height, 2, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat)
- L179 C38: new XRTexture2DArray :: _historyTextureStereoB = new XRTexture2DArray((uint)width, (uint)height, 2, EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat)
- L210 C41: new XRRenderProgram :: _computeProgramStereo = new XRRenderProgram(true, false, shader);
- L218 C31: new XRRenderProgram :: _computeProgram = new XRRenderProgram(true, false, monoShader);
- L257 C59: new Vector4 :: program.Uniform($"cascadeHalfExtents{i}", new Vector4(halfExtents, intensity));
- L303 C51: new IVector2 :: _computeProgram.Uniform("resolution", new IVector2((int)width, (int)height));
- L306 C60: new Vector4 :: _computeProgram.Uniform("volumeTintIntensity", new Vector4(component.Tint.R, component.Tint.G, component.Tint.B, component.Intensity));
- L355 C57: new IVector2 :: _computeProgramStereo.Uniform("resolution", new IVector2((int)width, (int)height));
- L358 C66: new Vector4 :: _computeProgramStereo.Uniform("volumeTintIntensity", new Vector4(component.Tint.R, component.Tint.G, component.Tint.B, component.Intensity));


## XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_ReSTIRPass.cs
- L139 C35: new XRRenderProgram :: _initialProgram = new XRRenderProgram(true, false,
- L145 C36: new XRRenderProgram :: _resampleProgram = new XRRenderProgram(true, false,
- L151 C33: new XRRenderProgram :: _finalProgram = new XRRenderProgram(true, false,
- L182 C26: new XRDataBuffer :: var buffer = new XRDataBuffer(name, EBufferTarget.ShaderStorageBuffer, elementCount, EComponentType.Struct, _reservoirStride, false, false)
- L201 C51: new IVector2 :: _initialProgram.Uniform("resolution", new IVector2((int)width, (int)height));
- L202 C47: new Vector2 :: _initialProgram.Uniform("invRes", new Vector2(1.0f / width, 1.0f / height));
- L223 C52: new IVector2 :: _resampleProgram.Uniform("resolution", new IVector2((int)width, (int)height));
- L224 C48: new Vector2 :: _resampleProgram.Uniform("invRes", new Vector2(1.0f / width, 1.0f / height));
- L240 C49: new IVector2 :: _finalProgram.Uniform("resolution", new IVector2((int)width, (int)height));


## XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_SpatialHashAOPass.cs
- L71 C113: new() :: private static readonly ConditionalWeakTable<XRRenderPipelineInstance, InstanceState> _instanceStates = new();
- L74 C56: new InstanceState :: => _instanceStates.GetValue(instance, _ => new InstanceState());
- L189 C41: new XRRenderProgram :: _computeProgramStereo = new XRRenderProgram(true, false, compute);
- L197 C35: new XRRenderProgram :: _computeProgram = new XRRenderProgram(true, false, compute);
- L290 C23: new ArgumentException :: throw new ArgumentException("Ambient occlusion texture must be an IFrameBufferAttachement");
- L293 C23: new ArgumentException :: throw new ArgumentException("Albedo texture must be an IFrameBufferAttachement");
- L296 C23: new ArgumentException :: throw new ArgumentException("Normal texture must be an IFrameBufferAttachement");
- L299 C23: new ArgumentException :: throw new ArgumentException("RMSE texture must be an IFrameBufferAttachement");
- L302 C23: new ArgumentException :: throw new ArgumentException("TransformId texture must be an IFrameBufferAttachement");
- L305 C23: new ArgumentException :: throw new ArgumentException("DepthStencil texture must be an IFrameBufferAttachement");
- L349 C32: new XRDataBuffer :: state.HashBuffer = new XRDataBuffer("SpatialHashKeys", EBufferTarget.ShaderStorageBuffer, state.HashCapacity, EComponentType.UInt, 1, false, true)
- L358 C36: new XRDataBuffer :: state.HashTimeBuffer = new XRDataBuffer("SpatialHashTime", EBufferTarget.ShaderStorageBuffer, state.HashCapacity, EComponentType.UInt, 1, false, true)
- L367 C35: new XRDataBuffer :: state.SpatialBuffer = new XRDataBuffer("SpatialHashData", EBufferTarget.ShaderStorageBuffer, state.HashCapacity, EComponentType.UInt, 2, false, true)
- L474 C54: new Vector2 :: _computeProgram.Uniform("InvResolution", new Vector2(1.0f / width, 1.0f / height));
- L559 C60: new Vector2 :: _computeProgramStereo.Uniform("InvResolution", new Vector2(1.0f / width, 1.0f / height));


## XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_SSAOPass.cs
- L57 C113: new() :: private static readonly ConditionalWeakTable<XRRenderPipelineInstance, InstanceState> _instanceStates = new();
- L60 C56: new InstanceState :: => _instanceStates.GetValue(instance, _ => new InstanceState());
- L64 C24: new() :: Random r = new();
- L66 C22: new Vector3 :: Kernel = new Vector3[Samples];
- L67 C21: new Vector2 :: Noise = new Vector2[NoiseWidth * NoiseHeight];
- L74 C26: new Vector3 :: sample = new Vector3(
- L84 C46: new Vector2 :: Noise[i] = Vector2.Normalize(new Vector2((float)r.NextDouble(), (float)r.NextDouble()));
- L194 C32: new Vector2 :: state.NoiseScale = new Vector2(
- L243 C48: new() :: RenderingParameters renderParams = new()
- L271 C23: new ArgumentException :: throw new ArgumentException("Albedo texture must be an IFrameBufferAttachement");
- L274 C23: new ArgumentException :: throw new ArgumentException("Normal texture must be an IFrameBufferAttachement");
- L277 C23: new ArgumentException :: throw new ArgumentException("RMSI texture must be an IFrameBufferAttachement");
- L280 C23: new ArgumentException :: throw new ArgumentException("TransformId texture must be an IFrameBufferAttachement");
- L283 C23: new ArgumentException :: throw new ArgumentException("DepthStencil texture must be an IFrameBufferAttachement");
- L297 C23: new ArgumentException :: throw new ArgumentException("SSAO texture must be an IFrameBufferAttachement");
- L342 C36: new() :: XRTexture2D noiseTex = new()
- L354 C21: new() :: new()
- L356 C76: new float :: Data = DataSource.FromArray(Noise!.SelectMany(v => new float[] { v.X, v.Y }).ToArray()),


## XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_SurfelDebugVisualization.cs
- L159 C29: new XRRenderProgram :: _debugProgram = new XRRenderProgram(true, false, shader);
- L192 C45: new Data.Vectors.IVector2 :: _debugProgram.Uniform("resolution", new Data.Vectors.IVector2((int)width, (int)height));
- L198 C42: new Data.Vectors.UVector3 :: _debugProgram.Uniform("gridDim", new Data.Vectors.UVector3(


## XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_SurfelGIPass.cs
- L152 C46: new Vector3 :: Vector3 gridOrigin = cameraPos - new Vector3(GridHalfExtent);
- L182 C32: new XRRenderProgram :: _initProgram = new XRRenderProgram(true, false, shader);
- L188 C35: new XRRenderProgram :: _recycleProgram = new XRRenderProgram(true, false, shader);
- L194 C37: new XRRenderProgram :: _resetGridProgram = new XRRenderProgram(true, false, shader);
- L200 C37: new XRRenderProgram :: _buildGridProgram = new XRRenderProgram(true, false, shader);
- L206 C33: new XRRenderProgram :: _spawnProgram = new XRRenderProgram(true, false, shader);
- L212 C33: new XRRenderProgram :: _shadeProgram = new XRRenderProgram(true, false, shader);
- L222 C33: new XRDataBuffer :: _surfelBuffer = new XRDataBuffer(
- L242 C34: new XRDataBuffer :: _counterBuffer = new XRDataBuffer(
- L261 C36: new XRDataBuffer :: _freeStackBuffer = new XRDataBuffer(
- L281 C37: new XRDataBuffer :: _gridCountsBuffer = new XRDataBuffer(
- L300 C38: new XRDataBuffer :: _gridIndicesBuffer = new XRDataBuffer(
- L374 C50: new UVector3 :: _buildGridProgram.Uniform("gridDim", new UVector3(GridDimX, GridDimY, GridDimZ));
- L401 C49: new IVector2 :: _spawnProgram.Uniform("resolution", new IVector2((int)width, (int)height));
- L409 C46: new UVector3 :: _spawnProgram.Uniform("gridDim", new UVector3(GridDimX, GridDimY, GridDimZ));
- L431 C49: new IVector2 :: _shadeProgram.Uniform("resolution", new IVector2((int)width, (int)height));
- L439 C46: new UVector3 :: _shadeProgram.Uniform("gridDim", new UVector3(GridDimX, GridDimY, GridDimZ));


## XRENGINE/Rendering/Pipelines/Commands/Features/VPRC_TemporalAccumulationPass.cs
- L50 C92: new() :: private static readonly ConditionalWeakTable<XRCamera, TemporalState> TemporalStates = new();
- L198 C20: new TemporalUniformData :: data = new TemporalUniformData
- L226 C54: new TemporalState :: state = TemporalStates.GetValue(camera, _ => new TemporalState());
- L358 C16: new Vector2 :: return new Vector2(x, y);


## XRENGINE/Rendering/Pipelines/Commands/Flow/VPRC_IfElse.cs
- L15 C102: new() :: private readonly ConditionalWeakTable<XRRenderPipelineInstance, BranchState> _branchStates = new();
- L57 C63: new BranchState :: var state = _branchStates.GetValue(instance, _ => new BranchState());


## XRENGINE/Rendering/Pipelines/Commands/Flow/VPRC_Switch.cs
- L15 C102: new() :: private readonly ConditionalWeakTable<XRRenderPipelineInstance, SwitchState> _switchStates = new();
- L59 C63: new SwitchState :: var state = _switchStates.GetValue(instance, _ => new SwitchState());


## XRENGINE/Rendering/Pipelines/Commands/State/ViewportStateRenderCommand.cs
- L7 C131: new() :: public abstract class ViewportStateRenderCommand<T> : ViewportStateRenderCommandBase where T : ViewportPopStateRenderCommand, new()
- L9 C40: new T :: public T PopCommand { get; } = new T();


## XRENGINE/Rendering/Pipelines/Commands/ViewportRenderCommandContainer.cs
- L75 C116: new() :: public StateObject AddUsing<T>(Action<T>? setOptionsFunc = null) where T : ViewportStateRenderCommandBase, new()
- L91 C23: new ArgumentException :: throw new ArgumentException("Type must be a subclass of ViewportStateRenderCommand.", nameof(t));
- L112 C60: new() :: public T Add<T>() where T : ViewportRenderCommand, new()
- L115 C21: new() :: T cmd = new();
- L128 C23: new ArgumentException :: throw new ArgumentException("Type must be a subclass of ViewportRenderCommand.", nameof(t));
- L130 C103: new ArgumentException :: ViewportRenderCommand cmd = Activator.CreateInstance(t) as ViewportRenderCommand ?? throw new ArgumentException("Type must have a public parameterless constructor.", nameof(t));
- L137 C23: new ArgumentException :: throw new ArgumentException("Type must be a subclass of ViewportRenderCommand.", nameof(t));
- L139 C112: new ArgumentException :: ViewportRenderCommand cmd = (ViewportRenderCommand)Activator.CreateInstance(t, arguments) ?? throw new ArgumentException("Type must have a public constructor with the specified arguments.", nameof(t));
- L212 C25: new InstanceResourceState :: state = new InstanceResourceState();


## XRENGINE/Rendering/Pipelines/Commands/VPRC_DispatchCompute.cs
- L33 C53: new XRShader :: _computeProgram.Shaders.Add(new XRShader(EShaderType.Compute, ComputeShaderCode));


## XRENGINE/Rendering/Pipelines/RenderingState.cs
- L122 C63: new() :: private readonly Stack<XRCamera?> _renderingCameras = new();
- L133 C72: new() :: private readonly Stack<BoundingRectangle> _renderRegionStack = new();
- L137 C31: new BoundingRectangle :: => PushRenderArea(new BoundingRectangle(x, y, width, height));
- L156 C70: new() :: private readonly Stack<BoundingRectangle> _cropRegionStack = new();
- L160 C29: new BoundingRectangle :: => PushCropArea(new BoundingRectangle(x, y, width, height));
- L185 C65: new() :: private readonly Stack<XRMaterial> _overrideMaterials = new();
- L243 C66: new() :: private readonly Stack<XRViewport> _renderingViewports = new();
- L259 C64: new() :: private readonly Stack<VisualScene> _renderingScenes = new();
- L275 C36: New() :: return StateObject.New();
- L285 C36: New() :: return StateObject.New();
- L294 C24: new Vector2 :: return new Vector2(region.Width, region.Height);
- L302 C28: new Vector2 :: return new Vector2(width, height);


## XRENGINE/Rendering/Pipelines/Types/CustomRenderPipeline.cs
- L10 C40: new Lazy :: => _invalidMaterialFactory ??= new Lazy<XRMaterial>(() => CustomInvalidMaterial is not null ? CustomInvalidMaterial : XRMaterial.CreateUnlitColorMaterialForward());


## XRENGINE/Rendering/Pipelines/Types/DebugOpaqueRenderPipeline.cs
- L20 C70: new() :: private readonly NearToFarRenderCommandSorter _nearToFarSorter = new();
- L57 C12: new() :: => new()


## XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.cs
- L24 C70: new() :: private readonly NearToFarRenderCommandSorter _nearToFarSorter = new();
- L25 C70: new() :: private readonly FarToNearRenderCommandSorter _farToNearSorter = new();
- L93 C12: new() :: => new()
- L192 C49: new Lazy :: _voxelConeTracingVoxelizationMaterial = new Lazy<XRMaterial>(CreateVoxelConeTracingVoxelizationMaterial, LazyThreadSafetyMode.PublicationOnly);
- L193 C34: new Lazy :: _motionVectorsMaterial = new Lazy<XRMaterial>(CreateMotionVectorsMaterial, LazyThreadSafetyMode.PublicationOnly);
- L328 C30: new() :: aoSwitch.Cases = new()
- L494 C21: new[] :: new[]
- L622 C17: new ViewportRenderCommandContainer :: var c = new ViewportRenderCommandContainer(this);
- L842 C25: new ViewportRenderCommandContainer :: var container = new ViewportRenderCommandContainer(this)
- L855 C25: new ViewportRenderCommandContainer :: var container = new ViewportRenderCommandContainer(this)
- L865 C25: new ViewportRenderCommandContainer :: var container = new ViewportRenderCommandContainer(this)
- L875 C25: new ViewportRenderCommandContainer :: var container = new ViewportRenderCommandContainer(this);
- L904 C25: new ViewportRenderCommandContainer :: var container = new ViewportRenderCommandContainer(this);
- L954 C34: new[] :: pass.DependentFboNames = new[] { LightCombineFBOName };
- L980 C34: new[] :: pass.DependentFboNames = new[] { LightCombineFBOName };
- L1005 C34: new[] :: pass.DependentFboNames = new[] { LightCombineFBOName };
- L1041 C72: new() :: private readonly Dictionary<Guid, Vector3> _cachedProbePositions = new();
- L1042 C111: new() :: private readonly Dictionary<Guid, (XRTexture2D Irradiance, XRTexture2D Prefilter)> _cachedProbeTextures = new();
- L1043 C75: new() :: private readonly Dictionary<Guid, uint> _cachedProbeCaptureVersions = new();
- L1186 C37: new IVector3 :: dimsI = IVector3.Min(dimsI, new IVector3(64, 64, 64));
- L1190 C25: new List :: var cellLists = new List<int>[cellCount];
- L1192 C28: new List :: cellLists[i] = new List<int>(4);
- L1197 C28: new Vector3 :: Vector3 rel = (new Vector3(pos4.X, pos4.Y, pos4.Z) - _probeGridOrigin) / _probeGridCellSize;
- L1206 C23: new List :: var offsets = new List<ProbeGridCell>(cellCount);
- L1207 C23: new List :: var indices = new List<int>();
- L1213 C25: new ProbeGridCell :: offsets.Add(new ProbeGridCell { OffsetCount = new IVector2(offset, list.Count) });
- L1213 C59: new IVector2 :: offsets.Add(new ProbeGridCell { OffsetCount = new IVector2(offset, list.Count) });
- L1216 C32: new XRDataBuffer :: _probeGridCellBuffer = new XRDataBuffer("LightProbeGridCells", EBufferTarget.ShaderStorageBuffer, (uint)offsets.Count, EComponentType.Struct, (uint)Marshal.SizeOf<ProbeGridCell>(), false, false)
- L1223 C33: new XRDataBuffer :: _probeGridIndexBuffer = new XRDataBuffer("LightProbeGridIndices", EBufferTarget.ShaderStorageBuffer, (uint)indices.Count, EComponentType.Int, sizeof(int), false, false)
- L1233 C27: new List :: var readyProbes = new List<LightProbeComponent>(probes.Count);
- L1323 C27: new List :: var irrTextures = new List<XRTexture2D>(readyProbes.Count);
- L1324 C27: new List :: var preTextures = new List<XRTexture2D>(readyProbes.Count);
- L1325 C25: new List :: var positions = new List<ProbePositionData>(readyProbes.Count);
- L1326 C26: new List :: var parameters = new List<ProbeParamData>(readyProbes.Count);
- L1334 C27: new ProbePositionData :: positions.Add(new ProbePositionData { Position = new Vector4(position, 1.0f) });
- L1334 C62: new Vector4 :: positions.Add(new ProbePositionData { Position = new Vector4(position, 1.0f) });
- L1336 C28: new ProbeParamData :: parameters.Add(new ProbeParamData
- L1338 C34: new Vector4 :: InfluenceInner = new Vector4(probe.InfluenceBoxInnerExtents, probe.InfluenceSphereInnerRadius),
- L1339 C34: new Vector4 :: InfluenceOuter = new Vector4(probe.InfluenceBoxOuterExtents, probe.InfluenceSphereOuterRadius),
- L1340 C40: new Vector4 :: InfluenceOffsetShape = new Vector4(probe.InfluenceOffset, probe.InfluenceShape == LightProbeComponent.EInfluenceShape.Box ? 1.0f : 0.0f),
- L1341 C37: new Vector4 :: ProxyCenterEnable = new Vector4(probe.ProxyBoxCenterOffset, probe.ParallaxCorrectionEnabled ? 1.0f : 0.0f),
- L1342 C36: new Vector4 :: ProxyHalfExtents = new Vector4(probe.ProxyBoxHalfExtents, probe.NormalizationScale),
- L1343 C33: new Vector4 :: ProxyRotation = new Vector4(probe.ProxyBoxRotation.X, probe.ProxyBoxRotation.Y, probe.ProxyBoxRotation.Z, probe.ProxyBoxRotation.W),
- L1353 C33: new XRTexture2DArray :: _probeIrradianceArray = new XRTexture2DArray([.. irrTextures])
- L1361 C32: new XRTexture2DArray :: _probePrefilterArray = new XRTexture2DArray([.. preTextures])
- L1369 C32: new XRDataBuffer :: _probePositionBuffer = new XRDataBuffer("LightProbePositions", EBufferTarget.ShaderStorageBuffer, (uint)positions.Count, EComponentType.Struct, (uint)Marshal.SizeOf<ProbePositionData>(), false, false)
- L1376 C29: new XRDataBuffer :: _probeParamBuffer = new XRDataBuffer("LightProbeParameters", EBufferTarget.ShaderStorageBuffer, (uint)parameters.Count, EComponentType.Struct, (uint)Marshal.SizeOf<ProbeParamData>(), false, false)
- L1400 C28: new Dictionary :: var probeIndices = new Dictionary<LightProbeComponent, int>(probes.Count);
- L1433 C25: new List :: var tetraData = new List<ProbeTetraData>(cells.Count);
- L1439 C31: new ProbeTetraData :: tetraData.Add(new ProbeTetraData
- L1441 C31: new Vector4 :: Indices = new Vector4(
- L1457 C20: new List :: var list = new List<ProbeTetraData>(1);
- L1465 C18: new ProbeTetraData :: list.Add(new ProbeTetraData
- L1467 C23: new Vector4 :: Indices = new Vector4(a, b, c, d)
- L1484 C29: new XRDataBuffer :: _probeTetraBuffer = new XRDataBuffer("LightProbeTetra", EBufferTarget.ShaderStorageBuffer, (uint)tetraList.Count, EComponentType.Struct, (uint)Marshal.SizeOf<ProbeTetraData>(), false, false)
- L1551 C29: new StencilTestFace :: stencil.FrontFace = new StencilTestFace()
- L1561 C28: new StencilTestFace :: stencil.BackFace = new StencilTestFace()


## XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.FBOs.cs
- L53 C29: new RenderingParameters :: RenderOptions = new RenderingParameters()
- L55 C29: new DepthTest :: DepthTest = new DepthTest()
- L63 C30: new XRQuadFrameBuffer :: var PostProcessFBO = new XRQuadFrameBuffer(postProcessMat);
- L72 C19: new InvalidOperationException :: throw new InvalidOperationException("Post-process output texture must be FBO attachable.");
- L74 C16: new XRFrameBuffer :: return new XRFrameBuffer((attach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
- L84 C19: new InvalidOperationException :: throw new InvalidOperationException("TransformId debug output texture must be FBO attachable.");
- L86 C16: new XRFrameBuffer :: return new XRFrameBuffer((attach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
- L98 C29: new RenderingParameters :: RenderOptions = new RenderingParameters()
- L100 C29: new DepthTest :: DepthTest = new DepthTest()
- L107 C19: new XRQuadFrameBuffer :: var fbo = new XRQuadFrameBuffer(mat)
- L122 C29: new RenderingParameters :: RenderOptions = new RenderingParameters()
- L124 C29: new DepthTest :: DepthTest = new DepthTest()
- L137 C19: new InvalidOperationException :: throw new InvalidOperationException("FXAA output texture is not an FBO-attachable texture.");
- L162 C17: new ShaderFloat :: new ShaderFloat(1.0f, "BloomIntensity"),
- L163 C17: new ShaderFloat :: new ShaderFloat(1.0f, "BloomThreshold"),
- L164 C17: new ShaderFloat :: new ShaderFloat(0.5f, "SoftKnee"),
- L165 C17: new ShaderVector3 :: new ShaderVector3(Engine.Rendering.Settings.DefaultLuminance, "Luminance")
- L170 C29: new RenderingParameters :: RenderOptions = new RenderingParameters()
- L172 C29: new() :: DepthTest = new()
- L181 C19: new XRQuadFrameBuffer :: var fbo = new XRQuadFrameBuffer(brightMat, false);
- L185 C19: new InvalidOperationException :: throw new InvalidOperationException("HDR Scene texture is not an FBO-attachable texture.");
- L188 C19: new InvalidOperationException :: throw new InvalidOperationException("Depth/Stencil texture is not an FBO-attachable texture.");
- L226 C19: new InvalidOperationException :: throw new InvalidOperationException("AO intensity texture must be FBO-attachable.");
- L228 C16: new XRFrameBuffer :: return new XRFrameBuffer((aoAttach, EFrameBufferAttachment.ColorAttachment0, 0, -1))
- L239 C29: new RenderingParameters :: RenderOptions = new RenderingParameters()
- L241 C29: new DepthTest :: DepthTest = new DepthTest()
- L289 C19: new InvalidOperationException :: throw new InvalidOperationException("Velocity texture is not an FBO-attachable texture.");
- L292 C19: new InvalidOperationException :: throw new InvalidOperationException("Depth/Stencil texture is not an FBO-attachable texture.");
- L294 C16: new XRFrameBuffer :: return new XRFrameBuffer(
- L305 C19: new InvalidOperationException :: throw new InvalidOperationException("History color texture is not an FBO-attachable texture.");
- L308 C19: new InvalidOperationException :: throw new InvalidOperationException("History depth texture is not an FBO-attachable texture.");
- L310 C16: new XRFrameBuffer :: return new XRFrameBuffer(
- L321 C19: new InvalidOperationException :: throw new InvalidOperationException("Temporal color input texture is not FBO attachable.");
- L323 C16: new XRFrameBuffer :: return new XRFrameBuffer((colorAttachment, EFrameBufferAttachment.ColorAttachment0, 0, -1))
- L344 C29: new RenderingParameters :: RenderOptions = new RenderingParameters()
- L346 C29: new() :: DepthTest = new()
- L355 C19: new XRQuadFrameBuffer :: var fbo = new XRQuadFrameBuffer(material) { Name = TemporalAccumulationFBOName };
- L358 C19: new InvalidOperationException :: throw new InvalidOperationException("HDR scene texture is not FBO attachable.");
- L361 C19: new InvalidOperationException :: throw new InvalidOperationException("Temporal exposure texture is not FBO attachable.");
- L374 C19: new InvalidOperationException :: throw new InvalidOperationException("Motion blur texture is not FBO attachable.");
- L376 C16: new XRFrameBuffer :: return new XRFrameBuffer((attachment, EFrameBufferAttachment.ColorAttachment0, 0, -1))
- L392 C29: new RenderingParameters :: RenderOptions = new RenderingParameters()
- L394 C29: new() :: DepthTest = new()
- L403 C19: new XRQuadFrameBuffer :: var fbo = new XRQuadFrameBuffer(material) { Name = MotionBlurFBOName };
- L411 C19: new InvalidOperationException :: throw new InvalidOperationException("Depth of field texture is not FBO attachable.");
- L413 C16: new XRFrameBuffer :: return new XRFrameBuffer((attachment, EFrameBufferAttachment.ColorAttachment0, 0, -1))
- L428 C29: new RenderingParameters :: RenderOptions = new RenderingParameters()
- L430 C29: new() :: DepthTest = new()
- L440 C19: new XRQuadFrameBuffer :: var fbo = new XRQuadFrameBuffer(material) { Name = DepthOfFieldFBOName };
- L448 C19: new InvalidOperationException :: throw new InvalidOperationException("History exposure texture is not FBO attachable.");
- L450 C16: new XRFrameBuffer :: return new XRFrameBuffer((attachment, EFrameBufferAttachment.ColorAttachment0, 0, -1))
- L467 C29: new RenderingParameters :: RenderOptions = new RenderingParameters()
- L469 C29: new() :: DepthTest = new()
- L482 C16: new XRQuadFrameBuffer :: return new XRQuadFrameBuffer(material, false) { Name = DepthPreloadFBOName };
- L501 C29: new RenderingParameters :: RenderOptions = new RenderingParameters()
- L503 C29: new() :: DepthTest = new()
- L513 C31: new XRQuadFrameBuffer :: var lightCombineFBO = new XRQuadFrameBuffer(lightCombineMat) { Name = LightCombineFBOName };
- L518 C19: new InvalidOperationException :: throw new InvalidOperationException("Diffuse texture is not an FBO-attachable texture.");
- L528 C35: new() :: BlendMode additiveBlend = new()
- L540 C29: new RenderingParameters :: RenderOptions = new RenderingParameters()
- L542 C29: new DepthTest :: DepthTest = new DepthTest()
- L551 C19: new XRQuadFrameBuffer :: var fbo = new XRQuadFrameBuffer(restirCompositeMaterial) { Name = RestirCompositeFBOName };
- L560 C35: new() :: BlendMode additiveBlend = new()
- L573 C29: new RenderingParameters :: RenderOptions = new RenderingParameters()
- L575 C29: new DepthTest :: DepthTest = new DepthTest()
- L585 C19: new XRQuadFrameBuffer :: var fbo = new XRQuadFrameBuffer(material) { Name = SurfelGICompositeFBOName };
- L594 C35: new() :: BlendMode additiveBlend = new()
- L607 C29: new RenderingParameters :: RenderOptions = new RenderingParameters()
- L609 C29: new DepthTest :: DepthTest = new DepthTest()
- L619 C19: new XRQuadFrameBuffer :: var fbo = new XRQuadFrameBuffer(material) { Name = LightVolumeCompositeFBOName };
- L630 C35: new() :: BlendMode additiveBlend = new()
- L643 C29: new RenderingParameters :: RenderOptions = new RenderingParameters()
- L645 C29: new DepthTest :: DepthTest = new DepthTest()
- L656 C19: new XRQuadFrameBuffer :: var fbo = new XRQuadFrameBuffer(material) { Name = RadianceCascadeCompositeFBOName };


## XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.PostProcessing.cs
- L235 C13: new Vector3 :: new Vector3(0.299f, 0.587f, 0.114f),
- L906 C13: new Vector3 :: new Vector3(0.5f, 0.5f, 0.5f),
- L914 C43: new PostProcessEnumOption :: PostProcessEnumOption[] options = new PostProcessEnumOption[values.Length];
- L919 C26: new PostProcessEnumOption :: options[i] = new PostProcessEnumOption(value.ToString(), Convert.ToInt32(value));
- L966 C22: new VignetteSettings :: (vignette ?? new VignetteSettings()).SetUniforms(program);
- L969 C19: new ColorGradingSettings :: (color ?? new ColorGradingSettings()).SetUniforms(program);
- L972 C20: new ChromaticAberrationSettings :: (chroma ?? new ChromaticAberrationSettings()).SetUniforms(program);
- L975 C17: new FogSettings :: (fog ?? new FogSettings()).SetUniforms(program);
- L998 C42: new Vector2 :: distortionCenterUv = new Vector2(
- L1004 C18: new LensDistortionSettings :: (lens ?? new LensDistortionSettings()).SetUniforms(program, cameraFov, aspectRatio, distortionCenterUv);
- L1007 C19: new BloomSettings :: (bloom ?? new BloomSettings()).SetCombineUniforms(program);
- L1027 C25: new Vector2 :: var texelStep = new Vector2(1.0f / width, 1.0f / height);
- L1041 C25: new Vector2 :: var texelSize = new Vector2(1.0f / width, 1.0f / height);
- L1064 C25: new Vector2 :: var texelSize = new Vector2(1.0f / width, 1.0f / height);
- L1090 C42: new Vector2 :: program.Uniform("TexelSize", new Vector2(1.0f / width, 1.0f / height));


## XRENGINE/Rendering/Pipelines/Types/DefaultRenderPipeline.Textures.cs
- L59 C20: new XRTexture2DArrayView :: return new XRTexture2DArrayView(
- L73 C20: new XRTexture2DView :: return new XRTexture2DView(
- L90 C20: new XRTexture2DArrayView :: return new XRTexture2DArrayView(
- L104 C20: new XRTexture2DView :: return new XRTexture2DView(
- L159 C20: new XRTexture2DArrayView :: return new XRTexture2DArrayView(
- L173 C20: new XRTexture2DView :: return new XRTexture2DView(


## XRENGINE/Rendering/Pipelines/Types/SurfelDebugRenderPipeline.cs
- L52 C70: new() :: private readonly NearToFarRenderCommandSorter _nearToFarSorter = new();
- L53 C70: new() :: private readonly FarToNearRenderCommandSorter _farToNearSorter = new();
- L126 C12: new() :: => new()
- L198 C41: new Dictionary :: visualizationSwitch.Cases = new Dictionary<int, ViewportRenderCommandContainer>
- L215 C38: new Dictionary :: outputChoice.Cases = new Dictionary<int, ViewportRenderCommandContainer>
- L262 C16: new ViewportRenderCommandContainer :: return new ViewportRenderCommandContainer(this);
- L267 C17: new ViewportRenderCommandContainer :: var c = new ViewportRenderCommandContainer(this);
- L277 C17: new ViewportRenderCommandContainer :: var c = new ViewportRenderCommandContainer(this);
- L292 C17: new ViewportRenderCommandContainer :: var c = new ViewportRenderCommandContainer(this);
- L307 C17: new ViewportRenderCommandContainer :: var c = new ViewportRenderCommandContainer(this);
- L314 C17: new ViewportRenderCommandContainer :: var c = new ViewportRenderCommandContainer(this);
- L321 C17: new ViewportRenderCommandContainer :: var c = new ViewportRenderCommandContainer(this);
- L399 C16: new XRTexture2DView :: return new XRTexture2DView(
- L488 C19: new InvalidOperationException :: throw new InvalidOperationException("Albedo texture must be FBO-attachable.");
- L491 C19: new InvalidOperationException :: throw new InvalidOperationException("Normal texture must be FBO-attachable.");
- L494 C19: new InvalidOperationException :: throw new InvalidOperationException("TransformId texture must be FBO-attachable.");
- L497 C19: new InvalidOperationException :: throw new InvalidOperationException("Depth/Stencil texture must be FBO-attachable.");
- L499 C16: new XRFrameBuffer :: return new XRFrameBuffer(
- L512 C19: new InvalidOperationException :: throw new InvalidOperationException("HDR scene texture must be FBO-attachable.");
- L515 C19: new InvalidOperationException :: throw new InvalidOperationException("Depth/Stencil texture must be FBO-attachable.");
- L520 C29: new RenderingParameters :: RenderOptions = new RenderingParameters()
- L522 C29: new DepthTest :: DepthTest = new DepthTest()
- L529 C19: new XRQuadFrameBuffer :: var fbo = new XRQuadFrameBuffer(mat)
- L546 C29: new RenderingParameters :: RenderOptions = new RenderingParameters()
- L548 C29: new DepthTest :: DepthTest = new DepthTest()
- L555 C19: new XRQuadFrameBuffer :: var fbo = new XRQuadFrameBuffer(mat)
- L592 C29: new RenderingParameters :: RenderOptions = new RenderingParameters()
- L594 C29: new DepthTest :: DepthTest = new DepthTest()
- L601 C19: new XRQuadFrameBuffer :: var fbo = new XRQuadFrameBuffer(mat)
- L617 C29: new RenderingParameters :: RenderOptions = new RenderingParameters()
- L619 C29: new DepthTest :: DepthTest = new DepthTest()
- L626 C19: new XRQuadFrameBuffer :: var fbo = new XRQuadFrameBuffer(mat)


## XRENGINE/Rendering/Pipelines/Types/TestRenderPipeline.cs
- L11 C70: new() :: private readonly FarToNearRenderCommandSorter _farToNearSorter = new();
- L21 C12: new() :: => new() { { (int)EDefaultRenderPass.OpaqueForward, _farToNearSorter }, };
- L52 C32: new ColorF4 :: ClearColor(new ColorF4(0.0f, 0.0f, 0.0f, 1.0f));
- L69 C32: new ColorF4 :: ClearColor(new ColorF4(0.0f, 0.0f, 0.0f, 1.0f));
- L123 C29: new RenderingParameters :: RenderOptions = new RenderingParameters()
- L125 C29: new DepthTest :: DepthTest = new DepthTest()
- L133 C19: new XRQuadFrameBuffer :: var fbo = new XRQuadFrameBuffer(mat);


## XRENGINE/Rendering/Pipelines/Types/UserInterfaceRenderPipeline.cs
- L14 C70: new() :: private readonly NearToFarRenderCommandSorter _nearToFarSorter = new();
- L15 C70: new() :: private readonly FarToNearRenderCommandSorter _farToNearSorter = new();
- L18 C12: new() :: => new()
- L131 C12: new XRTexture2DView :: => new XRTexture2DView(
- L142 C12: new XRTexture2DView :: => new XRTexture2DView(


## XRENGINE/Rendering/Pipelines/XRRenderPipeline.cs
- L99 C51: new() :: RenderPassMetadataCollection collection = new();
- L149 C30: new InvalidOperationException :: => TryState ?? throw new InvalidOperationException("Rendering pipeline state is not available.");
- L322 C29: new() :: RenderOptions = new()
- L324 C29: new() :: DepthTest = new()
- L335 C55: new IVector2 :: BoundingRectangle region = new(IVector2.Zero, new IVector2((int)width, (int)height));


## XRENGINE/Rendering/Pipelines/XRRenderPipelineInstance.cs
- L42 C66: new() :: public RenderCommandCollection MeshRenderCommands { get; } = new();
- L44 C56: new() :: public RenderResourceRegistry Resources { get; } = new();
- L103 C20: new List :: var tags = new List<string>(2);
- L239 C58: new() :: public RenderingState CollectVisibleState { get; } = new();
- L240 C50: new() :: public RenderingState RenderState { get; } = new();


## XRENGINE/Rendering/PostProcessing/CameraPostProcessStateCollection.cs
- L21 C46: new() :: private readonly object _pipelinesSync = new();
- L22 C69: new() :: private Dictionary<Guid, PipelinePostProcessState> _pipelines = new();
- L34 C25: new PipelinePostProcessState :: state = new PipelinePostProcessState();
- L68 C30: new() :: _pipelines = new();
- L72 C26: new Dictionary :: _pipelines = new Dictionary<Guid, PipelinePostProcessState>(_pipelines);
- L83 C43: new() :: private readonly object _stagesSync = new();
- L148 C25: new PostProcessStageState :: stage = new PostProcessStageState();
- L168 C23: new Dictionary :: _stages = new Dictionary<string, PostProcessStageState>(_stages, StringComparer.OrdinalIgnoreCase);
- L181 C44: new() :: private readonly object _backingSync = new();
- L242 C31: new HashSet :: var knownParameters = new HashSet<string>(descriptor.Parameters.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
- L276 C26: new Dictionary :: var backingMap = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);
- L347 C21: new Vector3 :: value = new Vector3(color.R, color.G, color.B);
- L404 C30: new Vector2 :: result = new Vector2(v3.X, v3.Y);
- L416 C38: new Vector3 :: ColorF3 color => new Vector3(color.R, color.G, color.B),
- L432 C30: new Vector4 :: result = new Vector4(v3, 0.0f);
- L444 C34: new ColorF3 :: Vector3 v => new ColorF3(v.X, v.Y, v.Z),
- L486 C23: new Dictionary :: _values = new Dictionary<string, object?>(_values, StringComparer.OrdinalIgnoreCase);


## XRENGINE/Rendering/PostProcessing/RenderPipelinePostProcessSchema.cs
- L76 C9: new Dictionary :: new Dictionary<string, PostProcessStageDescriptor>(StringComparer.Ordinal),
- L79 C101: new Dictionary :: public IReadOnlyDictionary<string, PostProcessStageDescriptor> StagesByKey { get; } = stages ?? new Dictionary<string, PostProcessStageDescriptor>(StringComparer.Ordinal);


## XRENGINE/Rendering/PostProcessing/RenderPipelinePostProcessSchemaBuilder.cs
- L20 C19: new ArgumentException :: throw new ArgumentException("Stage key cannot be empty.", nameof(key));
- L24 C26: new StageDefinition :: definition = new StageDefinition(key, displayName ?? key);
- L32 C16: new PostProcessStageBuilder :: return new PostProcessStageBuilder(definition);
- L38 C19: new ArgumentException :: throw new ArgumentException("Category key cannot be empty.", nameof(key));
- L42 C26: new CategoryDefinition :: definition = new CategoryDefinition(key, displayName ?? key);
- L50 C16: new PostProcessCategoryBuilder :: return new PostProcessCategoryBuilder(definition);
- L76 C35: new List :: var categoryDescriptors = new List<PostProcessCategoryDescriptor>(_categories.Count);
- L82 C37: new PostProcessCategoryDescriptor :: categoryDescriptors.Add(new PostProcessCategoryDescriptor(category.Key, category.DisplayName, category.Description, stageKeys));
- L89 C37: new PostProcessCategoryDescriptor :: categoryDescriptors.Add(new PostProcessCategoryDescriptor("default", "Post Processing", null, orderedKeys));
- L92 C16: new RenderPipelinePostProcessSchema :: return new RenderPipelinePostProcessSchema(stageDescriptors, categoryDescriptors);
- L97 C59: new() :: List<PostProcessParameterDescriptor> parameters = new();
- L119 C32: new PostProcessParameterDescriptor :: parameters.Add(new PostProcessParameterDescriptor(
- L137 C28: new PostProcessParameterDescriptor :: parameters.Add(new PostProcessParameterDescriptor(
- L155 C16: new PostProcessStageDescriptor :: return new PostProcessStageDescriptor(definition.Key, definition.DisplayName, parameters, definition.BackingType);
- L200 C76: new() :: public List<CustomParameterDefinition> CustomParameters { get; } = new();
- L204 C50: new ArgumentNullException :: => _shaderFactory = factory ?? throw new ArgumentNullException(nameof(factory));
- L218 C50: new() :: public List<string> StageKeys { get; } = new();
- L253 C87: new() :: public PostProcessStageBuilder BackedBy<TSettings>() where TSettings : class, new()
- L287 C33: new UniformCustomization :: customization = new UniformCustomization();
- L298 C33: new UniformCustomization :: customization = new UniformCustomization();
- L309 C33: new UniformCustomization :: customization = new UniformCustomization();
- L322 C33: new UniformCustomization :: customization = new UniformCustomization();
- L333 C33: new UniformCustomization :: customization = new UniformCustomization();
- L352 C23: new ArgumentException :: throw new ArgumentException("Parameter name cannot be empty.", nameof(name));
- L354 C46: new CustomParameterDefinition :: _definition.CustomParameters.Add(new CustomParameterDefinition


## XRENGINE/Rendering/RenderGraph/RenderGraphDescribeContext.cs
- L11 C64: new() :: private readonly Stack<RenderTargetBinding> _targetStack = new();
- L12 C70: new() :: private readonly Dictionary<string, int> _syntheticPassIndices = new();
- L29 C27: new RenderTargetBinding :: _targetStack.Push(new RenderTargetBinding(name!, writes, clearColor && writes, clearDepth && writes, clearStencil && writes));


## XRENGINE/Rendering/RenderGraph/RenderPassMetadata.cs
- L94 C70: new() :: private readonly List<RenderPassResourceUsage> _resourceUsages = new();
- L95 C59: new() :: private readonly HashSet<int> _explicitDependencies = new();
- L200 C28: new RenderPassResourceUsage :: _metadata.AddUsage(new RenderPassResourceUsage(resourceName, type, access, load, store));
- L210 C68: new() :: private readonly Dictionary<int, RenderPassMetadata> _passes = new();
- L216 C24: new RenderPassMetadata :: metadata = new RenderPassMetadata(passIndex, name ?? $"Pass{passIndex}", stage);
- L226 C16: new RenderPassBuilder :: return new RenderPassBuilder(metadata);
- L230 C12: new ReadOnlyCollection :: => new ReadOnlyCollection<RenderPassMetadata>(_passes.Values.OrderBy(p => p.PassIndex).ToList());


## XRENGINE/Rendering/Resources/RenderResourceRegistry.cs
- L71 C16: new TextureResourceDescriptor :: return new TextureResourceDescriptor(
- L130 C33: new FrameBufferAttachmentDescriptor :: attachments.Add(new FrameBufferAttachmentDescriptor(resourceName, attachment, mipLevel, layerIndex));
- L134 C16: new FrameBufferResourceDescriptor :: return new FrameBufferResourceDescriptor(
- L214 C22: new RenderTextureResource :: record = new RenderTextureResource(descriptor);
- L231 C22: new RenderFrameBufferResource :: record = new RenderFrameBufferResource(descriptor);
- L245 C45: new InvalidOperationException :: string name = texture.Name ?? throw new InvalidOperationException("Texture name must be set before binding to the registry.");
- L257 C49: new InvalidOperationException :: string name = frameBuffer.Name ?? throw new InvalidOperationException("FrameBuffer name must be set before binding to the registry.");


## XRENGINE/Rendering/RootNodeCollection.cs
- L24 C28: new SceneNode :: var node = new SceneNode(name);


## XRENGINE/Rendering/Shaders/ShaderSnippets.cs
- L41 C48: new() :: private static readonly object _scanLock = new();
- L159 C30: new HashSet :: resolvedSnippets ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
- L167 C22: new StringBuilder :: var result = new StringBuilder(source.Length * 2);


## XRENGINE/Rendering/Tools/OctahedralImposterGenerator.cs
- L159 C19: new XRTexture2D :: ? new XRTexture2D(settings.SheetSize, settings.SheetSize, EPixelInternalFormat.DepthComponent32f, EPixelFormat.DepthComponent, EPixelType.Float, false)
- L184 C20: new Result :: return new Result(viewArray, depthTexture, captureBounds, s_captureDirections);
- L199 C22: new() :: AABB total = new();
- L207 C22: new() :: AABB total = new();
- L242 C15: new XRFrameBuffer :: ? new XRFrameBuffer((colorArray, EFrameBufferAttachment.ColorAttachment0, 0, viewIndex))
- L243 C15: new XRFrameBuffer :: : new XRFrameBuffer(
- L268 C33: new CameraPostProcessStateCollection :: PostProcessStates = new CameraPostProcessStateCollection(),
- L276 C30: new DefaultRenderPipeline :: RenderPipeline = new DefaultRenderPipeline(),
- L348 C54: new Vector3 :: directions.Add(Vector3.Normalize(new Vector3(xSign, ySign, zSign)));


## XRENGINE/Rendering/UI/Flyleaf/PlayerGL.cs
- L25 C19: new Player :: _player = new Player(config);
- L26 C18: new VideoGL :: _video = new VideoGL(_player.Video);


## XRENGINE/Rendering/UI/ImGuiContextTracker.cs
- L10 C50: new() :: private static readonly object ContextLock = new();


## XRENGINE/Rendering/UI/ImGuiControllerUtilities.cs
- L110 C19: new DirectoryInfo :: var dir = new DirectoryInfo(AppContext.BaseDirectory);
- L119 C15: new DirectoryInfo :: dir = new DirectoryInfo(Environment.CurrentDirectory);


## XRENGINE/Rendering/Vertex/Vertex.cs
- L89 C24: new HashCode :: var hash = new HashCode();
- L101 C16: new() :: => new()
- L103 C52: new Dictionary :: Weights = Weights is null ? null : new Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>(Weights),
- L141 C32: new() :: Matrix4x4 matrix = new();
- L153 C32: new() :: Matrix4x4 matrix = new();
- L179 C24: new() :: Vertex v = new()
- L195 C48: new Vector2 :: v.TextureCoordinateSets = [new Vector2(uv.X, uv.Y)];
- L197 C49: new Vector2 :: v.TextureCoordinateSets.Add(new Vector2(uv.X, uv.Y));
- L221 C39: new() :: VertexData data = new()
- L240 C59: new Vector2 :: data.TextureCoordinateSets = [new Vector2(uv.X, uv.Y)];
- L242 C60: new Vector2 :: data.TextureCoordinateSets.Add(new Vector2(uv.X, uv.Y));


## XRENGINE/Rendering/Vertex/VertexLineStrip.cs
- L18 C34: new VertexLine :: VertexLine[] lines = new VertexLine[count];
- L22 C28: new VertexLine :: lines[i] = new VertexLine(_vertices[i], next);


## XRENGINE/Rendering/Vertex/VertexPolygon.cs
- L9 C23: new InvalidOperationException :: throw new InvalidOperationException("Not enough vertices for a polygon.");
- L14 C23: new InvalidOperationException :: throw new InvalidOperationException("Not enough vertices for a polygon.");
- L35 C37: new VertexTriangle :: VertexTriangle[] list = new VertexTriangle[triangleCount];
- L37 C27: new VertexTriangle :: list[i] = new VertexTriangle(
- L46 C34: new VertexLine :: VertexLine[] lines = new VertexLine[Vertices.Count];
- L49 C28: new VertexLine :: lines[i] = new VertexLine(Vertices[i].HardCopy(), Vertices[i + 1].HardCopy());
- L51 C41: new VertexLine :: lines[Vertices.Count - 1] = new VertexLine(Vertices[^1].HardCopy(), Vertices[0].HardCopy());


## XRENGINE/Rendering/Vertex/VertexPrimitive.cs
- L24 C20: new AABB :: return new AABB(XRMath.ComponentMin(positions), XRMath.ComponentMax(positions));


## XRENGINE/Rendering/Vertex/VertexQuad.cs
- L52 C24: new VertexQuad :: return new VertexQuad(
- L53 C21: new Vertex :: new Vertex(bottomLeft,  new Vector2(0.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
- L53 C45: new Vector2 :: new Vertex(bottomLeft,  new Vector2(0.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
- L54 C21: new Vertex :: new Vertex(bottomRight, new Vector2(1.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
- L54 C45: new Vector2 :: new Vertex(bottomRight, new Vector2(1.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
- L55 C21: new Vertex :: new Vertex(topRight,    new Vector2(1.0f, flipVerticalUVCoord ? 0.0f : 1.0f)),
- L55 C45: new Vector2 :: new Vertex(topRight,    new Vector2(1.0f, flipVerticalUVCoord ? 0.0f : 1.0f)),
- L56 C21: new Vertex :: new Vertex(topLeft,     new Vector2(0.0f, flipVerticalUVCoord ? 0.0f : 1.0f)));
- L56 C45: new Vector2 :: new Vertex(topLeft,     new Vector2(0.0f, flipVerticalUVCoord ? 0.0f : 1.0f)));
- L62 C17: new Vertex :: new Vertex(bottomLeft,  normal, new Vector2(0.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
- L62 C49: new Vector2 :: new Vertex(bottomLeft,  normal, new Vector2(0.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
- L63 C17: new Vertex :: new Vertex(bottomRight, normal, new Vector2(1.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
- L63 C49: new Vector2 :: new Vertex(bottomRight, normal, new Vector2(1.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
- L64 C17: new Vertex :: new Vertex(topRight,    normal, new Vector2(1.0f, flipVerticalUVCoord ? 0.0f : 1.0f)),
- L64 C49: new Vector2 :: new Vertex(topRight,    normal, new Vector2(1.0f, flipVerticalUVCoord ? 0.0f : 1.0f)),
- L65 C17: new Vertex :: new Vertex(topLeft,     normal, new Vector2(0.0f, flipVerticalUVCoord ? 0.0f : 1.0f)));
- L65 C49: new Vector2 :: new Vertex(topLeft,     normal, new Vector2(0.0f, flipVerticalUVCoord ? 0.0f : 1.0f)));
- L209 C27: new ArgumentOutOfRangeException :: throw new ArgumentOutOfRangeException(nameof(cubeMapFace), cubeMapFace, null);
- L230 C20: new VertexQuad :: return new VertexQuad(
- L231 C17: new Vertex :: new Vertex(bottomLeft, normal, bottomLeftUV),
- L232 C17: new Vertex :: new Vertex(bottomRight, normal, bottomRightUV),
- L233 C17: new Vertex :: new Vertex(topRight, normal, topRightUV),
- L234 C17: new Vertex :: new Vertex(topLeft, normal, topLeftUV));
- L244 C20: new VertexQuad :: return new VertexQuad(
- L245 C17: new Vertex :: new Vertex(bottomLeft,  bottomLeftInf,  normal, new Vector2(0.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
- L245 C65: new Vector2 :: new Vertex(bottomLeft,  bottomLeftInf,  normal, new Vector2(0.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
- L246 C17: new Vertex :: new Vertex(bottomRight, bottomRightInf, normal, new Vector2(1.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
- L246 C65: new Vector2 :: new Vertex(bottomRight, bottomRightInf, normal, new Vector2(1.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
- L247 C17: new Vertex :: new Vertex(topRight,    topRightInf,    normal, new Vector2(1.0f, flipVerticalUVCoord ? 0.0f : 1.0f)),
- L247 C65: new Vector2 :: new Vertex(topRight,    topRightInf,    normal, new Vector2(1.0f, flipVerticalUVCoord ? 0.0f : 1.0f)),
- L248 C17: new Vertex :: new Vertex(topLeft,     topLeftInf,     normal, new Vector2(0.0f, flipVerticalUVCoord ? 0.0f : 1.0f)));
- L248 C65: new Vector2 :: new Vertex(topLeft,     topLeftInf,     normal, new Vector2(0.0f, flipVerticalUVCoord ? 0.0f : 1.0f)));
- L257 C20: new VertexQuad :: return new VertexQuad(
- L258 C17: new Vertex :: new Vertex(bottomLeft,  bottomLeftInf,  bottomLeftNormal,   new Vector2(0.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
- L258 C77: new Vector2 :: new Vertex(bottomLeft,  bottomLeftInf,  bottomLeftNormal,   new Vector2(0.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
- L259 C17: new Vertex :: new Vertex(bottomRight, bottomRightInf, bottomRightNormal,  new Vector2(1.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
- L259 C77: new Vector2 :: new Vertex(bottomRight, bottomRightInf, bottomRightNormal,  new Vector2(1.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
- L260 C17: new Vertex :: new Vertex(topRight,    topRightInf,    topRightNormal,     new Vector2(1.0f, flipVerticalUVCoord ? 0.0f : 1.0f)),
- L260 C77: new Vector2 :: new Vertex(topRight,    topRightInf,    topRightNormal,     new Vector2(1.0f, flipVerticalUVCoord ? 0.0f : 1.0f)),
- L261 C17: new Vertex :: new Vertex(topLeft,     topLeftInf,     topLeftNormal,      new Vector2(0.0f, flipVerticalUVCoord ? 0.0f : 1.0f)));
- L261 C77: new Vector2 :: new Vertex(topLeft,     topLeftInf,     topLeftNormal,      new Vector2(0.0f, flipVerticalUVCoord ? 0.0f : 1.0f)));
- L274 C24: new VertexQuad :: return new VertexQuad(
- L275 C21: new Vertex :: new Vertex(bottomLeft,  bottomLeftInf,  normal, new Vector2(0.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
- L275 C69: new Vector2 :: new Vertex(bottomLeft,  bottomLeftInf,  normal, new Vector2(0.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
- L276 C21: new Vertex :: new Vertex(bottomRight, bottomRightInf, normal, new Vector2(1.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
- L276 C69: new Vector2 :: new Vertex(bottomRight, bottomRightInf, normal, new Vector2(1.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
- L277 C21: new Vertex :: new Vertex(topRight,    topRightInf,    normal, new Vector2(1.0f, flipVerticalUVCoord ? 0.0f : 1.0f)),
- L277 C69: new Vector2 :: new Vertex(topRight,    topRightInf,    normal, new Vector2(1.0f, flipVerticalUVCoord ? 0.0f : 1.0f)),
- L278 C21: new Vertex :: new Vertex(topLeft,     topLeftInf,     normal, new Vector2(0.0f, flipVerticalUVCoord ? 0.0f : 1.0f)));
- L278 C69: new Vector2 :: new Vertex(topLeft,     topLeftInf,     normal, new Vector2(0.0f, flipVerticalUVCoord ? 0.0f : 1.0f)));
- L281 C24: new VertexQuad :: return new VertexQuad(
- L282 C21: new Vertex :: new Vertex(bottomLeft,  bottomLeftInf,  new Vector2(0.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
- L282 C61: new Vector2 :: new Vertex(bottomLeft,  bottomLeftInf,  new Vector2(0.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
- L283 C21: new Vertex :: new Vertex(bottomRight, bottomRightInf, new Vector2(1.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
- L283 C61: new Vector2 :: new Vertex(bottomRight, bottomRightInf, new Vector2(1.0f, flipVerticalUVCoord ? 1.0f : 0.0f)),
- L284 C21: new Vertex :: new Vertex(topRight,    topRightInf,    new Vector2(1.0f, flipVerticalUVCoord ? 0.0f : 1.0f)),
- L284 C61: new Vector2 :: new Vertex(topRight,    topRightInf,    new Vector2(1.0f, flipVerticalUVCoord ? 0.0f : 1.0f)),
- L285 C21: new Vertex :: new Vertex(topLeft,     topLeftInf,     new Vector2(0.0f, flipVerticalUVCoord ? 0.0f : 1.0f)));
- L285 C61: new Vector2 :: new Vertex(topLeft,     topLeftInf,     new Vector2(0.0f, flipVerticalUVCoord ? 0.0f : 1.0f)));
- L322 C17: new Vector3 :: new Vector3(region.BottomLeft.X, 0.0f, region.BottomLeft.Y),
- L323 C17: new Vector3 :: new Vector3(region.BottomRight.X, 0.0f, region.BottomRight.Y),
- L324 C17: new Vector3 :: new Vector3(region.TopRight.X, 0.0f, region.TopRight.Y),
- L325 C17: new Vector3 :: new Vector3(region.TopLeft.X, 0.0f, region.TopLeft.Y),
- L362 C17: new Vector3 :: new Vector3(region.BottomLeft.X, region.BottomLeft.Y, 0.0f),
- L363 C17: new Vector3 :: new Vector3(region.BottomRight.X, region.BottomRight.Y, 0.0f),
- L364 C17: new Vector3 :: new Vector3(region.TopRight.X, region.TopRight.Y, 0.0f),
- L365 C17: new Vector3 :: new Vector3(region.TopLeft.X, region.TopLeft.Y, 0.0f),


## XRENGINE/Rendering/Vertex/VertexTriangleStrip.cs
- L10 C42: new VertexTriangle :: VertexTriangle[] triangles = new VertexTriangle[FaceCount];
- L12 C36: new VertexTriangle :: triangles[i - 2] = new VertexTriangle(


## XRENGINE/Rendering/VisualScene.cs
- L15 C48: new() :: public GPUScene GPUCommands { get; } = new();


## XRENGINE/Rendering/VisualScene2D.cs
- L25 C61: new Quadtree :: public Quadtree<RenderInfo2D> RenderTree { get; } = new Quadtree<RenderInfo2D>(new BoundingRectangleF());
- L25 C88: new BoundingRectangleF :: public Quadtree<RenderInfo2D> RenderTree { get; } = new Quadtree<RenderInfo2D>(new BoundingRectangleF());
- L34 C50: new Vector3 :: => Engine.Rendering.Debug.RenderQuad(new Vector3(center, 0.0f) + AbstractRenderer.UIPositionBias, AbstractRenderer.UIRotation, extents, false, color);
- L88 C110: new() :: private readonly ConcurrentQueue<(RenderInfo2D renderable, bool add)> _pendingRenderableOperations = new(); // staged until GlobalPreRender runs on the render thread


## XRENGINE/Rendering/VisualScene3D.cs
- L26 C59: new Octree :: public Octree<RenderInfo3D> RenderTree { get; } = new Octree<RenderInfo3D>(new AABB());
- L26 C84: new AABB :: public Octree<RenderInfo3D> RenderTree { get; } = new Octree<RenderInfo3D>(new AABB());
- L31 C60: new() :: public BvhRaycastDispatcher BvhRaycasts { get; } = new();
- L112 C110: new() :: private readonly ConcurrentQueue<(RenderInfo3D renderable, bool add)> _pendingRenderableOperations = new(); // staged until GlobalPreRender runs on the render thread
- L114 C67: new() :: private readonly HashSet<RenderableMesh> _skinnedMeshes = new();
- L158 C50: new() :: HashSet<XRMeshRenderer> dispatched = new();


## XRENGINE/Rendering/XeSS/IntelXessManager.cs
- L17 C94: new() :: private static readonly ConcurrentDictionary<XRViewport, float> _lastViewportScale = new();


## XRENGINE/Rendering/XRMeshRenderer.cs
- L207 C31: new SubMesh :: Submeshes.Add(new SubMesh() { Mesh = mesh, Material = material, InstanceCount = 1 });
- L214 C50: new Version :: GeneratedVertexShaderVersions.Add(0, new Version<DefaultVertexShaderGenerator>(this, NoSpecialExtensions, true));
- L215 C50: new Version :: GeneratedVertexShaderVersions.Add(1, new Version<OVRMultiViewVertexShaderGenerator>(this, HasOvrMultiView2, false));
- L216 C50: new Version :: GeneratedVertexShaderVersions.Add(2, new Version<NVStereoVertexShaderGenerator>(this, HasNVStereoViewRendering, false));
- L219 C50: new MeshDeformVersion :: GeneratedVertexShaderVersions.Add(3, new MeshDeformVersion(this, NoSpecialExtensions, true));
- L220 C50: new MeshDeformVersion :: GeneratedVertexShaderVersions.Add(4, new MeshDeformVersion(this, HasOvrMultiView2, false) { UseOVRMultiView = true });
- L221 C50: new MeshDeformVersion :: GeneratedVertexShaderVersions.Add(5, new MeshDeformVersion(this, HasNVStereoViewRendering, false) { UseNVStereo = true });
- L246 C33: new OVRMultiViewMeshDeformVertexShaderGenerator :: generator = new OVRMultiViewMeshDeformVertexShaderGenerator(m, maxInfluences, optimizeToVec4);
- L248 C33: new NVStereoMeshDeformVertexShaderGenerator :: generator = new NVStereoMeshDeformVertexShaderGenerator(m, maxInfluences, optimizeToVec4);
- L250 C33: new MeshDeformVertexShaderGenerator :: generator = new MeshDeformVertexShaderGenerator(m, maxInfluences, optimizeToVec4);
- L480 C42: new XRDataBuffer :: IndirectDrawBuffer = new XRDataBuffer(
- L516 C23: new DrawElementsIndirectCommand :: ? new DrawElementsIndirectCommand()
- L524 C23: new DrawElementsIndirectCommand :: : new DrawElementsIndirectCommand()
- L713 C33: new XRDataBuffer :: BlendshapeWeights = new XRDataBuffer($"{ECommonBufferType.BlendshapeWeights}Buffer", EBufferTarget.ShaderStorageBuffer, blendshapeCount.Align(4), EComponentType.Float, 1, false, false)
- L921 C22: new RenderBone :: _bones = new RenderBone[boneCount];
- L927 C26: new RenderBone :: var rb = new RenderBone(tfm, invBindWorldMtx, boneIndex);
- L1037 C39: new XRDataBuffer :: DeformerPositionsBuffer = new XRDataBuffer(
- L1050 C43: new XRDataBuffer :: DeformerRestPositionsBuffer = new XRDataBuffer(
- L1067 C55: new Vector4 :: DeformerPositionsBuffer.SetVector4(i, new Vector4(pos, 1.0f));
- L1068 C59: new Vector4 :: DeformerRestPositionsBuffer.SetVector4(i, new Vector4(pos, 1.0f));
- L1077 C41: new XRDataBuffer :: DeformerNormalsBuffer = new XRDataBuffer(
- L1093 C57: new Vector4 :: DeformerNormalsBuffer.SetVector4(i, new Vector4(nrm, 0.0f));
- L1102 C42: new XRDataBuffer :: DeformerTangentsBuffer = new XRDataBuffer(
- L1118 C58: new Vector4 :: DeformerTangentsBuffer.SetVector4(i, new Vector4(tan, 0.0f));
- L1142 C45: new XRDataBuffer :: MeshDeformVertexIndicesBuffer = new XRDataBuffer(
- L1155 C45: new XRDataBuffer :: MeshDeformVertexWeightsBuffer = new XRDataBuffer(
- L1205 C72: new IVector4 :: MeshDeformVertexIndicesBuffer.SetDataRawAtIndex(v, new IVector4((int)indices.X, (int)indices.Y, (int)indices.Z, (int)indices.W));
- L1229 C39: new XRDataBuffer :: MeshDeformIndicesBuffer = new XRDataBuffer(
- L1242 C39: new XRDataBuffer :: MeshDeformWeightsBuffer = new XRDataBuffer(
- L1256 C44: new XRDataBuffer :: MeshDeformVertexOffsetBuffer = new XRDataBuffer(
- L1269 C43: new XRDataBuffer :: MeshDeformVertexCountBuffer = new XRDataBuffer(
- L1343 C55: new Vector4 :: DeformerPositionsBuffer.SetVector4(i, new Vector4(pos, 1.0f));
- L1363 C53: new Vector4 :: DeformerNormalsBuffer.SetVector4(i, new Vector4(nrm, 0.0f));
- L1383 C54: new Vector4 :: DeformerTangentsBuffer.SetVector4(i, new Vector4(tan, 0.0f));


## XRENGINE/Rendering/XRViewport.cs
- L29 C45: new() :: private BoundingRectangle _region = new();
- L30 C63: new() :: private BoundingRectangle _internalResolutionRegion = new();
- L365 C69: new() :: private readonly XRRenderPipelineInstance _renderPipeline = new();
- L611 C23: new InvalidOperationException :: throw new InvalidOperationException("No camera is set to this viewport.");
- L616 C52: new Vector2 :: => NormalizedViewportToWorldCoordinate(new Vector2(normalizedViewportPoint.X, normalizedViewportPoint.Y), normalizedViewportPoint.Z);
- L620 C23: new InvalidOperationException :: throw new InvalidOperationException("No camera is set to this viewport.");
- L662 C23: new InvalidOperationException :: throw new InvalidOperationException("No camera is set to this viewport.");
- L673 C24: new Segment :: return new Segment(Vector3.Zero, Vector3.Zero);


## XRENGINE/Rendering/XRWorld.cs
- L16 C43: new() :: private WorldSettings _settings = new();


## XRENGINE/Rendering/XRWorldInstance.cs
- L73 C36: new XRScene :: _editorScene = new XRScene("__EditorScene__")
- L157 C91: new() :: private readonly ConcurrentQueue<PhysicsRaycastRequest> _pendingPhysicsRaycasts = new();
- L158 C94: new() :: private readonly ConcurrentQueue<PhysicsRaycastRequest> _physicsRaycastRequestPool = new();
- L279 C22: new Lights3DCollection :: Lights = new Lights3DCollection(this);
- L342 C102: new() :: private readonly ConcurrentQueue<IAbstractDynamicRigidBody> _pendingMinYPlaneResetRequests = new();
- L740 C99: new() :: private ConcurrentQueue<(TransformBase tfm, Matrix4x4 renderMatrix)> _pushToRenderWrite = new();
- L741 C102: new() :: private ConcurrentQueue<(TransformBase tfm, Matrix4x4 renderMatrix)> _pushToRenderSnapshot = new();
- L864 C25: new HashSet :: var moved = new HashSet<SceneNode>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
- L944 C48: new() :: private readonly Lock _listGroupLock = new();
- L955 C43: new TickList :: dic.Add(order, list = new TickList(Engine.Rendering.Settings.TickGroupedItemsInParallel));
- L1071 C27: new PhysicsRaycastRequest :: request = new PhysicsRaycastRequest();
- L1194 C22: new MeshEdgePickResult :: result = new MeshEdgePickResult(faceHit, bestStart, bestEnd, closest, bestEdgeIndex);
- L1226 C22: new MeshVertexPickResult :: result = new MeshVertexPickResult(faceHit, bestVertex, bestIndex);
- L1296 C31: new IndexTriangle :: triangleIndices = new IndexTriangle();
- L1397 C29: new Triangle :: worldTriangle = new Triangle(
- L1414 C28: new XRWorldInstance :: instance = new XRWorldInstance(targetWorld);
- L1515 C45: new WorldSettings :: return TargetWorld?.Settings ?? new WorldSettings();
- L1524 C60: new Data.Colors.ColorF3 :: return settings?.GetEffectiveAmbientColor() ?? new Data.Colors.ColorF3(0.03f, 0.03f, 0.03f);


## XRENGINE/Rendering/XRWorldObjectBase.cs
- L139 C34: new ReplicationInfo :: repl ??= new ReplicationInfo();
- L146 C34: new ReplicationInfo :: repl ??= new ReplicationInfo();


## XRENGINE/Scene/Components/Animation/AnimStateMachineComponent.cs
- L10 C50: new() :: private AnimStateMachine _stateMachine = new();
- L73 C27: new byte :: byte[] data = new byte[bitCount.Align(8) / 8];


## XRENGINE/Scene/Components/Animation/EyeTrackingBlendTrees.cs
- L9 C57: new() :: public static BlendTree2D RightEyeLidBlend() => new()
- L17 C17: new BlendTree2D.Child :: new BlendTree2D.Child
- L23 C17: new BlendTree2D.Child :: new BlendTree2D.Child
- L29 C17: new BlendTree2D.Child :: new BlendTree2D.Child
- L35 C17: new BlendTree2D.Child :: new BlendTree2D.Child
- L41 C17: new BlendTree2D.Child :: new BlendTree2D.Child
- L47 C17: new BlendTree2D.Child :: new BlendTree2D.Child
- L53 C17: new BlendTree2D.Child :: new BlendTree2D.Child
- L61 C62: new() :: private static AnimationClip EyeOpenSquintRight() => new()
- L71 C61: new() :: private static AnimationClip EyelidSquintRight() => new()
- L81 C59: new() :: private static AnimationClip EyelidWideRight() => new()
- L91 C62: new() :: private static AnimationClip EyelidNeutralRight() => new()
- L101 C60: new() :: private static AnimationClip EyelidBlinkRight() => new()
- L114 C56: new() :: public static BlendTree2D LeftEyeLidBlend() => new()
- L122 C17: new BlendTree2D.Child :: new BlendTree2D.Child
- L128 C17: new BlendTree2D.Child :: new BlendTree2D.Child
- L134 C17: new BlendTree2D.Child :: new BlendTree2D.Child
- L140 C17: new BlendTree2D.Child :: new BlendTree2D.Child
- L146 C17: new BlendTree2D.Child :: new BlendTree2D.Child
- L152 C17: new BlendTree2D.Child :: new BlendTree2D.Child
- L158 C17: new BlendTree2D.Child :: new BlendTree2D.Child
- L166 C61: new() :: private static AnimationClip EyeOpenSquintLeft() => new()
- L176 C60: new() :: private static AnimationClip EyelidSquintLeft() => new()
- L186 C58: new() :: private static AnimationClip EyelidWideLeft() => new()
- L196 C61: new() :: private static AnimationClip EyelidNeutralLeft() => new()
- L206 C59: new() :: private static AnimationClip EyelidBlinkLeft() => new()
- L219 C61: new() :: public static BlendTreeDirect BrowInnerUpBlend() => new()
- L224 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child
- L229 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child
- L236 C66: new() :: private static BlendTree1D LimitBrowSad_MouthClosed() => new()
- L242 C17: new BlendTree1D.Child :: new BlendTree1D.Child
- L247 C17: new BlendTree1D.Child :: new BlendTree1D.Child
- L254 C58: new() :: private static BlendTree2D BrowSadEmulation() => new()
- L262 C17: new BlendTree2D.Child :: new BlendTree2D.Child
- L268 C17: new BlendTree2D.Child :: new BlendTree2D.Child
- L276 C59: new() :: private static BlendTree2D BrowInnerUpBlend2() => new()
- L284 C17: new BlendTree2D.Child :: new BlendTree2D.Child
- L290 C17: new BlendTree2D.Child :: new BlendTree2D.Child
- L298 C55: new() :: private static AnimationClip BrowInnerUp() => new()
- L306 C56: new() :: private static AnimationClip BrowInnerUp0() => new()
- L319 C20: new BlendTree2D :: return new BlendTree2D
- L327 C21: new BlendTree2D.Child :: new BlendTree2D.Child
- L333 C21: new BlendTree2D.Child :: new BlendTree2D.Child
- L339 C21: new BlendTree2D.Child :: new BlendTree2D.Child
- L345 C21: new BlendTree2D.Child :: new BlendTree2D.Child
- L351 C21: new BlendTree2D.Child :: new BlendTree2D.Child
- L360 C59: new() :: private static AnimationClip EyeLookDownLeft() => new()
- L371 C57: new() :: private static AnimationClip EyeLookUpLeft() => new()
- L382 C58: new() :: private static AnimationClip EyeLookOutLeft() => new()
- L393 C57: new() :: private static AnimationClip EyeLookInLeft() => new()
- L404 C62: new() :: private static AnimationClip EyeLookNeutralLeft() => new()
- L418 C58: new() :: public static BlendTree2D EyeLookRightBlend() => new()
- L426 C21: new BlendTree2D.Child :: new BlendTree2D.Child
- L432 C21: new BlendTree2D.Child :: new BlendTree2D.Child
- L438 C21: new BlendTree2D.Child :: new BlendTree2D.Child
- L444 C21: new BlendTree2D.Child :: new BlendTree2D.Child
- L450 C21: new BlendTree2D.Child :: new BlendTree2D.Child
- L458 C60: new() :: private static AnimationClip EyeLookDownRight() => new()
- L469 C58: new() :: private static AnimationClip EyeLookUpRight() => new()
- L480 C59: new() :: private static AnimationClip EyeLookOutRight() => new()
- L491 C58: new() :: private static AnimationClip EyeLookInRight() => new()
- L502 C63: new() :: private static AnimationClip EyeLookNeutralRight() => new()
- L516 C62: new() :: public static BlendTreeDirect BrowDownLeftBlend() => new()
- L521 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child
- L526 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child
- L531 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child
- L538 C76: new() :: private static BlendTree1D BrowAngry_NoseSneer_Emulation_Left() => new()
- L544 C17: new BlendTree1D.Child :: new BlendTree1D.Child
- L549 C17: new BlendTree1D.Child :: new BlendTree1D.Child
- L556 C83: new() :: private static BlendTree1D BrowAngry_MouthRaiserLower_Emulation_Left() => new()
- L562 C17: new BlendTree1D.Child :: new BlendTree1D.Child
- L567 C17: new BlendTree1D.Child :: new BlendTree1D.Child
- L574 C60: new() :: private static BlendTree1D BrowDownLeftBlend2() => new()
- L580 C17: new BlendTree1D.Child :: new BlendTree1D.Child
- L585 C17: new BlendTree1D.Child :: new BlendTree1D.Child
- L592 C57: new() :: private static AnimationClip BrowDownLeft0() => new()
- L600 C56: new() :: private static AnimationClip BrowDownLeft() => new()
- L611 C63: new() :: public static BlendTreeDirect BrowDownRightBlend() => new()
- L616 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child
- L621 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child
- L626 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child
- L633 C77: new() :: private static BlendTree1D BrowAngry_NoseSneer_Emulation_Right() => new()
- L639 C17: new BlendTree1D.Child :: new BlendTree1D.Child
- L644 C17: new BlendTree1D.Child :: new BlendTree1D.Child
- L651 C84: new() :: private static BlendTree1D BrowAngry_MouthRaiserLower_Emulation_Right() => new()
- L657 C17: new BlendTree1D.Child :: new BlendTree1D.Child
- L662 C17: new BlendTree1D.Child :: new BlendTree1D.Child
- L669 C61: new() :: private static BlendTree1D BrowDownRightBlend2() => new()
- L675 C17: new BlendTree1D.Child :: new BlendTree1D.Child
- L680 C17: new BlendTree1D.Child :: new BlendTree1D.Child
- L687 C58: new() :: private static AnimationClip BrowDownRight0() => new()
- L695 C57: new() :: private static AnimationClip BrowDownRight() => new()
- L706 C65: new() :: public static BlendTreeDirect BrowOuterUpLeftBlend() => new()
- L711 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child
- L716 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child
- L723 C63: new() :: private static BlendTree1D BrowWideLeftEmulation() => new()
- L729 C17: new BlendTree1D.Child :: new BlendTree1D.Child
- L734 C17: new BlendTree1D.Child :: new BlendTree1D.Child
- L741 C63: new() :: private static BlendTree1D BrowOuterUpLeftBlend2() => new()
- L747 C17: new BlendTree1D.Child :: new BlendTree1D.Child
- L752 C17: new BlendTree1D.Child :: new BlendTree1D.Child
- L759 C59: new() :: private static AnimationClip BrowOuterUpLeft() => new()
- L767 C60: new() :: private static AnimationClip BrowOuterUpLeft0() => new()
- L778 C66: new() :: public static BlendTreeDirect BrowOuterUpRightBlend() => new()
- L783 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child
- L788 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child
- L795 C64: new() :: private static BlendTree1D BrowWideRightEmulation() => new()
- L801 C17: new BlendTree1D.Child :: new BlendTree1D.Child
- L806 C17: new BlendTree1D.Child :: new BlendTree1D.Child
- L813 C64: new() :: private static BlendTree1D BrowOuterUpRightBlend2() => new()
- L819 C17: new BlendTree1D.Child :: new BlendTree1D.Child
- L824 C17: new BlendTree1D.Child :: new BlendTree1D.Child
- L831 C60: new() :: private static AnimationClip BrowOuterUpRight() => new()
- L839 C61: new() :: private static AnimationClip BrowOuterUpRight0() => new()


## XRENGINE/Scene/Components/Animation/HumanoidComponent.cs
- L40 C46: new() :: private HumanoidSettings _settings = new();
- L267 C40: new() :: public BoneDef Hips { get; } = new();
- L268 C41: new() :: public BoneDef Spine { get; } = new();
- L269 C41: new() :: public BoneDef Chest { get; } = new();
- L270 C40: new() :: public BoneDef Neck { get; } = new();
- L271 C40: new() :: public BoneDef Head { get; } = new();
- L275 C46: new() :: public BoneDef EyesTarget { get; } = new();
- L286 C56: new() :: public BoneDef Proximal { get; } = new();
- L290 C60: new() :: public BoneDef Intermediate { get; } = new();
- L294 C54: new() :: public BoneDef Distal { get; } = new();
- L304 C48: new() :: public Finger Pinky { get; } = new();
- L305 C47: new() :: public Finger Ring { get; } = new();
- L306 C49: new() :: public Finger Middle { get; } = new();
- L307 C48: new() :: public Finger Index { get; } = new();
- L308 C48: new() :: public Finger Thumb { get; } = new();
- L320 C48: new() :: public BoneDef Shoulder { get; } = new();
- L321 C43: new() :: public BoneDef Arm { get; } = new();
- L322 C45: new() :: public BoneDef Elbow { get; } = new();
- L323 C45: new() :: public BoneDef Wrist { get; } = new();
- L324 C44: new() :: public Fingers Hand { get; } = new();
- L325 C43: new() :: public BoneDef Leg { get; } = new();
- L326 C44: new() :: public BoneDef Knee { get; } = new();
- L327 C44: new() :: public BoneDef Foot { get; } = new();
- L328 C44: new() :: public BoneDef Toes { get; } = new();
- L329 C43: new() :: public BoneDef Eye { get; } = new();
- L346 C41: new() :: public BodySide Left { get; } = new();
- L347 C42: new() :: public BodySide Right { get; } = new();
- L386 C40: new BoneChainItem :: : [.. bones.Select(bone => new BoneChainItem(bone.Node!, bone.Constraints))];
- L710 C20: new BoneIKConstraints :: return new BoneIKConstraints()
- L723 C20: new BoneIKConstraints :: return new BoneIKConstraints()
- L745 C69: new Vector3 :: LeftFootTarget = (null, Matrix4x4.CreateTranslation(new Vector3(x, LeftFootTarget.offset.Translation.Y, LeftFootTarget.offset.Translation.Z)));
- L747 C70: new Vector3 :: RightFootTarget = (null, Matrix4x4.CreateTranslation(new Vector3(x, RightFootTarget.offset.Translation.Y, RightFootTarget.offset.Translation.Z)));
- L752 C69: new Vector3 :: LeftFootTarget = (null, Matrix4x4.CreateTranslation(new Vector3(LeftFootTarget.offset.Translation.X, y, LeftFootTarget.offset.Translation.Z)));
- L754 C70: new Vector3 :: RightFootTarget = (null, Matrix4x4.CreateTranslation(new Vector3(RightFootTarget.offset.Translation.X, y, RightFootTarget.offset.Translation.Z)));
- L759 C69: new Vector3 :: LeftFootTarget = (null, Matrix4x4.CreateTranslation(new Vector3(LeftFootTarget.offset.Translation.X, LeftFootTarget.offset.Translation.Y, z)));
- L761 C70: new Vector3 :: RightFootTarget = (null, Matrix4x4.CreateTranslation(new Vector3(RightFootTarget.offset.Translation.X, RightFootTarget.offset.Translation.Y, z)));


## XRENGINE/Scene/Components/Animation/IK/BaseIKSolverComponent.cs
- L14 C41: new RenderCommandMethod3D :: RenderInfo = RenderInfo3D.New(this, new RenderCommandMethod3D(EDefaultRenderPass.OnTopForward, Visualize))


## XRENGINE/Scene/Components/Animation/IK/HumanoidIKSolverComponent.cs
- L17 C40: new() :: public IKSolverFABRIK _spine = new();
- L20 C45: new() :: public TransformConstrainer _hips = new();
- L151 C32: new Vector3 :: ik.RawIKPosition = new Vector3(x, ik.RawIKPosition.Y, ik.RawIKPosition.Z);
- L159 C32: new Vector3 :: ik.RawIKPosition = new Vector3(ik.RawIKPosition.X, y, ik.RawIKPosition.Z);
- L167 C32: new Vector3 :: ik.RawIKPosition = new Vector3(ik.RawIKPosition.X, ik.RawIKPosition.Y, z);


## XRENGINE/Scene/Components/Animation/IK/Solvers/IKSolverFABRIK.cs
- L66 C29: new bool :: _limitedBones = new bool[_bones.Length];
- L67 C37: new Vector3 :: _solverLocalPositions = new Vector3[_bones.Length];


## XRENGINE/Scene/Components/Animation/IK/Solvers/IKSolverHeuristic.cs
- L43 C26: new IKBone :: _bones = new IKBone[hierarchy.Length];
- L48 C33: new IKBone :: _bones[i] = new IKBone();
- L63 C37: new Transform :: Transform?[] newBones = new Transform?[_bones.Length + 1];


## XRENGINE/Scene/Components/Animation/IK/Solvers/IKSolverLimb.cs
- L180 C55: new AxisDirection :: private AxisDirection[] _axisDirectionsLeft = new AxisDirection[4];
- L181 C56: new AxisDirection :: private AxisDirection[] _axisDirectionsRight = new AxisDirection[4];
- L194 C33: new AxisDirection :: axisDirections[0] = new AxisDirection(
- L196 C17: new Vector3 :: new Vector3(-1f, 0f, 0f)); // default
- L198 C33: new AxisDirection :: axisDirections[1] = new AxisDirection(
- L199 C17: new Vector3 :: new Vector3(0.5f, 0f, -0.2f),
- L200 C17: new Vector3 :: new Vector3(-0.5f, 1f, 1f)); // behind head
- L202 C33: new AxisDirection :: axisDirections[2] = new AxisDirection(
- L203 C17: new Vector3 :: new Vector3(-0.5f, -1f, -0.2f),
- L204 C17: new Vector3 :: new Vector3(0f, -0.5f, -1f)); // arm twist
- L206 C33: new AxisDirection :: axisDirections[3] = new AxisDirection(
- L207 C17: new Vector3 :: new Vector3(-0.5f, -0.5f, 1f),
- L208 C17: new Vector3 :: new Vector3(-1f, 1f, -1f)); // cross heart


## XRENGINE/Scene/Components/Animation/IK/Solvers/IKSolverTrigonometric.cs
- L75 C43: new() :: public TrigonometricBone _bone1 = new();
- L79 C43: new() :: public TrigonometricBone _bone2 = new();
- L83 C43: new() :: public TrigonometricBone _bone3 = new();
- L229 C37: new Vector3 :: : Vector3.Transform(new Vector3(0f, y, x), XRMath.LookRotation(direction, bendDirection));
- L398 C38: new Vector3 :: return Vector3.Transform(new Vector3(0.0f, y, x), lookRot);


## XRENGINE/Scene/Components/Animation/IK/Solvers/VR/IKSolverVR.Arm.cs
- L23 C60: new ArmSettings :: private JsonAsset<ArmSettings> _settings = new(new ArmSettings());
- L367 C43: new() :: private AnimationCurve _stretchCurve = new();
- L491 C62: new JsonAsset :: _settings = Engine.LoadOrGenerateAsset(() => new JsonAsset<ArmSettings>(new ArmSettings()), "arms.json", true, "IKTweaks");
- L491 C89: new ArmSettings :: _settings = Engine.LoadOrGenerateAsset(() => new JsonAsset<ArmSettings>(new ArmSettings()), "arms.json", true, "IKTweaks");


## XRENGINE/Scene/Components/Animation/IK/Solvers/VR/IKSolverVR.cs
- L282 C36: new FloatKeyframe :: FloatKeyframe[] keys = new FloatKeyframe[3];
- L408 C26: new VirtualBone :: RootBone ??= new VirtualBone(_solverTransforms.Root);
- L745 C38: new() :: private SpineSolver _spine = new();
- L787 C37: new LegSolver :: private LegSolver[] _legs = new LegSolver[2];
- L788 C37: new ArmSolver :: private ArmSolver[] _arms = new ArmSolver[2];
- L796 C60: new() :: private PhysxScene.PhysxQueryFilter _queryFilter = new()


## XRENGINE/Scene/Components/Animation/IK/Solvers/VR/IKSolverVR.Leg.cs
- L117 C43: new() :: private AnimationCurve _stretchCurve = new();


## XRENGINE/Scene/Components/Animation/IK/Solvers/VR/IKSolverVR.Locomotion.cs
- L232 C69: new Vector3 :: Vector3 standOffsetWorld = rb.SolverRotation.Rotate(new Vector3(_standOffset.X, 0f, _standOffset.Y) * scale);


## XRENGINE/Scene/Components/Animation/IK/Solvers/VR/IKSolverVR.SolverTransforms.cs
- L45 C59: new SolverTransformsArm :: public SolverTransformsArm Arm { get; } = new SolverTransformsArm(shoulder, arm, elbow, wrist);
- L46 C59: new SolverTransformsLeg :: public SolverTransformsLeg Leg { get; } = new SolverTransformsLeg(leg, knee, foot, toes);
- L77 C57: new SolverTransformsSide :: public SolverTransformsSide Left { get; } = new SolverTransformsSide(
- L86 C58: new SolverTransformsSide :: public SolverTransformsSide Right { get; } = new SolverTransformsSide(
- L155 C28: new ArgumentOutOfRangeException :: _ => throw new ArgumentOutOfRangeException(nameof(index), "Invalid index for SolverTransforms."),
- L183 C32: new IndexOutOfRangeException :: _ => throw new IndexOutOfRangeException("Invalid index for SolverTransforms."),


## XRENGINE/Scene/Components/Animation/IK/Solvers/VR/IKSolverVR.SolverTransforms.TransformPoses.cs
- L16 C55: new PoseData :: public PoseData InputWorld { get; } = new PoseData();
- L20 C56: new PoseData :: public PoseData SolvedWorld { get; } = new PoseData();
- L24 C57: new PoseData :: public PoseData DefaultLocal { get; } = new PoseData();


## XRENGINE/Scene/Components/Animation/IK/Solvers/VR/IKSolverVR.Spine.cs
- L554 C26: new VirtualBone :: _bones = new VirtualBone[boneCount];
- L574 C29: new VirtualBone :: _bones[0] = new VirtualBone(transforms.Hips);
- L575 C29: new VirtualBone :: _bones[1] = new VirtualBone(transforms.Spine);
- L578 C43: new VirtualBone :: _bones[_chestIndex] = new VirtualBone(transforms.Chest);
- L581 C42: new VirtualBone :: _bones[_neckIndex] = new VirtualBone(transforms.Neck);
- L583 C38: new VirtualBone :: _bones[_headIndex] = new VirtualBone(transforms.Head);


## XRENGINE/Scene/Components/Animation/IK/Solvers/VR/IKSolverVR.VirtualBone.cs
- L13 C65: new ArgumentNullException :: public TransformPoses Pose { get; } = pose ?? throw new ArgumentNullException(nameof(pose), "VirtualBone Pose cannot be null.");
- L233 C65: new Vector3 :: return XRMath.LookRotation(dir, lookRot).Rotate(new Vector3(0.0f, y, -x));


## XRENGINE/Scene/Components/Animation/IK/VRIKCalibrator.cs
- L107 C36: new() :: CalibrationData data = new();
- L162 C25: new CalibrationData.Target :: data.Head = new CalibrationData.Target(spine.HeadTarget);
- L163 C25: new CalibrationData.Target :: data.Hips = new CalibrationData.Target(spine.HipsTarget);
- L164 C29: new CalibrationData.Target :: data.LeftHand = new CalibrationData.Target(ik.Solver.LeftArm.Target);
- L165 C30: new CalibrationData.Target :: data.RightHand = new CalibrationData.Target(ik.Solver.RightArm.Target);
- L166 C29: new CalibrationData.Target :: data.LeftFoot = new CalibrationData.Target(ik.Solver.LeftLeg.Target);
- L167 C30: new CalibrationData.Target :: data.RightFoot = new CalibrationData.Target(ik.Solver.RightLeg.Target);
- L168 C32: new CalibrationData.Target :: data.LeftLegGoal = new CalibrationData.Target(ik.Solver.LeftLeg.KneeTarget);
- L169 C33: new CalibrationData.Target :: data.RightLegGoal = new CalibrationData.Target(ik.Solver.RightLeg.KneeTarget);


## XRENGINE/Scene/Components/Animation/IK/VRIKSolverComponent.cs
- L17 C120: new() :: private static readonly Dictionary<ushort, (QuantizedHumanoidPose pose, ushort sequence)> _receivedBaselines = new();
- L18 C85: new() :: private static readonly Dictionary<ushort, VRIKSolverComponent> _registry = new();
- L20 C45: new() :: public IKSolverVR Solver { get; } = new();
- L156 C20: new HumanoidPoseSample :: return new HumanoidPoseSample(


## XRENGINE/Scene/Components/Animation/LipTrackingBlendTrees.cs
- L9 C55: new() :: public static BlendTree1D CheekPuffBlend() => new()
- L14 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L19 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L26 C53: new() :: private static AnimationClip CheekPuff() => new()
- L34 C54: new() :: private static AnimationClip CheekPuff0() => new()
- L45 C61: new() :: public static BlendTree1D CheekSquintLeftBlend() => new()
- L50 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L55 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L62 C59: new() :: private static AnimationClip CheekSquintLeft() => new()
- L70 C60: new() :: private static AnimationClip CheekSquintLeft0() => new()
- L81 C62: new() :: public static BlendTree1D CheekSquintRightBlend() => new()
- L86 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L91 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L98 C60: new() :: private static AnimationClip CheekSquintRight() => new()
- L106 C61: new() :: private static AnimationClip CheekSquintRight0() => new()
- L117 C56: new() :: public static BlendTree1D JawForwardBlend() => new()
- L122 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L127 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L134 C54: new() :: private static AnimationClip JawForward() => new()
- L142 C55: new() :: private static AnimationClip JawForward0() => new()
- L153 C53: new() :: public static BlendTree1D JawOpenBlend() => new()
- L158 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L163 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L170 C55: new() :: private static BlendTree1D JawOpenHelper() => new()
- L175 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L180 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L187 C52: new() :: private static AnimationClip JawOpen0() => new()
- L195 C51: new() :: private static AnimationClip JawOpen() => new()
- L206 C57: new() :: public static BlendTree1D LimitJawX_MouthX() => new()
- L211 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L216 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L221 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L228 C51: new() :: private static BlendTree1D JawXBlend() => new()
- L233 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L238 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L243 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L250 C52: new() :: private static AnimationClip JawRight() => new()
- L259 C51: new() :: private static AnimationClip JawLeft() => new()
- L268 C49: new() :: private static AnimationClip JawX0() => new()
- L280 C55: new() :: public static BlendTree1D LipPuckerBlend() => new()
- L285 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L290 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L297 C68: new() :: private static BlendTree1D LimitMouthPucker_LipFunnel() => new()
- L302 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L307 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L314 C55: new() :: private static AnimationClip MouthPucker() => new()
- L322 C56: new() :: private static AnimationClip MouthPucker0() => new()
- L333 C57: new() :: public static BlendTree1D MouthClosedBlend() => new()
- L338 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L343 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L350 C55: new() :: private static AnimationClip MouthClosed() => new()
- L358 C56: new() :: private static AnimationClip MouthClosed0() => new()
- L369 C60: new() :: public static BlendTree1D MouthFrownLeftBlend() => new()
- L374 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L379 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L386 C59: new() :: private static AnimationClip MouthFrownLeft0() => new()
- L394 C72: new() :: private static BlendTree1D LimitMouthFrownLeft_MouthXLeft() => new()
- L399 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L404 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L411 C58: new() :: private static AnimationClip MouthFrownLeft() => new()
- L422 C61: new() :: public static BlendTree1D MouthFrownRightBlend() => new()
- L427 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L432 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L439 C74: new() :: private static BlendTree1D LimitMouthFrownRight_MouthXRight() => new()
- L444 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L449 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L456 C59: new() :: private static AnimationClip MouthFrownRight() => new()
- L464 C60: new() :: private static AnimationClip MouthFrownRight0() => new()
- L475 C57: new() :: public static BlendTree1D MouthFunnelBlend() => new()
- L480 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L485 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L492 C55: new() :: private static AnimationClip MouthFunnel() => new()
- L500 C56: new() :: private static AnimationClip MouthFunnel0() => new()
- L511 C60: new() :: public static BlendTree1D MouthLowerDownBlend() => new()
- L516 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L521 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L528 C71: new() :: private static BlendTree1D LimitMouthLowerDown_LipFunnel() => new()
- L533 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L538 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L545 C58: new() :: private static AnimationClip MouthLowerDown() => new()
- L554 C59: new() :: private static AnimationClip MouthLowerDown0() => new()
- L566 C56: new() :: public static BlendTree1D MouthPressBlend() => new()
- L571 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L576 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L583 C54: new() :: private static AnimationClip MouthPress() => new()
- L592 C55: new() :: private static AnimationClip MouthPress0() => new()
- L604 C60: new() :: public static BlendTree1D MouthRollLowerBlend() => new()
- L609 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L614 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L621 C73: new() :: private static BlendTree1D LimitMouthRollLower_MouthClosed() => new()
- L626 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L631 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L638 C58: new() :: private static AnimationClip MouthRollLower() => new()
- L646 C59: new() :: private static AnimationClip MouthRollLower0() => new()
- L657 C60: new() :: public static BlendTree1D MouthRollUpperBlend() => new()
- L662 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L667 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L674 C73: new() :: private static BlendTree1D LimitMouthRollUpper_MouthClosed() => new()
- L679 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L684 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L691 C58: new() :: private static AnimationClip MouthRollUpper() => new()
- L699 C59: new() :: private static AnimationClip MouthRollUpper0() => new()
- L710 C61: new() :: public static BlendTree1D MouthShrugLowerBlend() => new()
- L715 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L720 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L727 C74: new() :: private static BlendTree1D LimitMouthShrugLower_MouthClosed() => new()
- L732 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L737 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L744 C59: new() :: private static AnimationClip MouthShrugLower() => new()
- L752 C60: new() :: private static AnimationClip MouthShrugLower0() => new()
- L763 C61: new() :: public static BlendTree1D MouthShrugUpperBlend() => new()
- L768 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L773 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L780 C74: new() :: private static BlendTree1D LimitMouthShrugUpper_MouthClosed() => new()
- L785 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L790 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L797 C59: new() :: private static AnimationClip MouthShrugUpper() => new()
- L805 C60: new() :: private static AnimationClip MouthShrugUpper0() => new()
- L816 C60: new() :: public static BlendTree1D MouthSmileLeftBlend() => new()
- L821 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L826 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L833 C58: new() :: private static AnimationClip MouthSmileLeft() => new()
- L841 C59: new() :: private static AnimationClip MouthSmileLeft0() => new()
- L852 C61: new() :: public static BlendTree1D MouthSmileRightBlend() => new()
- L857 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L862 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L869 C59: new() :: private static AnimationClip MouthSmileRight() => new()
- L877 C60: new() :: private static AnimationClip MouthSmileRight0() => new()
- L888 C62: new() :: public static BlendTree1D MouthStretchLeftBlend() => new()
- L893 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L898 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L905 C60: new() :: private static AnimationClip MouthStretchLeft() => new()
- L913 C61: new() :: private static AnimationClip MouthStretchLeft0() => new()
- L924 C63: new() :: public static BlendTree1D MouthStretchRightBlend() => new()
- L929 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L934 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L941 C61: new() :: private static AnimationClip MouthStretchRight() => new()
- L949 C62: new() :: private static AnimationClip MouthStretchRight0() => new()
- L960 C62: new() :: public static BlendTree1D MouthUpperUpLeftBlend() => new()
- L965 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L970 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L977 C52: new() :: private static BlendTree1D MouthRight() => new()
- L982 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L987 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L994 C73: new() :: private static BlendTree1D LimitMouthUpperUpLeft_LipFunnel() => new()
- L999 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L1004 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L1011 C60: new() :: private static AnimationClip MouthUpperUpLeft() => new()
- L1019 C61: new() :: private static AnimationClip MouthUpperUpLeft0() => new()
- L1030 C63: new() :: public static BlendTree1D MouthUpperUpRightBlend() => new()
- L1035 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L1040 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L1047 C51: new() :: private static BlendTree1D MouthLeft() => new()
- L1052 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L1057 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L1064 C74: new() :: private static BlendTree1D LimitMouthUpperUpRight_LipFunnel() => new()
- L1069 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L1074 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L1081 C61: new() :: private static AnimationClip MouthUpperUpRight() => new()
- L1089 C62: new() :: private static AnimationClip MouthUpperUpRight0() => new()
- L1100 C52: new() :: public static BlendTree1D MouthXBlend() => new()
- L1105 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L1110 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L1115 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L1122 C58: new() :: private static AnimationClip MouthRightClip() => new()
- L1131 C57: new() :: private static AnimationClip MouthLeftClip() => new()
- L1140 C51: new() :: private static AnimationClip MouthX0() => new()
- L1152 C55: new() :: public static BlendTree1D NoseSneerBlend() => new()
- L1157 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L1162 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L1169 C53: new() :: private static AnimationClip NoseSneer() => new()
- L1178 C54: new() :: private static AnimationClip NoseSneer0() => new()
- L1190 C55: new() :: public static BlendTree1D TongueOutBlend() => new()
- L1195 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L1200 C17: new BlendTree1D.Child :: new BlendTree1D.Child()
- L1207 C53: new() :: private static AnimationClip TongueOut() => new()
- L1215 C54: new() :: private static AnimationClip TongueOut0() => new()


## XRENGINE/Scene/Components/Animation/MotionCapture/FaceMotion3DCaptureComponent.cs
- L144 C35: new() :: using (UdpClient sender = new())
- L238 C28: new IPEndPoint :: remoteEP = new IPEndPoint(IPAddress.Any, 0);
- L256 C37: new IPEndPoint :: EndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
- L278 C39: new() :: using (UdpClient sender = new())
- L318 C29: new byte :: byte[] buffer = new byte[10000]; // buffer size as per sample
- L319 C41: new() :: StringBuilder dataBuilder = new();
- L502 C25: new Vector3 :: _headPosition = new Vector3(


## XRENGINE/Scene/Components/Animation/MotionCapture/FaceTrackingReceiverComponent.cs
- L720 C31: new() :: AnimLayer layer = new()
- L726 C26: new BlendTreeDirect :: Motion = new BlendTreeDirect()
- L735 C25: new() :: new()
- L740 C25: new() :: new()
- L753 C65: new() :: private static AnimationClip ResetAdditiveAnimator() => new()
- L757 C26: new AnimationMember :: RootMember = new AnimationMember("Set Humanoid Values", EAnimationMemberType.Group)
- L761 C21: new AnimationMember :: new AnimationMember("SetFloat", EAnimationMemberType.Method)
- L766 C21: new AnimationMember :: new AnimationMember("SetFloat", EAnimationMemberType.Method)
- L771 C21: new AnimationMember :: new AnimationMember("SetFloat", EAnimationMemberType.Method)
- L780 C62: new() :: private static BlendTree2D MakeEyeRightRotation() => new()
- L787 C17: new() :: new()
- L793 C17: new() :: new()
- L799 C17: new() :: new()
- L805 C17: new() :: new()
- L811 C17: new() :: new()
- L820 C63: new() :: private static AnimationClip EyeLookDownRightRot() => new()
- L824 C26: new AnimationMember :: RootMember = new AnimationMember("SetHumanoidValue", EAnimationMemberType.Method)
- L831 C61: new() :: private static AnimationClip EyeLookUpRightRot() => new()
- L835 C26: new AnimationMember :: RootMember = new AnimationMember("SetHumanoidValue", EAnimationMemberType.Method)
- L842 C61: new() :: private static AnimationClip EyeLookInRightRot() => new()
- L846 C26: new AnimationMember :: RootMember = new AnimationMember("SetHumanoidValue", EAnimationMemberType.Method)
- L853 C62: new() :: private static AnimationClip EyeLookOutRightRot() => new()
- L857 C26: new AnimationMember :: RootMember = new AnimationMember("SetHumanoidValue", EAnimationMemberType.Method)
- L864 C66: new() :: private static AnimationClip EyeLookNeutralRightRot() => new()
- L868 C26: new AnimationMember :: RootMember = new AnimationMember("Set Humanoid Values", EAnimationMemberType.Group)
- L872 C21: new AnimationMember :: new AnimationMember("SetHumanoidValue", EAnimationMemberType.Method)
- L877 C21: new AnimationMember :: new AnimationMember("SetHumanoidValue", EAnimationMemberType.Method)
- L886 C61: new() :: private static BlendTree2D MakeEyeLeftRotation() => new()
- L893 C17: new() :: new()
- L899 C17: new() :: new()
- L905 C17: new() :: new()
- L911 C17: new() :: new()
- L917 C17: new() :: new()
- L927 C62: new() :: private static AnimationClip EyeLookDownLeftRot() => new()
- L931 C26: new AnimationMember :: RootMember = new AnimationMember("SetHumanoidValue", EAnimationMemberType.Method)
- L938 C60: new() :: private static AnimationClip EyeLookUpLeftRot() => new()
- L942 C26: new AnimationMember :: RootMember = new AnimationMember("SetHumanoidValue", EAnimationMemberType.Method)
- L949 C60: new() :: private static AnimationClip EyeLookInLeftRot() => new()
- L953 C26: new AnimationMember :: RootMember = new AnimationMember("SetHumanoidValue", EAnimationMemberType.Method)
- L960 C61: new() :: private static AnimationClip EyeLookOutLeftRot() => new()
- L964 C26: new AnimationMember :: RootMember = new AnimationMember("SetHumanoidValue", EAnimationMemberType.Method)
- L971 C65: new() :: private static AnimationClip EyeLookNeutralLeftRot() => new()
- L975 C26: new AnimationMember :: RootMember = new AnimationMember("Set Humanoid Values", EAnimationMemberType.Group)
- L979 C21: new AnimationMember :: new AnimationMember("SetHumanoidValue", EAnimationMemberType.Method)
- L984 C21: new AnimationMember :: new AnimationMember("SetHumanoidValue", EAnimationMemberType.Method)
- L995 C31: new() :: AnimLayer layer = new();
- L1133 C31: new() :: AnimLayer layer = new();
- L1164 C39: new BlendTreeDirect :: ftLocalRootState.Motion = new BlendTreeDirect()
- L1169 C21: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1181 C21: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1220 C68: new() :: private static BlendTreeDirect MakeFTBlendShapeDriver() => new()
- L1225 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1230 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1235 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1240 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1245 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1250 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1255 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1260 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1265 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1270 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1275 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1280 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1285 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1290 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1295 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1300 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1305 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1310 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1315 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1320 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1325 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1330 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1335 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1340 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1345 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1350 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1355 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1360 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1365 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1370 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1375 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1380 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1385 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1390 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1395 C17: new BlendTreeDirect.Child :: new BlendTreeDirect.Child()
- L1519 C63: new() :: private static AnimationClip MakeResetFTAnimator() => new()
- L1524 C26: new AnimationMember :: RootMember = new AnimationMember("SetFloat Group", EAnimationMemberType.Group)
- L1578 C31: new AnimationMember :: setFloats.Add(new AnimationMember(nameof(AnimStateMachineComponent.SetFloat), EAnimationMemberType.Method)


## XRENGINE/Scene/Components/Animation/MotionCapture/VMCCaptureComponent.cs
- L143 C13: new Vector3 :: new Vector3(
- L165 C13: new Vector3 :: new Vector3(
- L169 C13: new Quaternion :: new Quaternion(
- L188 C13: new Vector3 :: new Vector3(
- L192 C13: new Quaternion :: new Quaternion(
- L208 C13: new Vector3 :: new Vector3(
- L212 C13: new Quaternion :: new Quaternion(
- L217 C13: new ColorF4 :: new ColorF4(
- L232 C13: new Vector3 :: new Vector3(
- L236 C13: new Quaternion :: new Quaternion(
- L271 C13: new Vector3 :: new Vector3(
- L275 C13: new Quaternion :: new Quaternion(
- L287 C13: new Vector3 :: new Vector3(
- L291 C13: new Quaternion :: new Quaternion(
- L303 C13: new Vector3 :: new Vector3(
- L307 C13: new Quaternion :: new Quaternion(
- L319 C13: new Vector3 :: new Vector3(
- L323 C13: new Quaternion :: new Quaternion(
- L335 C13: new Vector3 :: new Vector3(
- L339 C13: new Quaternion :: new Quaternion(
- L351 C13: new Vector3 :: new Vector3(
- L355 C13: new Quaternion :: new Quaternion(
- L402 C64: new() :: private readonly Queue<(string, float)> _blendshapeQueue = new();
- L438 C30: new XRPerspectiveCameraParameters :: cam.Parameters = new XRPerspectiveCameraParameters(fov, null, cam.Parameters.NearZ, cam.Parameters.FarZ);
- L454 C23: new ColorF3 :: light.Color = new ColorF3(color.R, color.G, color.B);


## XRENGINE/Scene/Components/Animation/TransformParameterDriverComponent.cs
- L375 C20: new Vector3 :: return new Vector3(rotator.Pitch, rotator.Yaw, rotator.Roll);
- L398 C27: new TransformParameterBinding :: var binding = new TransformParameterBinding


## XRENGINE/Scene/Components/Audio/AudioSourceComponent.cs
- L769 C31: new float :: float[] samples = new float[sampleCount.CeilingToPowerOfTwo()];


## XRENGINE/Scene/Components/Audio/Converters/MicrophoneComponent.ElevenLabsConverter.cs
- L9 C55: new() :: private readonly HttpClient _httpClient = new();
- L21 C63: new() :: private readonly Queue<byte[]> _processingQueue = new();
- L22 C48: new() :: private readonly Lock _queueLock = new();
- L189 C46: new MultipartFormDataContent :: using var formData = new MultipartFormDataContent();
- L192 C49: new MemoryStream :: using var audioStream = new MemoryStream(pcmData);
- L193 C38: new StreamContent :: formData.Add(new StreamContent(audioStream), "audio", "input.pcm");
- L196 C43: new List :: var queryParams = new List<string>
- L219 C38: new StringContent :: formData.Add(new StringContent(voiceSettingsJson), "voice_settings");
- L277 C46: new MemoryStream :: using var memoryStream = new MemoryStream();
- L280 C40: new NAudio.Wave.WaveFormat :: var targetFormat = new NAudio.Wave.WaveFormat(targetSampleRate, 16, 1);
- L293 C41: new byte :: convertedData = new byte[targetSampleRate * 2]; // 1 second of silence at 16-bit
- L315 C28: new byte :: return new byte[targetSampleRate * targetBitsPerSample / 8];
- L320 C30: new byte :: var result = new byte[targetLength];
- L334 C43: new MemoryStream :: using var mp3Stream = new MemoryStream(mp3Data);
- L335 C43: new Mp3FileReader :: using var mp3Reader = new Mp3FileReader(mp3Stream);
- L338 C44: new byte :: var convertedSamples = new byte[mp3Reader.Length];


## XRENGINE/Scene/Components/Audio/Converters/MicrophoneComponent.RVCConverter.cs
- L20 C63: new() :: private readonly Queue<byte[]> _processingQueue = new();
- L21 C50: new() :: private readonly object _queueLock = new();
- L260 C37: new List :: var arguments = new List<string>
- L288 C37: new ProcessStartInfo :: var startInfo = new ProcessStartInfo
- L299 C41: new Process :: using var process = new Process { StartInfo = startInfo };
- L332 C46: new MemoryStream :: using var memoryStream = new MemoryStream();
- L333 C38: new NAudio.Wave.WaveFormat :: var waveFormat = new NAudio.Wave.WaveFormat(sampleRate, bitsPerSample, 1);
- L335 C44: new NAudio.Wave.WaveFileWriter :: using var waveWriter = new NAudio.Wave.WaveFileWriter(filePath, waveFormat);
- L349 C44: new NAudio.Wave.WaveFileReader :: using var waveReader = new NAudio.Wave.WaveFileReader(filePath);
- L350 C37: new byte :: var audioData = new byte[waveReader.Length];


## XRENGINE/Scene/Components/Audio/MicrophoneComponent.cs
- L173 C23: new WaveInEvent :: _waveIn = new WaveInEvent
- L176 C30: new WaveFormat :: WaveFormat = new WaveFormat(SampleRate, _bitsPerSample, channels: 1),
- L183 C30: new byte :: _currentBuffer = new byte[bufferSize];
- L288 C23: new NotSupportedException :: throw new NotSupportedException($"Unsupported bits per sample: {_bitsPerSample}");
- L336 C44: new byte :: byte[] smoothedBytes = new byte[length];
- L364 C39: new short :: short[] samples = new short[shortLength];
- L365 C46: new short :: short[] smoothedShorts = new short[shortLength];
- L395 C44: new float :: float[] floatSamples = new float[floatLength];
- L396 C46: new float :: float[] smoothedFloats = new float[floatLength];
- L424 C27: new NotSupportedException :: throw new NotSupportedException($"Unsupported bits per sample: {_bitsPerSample}");
- L441 C26: new byte :: var buffer = new byte[col.Count];
- L466 C42: new short :: short[] shorts = new short[buffer.Length / 2];
- L474 C42: new float :: float[] floats = new float[buffer.Length / 4];
- L499 C29: new ElevenLabsConverter :: var converter = new ElevenLabsConverter(apiKey, voiceId, modelId);
- L546 C29: new RVCConverter :: var converter = new RVCConverter(modelPath, indexPath, rvcPythonPath, rvcScriptPath);


## XRENGINE/Scene/Components/Audio/OVRLipSyncComponent.cs
- L34 C42: new() :: private ovrLipSyncContext _ctx = new();
- L41 C54: new float :: private readonly float[] _lastInputVisemes = new float[VisemeCount];
- L44 C45: new float :: private readonly float[] _visemes = new float[VisemeCount];


## XRENGINE/Scene/Components/Audio/STT/Providers/AmazonSTTProvider.cs
- L5 C51: new() :: private readonly HttpClient _httpClient = new();
- L11 C20: new STTResult :: return new STTResult


## XRENGINE/Scene/Components/Audio/STT/Providers/AssemblyAISTTProvider.cs
- L16 C27: new HttpClient :: _httpClient = new HttpClient();
- L24 C31: new ByteArrayContent :: var content = new ByteArrayContent(audioData);
- L25 C47: new System.Net.Http.Headers.MediaTypeHeaderValue :: content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
- L33 C28: new STTResult :: return new STTResult
- L43 C28: new STTResult :: return new STTResult { Success = false, Error = "Failed to get upload URL" };
- L47 C37: new ByteArrayContent :: var uploadContent = new ByteArrayContent(audioData);
- L52 C28: new STTResult :: return new STTResult { Success = false, Error = "Failed to upload audio file" };
- L62 C44: new StringContent :: var transcriptionContent = new StringContent(JsonSerializer.Serialize(transcriptionRequest), Encoding.UTF8, "application/json");
- L67 C28: new STTResult :: return new STTResult { Success = false, Error = "Failed to start transcription" };
- L73 C28: new STTResult :: return new STTResult { Success = false, Error = "Failed to get transcription ID" };
- L89 C32: new STTResult :: return new STTResult
- L99 C32: new STTResult :: return new STTResult { Success = false, Error = statusResult.Error ?? "Transcription failed" };
- L103 C24: new STTResult :: return new STTResult { Success = false, Error = "Transcription timeout" };
- L107 C24: new STTResult :: return new STTResult { Success = false, Error = ex.Message };


## XRENGINE/Scene/Components/Audio/STT/Providers/AzureSTTProvider.cs
- L16 C27: new HttpClient :: _httpClient = new HttpClient();
- L24 C31: new ByteArrayContent :: var content = new ByteArrayContent(audioData);
- L25 C47: new System.Net.Http.Headers.MediaTypeHeaderValue :: content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
- L33 C28: new STTResult :: return new STTResult
- L43 C24: new STTResult :: return new STTResult
- L53 C24: new STTResult :: return new STTResult { Success = false, Error = ex.Message };


## XRENGINE/Scene/Components/Audio/STT/Providers/DeepgramSTTProvider.cs
- L15 C27: new HttpClient :: _httpClient = new HttpClient();
- L23 C31: new ByteArrayContent :: var content = new ByteArrayContent(audioData);
- L24 C47: new System.Net.Http.Headers.MediaTypeHeaderValue :: content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
- L32 C28: new STTResult :: return new STTResult
- L45 C28: new STTResult :: return new STTResult
- L54 C24: new STTResult :: return new STTResult { Success = false, Error = "No transcription results" };
- L58 C24: new STTResult :: return new STTResult { Success = false, Error = ex.Message };


## XRENGINE/Scene/Components/Audio/STT/Providers/GoogleSTTProvider.cs
- L8 C51: new() :: private readonly HttpClient _httpClient = new();
- L34 C31: new StringContent :: var content = new StringContent(json, Encoding.UTF8, "application/json");
- L42 C28: new STTResult :: return new STTResult
- L55 C28: new STTResult :: return new STTResult
- L64 C24: new STTResult :: return new STTResult { Success = false, Error = "No transcription results" };
- L68 C24: new STTResult :: return new STTResult { Success = false, Error = ex.Message };


## XRENGINE/Scene/Components/Audio/STT/Providers/OpenAIWhisperProvider.cs
- L15 C27: new HttpClient :: _httpClient = new HttpClient();
- L23 C38: new MultipartFormDataContent :: using var formData = new MultipartFormDataContent();
- L24 C41: new MemoryStream :: using var audioStream = new MemoryStream(audioData);
- L25 C30: new StreamContent :: formData.Add(new StreamContent(audioStream), "file", "audio.wav");
- L26 C30: new StringContent :: formData.Add(new StringContent("whisper-1"), "model");
- L27 C30: new StringContent :: formData.Add(new StringContent(_language), "language");
- L33 C28: new STTResult :: return new STTResult
- L43 C24: new STTResult :: return new STTResult
- L53 C24: new STTResult :: return new STTResult { Success = false, Error = ex.Message };


## XRENGINE/Scene/Components/Audio/STT/Providers/RevAISTTProvider.cs
- L15 C27: new HttpClient :: _httpClient = new HttpClient();
- L23 C38: new MultipartFormDataContent :: using var formData = new MultipartFormDataContent();
- L24 C41: new MemoryStream :: using var audioStream = new MemoryStream(audioData);
- L25 C30: new StreamContent :: formData.Add(new StreamContent(audioStream), "file", "audio.wav");
- L26 C30: new StringContent :: formData.Add(new StringContent(_language), "language");
- L32 C28: new STTResult :: return new STTResult
- L42 C28: new STTResult :: return new STTResult { Success = false, Error = "Failed to get job ID" };
- L65 C32: new STTResult :: return new STTResult
- L75 C32: new STTResult :: return new STTResult { Success = false, Error = "Transcription failed" };
- L79 C24: new STTResult :: return new STTResult { Success = false, Error = "Transcription timeout" };
- L83 C24: new STTResult :: return new STTResult { Success = false, Error = ex.Message };


## XRENGINE/Scene/Components/Audio/STT/SpeechToTextComponent.cs
- L13 C54: new() :: private readonly List<byte[]> _audioBuffer = new();
- L14 C47: new() :: private readonly object _bufferLock = new();
- L132 C29: new byte :: audioData = new byte[totalSize];
- L198 C36: new GoogleSTTProvider :: ESTTProvider.Google => new GoogleSTTProvider(ApiKey, Language),
- L199 C36: new OpenAIWhisperProvider :: ESTTProvider.OpenAI => new OpenAIWhisperProvider(ApiKey, Language),
- L200 C35: new AzureSTTProvider :: ESTTProvider.Azure => new AzureSTTProvider(ApiKey, Language),
- L201 C36: new AmazonSTTProvider :: ESTTProvider.Amazon => new AmazonSTTProvider(ApiKey, Language),
- L202 C38: new DeepgramSTTProvider :: ESTTProvider.Deepgram => new DeepgramSTTProvider(ApiKey, Language),
- L203 C40: new AssemblyAISTTProvider :: ESTTProvider.AssemblyAI => new AssemblyAISTTProvider(ApiKey, Language),
- L204 C35: new RevAISTTProvider :: ESTTProvider.RevAI => new RevAISTTProvider(ApiKey, Language),
- L205 C24: new NotSupportedException :: _ => throw new NotSupportedException($"STT provider {_selectedProvider} is not supported")


## XRENGINE/Scene/Components/Camera/CameraComponent.cs
- L185 C25: new List :: var parts = new List<string>();
- L260 C23: new XRCamera :: var cam = new XRCamera(Transform);
- L478 C36: new XROrthographicCameraParameters :: => Camera.Parameters = new XROrthographicCameraParameters(width, height, nearPlane, farPlane);
- L488 C36: new XRPerspectiveCameraParameters :: => Camera.Parameters = new XRPerspectiveCameraParameters(verticalFieldOfView, aspectRatio, nearPlane, farPlane);


## XRENGINE/Scene/Components/Camera/StereoCameraComponent.cs
- L33 C32: new Transform :: LeftEyeTransform = new Transform(Globals.Left * halfIpd, Transform);
- L34 C33: new Transform :: RightEyeTransform = new Transform(Globals.Right * halfIpd, Transform);
- L35 C40: new XRCamera :: _leftEyeCamera = new(() => new XRCamera(LeftEyeTransform, StereoCameraParameters), true);
- L36 C41: new XRCamera :: _rightEyeCamera = new(() => new XRCamera(RightEyeTransform, StereoCameraParameters), true);


## XRENGINE/Scene/Components/Capture/EditorSelectionAccessor.cs
- L38 C56: new EditorSelectionAccessor :: return sceneNodesProperty is null ? null : new EditorSelectionAccessor(sceneNodesProperty);


## XRENGINE/Scene/Components/Capture/LightProbeComponent.cs
- L132 C37: new GameTimer :: _realtimeCaptureTimer = new GameTimer(this);
- L133 C33: new RenderCommandMethod3D :: _debugAxesCommand = new RenderCommandMethod3D(EDefaultRenderPass.OnTopForward, RenderCameraOrientationDebug)
- L137 C38: new RenderCommandMethod3D :: _debugInfluenceCommand = new RenderCommandMethod3D(EDefaultRenderPass.OnTopForward, RenderVolumesDebug)
- L143 C71: new RenderCommandMesh3D :: VisualRenderInfo = RenderInfo3D.New(this, _visualRC = new RenderCommandMesh3D((int)EDefaultRenderPass.OpaqueForward)),
- L566 C48: new() :: RenderingParameters renderParams = new();
- L583 C30: new XRQuadFrameBuffer :: _irradianceFBO = new XRQuadFrameBuffer(irradianceMaterial);
- L586 C29: new XRQuadFrameBuffer :: _prefilterFBO = new XRQuadFrameBuffer(prefilterMaterial);
- L612 C53: new BoundingRectangle :: AbstractRenderer.Current?.SetRenderArea(new BoundingRectangle(IVector2.Zero, new IVector2(width, height)));
- L612 C90: new IVector2 :: AbstractRenderer.Current?.SetRenderArea(new BoundingRectangle(IVector2.Zero, new IVector2(width, height)));
- L640 C57: new BoundingRectangle :: AbstractRenderer.Current?.SetRenderArea(new BoundingRectangle(IVector2.Zero, new IVector2(mipWidth, mipHeight)));
- L640 C94: new IVector2 :: AbstractRenderer.Current?.SetRenderArea(new BoundingRectangle(IVector2.Zero, new IVector2(mipWidth, mipHeight)));
- L669 C17: new ShaderFloat :: new ShaderFloat(0.0f, "Roughness"),
- L670 C17: new ShaderInt :: new ShaderInt(Math.Max(1, sourceDimension), "SourceDim"),
- L757 C23: new XRMaterial :: var mat = new XRMaterial([GetPreviewTexture()], XRShader.EngineShader(GetPreviewShaderPath(), EShaderType.Fragment)) { RenderPass = pass };
- L758 C29: new XRMeshRenderer :: PreviewSphere = new XRMeshRenderer(mesh, mat);


## XRENGINE/Scene/Components/Capture/LightProbeGridSpawnerComponent.cs
- L18 C58: new() :: private readonly List<SceneNode> _spawnedNodes = new();
- L30 C31: new IVector3 :: var clamped = new IVector3(
- L49 C31: new Vector3 :: var clamped = new Vector3(
- L270 C52: new Vector3 :: Vector3 localPos = start + new Vector3(x * Spacing.X, y * Spacing.Y, z * Spacing.Z);
- L271 C37: new SceneNode :: var child = new SceneNode(SceneNode, $"LightProbe[{x},{y},{z}]", new Transform(localPos));
- L271 C90: new Transform :: var child = new SceneNode(SceneNode, $"LightProbe[{x},{y},{z}]", new Transform(localPos));


## XRENGINE/Scene/Components/Capture/MirrorCaptureComponent.cs
- L25 C25: new XRMaterial :: _material = new XRMaterial(ShaderHelper.LoadEngineShader(Path.Combine("Common", "Mirror.fs")));
- L28 C26: new XRFrameBuffer :: _renderFBO = new XRFrameBuffer();
- L33 C30: new RenderCommandMesh3D :: _displayQuadRC = new RenderCommandMesh3D((int)EDefaultRenderPass.OpaqueForward, meshRenderer, Matrix4x4.Identity);
- L37 C80: new Vector3 :: _renderInfo.LocalCullingVolume = AABB.FromCenterSize(Vector3.Zero, new Vector3(1.0f, 1.0f, 0.001f));
- L142 C66: new() :: private readonly DrivenWorldTransform _mirrorTransform = new();
- L181 C35: new XRTexture2D :: _environmentTexture = new XRTexture2D(width, height, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, false)
- L199 C44: new XRTexture2D :: _environmentDepthTexture = new XRTexture2D(width, height, EPixelInternalFormat.DepthComponent24, EPixelFormat.DepthStencil, EPixelType.UnsignedInt248, false)
- L215 C30: new XRRenderBuffer :: _tempDepth = new XRRenderBuffer(width, height, ERenderBufferStorage.Depth24Stencil8);
- L221 C24: new XRViewport :: Viewport = new XRViewport(null, width, height)
- L291 C42: new Vector3 :: return Matrix4x4.CreateScale(new Vector3(-1.0f, 1.0f, 1.0f)) * Matrix4x4.CreateWorld(camPosMirror, camFwdDirMirror, camUpDirMirror);


## XRENGINE/Scene/Components/Capture/SceneCaptureComponent.cs
- L40 C51: new XRViewport :: public XRViewport?[] Viewports { get; } = new XRViewport?[6];
- L44 C13: new Rotator :: new Rotator(0.0f, -90.0f, 180.0f).ToQuaternion(), // +X
- L45 C13: new Rotator :: new Rotator(0.0f, 90.0f, 180.0f).ToQuaternion(),  // -X
- L46 C13: new Rotator :: new Rotator(90.0f, 0.0f, 0.0f).ToQuaternion(),    // +Y
- L47 C13: new Rotator :: new Rotator(-90.0f, 0.0f, 0.0f).ToQuaternion(),   // -Y
- L48 C13: new Rotator :: new Rotator(0.0f, 180.0f, 180.0f).ToQuaternion(), // +Z
- L49 C13: new Rotator :: new Rotator(0.0f, 0.0f, 180.0f).ToQuaternion(),   // -Z
- L95 C42: new XRTextureCube :: _environmentTextureCubemap = new XRTextureCube(Resolution, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, false)
- L113 C51: new XRTextureCube :: _environmentDepthTextureCubemap = new XRTextureCube(Resolution, EPixelInternalFormat.DepthComponent24, EPixelFormat.DepthStencil, EPixelType.UnsignedInt248, false)
- L130 C30: new XRRenderBuffer :: _tempDepth = new XRRenderBuffer(Resolution, Resolution, ERenderBufferStorage.Depth24Stencil8);
- L135 C26: new XRCubeFrameBuffer :: _renderFBO = new XRCubeFrameBuffer(null);
- L146 C32: new XRViewport :: Viewports[i] = new XRViewport(null, Resolution, Resolution)
- L150 C38: new DefaultRenderPipeline :: RenderPipeline = new DefaultRenderPipeline(),
- L297 C52: new() :: RenderingParameters renderParams = new();
- L304 C39: new XRMaterial :: _octahedralMaterial = new XRMaterial([_environmentTextureCubemap], GetFullscreenTriVertexShader(), GetCubemapToOctaShader())
- L308 C34: new XRQuadFrameBuffer :: _octahedralFBO = new XRQuadFrameBuffer(_octahedralMaterial);
- L400 C57: new BoundingRectangle :: AbstractRenderer.Current?.SetRenderArea(new BoundingRectangle(IVector2.Zero, new IVector2(width, height)));
- L400 C94: new IVector2 :: AbstractRenderer.Current?.SetRenderArea(new BoundingRectangle(IVector2.Zero, new IVector2(width, height)));


## XRENGINE/Scene/Components/Debug/Draw/DebugDrawComponent.cs
- L14 C35: new RenderCommandMethod3D :: ri.RenderCommands.Add(new RenderCommandMethod3D(EDefaultRenderPass.OnTopForward, Render));
- L36 C27: new DebugDrawSphere :: => Shapes.Add(new DebugDrawSphere(radius, localOffset, color, solid));
- L38 C27: new DebugDrawBox :: => Shapes.Add(new DebugDrawBox(halfExtents, localOffset, color, solid));
- L40 C27: new DebugDrawCircle :: => Shapes.Add(new DebugDrawCircle(radius, localOffset, localNormal, color, solid));
- L42 C27: new DebugDrawCapsule :: => Shapes.Add(new DebugDrawCapsule(radius, localStartOffset, localEndOffset, color, solid));
- L44 C27: new DebugDrawCone :: => Shapes.Add(new DebugDrawCone(radius, height, localOffset, localUpAxis, color, solid));
- L46 C27: new DebugDrawCylinder :: => Shapes.Add(new DebugDrawCylinder(radius, halfHeight, localOffset, localUpAxis, color, solid));
- L48 C27: new DebugDrawLine :: => Shapes.Add(new DebugDrawLine(localStartOffset, localEndOffset, color));
- L50 C27: new DebugDrawPoint :: => Shapes.Add(new DebugDrawPoint(localOffset, color));


## XRENGINE/Scene/Components/Debug/Visualize/ModelBvhPreviewComponent.cs
- L22 C60: new() :: private readonly Stack<BVHNode<Triangle>> _nodeStack = new();
- L23 C60: new() :: private readonly HashSet<XRMesh> _attemptedBvhBuilds = new();


## XRENGINE/Scene/Components/Editing/BSPMeshComponent.cs
- L19 C66: new() :: private readonly EventList<BSPMeshModifier> _modifiers = new() { ThreadSafe = true };
- L91 C29: new() :: BSPNode a = new();
- L94 C29: new() :: BSPNode b = new();
- L108 C24: new RenderableMesh :: Meshes.Add(new RenderableMesh(new SubMesh(resultMesh, Material), this));
- L108 C43: new SubMesh :: Meshes.Add(new RenderableMesh(new SubMesh(resultMesh, Material), this));
- L123 C31: new Triangle :: triangles.Add(new Triangle(a, b, c));
- L131 C24: new XRMesh :: return new XRMesh([]);
- L138 C27: new VertexTriangle :: prims.Add(new VertexTriangle(new Vertex(t.A, n), new Vertex(t.B, n), new Vertex(t.C, n)));
- L138 C46: new Vertex :: prims.Add(new VertexTriangle(new Vertex(t.A, n), new Vertex(t.B, n), new Vertex(t.C, n)));
- L138 C66: new Vertex :: prims.Add(new VertexTriangle(new Vertex(t.A, n), new Vertex(t.B, n), new Vertex(t.C, n)));
- L138 C86: new Vertex :: prims.Add(new VertexTriangle(new Vertex(t.A, n), new Vertex(t.B, n), new Vertex(t.C, n)));
- L140 C20: new XRMesh :: return new XRMesh(prims);


## XRENGINE/Scene/Components/Editing/TransformTool3D.cs
- L31 C19: new RenderCommandMethod3D :: _rc = new RenderCommandMethod3D((int)EDefaultRenderPass.OnTopForward, Render);
- L117 C33: new SceneNode :: _instanceNode = new SceneNode("TransformTool3D");
- L141 C50: new XRMaterial :: private readonly XRMaterial[] _axisMat = new XRMaterial[3];
- L142 C56: new XRMaterial :: private readonly XRMaterial[] _transPlaneMat = new XRMaterial[6];
- L143 C56: new XRMaterial :: private readonly XRMaterial[] _scalePlaneMat = new XRMaterial[3];
- L174 C42: new Model :: translationModelComp.Model = new Model(translationMeshes);
- L178 C42: new Model :: nonRotationModelComp.Model = new Model(nonRotationMeshes);
- L182 C36: new Model :: scaleModelComp.Model = new Model(scaleMeshes);
- L186 C39: new Model :: rotationModelComp.Model = new Model(rotationMeshes);
- L195 C45: new Model :: screenRotationModelComp.Model = new Model(screenRotationMeshes);
- L199 C48: new Model :: screenTranslationModelComp.Model = new Model(screenTranslationMeshes);
- L269 C39: new SubMesh :: nonRotationMeshes.Add(new SubMesh(axisPrim, axisMat));
- L270 C39: new SubMesh :: nonRotationMeshes.Add(new SubMesh(arrowPrim, axisMat));
- L273 C39: new SubMesh :: translationMeshes.Add(new SubMesh(transPrim1, planeMat1));
- L274 C39: new SubMesh :: translationMeshes.Add(new SubMesh(transPrim2, planeMat2));
- L277 C33: new SubMesh :: scaleMeshes.Add(new SubMesh(scalePrim, scalePlaneMat));
- L280 C36: new SubMesh :: rotationMeshes.Add(new SubMesh(rotPrim, axisMat));
- L287 C25: new Vector3 :: Vertex v1 = new Vector3(-_screenTransExtent, -_screenTransExtent, 0.0f);
- L288 C25: new Vector3 :: Vertex v2 = new Vector3(_screenTransExtent, -_screenTransExtent, 0.0f);
- L289 C25: new Vector3 :: Vertex v3 = new Vector3(_screenTransExtent, _screenTransExtent, 0.0f);
- L290 C25: new Vector3 :: Vertex v4 = new Vector3(-_screenTransExtent, _screenTransExtent, 0.0f);
- L295 C38: new SubMesh :: screenRotationMeshes.Add(new SubMesh(screenRotPrim, _screenMat));
- L297 C41: new SubMesh :: screenTranslationMeshes.Add(new SubMesh(screenTransPrim, _screenMat));
- L332 C45: new Vector4 :: transLine1.Vertex0.ColorSets = [new Vector4(unit1, 1.0f)];
- L333 C45: new Vector4 :: transLine1.Vertex1.ColorSets = [new Vector4(unit1, 1.0f)];
- L336 C45: new Vector4 :: transLine2.Vertex0.ColorSets = [new Vector4(unit2, 1.0f)];
- L337 C45: new Vector4 :: transLine2.Vertex1.ColorSets = [new Vector4(unit2, 1.0f)];
- L340 C45: new Vector4 :: scaleLine1.Vertex0.ColorSets = [new Vector4(unit, 1.0f)];
- L341 C45: new Vector4 :: scaleLine1.Vertex1.ColorSets = [new Vector4(unit, 1.0f)];
- L344 C45: new Vector4 :: scaleLine2.Vertex0.ColorSets = [new Vector4(unit, 1.0f)];
- L345 C45: new Vector4 :: scaleLine2.Vertex1.ColorSets = [new Vector4(unit, 1.0f)];
- L409 C32: new SubMesh :: rotationMeshes.Add(new SubMesh(spherePrim, sphereMat));
- L471 C41: new() :: phys.AddActor(_linkRB = new() { Flags = PxRigidBodyFlags.Kinematic, ActorFlags = PxActorFlags.DisableGravity });
- L494 C41: new() :: phys.AddActor(_linkRB = new() { Flags = PxRigidBodyFlags.Kinematic, ActorFlags = PxActorFlags.DisableGravity });
- L1119 C45: new Vector3 :: Vector3?[] intersectionPoints = new Vector3?[3];
- L1177 C45: new Vector3 :: Vector3?[] intersectionPoints = new Vector3?[3];
- L1218 C51: new Vector3 :: if (XRMath.IsInTriangle(diff, new Vector3(), new Vector3(halfDist, 0, 0), new Vector3(0, halfDist, 0)))
- L1218 C66: new Vector3 :: if (XRMath.IsInTriangle(diff, new Vector3(), new Vector3(halfDist, 0, 0), new Vector3(0, halfDist, 0)))
- L1218 C95: new Vector3 :: if (XRMath.IsInTriangle(diff, new Vector3(), new Vector3(halfDist, 0, 0), new Vector3(0, halfDist, 0)))
- L1220 C55: new Vector3 :: if (XRMath.IsInTriangle(diff, new Vector3(), new Vector3(centerDist, 0, 0), new Vector3(0, centerDist, 0)))
- L1220 C70: new Vector3 :: if (XRMath.IsInTriangle(diff, new Vector3(), new Vector3(centerDist, 0, 0), new Vector3(0, centerDist, 0)))
- L1220 C101: new Vector3 :: if (XRMath.IsInTriangle(diff, new Vector3(), new Vector3(centerDist, 0, 0), new Vector3(0, centerDist, 0)))
- L1225 C56: new Vector3 :: else if (XRMath.IsInTriangle(diff, new Vector3(), new Vector3(halfDist, 0, 0), new Vector3(0, 0, halfDist)))
- L1225 C71: new Vector3 :: else if (XRMath.IsInTriangle(diff, new Vector3(), new Vector3(halfDist, 0, 0), new Vector3(0, 0, halfDist)))
- L1225 C100: new Vector3 :: else if (XRMath.IsInTriangle(diff, new Vector3(), new Vector3(halfDist, 0, 0), new Vector3(0, 0, halfDist)))
- L1227 C55: new Vector3 :: if (XRMath.IsInTriangle(diff, new Vector3(), new Vector3(centerDist, 0, 0), new Vector3(0, 0, centerDist)))
- L1227 C70: new Vector3 :: if (XRMath.IsInTriangle(diff, new Vector3(), new Vector3(centerDist, 0, 0), new Vector3(0, 0, centerDist)))
- L1227 C101: new Vector3 :: if (XRMath.IsInTriangle(diff, new Vector3(), new Vector3(centerDist, 0, 0), new Vector3(0, 0, centerDist)))
- L1232 C56: new Vector3 :: else if (XRMath.IsInTriangle(diff, new Vector3(), new Vector3(0, halfDist, 0), new Vector3(0, 0, halfDist)))
- L1232 C71: new Vector3 :: else if (XRMath.IsInTriangle(diff, new Vector3(), new Vector3(0, halfDist, 0), new Vector3(0, 0, halfDist)))
- L1232 C100: new Vector3 :: else if (XRMath.IsInTriangle(diff, new Vector3(), new Vector3(0, halfDist, 0), new Vector3(0, 0, halfDist)))
- L1234 C55: new Vector3 :: if (XRMath.IsInTriangle(diff, new Vector3(), new Vector3(0, centerDist, 0), new Vector3(0, 0, centerDist)))
- L1234 C70: new Vector3 :: if (XRMath.IsInTriangle(diff, new Vector3(), new Vector3(0, centerDist, 0), new Vector3(0, 0, centerDist)))
- L1234 C101: new Vector3 :: if (XRMath.IsInTriangle(diff, new Vector3(), new Vector3(0, centerDist, 0), new Vector3(0, 0, centerDist)))


## XRENGINE/Scene/Components/Landscape/LandscapeComponent.cs
- L154 C64: new() :: private readonly TerrainShaderAssembler _shaderAssembler = new();
- L189 C36: new XRMesh :: private XRMesh?[] _lodMeshes = new XRMesh?[MaxLODLevels];
- L192 C46: new float :: private readonly float[] _lodDistances = new float[MaxLODLevels];
- L315 C19: new InvalidOperationException :: throw new InvalidOperationException($"Maximum of {MaxLayers} layers supported.");
- L451 C19: new HeightmapModule :: AddModule(new HeightmapModule());
- L452 C19: new SlopeSplatModule :: AddModule(new SlopeSplatModule());
- L455 C18: new TerrainLayer :: AddLayer(new TerrainLayer { Name = "Grass", Tint = new ColorF4(0.3f, 0.5f, 0.2f, 1.0f), Roughness = 0.7f });
- L455 C60: new ColorF4 :: AddLayer(new TerrainLayer { Name = "Grass", Tint = new ColorF4(0.3f, 0.5f, 0.2f, 1.0f), Roughness = 0.7f });
- L456 C18: new TerrainLayer :: AddLayer(new TerrainLayer { Name = "Rock", Tint = new ColorF4(0.5f, 0.5f, 0.5f, 1.0f), Roughness = 0.8f });
- L456 C59: new ColorF4 :: AddLayer(new TerrainLayer { Name = "Rock", Tint = new ColorF4(0.5f, 0.5f, 0.5f, 1.0f), Roughness = 0.8f });
- L457 C18: new TerrainLayer :: AddLayer(new TerrainLayer { Name = "Cliff", Tint = new ColorF4(0.4f, 0.35f, 0.3f, 1.0f), Roughness = 0.9f });
- L457 C60: new ColorF4 :: AddLayer(new TerrainLayer { Name = "Cliff", Tint = new ColorF4(0.4f, 0.35f, 0.3f, 1.0f), Roughness = 0.9f });
- L551 C27: new XRRenderProgram :: _lodProgram = new XRRenderProgram(true, false, _lodShader);
- L552 C31: new XRRenderProgram :: _cullingProgram = new XRRenderProgram(true, false, _cullingShader);
- L553 C31: new XRRenderProgram :: _normalsProgram = new XRRenderProgram(true, false, _normalsShader);
- L583 C31: new XRShader :: _splatGenShader = new XRShader(EShaderType.Compute, splatSource);
- L584 C32: new XRShader :: _heightGenShader = new XRShader(EShaderType.Compute, heightSource);
- L586 C32: new XRRenderProgram :: _splatGenProgram = new XRRenderProgram(true, false, _splatGenShader);
- L587 C33: new XRRenderProgram :: _heightGenProgram = new XRRenderProgram(true, false, _heightGenShader);
- L607 C32: new XRRenderProgram :: _splatGenProgram = new XRRenderProgram(true, false, _splatGenShader);
- L687 C26: new float :: _heightmapData = new float[res * res];
- L943 C21: new Vector4 :: new Vector4(layer.Tint.R, layer.Tint.G, layer.Tint.B, layer.Tint.A));
- L975 C22: new GPUTerrainChunk :: _chunkData = new GPUTerrainChunk[totalChunks];
- L979 C25: new XRDataBuffer :: _chunksBuffer = new XRDataBuffer(
- L990 C32: new XRDataBuffer :: _visibleChunksBuffer = new XRDataBuffer(
- L1001 C32: new XRDataBuffer :: _terrainParamsBuffer = new XRDataBuffer(
- L1012 C29: new XRDataBuffer :: _chunkCountBuffer = new XRDataBuffer(
- L1023 C31: new XRDataBuffer :: _indirectDrawBuffer = new XRDataBuffer(
- L1063 C37: new GPUTerrainChunk :: _chunkData[index] = new GPUTerrainChunk
- L1065 C37: new Vector3 :: WorldPosition = new Vector3(worldX, (MinHeight + MaxHeight) * 0.5f, worldZ),
- L1067 C39: new Vector2 :: HeightmapOffset = new Vector2(u0, v0),
- L1068 C38: new Vector2 :: HeightmapScale = new Vector2(uScale, vScale),
- L1117 C56: new Vector3 :: _chunkData[chunkIndex].WorldPosition = new Vector3(
- L1184 C24: new Vertex :: var vertices = new Vertex[vertexCount];
- L1185 C23: new List :: var indices = new List<ushort>(indexCount);
- L1199 C27: new Vector3 :: var pos = new Vector3(u - 0.5f, 0, v - 0.5f);
- L1200 C30: new Vertex :: var vertex = new Vertex(pos);
- L1201 C49: new Vector2 :: vertex.TextureCoordinateSets = [new Vector2(u, v)];
- L1230 C16: new XRMesh :: return new XRMesh(vertices, indices);
- L1253 C32: new XRMeshRenderer :: _terrainRenderer = new XRMeshRenderer(_lodMeshes[0], material);
- L1278 C42: new AABB :: _renderInfo.LocalCullingVolume = new AABB(
- L1279 C13: new Vector3 :: new Vector3(-halfSize, MinHeight, -halfSize),
- L1280 C13: new Vector3 :: new Vector3(halfSize, MaxHeight, halfSize));
- L1300 C24: new XRMaterial :: var material = new XRMaterial(vertShader, fragShader)
- L1398 C26: new GPUTerrainParams :: _terrainParams = new GPUTerrainParams
- L1402 C29: new Vector2 :: HeightmapSize = new Vector2(HeightmapResolution, HeightmapResolution),
- L1419 C42: new[] :: _terrainParamsBuffer?.SetDataRaw(new[] { _terrainParams });
- L1424 C16: new Vector4 :: return new Vector4(
- L1458 C39: new uint :: _chunkCountBuffer?.SetDataRaw(new uint[] { 0 });
- L1512 C13: new Vector4 :: new Vector4(1, 0, 0, float.MaxValue),
- L1513 C13: new Vector4 :: new Vector4(-1, 0, 0, float.MaxValue),
- L1514 C13: new Vector4 :: new Vector4(0, 1, 0, float.MaxValue),
- L1515 C13: new Vector4 :: new Vector4(0, -1, 0, float.MaxValue),
- L1516 C13: new Vector4 :: new Vector4(0, 0, 1, float.MaxValue),
- L1517 C13: new Vector4 :: new Vector4(0, 0, -1, float.MaxValue)
- L1523 C16: new Vector4 :: return new Vector4(plane.Normal, plane.D);
- L1590 C26: new Vector3 :: Vector3 normal = new Vector3(hL - hR, 2.0f * delta, hD - hU);
- L1766 C20: new float :: var copy = new float[_heightmapData.Length];
- L1807 C36: new ShaderFloat :: param = (T)(object)new ShaderFloat((float)(object)value, name, null);
- L1809 C36: new ShaderInt :: param = (T)(object)new ShaderInt((int)(object)value, name, null);
- L1811 C36: new ShaderVector3 :: param = (T)(object)new ShaderVector3((Vector3)(object)value, name, null);
- L1813 C36: new ShaderVector4 :: param = (T)(object)new ShaderVector4((Vector4)(object)value, name, null);
- L1815 C23: new NotImplementedException :: throw new NotImplementedException($"Shader type {typeof(T).Name} not supported in SetUniform auto-creation yet.");


## XRENGINE/Scene/Components/Landscape/TerrainShaderAssembler.cs
- L419 C35: new StringBuilder :: var uniformDeclarations = new StringBuilder();
- L420 C31: new StringBuilder :: var moduleFunctions = new StringBuilder();
- L421 C31: new StringBuilder :: var moduleSplatCode = new StringBuilder();
- L460 C35: new StringBuilder :: var uniformDeclarations = new StringBuilder();
- L461 C32: new StringBuilder :: var moduleHeightCode = new StringBuilder();
- L497 C35: new StringBuilder :: var uniformDeclarations = new StringBuilder();
- L519 C35: new StringBuilder :: var uniformDeclarations = new StringBuilder();
- L566 C22: new string :: var indent = new string(' ', spaces);
- L568 C22: new StringBuilder :: var result = new StringBuilder();


## XRENGINE/Scene/Components/Lights/RadianceCascadeComponents.cs
- L274 C43: new PropertyChangedEventArgs :: PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));


## XRENGINE/Scene/Components/Lights/Types/DirectionalLightComponent.cs
- L94 C39: new Vector3 :: => XRMesh.Shapes.SolidBox(new Vector3(-0.5f), new Vector3(0.5f));
- L94 C59: new Vector3 :: => XRMesh.Shapes.SolidBox(new Vector3(-0.5f), new Vector3(0.5f));
- L96 C43: new Vector3 :: => XRMesh.Shapes.WireframeBox(new Vector3(-0.5f), new Vector3(0.5f));
- L96 C63: new Vector3 :: => XRMesh.Shapes.WireframeBox(new Vector3(-0.5f), new Vector3(0.5f));
- L104 C30: new float :: float[] result = new float[count];
- L162 C30: new float :: float[] result = new float[_cascadeCount];
- L214 C32: new Vector3 :: cornersWS[0] = new Vector3(min.X, min.Y, min.Z);
- L215 C32: new Vector3 :: cornersWS[1] = new Vector3(min.X, min.Y, max.Z);
- L216 C32: new Vector3 :: cornersWS[2] = new Vector3(min.X, max.Y, min.Z);
- L217 C32: new Vector3 :: cornersWS[3] = new Vector3(min.X, max.Y, max.Z);
- L218 C32: new Vector3 :: cornersWS[4] = new Vector3(max.X, min.Y, min.Z);
- L219 C32: new Vector3 :: cornersWS[5] = new Vector3(max.X, min.Y, max.Z);
- L220 C32: new Vector3 :: cornersWS[6] = new Vector3(max.X, max.Y, min.Z);
- L221 C32: new Vector3 :: cornersWS[7] = new Vector3(max.X, max.Y, max.Z);
- L297 C84: new Vector3 :: Vector3 centerLS = fwdLS * centerSlice + perpLS * centerPerp + new Vector3(0, 0, centerZ);
- L304 C39: new CascadedShadowAabb :: _cascadeAabbs.Add(new CascadedShadowAabb(
- L325 C79: new Transform :: private Transform ShadowCameraTransform => _shadowCameraTransform ??= new Transform()
- L409 C18: new XRTexture2D :: new XRTexture2D(width, height, GetShadowDepthMapFormat(precision), EPixelFormat.DepthComponent, EPixelType.Float)
- L421 C40: new XRShader :: XRMaterial mat = new(refs, new XRShader(EShaderType.Fragment, ShaderHelper.Frag_Nothing));


## XRENGINE/Scene/Components/Lights/Types/LightComponent.cs
- L96 C73: new ColorF4 :: XRMaterial mat = XRMaterial.CreateUnlitColorMaterialForward(new ColorF4(0.0f, 1.0f, 0.0f, 0.0f));
- L99 C36: new XRMeshRenderer :: _shadowVolumeRC.Mesh = new XRMeshRenderer(GetWireframeMesh(), mat);
- L225 C29: new XRMaterialFrameBuffer :: ShadowMap = new XRMaterialFrameBuffer(GetShadowMapMaterial(width, height));
- L283 C46: new FrustumIntersectionAabb :: _cameraIntersections.Add(new FrustumIntersectionAabb(i, min, max));


## XRENGINE/Scene/Components/Lights/Types/OneViewLightComponent.cs
- L17 C30: new ShadowRenderPipeline :: RenderPipeline = new ShadowRenderPipeline(),


## XRENGINE/Scene/Components/Lights/Types/PointLightComponent.cs
- L19 C54: new XRViewport :: protected readonly XRViewport[] _viewports = new XRViewport[6].Fill(x => new(null, 1024, 1024)
- L21 C30: new ShadowRenderPipeline :: RenderPipeline = new ShadowRenderPipeline(),
- L112 C32: new Sphere :: _influenceVolume = new Sphere(Vector3.Zero, radius);
- L227 C17: new XRTextureCube :: new XRTextureCube(cubeExtent, GetShadowDepthMapFormat(precision), EPixelFormat.DepthComponent, EPixelType.UnsignedByte, false)
- L236 C17: new XRTextureCube :: new XRTextureCube(cubeExtent, EPixelInternalFormat.R16f, EPixelFormat.Red, EPixelType.HalfFloat, false)


## XRENGINE/Scene/Components/Lights/Types/ShadowRenderPipeline.cs
- L43 C20: new() :: return new()


## XRENGINE/Scene/Components/Lights/Types/SpotLightComponent.cs
- L153 C17: new XRTexture2D :: new XRTexture2D(width, height, GetShadowDepthMapFormat(precision), EPixelFormat.DepthComponent, EPixelType.UnsignedByte)
- L161 C17: new XRTexture2D :: new XRTexture2D(width, height, EPixelInternalFormat.R16f, EPixelFormat.Red, EPixelType.HalfFloat)
- L173 C40: new XRShader :: XRMaterial mat = new(refs, new XRShader(EShaderType.Fragment, ShaderHelper.Frag_DepthOutput));


## XRENGINE/Scene/Components/Mesh/GaussianSplatComponent.cs
- L94 C31: new SubMeshLOD :: SubMesh subMesh = new(new SubMeshLOD(material, mesh, float.PositiveInfinity))
- L100 C17: new Model :: Model = new Model(subMesh);
- L142 C25: new XRMesh :: return (new XRMesh([]), new AABB(), 0);
- L142 C41: new AABB :: return (new XRMesh([]), new AABB(), 0);
- L146 C35: new Vector3 :: Vector3[] positions = new Vector3[count];
- L147 C32: new Vector4 :: Vector4[] colors = new Vector4[count];
- L148 C32: new Vector4 :: Vector4[] scales = new Vector4[count];
- L149 C35: new Vector4 :: Vector4[] rotations = new Vector4[count];
- L162 C29: new Vector4 :: scales[i] = new Vector4(scaledExtents, scaledExtents.Length());
- L237 C33: new RenderingParameters :: RenderOptions = new RenderingParameters()
- L240 C33: new DepthTest :: DepthTest = new DepthTest()


## XRENGINE/Scene/Components/Mesh/HlodGroupComponent.cs
- L41 C58: new() :: private readonly List<SourceHook> _sourceHooks = new();
- L53 C30: new RenderCommandMesh3D :: _renderCommand = new RenderCommandMesh3D((int)EDefaultRenderPass.OpaqueForward)
- L295 C34: new SourceHook :: _sourceHooks.Add(new SourceHook(ri, prev));
- L336 C27: new List :: var results = new List<RenderableMesh>();
- L408 C28: new OctahedralImposterGenerator.Settings :: var settings = new OctahedralImposterGenerator.Settings(ImposterSheetSize, ImposterCapturePadding, ImposterCaptureDepth);
- L444 C27: new SubMeshLOD :: var lod = new SubMeshLOD(sub.Material, sub.Mesh, 0.0f);
- L445 C31: new SubMesh :: subMeshes.Add(new SubMesh(lod));
- L448 C50: new Model :: return subMeshes.Count == 0 ? null : new Model(subMeshes);
- L457 C39: new Dictionary :: var trianglesByMaterial = new Dictionary<XRMaterial, List<VertexTriangle>>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
- L489 C86: new AABB :: localBounds = localBounds?.ExpandedToInclude(localCorner) ?? new AABB(localCorner, localCorner);
- L508 C32: new List :: tris = new List<VertexTriangle>(Math.Max(64, indices.Length / 3));
- L536 C34: new VertexTriangle :: tris.Add(new VertexTriangle(MakeVertex(i0), MakeVertex(i1), MakeVertex(i2)));
- L548 C29: new List :: var submeshes = new List<(XRMesh mesh, XRMaterial material)>(trianglesByMaterial.Count);
- L568 C30: new XRMeshRenderer :: _proxyRenderer = new XRMeshRenderer(submeshes)


## XRENGINE/Scene/Components/Mesh/ModelComponent.cs
- L21 C85: new() :: private readonly ConcurrentDictionary<SubMesh, RenderableMesh> _meshLinks = new();
- L155 C30: new JobProgress :: yield return new JobProgress(1f, "BVH ready");
- L183 C26: new JobProgress :: yield return new JobProgress(1f, "BVH warmup complete");


## XRENGINE/Scene/Components/Mesh/OctahedralBillboardComponent.cs
- L57 C30: new RenderCommandMesh3D :: _renderCommand = new RenderCommandMesh3D((int)EDefaultRenderPass.TransparentForward)
- L124 C33: new Vector2 :: BillboardSize = new Vector2(width, height);
- L158 C25: new XRMeshRenderer :: _renderer = new XRMeshRenderer(mesh, material);
- L195 C33: new RenderingParameters :: RenderOptions = new RenderingParameters
- L199 C33: new DepthTest :: DepthTest = new DepthTest
- L234 C26: new XRShader :: shader = new XRShader(EShaderType.Fragment, ShaderHelper.Frag_Nothing);
- L274 C46: new AABB :: _renderInfo.LocalCullingVolume = new AABB(-halfExtents, halfExtents);
- L336 C20: new Vector2 :: return new Vector2(width, height);


## XRENGINE/Scene/Components/Mesh/RenderableComponent.cs
- L45 C73: new EventList :: public EventList<RenderableMesh> Meshes { get; private set; } = new EventList<RenderableMesh>() { ThreadSafe = true };


## XRENGINE/Scene/Components/Mesh/RenderableMesh.cs
- L29 C80: new() :: private readonly Dictionary<TransformBase, int> _trackedSkinnedBones = new();
- L30 C86: new() :: private readonly Dictionary<TransformBase, Matrix4x4> _currentSkinMatrices = new();
- L32 C54: new() :: private readonly object _relativeCacheLock = new();
- L33 C52: new() :: private readonly object _skinnedDataLock = new();
- L66 C71: new() :: public LinkedList<RenderableLOD> LODs { get; private set; } = new();
- L132 C30: new RenderableLOD :: LODs.AddLast(new RenderableLOD(renderer, lod.MaxVisibleDistance));
- L136 C36: new RenderCommandMethod3D :: _renderBoundsCommand = new RenderCommandMethod3D((int)EDefaultRenderPass.OpaqueForward, DoRenderBounds);
- L137 C60: new RenderCommandMesh3D :: RenderInfo = RenderInfo3D.New(component, _rc = new RenderCommandMesh3D(0));
- L266 C24: new SkinnedMeshBoundsCalculator.Result :: return new SkinnedMeshBoundsCalculator.Result(worldPositions, result.Bounds, basis);
- L268 C34: new Vector3 :: var localPositions = new Vector3[worldPositions.Length];
- L273 C20: new SkinnedMeshBoundsCalculator.Result :: return new SkinnedMeshBoundsCalculator.Result(localPositions, localBounds, basis);
- L438 C43: new Vector3 :: _skinnedVertexPositions = new Vector3[vertices.Length];
- L472 C31: new AABB :: var localBounds = new AABB(min, max);
- L473 C35: new SkinnedMeshBoundsCalculator.Result :: var localizedResult = new SkinnedMeshBoundsCalculator.Result((Vector3[])_skinnedVertexPositions.Clone(), localBounds, basis);


## XRENGINE/Scene/Components/Mesh/Shapes/ConeMeshComponent.cs
- L22 C29: new Cone :: Shape = new Cone(Vector3.Zero, Globals.Up, Height, Radius);


## XRENGINE/Scene/Components/Mesh/Shapes/ShapeMeshComponent.cs
- L34 C36: new RenderableMesh :: Meshes.Add(new RenderableMesh(new(XRMesh.Shapes.FromVolume(Shape, false), Material), this));


## XRENGINE/Scene/Components/Mesh/Shapes/SphereMeshComponent.cs
- L30 C29: new Sphere :: Shape = new Sphere(Vector3.Zero, Radius);


## XRENGINE/Scene/Components/Misc/AdvancedForwardMirrorComponent.cs
- L24 C33: new() :: mat.RenderOptions = new()
- L29 C31: new StencilTest :: StencilTest = new StencilTest()
- L36 C29: new DepthTest :: DepthTest = new DepthTest()
- L49 C25: new RenderCommandMesh3D :: _rcMirror = new RenderCommandMesh3D((int)EDefaultRenderPass.Background, rend, Matrix4x4.Identity);
- L56 C59: new() :: private static StencilTestFace MirrorStencil() => new()
- L93 C62: new Plane :: public Plane ReflectionPlane { get; private set; } = new Plane(Globals.Backward, 0);
- L95 C64: new AABB :: public AABB LocalCullingVolume { get; private set; } = new AABB(Vector3.Zero, Vector3.Zero);
- L96 C63: new Box :: public Box WorldCullingVolume { get; private set; } = new Box(Vector3.Zero, Vector3.Zero, Matrix4x4.Identity);
- L115 C42: new AABB :: LocalCullingVolume = new AABB(
- L116 C25: new Vector3 :: new Vector3(0, 0, 0),
- L117 C25: new Vector3 :: new Vector3(MirrorWidth, MirrorHeight, 0.001f));


## XRENGINE/Scene/Components/Misc/DeferredDecalComponent.cs
- L38 C75: new RenderCommandMethod3D :: DebugRenderInfo = RenderInfo3D.New(this, DebugRenderCommand = new RenderCommandMethod3D((int)EDefaultRenderPass.OpaqueForward, RenderDebug));
- L53 C27: new Vector3 :: HalfExtents = new Vector3(texture.Width * 0.5f, depth, texture.Height * 0.5f);
- L61 C27: new Vector3 :: HalfExtents = new Vector3(texture.Width * 0.5f, Math.Max(texture.Width, texture.Height) * 0.5f, texture.Height * 0.5f);
- L96 C45: new AABB :: RenderInfo.LocalCullingVolume = new AABB(-HalfExtents, HalfExtents);
- L122 C53: new() :: RenderingParameters decalRenderParams = new()
- L126 C29: new DepthTest :: DepthTest = new DepthTest() { Enabled = ERenderParamUsage.Disabled }
- L128 C20: new XRMaterial :: return new XRMaterial(decalVars, decalRefs, GetDefaultShader())
- L141 C39: new XRMeshRenderer :: RenderCommandDecal.Mesh = new XRMeshRenderer(XRMesh.Shapes.SolidBox(-Vector3.One, Vector3.One), Material);
- L173 C15: new RenderCommandMesh3D :: = new RenderCommandMesh3D(EDefaultRenderPass.DeferredDecals);


## XRENGINE/Scene/Components/Misc/SkyboxComponent.cs
- L109 C30: new RenderCommandMesh3D :: _renderCommand = new RenderCommandMesh3D(EDefaultRenderPass.Background);
- L318 C17: new Vertex :: new Vertex(new Vector3(-1, -1, 0)),
- L318 C28: new Vector3 :: new Vertex(new Vector3(-1, -1, 0)),
- L319 C17: new Vertex :: new Vertex(new Vector3(3, -1, 0)),
- L319 C28: new Vector3 :: new Vertex(new Vector3(3, -1, 0)),
- L320 C17: new Vertex :: new Vertex(new Vector3(-1, 3, 0)));
- L320 C28: new Vector3 :: new Vertex(new Vector3(-1, 3, 0)));
- L342 C48: new() :: RenderingParameters renderParams = new()
- L345 C29: new DepthTest :: DepthTest = new DepthTest()
- L355 C25: new XRMaterial :: _material = new XRMaterial(tex is not null ? [tex] : [], vertexShader, fragmentShader)
- L369 C33: new XRMeshRenderer :: _meshRenderer = new XRMeshRenderer(_mesh, _material);
- L438 C36: new XRShader :: s_vertexShader ??= new XRShader(EShaderType.Vertex, VertexShaderSource);
- L453 C41: new XRShader :: return s_gradientShader ??= new XRShader(EShaderType.Fragment, GradientShaderSource);
- L465 C38: new XRShader :: s_equirectShader ??= new XRShader(EShaderType.Fragment, EquirectShaderSource);
- L479 C40: new XRShader :: s_octahedralShader ??= new XRShader(EShaderType.Fragment, OctahedralShaderSource);
- L493 C37: new XRShader :: s_cubemapShader ??= new XRShader(EShaderType.Fragment, CubemapShaderSource);
- L507 C42: new XRShader :: s_cubemapArrayShader ??= new XRShader(EShaderType.Fragment, CubemapArrayShaderSource);
- L771 C20: new SkyboxComponent :: return new SkyboxComponent
- L784 C20: new SkyboxComponent :: return new SkyboxComponent
- L797 C20: new SkyboxComponent :: return new SkyboxComponent
- L810 C20: new SkyboxComponent :: return new SkyboxComponent


## XRENGINE/Scene/Components/Movement/CharacterMovementComponent.cs
- L113 C42: new XRMeshRenderer :: _capsuleRenderMeshRenderer = new XRMeshRenderer(null, _capsuleRenderMaterial);
- L114 C37: new RenderCommandMesh3D :: _capsuleRenderCommand = new RenderCommandMesh3D((int)EDefaultRenderPass.OnTopForward, _capsuleRenderMeshRenderer, Matrix4x4.Identity, null);
- L141 C41: new FeetInches :: private float _standingHeight = new FeetInches(5, 2.0f).ToMeters();
- L142 C41: new FeetInches :: private float _crouchedHeight = new FeetInches(3, 0.0f).ToMeters();
- L143 C38: new FeetInches :: private float _proneHeight = new FeetInches(1, 0.0f).ToMeters();
- L169 C50: new ModernMovementModule :: private MovementModule _movementModule = new ModernMovementModule();
- L203 C59: new ModernMovementModule :: set => SetField(ref _movementModule, value ?? new ModernMovementModule());
- L627 C67: new Vector3 :: _capsuleRenderInfo.LocalCullingVolume = AABB.FromSize(new Vector3(radius * 2.0f, yHalfExtent * 2.0f, radius * 2.0f));
- L690 C36: new PhysxCharacterControllerAdapter :: ActiveController = new PhysxCharacterControllerAdapter(_physxController);
- L693 C50: new PhysxControllerActorProxy :: unsafe { _controllerActorProxy = new PhysxControllerActorProxy(_physxController.ControllerPtr); }
- L705 C35: new JoltCharacterVirtualController :: _joltController = new JoltCharacterVirtualController(joltScene, pos)
- L715 C36: new JoltCharacterControllerAdapter :: ActiveController = new JoltCharacterControllerAdapter(_joltController);
- L898 C20: new MovementModule.MovementContext :: return new MovementModule.MovementContext(
- L998 C65: new Vector3 :: Vector3 inputDirection = posDelta != Vector3.Zero ? new Vector3(posDelta.X, 0, posDelta.Z).Normalized() : Vector3.Zero;
- L1027 C25: new Vector3 :: delta = new Vector3(delta.X * frictionFactor, delta.Y, delta.Z * frictionFactor);


## XRENGINE/Scene/Components/Movement/HeightScaleBaseComponent.cs
- L277 C30: new Vector3 :: eyePosWorldAvg = new Vector3(sumX, sumY, sumZ);


## XRENGINE/Scene/Components/Movement/Modules/MovementModule.cs
- L386 C24: new MovementResult :: return new MovementResult(horizontal + up * vertical, gravityApplied, requestedMode);
- L394 C24: new MovementResult :: return new MovementResult(NewVelocity, GravityApplied, mode);
- L440 C20: new MovementResult :: return new MovementResult(newVelocity);


## XRENGINE/Scene/Components/Movement/PlayerMovementComponentBase.cs
- L48 C33: new Vector3 :: => AddMovementInput(new Vector3(x, y, z));


## XRENGINE/Scene/Components/Networking/NetworkDiscoveryComponent.cs
- L222 C23: new TaskCompletionSource :: var tcs = new TaskCompletionSource<Engine.BaseNetworkingManager?>();
- L251 C23: new TaskCompletionSource :: var tcs = new TaskCompletionSource<Engine.BaseNetworkingManager?>();
- L257 C85: new GameStartupSettings :: GameStartupSettings startup = settings ?? AdvertisedSettings ?? new GameStartupSettings();
- L278 C79: new GameStartupSettings :: GameStartupSettings settings = ClientSettingsFactory?.Invoke() ?? new GameStartupSettings();
- L296 C34: new CancellationTokenSource :: => _discoveryCts ??= new CancellationTokenSource();
- L325 C36: new IPEndPoint :: client.Client.Bind(new IPEndPoint(IPAddress.Any, _discoveryPort));
- L362 C37: new IPEndPoint :: multicastEndPoint = new IPEndPoint(multicast, _discoveryPort);
- L394 C66: new GameStartupSettings :: GameStartupSettings settings = AdvertisedSettings ?? new GameStartupSettings();
- L397 C20: new DiscoveryAnnouncement :: return new DiscoveryAnnouncement
- L460 C32: new DiscoveredEndpoint :: _discovered[key] = new DiscoveredEndpoint(announcement, now);


## XRENGINE/Scene/Components/Networking/OscSenderComponent.cs
- L35 C22: new OscClient :: Client = new OscClient("127.0.0.1", port);


## XRENGINE/Scene/Components/Networking/RestApiComponent.cs
- L26 C51: new() :: private readonly object _httpClientLock = new();
- L29 C60: new() :: private CancellationTokenSource _requestScopeCts = new();
- L88 C27: new ArgumentOutOfRangeException :: throw new ArgumentOutOfRangeException(nameof(value), "Timeout must be greater than zero.");
- L121 C23: new ArgumentException :: throw new ArgumentException("Header name cannot be empty.", nameof(name));
- L174 C26: new RestApiRequest :: => SendAsync(new RestApiRequest
- L185 C37: new RestApiRequest :: => SendAsync<TResponse>(new RestApiRequest
- L196 C26: new RestApiRequest :: => SendAsync(new RestApiRequest
- L207 C37: new RestApiRequest :: => SendAsync<TResponse>(new RestApiRequest
- L223 C23: new InvalidOperationException :: throw new InvalidOperationException("REST client has not been initialized.");
- L240 C36: new RestApiResponse :: var restResponse = new RestApiResponse(
- L255 C27: new RestApiRequestException :: throw new RestApiRequestException(request, restResponse);
- L277 C24: new RestApiResponse :: return new RestApiResponse<TResponse>(response, default);
- L284 C24: new RestApiResponse :: return new RestApiResponse<TResponse>(response, payload);
- L288 C48: new RestApiDeserializationException :: var deserializationException = new RestApiDeserializationException(typeof(TResponse), request, response, ex);
- L299 C71: new CancellationTokenSource :: var previous = Interlocked.Exchange(ref _requestScopeCts, new CancellationTokenSource());
- L340 C34: new SocketsHttpHandler :: _httpHandler ??= new SocketsHttpHandler
- L347 C27: new HttpClient :: _client = new HttpClient(_httpHandler, false)
- L397 C27: new HttpRequestMessage :: var message = new HttpRequestMessage(request.Method, targetUri);
- L417 C24: new FormUrlEncodedContent :: return new FormUrlEncodedContent(request.FormFields);
- L421 C31: new ByteArrayContent :: var content = new ByteArrayContent(binary.ToArray());
- L428 C24: new StringContent :: return new StringContent(request.RawPayload, Encoding.UTF8, request.ContentType);
- L436 C24: new StringContent :: return new StringContent(json, Encoding.UTF8, request.ContentType);
- L454 C23: new InvalidOperationException :: throw new InvalidOperationException("BaseUrl must be set to send relative REST requests.");
- L456 C20: new Uri :: return new Uri(_client.BaseAddress, target);
- L468 C27: new StringBuilder :: var builder = new StringBuilder(normalized);
- L506 C27: new Dictionary :: var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
- L728 C20: new ReadOnlyMemory :: Body = new ReadOnlyMemory<byte>(body);
- L793 C16: new MemoryStream :: => new MemoryStream(Body.ToArray(), false);


## XRENGINE/Scene/Components/Networking/TcpClientComponent.cs
- L194 C23: new InvalidOperationException :: throw new InvalidOperationException("TCP connection is not established.");
- L240 C34: new CancellationTokenSource :: _connectionCts = new CancellationTokenSource();
- L282 C31: new InvalidOperationException :: DispatchError(new InvalidOperationException("Host and Port must be configured before connecting."));
- L287 C26: new TcpClient :: var client = new TcpClient
- L302 C37: new SslStream :: var sslStream = new SslStream(stream, leaveInnerStreamOpen: false, ValidateCertificate);
- L303 C39: new SslClientAuthenticationOptions :: var authOptions = new SslClientAuthenticationOptions
- L361 C38: new byte :: byte[] payload = new byte[read];


## XRENGINE/Scene/Components/Networking/TcpServerComponent.cs
- L19 C77: new() :: private readonly ConcurrentDictionary<Guid, ClientState> _clients = new();
- L121 C23: new InvalidOperationException :: throw new InvalidOperationException("Client is no longer connected.");
- L185 C27: new InvalidOperationException :: throw new InvalidOperationException("ListenPort must be greater than zero.");
- L188 C32: new TcpListener :: var listener = new TcpListener(address, _listenPort);
- L192 C32: new CancellationTokenSource :: _listenerCts = new CancellationTokenSource();
- L288 C30: new TcpServerClient :: var descriptor = new TcpServerClient(Guid.NewGuid(), (IPEndPoint?)client.Client.RemoteEndPoint);
- L290 C25: new ClientState :: var state = new ClientState(descriptor.Id, client, stream);
- L309 C38: new byte :: byte[] payload = new byte[read];
- L440 C28: new SemaphoreSlim :: SendLock = new SemaphoreSlim(1, 1);
- L441 C30: new CancellationTokenSource :: ReceiveCts = new CancellationTokenSource();


## XRENGINE/Scene/Components/Networking/UdpSocketComponent.cs
- L165 C23: new InvalidOperationException :: throw new InvalidOperationException("Socket is not bound.");
- L168 C23: new InvalidOperationException :: throw new InvalidOperationException("A valid host and port are required.");
- L172 C23: new InvalidOperationException :: throw new InvalidOperationException($"Unable to resolve host '{host}'.");
- L174 C28: new IPEndPoint :: var endpoint = new IPEndPoint(address, port);
- L219 C37: new IPEndPoint :: var localEndPoint = new IPEndPoint(address, _localPort);
- L220 C30: new UdpClient :: var client = new UdpClient(localEndPoint.AddressFamily);
- L231 C31: new CancellationTokenSource :: _receiveCts = new CancellationTokenSource();
- L280 C36: new UdpDatagram :: var datagram = new UdpDatagram(result.RemoteEndPoint, result.Buffer);


## XRENGINE/Scene/Components/Networking/WebhookListenerComponent.cs
- L19 C73: new() :: private readonly ConcurrentQueue<WebhookEvent> _pendingEvents = new();
- L96 C23: new ArgumentException :: throw new ArgumentException("Header name cannot be blank.", nameof(name));
- L121 C25: new HttpListener :: _listener = new HttpListener();
- L127 C28: new CancellationTokenSource :: _listenerCts = new CancellationTokenSource();
- L202 C36: new StreamReader :: using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8, leaveOpen: false);
- L205 C36: new WebhookEvent :: var webhookEvent = new WebhookEvent(
- L252 C27: new Dictionary :: var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
- L262 C29: new char :: char[] buffer = new char[Math.Min(4096, maxBytes)];
- L264 C32: new StringWriter :: using var writer = new StringWriter();
- L315 C71: new JsonSerializerOptions :: return JsonSerializer.Deserialize<T>(Body, options ?? new JsonSerializerOptions(JsonSerializerDefaults.Web));


## XRENGINE/Scene/Components/Networking/WebSocketClientComponent.cs
- L107 C23: new ArgumentException :: throw new ArgumentException("Header name cannot be blank.", nameof(name));
- L171 C23: new InvalidOperationException :: throw new InvalidOperationException("WebSocket is not connected.");
- L208 C34: new CancellationTokenSource :: _connectionCts = new CancellationTokenSource();
- L249 C31: new InvalidOperationException :: DispatchError(new InvalidOperationException($"WebSocket endpoint '{_endpoint}' is invalid."));
- L254 C26: new ClientWebSocket :: var socket = new ClientWebSocket();
- L306 C47: new MemoryStream :: using var messageStream = new MemoryStream();
- L325 C35: new WebSocketMessage :: var message = new WebSocketMessage(result.MessageType == WebSocketMessageType.Text, payload);


## XRENGINE/Scene/Components/Particles/ParticleEmitterComponent.cs
- L77 C65: new() :: private readonly ParticleShaderAssembler _shaderAssembler = new();
- L171 C45: new AABB :: public AABB LocalBounds { get; set; } = new AABB(new Vector3(-10), new Vector3(10));
- L171 C54: new Vector3 :: public AABB LocalBounds { get; set; } = new AABB(new Vector3(-10), new Vector3(10));
- L171 C72: new Vector3 :: public AABB LocalBounds { get; set; } = new AABB(new Vector3(-10), new Vector3(10));
- L281 C19: new PointSpawnModule :: AddModule(new PointSpawnModule());
- L282 C19: new GravityModule :: AddModule(new GravityModule());
- L371 C28: new XRShader :: _spawnShader = new XRShader(EShaderType.Compute, spawnSource);
- L372 C29: new XRShader :: _updateShader = new XRShader(EShaderType.Compute, updateSource);
- L374 C29: new XRRenderProgram :: _spawnProgram = new XRRenderProgram(true, false, _spawnShader);
- L375 C30: new XRRenderProgram :: _updateProgram = new XRRenderProgram(true, false, _updateShader);
- L397 C29: new XRRenderProgram :: _spawnProgram = new XRRenderProgram(true, false, _spawnShader);
- L398 C30: new XRRenderProgram :: _updateProgram = new XRRenderProgram(true, false, _updateShader);
- L428 C25: new GPUParticle :: _particleData = new GPUParticle[maxParticles];
- L429 C25: new uint :: _deadListData = new uint[maxParticles];
- L430 C26: new uint :: _aliveListData = new uint[maxParticles];
- L436 C32: new GPUParticle :: _particleData[i] = new GPUParticle { Flags = 0 }; // All dead initially
- L440 C21: new ParticleCounters :: _counters = new ParticleCounters
- L450 C28: new XRDataBuffer :: _particlesBuffer = new XRDataBuffer(
- L461 C27: new XRDataBuffer :: _deadListBuffer = new XRDataBuffer(
- L472 C28: new XRDataBuffer :: _aliveListBuffer = new XRDataBuffer(
- L483 C27: new XRDataBuffer :: _countersBuffer = new XRDataBuffer(
- L494 C32: new XRDataBuffer :: _emitterParamsBuffer = new XRDataBuffer(
- L505 C31: new XRDataBuffer :: _indirectDrawBuffer = new XRDataBuffer(
- L525 C37: new[] :: _countersBuffer?.SetDataRaw(new[] { _counters });
- L564 C29: new XRMeshRenderer :: _particleRenderer = new XRMeshRenderer(_particleMesh, material);
- L590 C18: new Vertex :: var v0 = new Vertex(new Vector3(-halfSize, -halfSize, 0))
- L590 C29: new Vector3 :: var v0 = new Vertex(new Vector3(-halfSize, -halfSize, 0))
- L592 C38: new Vector2 :: TextureCoordinateSets = [new Vector2(0, 0)]
- L595 C18: new Vertex :: var v1 = new Vertex(new Vector3(halfSize, -halfSize, 0))
- L595 C29: new Vector3 :: var v1 = new Vertex(new Vector3(halfSize, -halfSize, 0))
- L597 C38: new Vector2 :: TextureCoordinateSets = [new Vector2(1, 0)]
- L600 C18: new Vertex :: var v2 = new Vertex(new Vector3(halfSize, halfSize, 0))
- L600 C29: new Vector3 :: var v2 = new Vertex(new Vector3(halfSize, halfSize, 0))
- L602 C38: new Vector2 :: TextureCoordinateSets = [new Vector2(1, 1)]
- L605 C18: new Vertex :: var v3 = new Vertex(new Vector3(-halfSize, halfSize, 0))
- L605 C29: new Vector3 :: var v3 = new Vertex(new Vector3(-halfSize, halfSize, 0))
- L607 C38: new Vector2 :: TextureCoordinateSets = [new Vector2(0, 1)]
- L610 C24: new[] :: var vertices = new[] { v0, v1, v2, v3 };
- L611 C23: new List :: var indices = new List<ushort> { 0, 1, 2, 0, 2, 3 };
- L613 C16: new XRMesh :: return new XRMesh(vertices, indices);
- L626 C24: new XRMaterial :: var material = new XRMaterial(vertShader, fragShader)
- L635 C44: new Rendering.Models.Materials.BlendMode :: EParticleBlendMode.Additive => new Rendering.Models.Materials.BlendMode
- L643 C48: new Rendering.Models.Materials.BlendMode :: EParticleBlendMode.SoftAdditive => new Rendering.Models.Materials.BlendMode
- L651 C44: new Rendering.Models.Materials.BlendMode :: EParticleBlendMode.Multiply => new Rendering.Models.Materials.BlendMode
- L738 C26: new GPUEmitterParams :: _emitterParams = new GPUEmitterParams
- L750 C28: new Vector4 :: InitialColor = new Vector4(InitialColor.R, InitialColor.G, InitialColor.B, InitialColor.A),
- L760 C42: new[] :: _emitterParamsBuffer?.SetDataRaw(new[] { _emitterParams });
- L881 C33: new GPUParticle :: _particleData![i] = new GPUParticle { Flags = 0 };
- L884 C21: new ParticleCounters :: _counters = new ParticleCounters


## XRENGINE/Scene/Components/Particles/ParticleModules/ColorOverLifetimeModule.cs
- L22 C45: new ColorF4 :: public ColorF4 EndColor { get; set; } = new ColorF4(1, 1, 1, 0);
- L31 C40: new Vector4 :: program.Uniform("uColorStart", new Vector4(StartColor.R, StartColor.G, StartColor.B, StartColor.A));
- L32 C38: new Vector4 :: program.Uniform("uColorEnd", new Vector4(EndColor.R, EndColor.G, EndColor.B, EndColor.A));


## XRENGINE/Scene/Components/Particles/ParticleShaderAssembler.cs
- L267 C35: new StringBuilder :: var uniformDeclarations = new StringBuilder();
- L268 C31: new StringBuilder :: var moduleFunctions = new StringBuilder();
- L269 C31: new StringBuilder :: var moduleSpawnCode = new StringBuilder();
- L308 C35: new StringBuilder :: var uniformDeclarations = new StringBuilder();
- L309 C31: new StringBuilder :: var moduleFunctions = new StringBuilder();
- L310 C32: new StringBuilder :: var moduleUpdateCode = new StringBuilder();
- L372 C22: new string :: var indent = new string(' ', spaces);
- L374 C22: new StringBuilder :: var result = new StringBuilder();


## XRENGINE/Scene/Components/Pawns/CharacterPawnComponent.cs
- L687 C17: new Vector2 :: new Vector2(-1.0f, -1.0f),
- L688 C17: new Vector2 :: new Vector2(1.0f, 1.0f));
- L690 C42: new() :: NetworkInputState snapshot = new()
- L693 C30: new Vector2 :: ViewAngles = new Vector2(_viewRotation.Yaw, _viewRotation.Pitch),


## XRENGINE/Scene/Components/Pawns/PawnComponent.cs
- L77 C100: new Segment :: public Segment CursorPositionWorld => Viewport?.GetWorldSegment(CursorPositionViewport) ?? new Segment(Vector3.Zero, Vector3.Zero);


## XRENGINE/Scene/Components/Pawns/UICanvasComponent.cs
- L74 C39: new XROrthographicCameraParameters :: Camera2D.Parameters = new XROrthographicCameraParameters(bounds.Width, bounds.Height, DefaultNearZ, DefaultFarZ);
- L86 C38: new Transform :: get => _camera2D ??= new(new Transform());
- L97 C39: new() :: get => _visualScene2D ??= new();
- L209 C88: new UserInterfaceRenderPipeline :: private readonly XRRenderPipelineInstance _renderPipeline = new() { Pipeline = new UserInterfaceRenderPipeline() };
- L209 C69: new() :: private readonly XRRenderPipelineInstance _renderPipeline = new() { Pipeline = new UserInterfaceRenderPipeline() };


## XRENGINE/Scene/Components/Pawns/UICanvasInputComponent.cs
- L260 C57: new Vector2 :: var vpCoord = vp.ScreenToViewportCoordinate(new Vector2(x, y));
- L463 C74: new Comparer :: private SortedSet<RenderInfo2D> LastUIElementIntersections = new(new Comparer());
- L464 C70: new Comparer :: private SortedSet<RenderInfo2D> UIElementIntersections = new(new Comparer());


## XRENGINE/Scene/Components/Pawns/VRPlayerInputSet.cs
- L151 C55: new RenderCommandMethod3D :: RenderedObjects = [RenderInfo3D.New(this, new RenderCommandMethod3D(EDefaultRenderPass.PostRender, PostRender))];
- L171 C36: new BoundingRectangle :: BoundingRectangle vp = new BoundingRectangle();
- L433 C16: new() :: => new()
- L579 C26: new IPhysicsGeometry.Sphere :: var sphere = new IPhysicsGeometry.Sphere(GrabRadius);


## XRENGINE/Scene/Components/Physics/ConvexHullUtility.cs
- L89 C31: new Vector3 :: Vector3[] positions = new Vector3[vertices.Length];
- L93 C17: new ConvexHullInput :: input = new ConvexHullInput(positions, indices);


## XRENGINE/Scene/Components/Physics/DynamicRigidBodyComponent.cs
- L439 C56: new PhysicsMassFrame :: get => RigidBody is PhysxRigidBody physx ? new PhysicsMassFrame(physx.CMassLocalPose.Item2, physx.CMassLocalPose.Item1) : _centerOfMassPose;
- L574 C63: new PhysicsSolverIterations :: get => RigidBody is PhysxDynamicRigidBody physx ? new PhysicsSolverIterations(physx.SolverIterationCounts.minPositionIters, physx.SolverIterationCounts.minVelocityIters) : _solverIterations;
- L728 C31: new PhysxDynamicRigidBody :: var created = new PhysxDynamicRigidBody(
- L740 C24: new PhysxDynamicRigidBody :: var body = new PhysxDynamicRigidBody(position, rotation);
- L753 C19: new LayerMask :: ? new LayerMask(1)
- L754 C19: new LayerMask :: : new LayerMask(1 << CollisionGroup);
- L775 C27: new PhysxMaterial :: var created = new PhysxMaterial(0.5f, 0.5f, 0.1f);


## XRENGINE/Scene/Components/Physics/GPUPhysicsChainComponent.cs
- L229 C31: new XRRenderProgram :: _mainPhysicsProgram = new XRRenderProgram(true, false, _mainPhysicsShader);
- L230 C39: new XRRenderProgram :: _skipUpdateParticlesProgram = new XRRenderProgram(true, false, _skipUpdateParticlesShader);
- L449 C46: new Vector4 :: _mainPhysicsProgram.Uniform("Force", new Vector4(Force.X, Force.Y, Force.Z, 0));
- L450 C48: new Vector4 :: _mainPhysicsProgram.Uniform("Gravity", new Vector4(Gravity.X, Gravity.Y, Gravity.Z, 0));
- L451 C69: new Vector4 :: _mainPhysicsProgram.Uniform("ObjectMove", applyObjectMove ? new Vector4(_objectMove.X, _objectMove.Y, _objectMove.Z, 0) : Vector4.Zero);
- L474 C59: new Vector4 :: _skipUpdateParticlesProgram.Uniform("ObjectMove", new Vector4(_objectMove.X, _objectMove.Y, _objectMove.Z, 0));
- L495 C28: new XRDataBuffer :: _particlesBuffer = new XRDataBuffer("Particles", EBufferTarget.ShaderStorageBuffer, (uint)_particlesData.Count, EComponentType.Float, 16, false, false);
- L499 C32: new XRDataBuffer :: _particleTreesBuffer = new XRDataBuffer("ParticleTrees", EBufferTarget.ShaderStorageBuffer, (uint)_particleTreesData.Count, EComponentType.Float, 20, false, false);
- L503 C36: new XRDataBuffer :: _transformMatricesBuffer = new XRDataBuffer("TransformMatrices", EBufferTarget.ShaderStorageBuffer, (uint)_transformMatrices.Count, EComponentType.Float, 16, false, false);
- L507 C28: new XRDataBuffer :: _collidersBuffer = new XRDataBuffer("Colliders", EBufferTarget.ShaderStorageBuffer, (uint)Math.Max(_collidersData.Count, 1), EComponentType.Float, 16, false, false);
- L541 C28: new ParticleTreeData :: var treeData = new ParticleTreeData
- L557 C36: new ParticleData :: var particleData = new ParticleData
- L593 C36: new ColliderData :: _collidersData.Add(new ColliderData
- L595 C30: new Vector4 :: Center = new Vector4(
- L604 C72: new Vector3 :: Vector3 end = capsuleCollider.Transform.TransformPoint(new Vector3(0, capsuleCollider.Height, 0));
- L606 C36: new ColliderData :: _collidersData.Add(new ColliderData
- L608 C30: new Vector4 :: Center = new Vector4(start, capsuleCollider.Radius),
- L609 C30: new Vector4 :: Params = new Vector4(end, 0),
- L615 C36: new ColliderData :: _collidersData.Add(new ColliderData
- L617 C30: new Vector4 :: Center = new Vector4(boxCollider.Transform.WorldTranslation, 0),
- L618 C30: new Vector4 :: Params = new Vector4(boxCollider.Size * 0.5f, 0),
- L721 C28: new ParticleTree :: _particleTrees.Add(new ParticleTree(root));
- L726 C20: new Particle :: var ptcl = new Particle(tfm, parentIndex);
- L744 C27: new Vector3 :: : new Vector3(EndLength, 0.0f, 0.0f);
- L840 C21: new List :: var roots = new List<Transform>();


## XRENGINE/Scene/Components/Physics/PhysicsActorComponent.cs
- L16 C109: new() :: private readonly Dictionary<CoACD.CoACDParameters, List<CoACD.ConvexHullMesh>> _cachedConvexHulls = new();
- L17 C156: new() :: private readonly Dictionary<(CoACD.CoACDParameters parameters, PxConvexFlags flags, bool requestGpuData), List<PhysxConvexMesh>> _physxMeshCache = new();
- L43 C78: new CoAcdRunner :: private static readonly IConvexDecompositionRunner s_defaultRunner = new CoAcdRunner();
- L64 C27: new List :: var results = new List<CoACD.ConvexHullMesh>();
- L70 C34: new ConvexHullGenerationProgress :: progress?.Report(new ConvexHullGenerationProgress(0, 0, "No collision meshes available."));
- L74 C30: new ConvexHullGenerationProgress :: progress?.Report(new ConvexHullGenerationProgress(0, totalInputs, "Starting convex decomposition."));
- L81 C34: new ConvexHullGenerationProgress :: progress?.Report(new ConvexHullGenerationProgress(i, totalInputs, $"Decomposing mesh {i + 1} of {totalInputs}..."));
- L90 C34: new ConvexHullGenerationProgress :: progress?.Report(new ConvexHullGenerationProgress(i + 1, totalInputs, $"Completed mesh {i + 1} of {totalInputs}."));
- L97 C34: new ConvexHullGenerationProgress :: progress?.Report(new ConvexHullGenerationProgress(totalInputs, totalInputs, "Convex hulls cached."));
- L101 C34: new ConvexHullGenerationProgress :: progress?.Report(new ConvexHullGenerationProgress(totalInputs, totalInputs, "No convex hulls generated."));


## XRENGINE/Scene/Components/Physics/PhysicsChainCollider.cs
- L27 C36: new RenderCommandMethod3D :: RenderInfo3D.New(this, new RenderCommandMethod3D((int)EDefaultRenderPass.OpaqueForward, RenderGizmos))


## XRENGINE/Scene/Components/Physics/PhysicsChainComponent Fields.cs
- L124 C36: new RenderCommandMethod3D :: RenderInfo3D.New(this, new RenderCommandMethod3D((int)EDefaultRenderPass.OpaqueForward, Render))


## XRENGINE/Scene/Components/Physics/PhysicsChainComponent.cs
- L206 C21: new List :: var roots = new List<Transform>();
- L362 C28: new ParticleTree :: _particleTrees.Add(new ParticleTree(root));
- L367 C20: new Particle :: var ptcl = new Particle(tfm, parentIndex);
- L385 C27: new Vector3 :: : new Vector3(EndLength, 0.0f, 0.0f);
- L752 C30: new AutoResetEvent :: _allWorksDoneEvent = new AutoResetEvent(false);
- L753 C31: new Semaphore :: _workQueueSemaphore = new Semaphore(0, int.MaxValue);
- L759 C21: new Thread :: var t = new Thread(ThreadProc)


## XRENGINE/Scene/Components/Physics/PhysicsChainPlaneCollider.cs
- L20 C49: new RenderCommandMethod3D :: var renderInfo = RenderInfo3D.New(this, new RenderCommandMethod3D((int)EDefaultRenderPass.OpaqueForward, OnDrawGizmosSelected));


## XRENGINE/Scene/Components/Physics/PhysxHeightFieldComponent.cs
- L21 C23: new PhysxHeightField :: HeightField = new PhysxHeightField(imagePath);
- L62 C19: new PhysxMaterial :: var mat = new PhysxMaterial(0.5f, 0.5f, 0.1f);
- L64 C40: new PhysxDynamicRigidBody :: RigidBodyComponent.RigidBody = new PhysxDynamicRigidBody(mat, hf, 1.0f);


## XRENGINE/Scene/Components/Physics/StaticRigidBodyComponent.cs
- L281 C31: new PhysxStaticRigidBody :: var created = new PhysxStaticRigidBody(
- L292 C24: new PhysxStaticRigidBody :: var body = new PhysxStaticRigidBody(position, rotation);
- L305 C19: new LayerMask :: ? new LayerMask(1)
- L306 C19: new LayerMask :: : new LayerMask(1 << CollisionGroup);
- L327 C27: new PhysxMaterial :: var created = new PhysxMaterial(0.5f, 0.5f, 0.1f);


## XRENGINE/Scene/Components/Scripting/GameCSProjLoader.cs
- L42 C56: new WeakReference :: _loadedAssemblies[id] = (source, assembly, new WeakReference<AssemblyLoadContext>(context), new AssemblyData(components, menuItems));
- L42 C105: new AssemblyData :: _loadedAssemblies[id] = (source, assembly, new WeakReference<AssemblyLoadContext>(context), new AssemblyData(components, menuItems));
- L43 C42: new AssemblyData :: OnAssemblyLoaded?.Invoke(id, new AssemblyData(components, menuItems));
- L54 C47: new DynamicEngineAssemblyLoadContext :: AssemblyLoadContext context = new DynamicEngineAssemblyLoadContext();
- L90 C47: new DynamicEngineAssemblyLoadContext :: AssemblyLoadContext context = new DynamicEngineAssemblyLoadContext();
- L93 C45: new MemoryStream :: using (var assemblyStream = new MemoryStream(assemblyBytes))
- L97 C47: new MemoryStream :: using var pdbStream = new MemoryStream(pdbBytes);


## XRENGINE/Scene/Components/Splines/Spline3DPreviewComponent.cs
- L34 C53: new AABB :: RenderInfo.LocalCullingVolume = new AABB(Vector3.Zero, Vector3.Zero);
- L185 C43: new Vector3 :: Vector3[] keyframePositions = new Vector3[kfCount];
- L186 C42: new VertexLine :: VertexLine[] keyframeLines = new VertexLine[kfCount];
- L187 C42: new Vector3 :: Vector3[] tangentPositions = new Vector3[kfCount];
- L225 C36: new VertexLine :: keyframeLines[i] = new VertexLine(p0, p1);
- L230 C36: new VertexLine :: keyframeLines[i] = new VertexLine(p0, p1);
- L239 C36: new VertexLine :: keyframeLines[i] = new VertexLine(p0, p1);
- L249 C33: new Vector3 :: Vector3[] extrema = new Vector3[6];
- L265 C23: new AABB :: var box = new AABB(minVal, maxVal);
- L269 C37: new() :: RenderingParameters p = new()
- L276 C34: new XRShader :: XRMaterial mat = new(new XRShader(EShaderType.Fragment,
- L291 C32: new XRMeshRenderer :: _splinePrimitive = new XRMeshRenderer(splineData, mat);
- L296 C42: new XRMeshRenderer :: _velocityTangentsPrimitive = new XRMeshRenderer(velocityData, mat);
- L301 C31: new XRMeshRenderer :: _pointPrimitive = new XRMeshRenderer(pointData, mat);
- L306 C33: new XRMeshRenderer :: _extremaPrimitive = new XRMeshRenderer(extremaData, mat);
- L311 C33: new XRMeshRenderer :: _tangentPrimitive = new XRMeshRenderer(tangentData, mat);
- L316 C39: new XRMeshRenderer :: _keyframeLinesPrimitive = new XRMeshRenderer(kfLineData, mat);
- L321 C35: new XRMeshRenderer :: _timePointPrimitive = new XRMeshRenderer(timePointData, mat);
- L341 C31: new Vector4 :: pos.ColorSets.Add(new Vector4(Vector3.Lerp(Vector3.UnitZ, Vector3.UnitX, t), 1.0f));
- L343 C26: new VertexLine :: velocity.Add(new VertexLine(pos, new Vertex(pos.Position + vel.Normalized() * velocityScale)));
- L343 C46: new Vertex :: velocity.Add(new VertexLine(pos, new Vertex(pos.Position + vel.Normalized() * velocityScale)));


## XRENGINE/Scene/Components/UI/Core/Arrangements/UIGridTransform.cs
- L136 C24: new List :: _indices = new List<int>[Rows.Count, Columns.Count];
- L316 C42: new BoundingRectangleF :: uiComp.FitLayout(new BoundingRectangleF(x, y, width, height));
- L360 C33: new UIGridChildPlacementInfo :: placementInfo = new UIGridChildPlacementInfo(childTransform);
- L368 C48: new UIGridChildPlacementInfo :: uic.PlacementInfo = info = new UIGridChildPlacementInfo(uic);


## XRENGINE/Scene/Components/UI/Core/Arrangements/UIGridTransform.UIGridChildPlacementInfo.cs
- L41 C23: new NotImplementedException :: throw new NotImplementedException();


## XRENGINE/Scene/Components/UI/Core/Arrangements/UIListTransform.cs
- L336 C28: new BoundingRectangleF :: bc.Arrange(new BoundingRectangleF(x, y, size, parentHeight));
- L357 C28: new BoundingRectangleF :: bc.Arrange(new BoundingRectangleF(x, y, parentWidth, size));
- L401 C34: new BoundingRectangleF :: bc.FitLayout(new BoundingRectangleF(x, y, size, parentHeight));
- L412 C34: new BoundingRectangleF :: bc.FitLayout(new BoundingRectangleF(x, y, parentWidth, size));
- L425 C29: new float :: float[] sizes = new float[Children.Count];
- L533 C34: new BoundingRectangleF :: bc.FitLayout(new BoundingRectangleF(x, y, size, parentHeight));
- L538 C30: new BoundingRectangleF :: bc.FitLayout(new BoundingRectangleF(x, y, size, parentHeight));
- L558 C34: new BoundingRectangleF :: bc.FitLayout(new BoundingRectangleF(x, y, parentWidth, size));
- L563 C30: new BoundingRectangleF :: bc.FitLayout(new BoundingRectangleF(x, y, parentWidth, size));
- L577 C33: new UIListChildPlacementInfo :: placementInfo = new UIListChildPlacementInfo(childTransform);


## XRENGINE/Scene/Components/UI/Core/Arrangements/UISizingDefinition.cs
- L7 C40: new() :: private UISizingValue _value = new();


## XRENGINE/Scene/Components/UI/Core/Interactable/UIPropertyTextDriverComponent.cs
- L33 C22: new GameTimer :: _timer = new GameTimer(this);


## XRENGINE/Scene/Components/UI/Core/Transforms/UIBoundableTransform.cs
- L33 C41: new() :: protected Vector2 _actualSize = new();
- L120 C38: new Vector2 :: set => NormalizedPivot = new Vector2(ActualWidth.IsZero() ? 0.0f : value / ActualWidth, NormalizedPivot.Y);
- L125 C38: new Vector2 :: set => NormalizedPivot = new Vector2(NormalizedPivot.X, ActualHeight.IsZero() ? 0.0f : value / ActualHeight);
- L202 C57: new Vector3 :: Matrix4x4 mtx = Matrix4x4.CreateTranslation(new Vector3(ActualLocalBottomLeftTranslation, DepthTranslation));
- L212 C49: new Vector3 :: Matrix4x4.CreateTranslation(new Vector3(LocalPivotTranslation, 0.0f)) *
- L215 C49: new Vector3 :: Matrix4x4.CreateTranslation(new Vector3(-LocalPivotTranslation, 0.0f));
- L412 C28: new Vector2 :: _desiredSize = new Vector2(desiredWidth, desiredHeight);
- L418 C28: new Vector2 :: _desiredSize = new Vector2(
- L646 C20: new Vector2 :: pos += new Vector2(left, bottom);
- L647 C21: new Vector2 :: size -= new Vector2(left + right, bottom + top);
- L648 C22: new BoundingRectangleF :: bounds = new BoundingRectangleF(pos, size);
- L666 C20: new Vector2 :: pos += new Vector2(left, bottom);
- L667 C21: new Vector2 :: size -= new Vector2(left + right, bottom + top);
- L668 C22: new BoundingRectangleF :: bounds = new BoundingRectangleF(pos, size);
- L711 C17: new Vector3 :: new Vector3(region.TopLeft, 0.0f) + Engine.Rendering.Debug.UIPositionBias,
- L712 C17: new Vector3 :: new Vector3(region.TopRight, 0.0f) + Engine.Rendering.Debug.UIPositionBias,
- L716 C17: new Vector3 :: new Vector3(region.TopRight, 0.0f) + Engine.Rendering.Debug.UIPositionBias,
- L717 C17: new Vector3 :: new Vector3(region.BottomRight, 0.0f) + Engine.Rendering.Debug.UIPositionBias,
- L721 C17: new Vector3 :: new Vector3(region.BottomRight, 0.0f) + Engine.Rendering.Debug.UIPositionBias,
- L722 C17: new Vector3 :: new Vector3(region.BottomLeft, 0.0f) + Engine.Rendering.Debug.UIPositionBias,
- L726 C17: new Vector3 :: new Vector3(region.BottomLeft, 0.0f) + Engine.Rendering.Debug.UIPositionBias,
- L727 C17: new Vector3 :: new Vector3(region.TopLeft, 0.0f) + Engine.Rendering.Debug.UIPositionBias,
- L740 C48: new Vector3 :: Vector3 maxPos = Vector3.Transform(new Vector3(Vector2.One, 0.0f), mtx);
- L846 C34: new Vector3 :: => Vector3.Transform(new Vector3(worldPoint, worldZ), InverseWorldMatrix);
- L848 C34: new Vector3 :: => Vector3.Transform(new Vector3(localPoint, worldZ), WorldMatrix);
- L859 C25: new Vector2 :: MinAnchor = new Vector2(0.0f, 0.0f);
- L860 C25: new Vector2 :: MaxAnchor = new Vector2(1.0f, 1.0f);
- L885 C73: new Vector3 :: renderInfo3D.LocalCullingVolume = AABB.FromSize(new Vector3(w, h, 0.1f));


## XRENGINE/Scene/Components/UI/Core/Transforms/UICanvasTransform.cs
- L187 C27: new LayoutCounter :: var counter = new LayoutCounter();
- L345 C34: new Vector2 :: => new(Vector2.Zero, new Vector2(GetWidth(), GetHeight()));


## XRENGINE/Scene/Components/UI/Core/Transforms/UIDockingRootTransform.cs
- L55 C37: new Vector2 :: tfm.MaxAnchor = new Vector2(1.0f, 1.0f);
- L56 C37: new Vector2 :: tfm.MinAnchor = new Vector2(0.0f, 0.0f);
- L65 C36: new Vector2 :: aInfo.BottomLeft = new Vector2(LeftSizeWidth, BottomSizeHeight);
- L66 C31: new BoundingRectangleF :: Center?.FitLayout(new BoundingRectangleF(parentRegion.X + LeftSizeWidth, parentRegion.Y + BottomSizeHeight, parentRegion.Width - LeftSizeWidth - RightSizeWidth, parentRegion.Height));
- L69 C36: new Vector2 :: bInfo.BottomLeft = new Vector2(0.0f, BottomSizeHeight);
- L70 C29: new BoundingRectangleF :: Left?.FitLayout(new BoundingRectangleF(parentRegion.X, parentRegion.Y + BottomSizeHeight, LeftSizeWidth, parentRegion.Height));
- L73 C36: new Vector2 :: cInfo.BottomLeft = new Vector2(parentRegion.Width - RightSizeWidth, BottomSizeHeight);
- L74 C30: new BoundingRectangleF :: Right?.FitLayout(new BoundingRectangleF(parentRegion.X + parentRegion.Width - RightSizeWidth, parentRegion.Y, RightSizeWidth, parentRegion.Height));
- L77 C36: new Vector2 :: dInfo.BottomLeft = new Vector2(0.0f, parentRegion.Height - BottomSizeHeight);
- L78 C31: new BoundingRectangleF :: Bottom?.FitLayout(new BoundingRectangleF(parentRegion.X, parentRegion.Y + parentRegion.Height - BottomSizeHeight, parentRegion.Width, BottomSizeHeight));
- L84 C33: new UIDockingPlacementInfo :: placementInfo = new UIDockingPlacementInfo(this);
- L108 C33: new Vector2 :: tfm.MaxAnchor = new Vector2(1.0f, 1.0f);
- L109 C33: new Vector2 :: tfm.MinAnchor = new Vector2(0.0f, 0.0f);


## XRENGINE/Scene/Components/UI/Core/Transforms/UIDualSplitTransform.cs
- L168 C33: new UISplitChildPlacementInfo :: placementInfo = new UISplitChildPlacementInfo(childTransform);


## XRENGINE/Scene/Components/UI/Core/Transforms/UIMultiSplitTransform.cs
- L271 C33: new UISplitChildPlacementInfo :: placementInfo = new UISplitChildPlacementInfo(childTransform);


## XRENGINE/Scene/Components/UI/Core/Transforms/UIScrollableTransform.cs
- L88 C48: new Vector3 :: => Matrix4x4.CreateTranslation(new Vector3(BottomLeftOffset, 0.0f));


## XRENGINE/Scene/Components/UI/Core/Transforms/UITransform.cs
- L80 C63: new() :: protected Vector2 _actualLocalBottomLeftTranslation = new();
- L208 C71: new RenderCommandMethod2D :: => [DebugRenderInfo2D = RenderInfo2D.New(this, _debugRC = new RenderCommandMethod2D((int)EDefaultRenderPass.OnTopForward, RenderDebug))];
- L213 C41: new Vector3 :: Matrix4x4.CreateTranslation(new Vector3(Translation, DepthTranslation));
- L228 C40: new Vector2 :: Vector2 newScale = scale - new Vector2(delta);
- L251 C48: new Vector2 :: Translation += (worldScreenPoint - new Vector2(WorldTranslation.X, WorldTranslation.Y)) * Vector2.One / scale * delta;
- L252 C21: new Vector3 :: Scale = new Vector3(newScale, Scale.Z);
- L511 C49: new Vector2 :: return Vector2.Distance(worldPoint, new Vector2(worldTranslation.X, worldTranslation.Y)) < 0.0001f;
- L516 C20: new Vector2 :: return new Vector2(worldTranslation.X, worldTranslation.Y);


## XRENGINE/Scene/Components/UI/Core/UIMaterialComponent.cs
- L70 C20: new XRMeshRenderer :: Mesh = new XRMeshRenderer(XRMesh.Create(VertexQuad.PosZ(1.0f, true, 0.0f, FlipVerticalUVCoord)), material);
- L73 C66: new() :: private readonly RenderingParameters _renderParameters = new()
- L76 C25: new() :: DepthTest = new()


## XRENGINE/Scene/Components/UI/Core/UIRenderableComponent.cs
- L87 C63: new RenderCommandMesh3D :: public RenderCommandMesh3D RenderCommand3D { get; } = new RenderCommandMesh3D(EDefaultRenderPass.OpaqueForward);
- L88 C63: new RenderCommandMesh2D :: public RenderCommandMesh2D RenderCommand2D { get; } = new RenderCommandMesh2D((int)EDefaultRenderPass.OpaqueForward);
- L143 C26: new Vector4 :: var bounds = new Vector4(x, y, w, h);


## XRENGINE/Scene/Components/UI/Core/UIVideoComponent.cs
- L363 C39: new() :: using HttpClient client = new();
- L389 C27: new StringContent :: var content = new StringContent(
- L411 C23: new Exception :: throw new Exception("Failed to obtain stream token");
- L587 C44: new() :: private readonly Lock _frameLock = new();
- L596 C23: new XRMaterialFrameBuffer :: => _fbo = new XRMaterialFrameBuffer(Material);
- L609 C20: new XRMaterial :: return new XRMaterial([texture], XRShader.EngineShader(Path.Combine("Common", "UnlitTexturedForward.fs"), EShaderType.Fragment));
- L657 C30: new PlayerGL :: _flyleafPlayer = new PlayerGL(_flyleafConfig, renderer.RawGL);
- L743 C59: new IVector2 :: Engine.InvokeOnMainThread(() => WidthHeight = new IVector2(video.Width, video.Height), true);
- L763 C26: new FlyleafConfig :: var config = new FlyleafConfig
- L775 C64: new() :: private static readonly object FlyleafEngineInitLock = new();
- L810 C20: new FlyleafLib.EngineConfig :: return new FlyleafLib.EngineConfig
- L888 C55: new XRDataBuffer :: _pboBuffers[_currentPboIndex] = pbo = new XRDataBuffer("", EBufferTarget.PixelUnpackBuffer, (uint)frame.Length / 3, EComponentType.Byte, 3, false, false)
- L990 C38: new byte :: byte[] errorBuffer = new byte[1024];
- L994 C39: new string :: string errorMsg = new string((sbyte*)ptr).Trim('\0');
- L1145 C38: new byte :: byte[] errorBuffer = new byte[1024];
- L1150 C32: new string :: errorMsg = new string((sbyte*)ptr).Trim('\0');
- L1193 C43: new[] :: [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
- L1309 C23: new Exception :: throw new Exception("Unsupported codec");
- L1317 C23: new Exception :: throw new Exception("Could not open codec");
- L1371 C38: new byte :: byte[] errorBuffer = new byte[1024];
- L1376 C32: new string :: errorMsg = new string((char*)ptr).Trim('\0');
- L1389 C31: new short :: short[] samples = new short[_frame->nb_samples * _audioCodecContext->ch_layout.nb_channels];
- L1422 C27: new IVector2 :: WidthHeight = new IVector2(decodedFrame->width, decodedFrame->height);
- L1463 C24: new byte :: var data = new byte[decodedFrame->width * decodedFrame->height * 3];


## XRENGINE/Scene/Components/UI/Core/UIViewportComponent.cs
- L25 C20: new XRMaterialFrameBuffer :: _fbo = new XRMaterialFrameBuffer(Material);
- L43 C60: new XRViewport :: public XRViewport Viewport { get; private set; } = new XRViewport(null, 1, 1);


## XRENGINE/Scene/Components/UI/DearImGuiComponent.cs
- L30 C34: new RenderCommandMethod2D :: _renderCommand = new RenderCommandMethod2D((int)EDefaultRenderPass.OnTopForward, RenderImGui))


## XRENGINE/Scene/Components/UI/Interactable/MenuComponent.cs
- L34 C29: new Vector3 :: => Show(canvas, new Vector3(worldPosition, z));
- L69 C55: new MenuDivider :: public static MenuDivider Instance { get; } = new MenuDivider();


## XRENGINE/Scene/Components/UI/Rive/BoolInput.cs
- L14 C43: new value :: Apply(); // Apply the new value to the state machine.


## XRENGINE/Scene/Components/UI/Rive/NumberInput.cs
- L14 C43: new value :: Apply(); // Apply the new value to the state machine.


## XRENGINE/Scene/Components/UI/Rive/RiveUIComponent.cs
- L73 C35: new CancellationTokenSource :: _activeSourceFileLoader = new CancellationTokenSource();
- L114 C31: new DllNotFoundException :: ReportMissingRive(new DllNotFoundException("rive.dll could not be located."));
- L120 C22: new RiveSharp.Scene :: _scene = new RiveSharp.Scene();
- L190 C59: new() :: readonly ConcurrentQueue<Action> _sceneActionsQueue = new();
- L202 C32: new WeakReference :: => input.SetRivePlayer(new WeakReference<RiveUIComponent?>(this));
- L204 C32: new WeakReference :: => input.SetRivePlayer(new WeakReference<RiveUIComponent?>(null));
- L213 C41: new RenderCommandMethod3D :: RenderInfo3D.RenderCommands.Add(new RenderCommandMethod3D((int)EDefaultRenderPass.PreRender, RenderFBO));
- L231 C40: new() :: private readonly Lock _deltaLock = new();
- L269 C32: new HttpClient :: using var client = new HttpClient();
- L373 C17: new RiveSharp.AABB :: new RiveSharp.AABB(0, 0, (float)viewSize.X, (float)viewSize.Y),
- L374 C17: new RiveSharp.AABB :: new RiveSharp.AABB(0, 0, scene.Width, scene.Height));
- L376 C42: new Vec2D :: handler(scene, inverse * new Vec2D(pointerPos.X, pointerPos.Y));
- L471 C25: new Renderer :: _renderer = new Renderer(_surface.Canvas);
- L474 C16: new SKAutoCanvasRestore :: using (new SKAutoCanvasRestore(_surface.Canvas, doSave: true))
- L513 C25: new GRBackendRenderTarget :: _renderTarget = new GRBackendRenderTarget(
- L531 C16: new GRGlFramebufferInfo :: return new GRGlFramebufferInfo(bindingID, SKColorType.Rgba8888.ToGlSizedFormat());
- L601 C39: new DepthTest :: mat.RenderOptions.DepthTest = new DepthTest
- L641 C13: new RiveSharp.AABB :: new RiveSharp.AABB(0, 0, (float)width, (float)height),
- L642 C13: new RiveSharp.AABB :: new RiveSharp.AABB(0, 0, scene.Width, scene.Height));
- L658 C79: new RiveSharp.AABB :: : Renderer.ComputeAlignment(Fit.Contain, Alignment.Center, frame, new RiveSharp.AABB(0, 0, scene.Width, scene.Height));


## XRENGINE/Scene/Components/UI/Text/UIText.cs
- L97 C46: new() :: private readonly object _glyphLock = new();
- L276 C24: new XRMeshRenderer :: var rend = new XRMeshRenderer(
- L285 C57: new() :: private RenderingParameters _renderParameters = new()
- L288 C25: new() :: DepthTest = new()
- L326 C45: new() :: private readonly Lock _matrixLock = new();
- L354 C25: new ShaderVector4 :: return new([new ShaderVector4(Color, TextColorUniformName)], [atlas], new XRShader[] { vertexShader, stereoVertexShader }.Concat(nonVertexShaders))
- L354 C83: new XRShader :: return new([new ShaderVector4(Color, TextColorUniformName)], [atlas], new XRShader[] { vertexShader, stereoVertexShader }.Concat(nonVertexShaders))


## XRENGINE/Scene/Components/UI/Text/UITextComponent.cs
- L63 C44: new() :: private readonly Lock _glyphLock = new();
- L177 C57: new() :: private RenderingParameters _renderParameters = new()
- L180 C25: new() :: DepthTest = new()
- L450 C24: new XRMeshRenderer :: var rend = new XRMeshRenderer(
- L469 C25: new ShaderVector4 :: return new([new ShaderVector4(Color, TextColorUniformName)], [atlas], new XRShader[] { vertexShader, stereoVertexShader }.Concat(nonVertexShaders))
- L469 C83: new XRShader :: return new([new ShaderVector4(Color, TextColorUniformName)], [atlas], new XRShader[] { vertexShader, stereoVertexShader }.Concat(nonVertexShaders))


## XRENGINE/Scene/Components/UI/UISvgComponent.cs
- L47 C49: new() :: private static readonly object _cacheLock = new();
- L48 C88: new SvgCacheKeyComparer :: private static readonly Dictionary<SvgCacheKey, SvgCacheEntry> _textureCache = new(new SvgCacheKeyComparer());
- L167 C31: new() :: using SKSvg svg = new();
- L170 C36: new MemoryStream :: using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(SvgData));
- L221 C34: new SvgCacheEntry :: _textureCache[key] = new SvgCacheEntry(texture);
- L314 C46: new Vector2 :: Vector2 size = RasterSizeOverride ?? new Vector2(BoundableTransform.ActualWidth, BoundableTransform.ActualHeight);
- L331 C16: new SKSizeI :: return new SKSizeI((int)MathF.Max(1.0f, MathF.Round(width)), (int)MathF.Max(1.0f, MathF.Round(height)));
- L373 C71: new() :: private static RenderingParameters SvgRenderParameters { get; } = new()
- L376 C21: new() :: DepthTest = new()


## XRENGINE/Scene/Components/Volumes/Blocking.cs
- L16 C20: new Vector3 :: : this(new Vector3(0.5f), 0, 0) { }
- L19 C24: new IPhysicsGeometry.Box :: Geometry = new IPhysicsGeometry.Box(halfExtents);
- L21 C26: new PhysicsGroupsMask :: GroupsMask = new PhysicsGroupsMask(collidesWith, 0, 0, 0);


## XRENGINE/Scene/Components/Volumes/Trigger.cs
- L66 C28: new IPhysicsGeometry.Box :: var geometry = new IPhysicsGeometry.Box(HalfExtents);


## XRENGINE/Scene/Components/VR/VRDeviceModelComponent.cs
- L58 C23: new() :: Model m = new();
- L84 C27: new() :: XRMesh mesh = new();
- L85 C30: new() :: XRMaterial mat = new();
- L98 C30: new XRTexture2D :: textures.Add(new XRTexture2D(texture.LoadImage(true)));
- L108 C30: new Vertex :: vertices.Add(new Vertex(position, normal, uv));
- L121 C24: new XRMesh :: mesh = new XRMesh(vertices, triangleIndices);
- L126 C25: new SubMeshLOD :: m = new(new SubMeshLOD(mat, mesh, 0.0f));


## XRENGINE/Scene/Components/VR/VRHeadsetComponent.cs
- L19 C33: new VREyeTransform :: _leftEyeTransform = new VREyeTransform(true, Transform);
- L20 C34: new VREyeTransform :: _rightEyeTransform = new VREyeTransform(false, Transform);
- L22 C40: new XRCamera :: _leftEyeCamera = new(() => new XRCamera(_leftEyeTransform, _leftEyeParams), true);
- L23 C41: new XRCamera :: _rightEyeCamera = new(() => new XRCamera(_rightEyeTransform, _rightEyeParams), true);
- L25 C49: new RenderCommandMethod3D :: RenderInfo = RenderInfo3D.New(this, new RenderCommandMethod3D((int)EDefaultRenderPass.OpaqueForward, Render));


## XRENGINE/Scene/Components/VR/VRPlayerCharacterComponent.cs
- L21 C58: new RenderCommandMethod3D :: => RenderedObjects = [RenderInfo3D.New(this, new RenderCommandMethod3D((int)EDefaultRenderPass.OpaqueForward, Render))];
- L66 C55: new Vector3 :: Vector3 ipdOffset = Vector3.Transform(new Vector3(halfIpd, 0.0f, 0.0f), headNodeRot);
- L403 C58: new Vector3 :: GetCharacterMovement()?.AddLiteralInputDelta(new Vector3(dx, 0.0f, dz));
- L429 C41: new Vector3 :: avatarRootTfm.Translation = new Vector3(0.0f, rootTrans.Y, 0.0f);
- L482 C41: new Vector3 :: avatarRootTfm.Translation = new Vector3(0.0f, 0.0f, 0.0f);


## XRENGINE/Scene/Components/XRComponent.cs
- L35 C16: new string :: public new string? Name
- L154 C166: new() :: public T? TransformAs<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(bool forceConvert = false) where T : TransformBase, new()


## XRENGINE/Scene/LayerMask.cs
- L58 C55: new LayerMask :: public static readonly LayerMask Everything = new LayerMask(-1);


## XRENGINE/Scene/Physics/Jitter2/JitterScene.cs
- L14 C41: new() :: private readonly World _world = new();
- L25 C23: new Vector3 :: Gravity = new Vector3(0, -9.81f, 0);
- L94 C19: new NotImplementedException :: throw new NotImplementedException();
- L99 C19: new NotImplementedException :: throw new NotImplementedException();
- L104 C19: new NotImplementedException :: throw new NotImplementedException();
- L109 C19: new NotImplementedException :: throw new NotImplementedException();
- L114 C19: new NotImplementedException :: throw new NotImplementedException();
- L119 C19: new NotImplementedException :: throw new NotImplementedException();
- L124 C19: new NotImplementedException :: throw new NotImplementedException();
- L129 C19: new NotImplementedException :: throw new NotImplementedException();
- L134 C19: new NotImplementedException :: throw new NotImplementedException();
- L139 C19: new NotImplementedException :: throw new NotImplementedException();
- L144 C19: new NotImplementedException :: throw new NotImplementedException();


## XRENGINE/Scene/Physics/Jolt/JoltBootstrap.cs
- L65 C23: new InvalidOperationException :: throw new InvalidOperationException("Jolt Foundation.Init() failed. The native joltc.dll may not be found or is incompatible.");


## XRENGINE/Scene/Physics/Jolt/JoltCharacterVirtualController.cs
- L11 C108: new() :: private readonly ConcurrentQueue<(Vector3 delta, float minDist, float elapsedTime)> _inputBuffer = new();
- L192 C47: new() :: ExtendedUpdateSettings settings = new()
- L243 C53: new() :: CharacterVirtualSettings settings = new()
- L253 C30: new CharacterVirtual :: _character = new CharacterVirtual(settings, in _position, in capsuleRotation, 0, Scene.PhysicsSystem)
- L276 C22: new CapsuleShape :: _shape = new CapsuleShape(Height * 0.5f, Radius);


## XRENGINE/Scene/Physics/Jolt/JoltScene.cs
- L14 C84: new() :: private readonly HashSet<IJoltCharacterController> _characterControllers = new();
- L44 C57: new() :: private Dictionary<BodyID, JoltActor> _actors = new();
- L45 C67: new() :: private Dictionary<BodyID, JoltRigidActor> _rigidActors = new();
- L46 C73: new() :: private Dictionary<BodyID, JoltStaticRigidBody> _staticBodies = new();
- L47 C75: new() :: private Dictionary<BodyID, JoltDynamicRigidBody> _dynamicBodies = new();
- L98 C28: new JoltStaticRigidBody :: var joltBody = new JoltStaticRigidBody(bodyId);
- L150 C28: new JoltDynamicRigidBody :: var joltBody = new JoltDynamicRigidBody(bodyId);
- L231 C30: new JobSystemThreadPool :: _jobSystem = new JobSystemThreadPool();
- L238 C42: new ObjectLayerPairFilterTable :: _objectLayerPairFilter = new ObjectLayerPairFilterTable(NumLayers);
- L243 C45: new BroadPhaseLayerInterfaceTable :: _broadPhaseLayerInterface = new BroadPhaseLayerInterfaceTable(NumLayers, NumLayers);
- L247 C50: new ObjectVsBroadPhaseLayerFilterTable :: _objectVsBroadPhaseLayerFilter = new ObjectVsBroadPhaseLayerFilterTable(_broadPhaseLayerInterface, NumLayers, _objectLayerPairFilter, NumLayers);
- L249 C50: new() :: PhysicsSystemSettings settings = new()
- L264 C34: new Vector3 :: system.Gravity = new Vector3(0, -9.81f, 0);
- L265 C35: new PhysicsSettings :: system.Settings = new PhysicsSettings
- L335 C34: new List :: var collideResults = new List<CollideShapeResult>();
- L352 C38: new OverlapHit :: list.Add((component, new OverlapHit { FaceIndex = 0 }));
- L378 C34: new List :: var collideResults = new List<CollideShapeResult>();
- L395 C38: new OverlapHit :: list.Add((component, new OverlapHit { FaceIndex = 0 }));
- L417 C24: new List :: var hits = new List<RayCastResult>();
- L418 C57: new Ray :: if (_physicsSystem.NarrowPhaseQuery.CastRay(new Ray(start, direction), new RayCastSettings(), CollisionCollectorType.AnyHit, hits))
- L418 C84: new RayCastSettings :: if (_physicsSystem.NarrowPhaseQuery.CastRay(new Ray(start, direction), new RayCastSettings(), CollisionCollectorType.AnyHit, hits))
- L447 C27: new Ray :: var rayCast = new Ray(start, direction);
- L448 C35: new RayCastSettings :: var rayCastSettings = new RayCastSettings();
- L465 C46: new RaycastHit :: list.Add((component, new RaycastHit
- L496 C27: new JoltPhysicsSharp.Ray :: var rayCast = new JoltPhysicsSharp.Ray(start, direction);
- L497 C35: new RayCastSettings :: var rayCastSettings = new RayCastSettings();
- L517 C46: new RaycastHit :: list.Add((component, new RaycastHit
- L615 C32: new List :: var sweepResults = new List<ShapeCastResult>();
- L669 C32: new List :: var sweepResults = new List<ShapeCastResult>();
- L696 C38: new SweepHit :: list.Add((component, new SweepHit
- L741 C32: new List :: var sweepResults = new List<ShapeCastResult>();
- L767 C38: new SweepHit :: list.Add((component, new SweepHit


## XRENGINE/Scene/Physics/Physx/Controller.cs
- L66 C102: new() :: private static readonly ConcurrentDictionary<nint, PhysxController> _hitReportToController = new();
- L69 C109: new() :: private static readonly ConcurrentDictionary<nint, PhysxController> _behaviorCallbackToController = new();
- L91 C108: new() :: private readonly ConcurrentQueue<(Vector3 delta, float minDist, float elapsedTime)> _inputBuffer = new();
- L128 C23: new KeyNotFoundException :: throw new KeyNotFoundException($"PhysxScene not found for PxScene* 0x{scenePtr:X}");
- L203 C24: new Vector3 :: return new Vector3((float)pos->x, (float)pos->y, (float)pos->z);
- L219 C24: new Vector3 :: return new Vector3((float)pos.x, (float)pos.y, (float)pos.z);
- L235 C24: new Vector3 :: return new Vector3(up.x, up.y, up.z);
- L337 C74: new PxUserControllerHitReportVTable :: _userControllerHitReportVTableSource = DataSource.FromStruct(new PxUserControllerHitReportVTable
- L345 C68: new PxUserControllerHitReport :: _userControllerHitReportSource = DataSource.FromStruct(new PxUserControllerHitReport()
- L355 C77: new PxControllerBehaviorCallbackVTable :: _controllerBehaviorCallbackVTableSource = DataSource.FromStruct(new PxControllerBehaviorCallbackVTable
- L363 C71: new PxControllerBehaviorCallback :: _controllerBehaviorCallbackSource = DataSource.FromStruct(new PxControllerBehaviorCallback()


## XRENGINE/Scene/Physics/Physx/ControllerManager.cs
- L66 C108: new() :: private static readonly ConcurrentDictionary<nint, ControllerManager> _controllerFilterToManager = new();
- L69 C103: new() :: private static readonly ConcurrentDictionary<nint, ControllerManager> _queryFilterToManager = new();
- L116 C23: new KeyNotFoundException :: throw new KeyNotFoundException($"PhysxScene not found for PxScene* 0x{scenePtr:X}");
- L177 C75: new PxControllerFilterCallbackVTable :: _controllerFilterCallbackVTableSource = DataSource.FromStruct(new PxControllerFilterCallbackVTable
- L183 C69: new PxControllerFilterCallback :: _controllerFilterCallbackSource = DataSource.FromStruct(new PxControllerFilterCallback()
- L192 C70: new PxQueryFilterCallbackVTable :: _queryFilterCallbackVTableSource = DataSource.FromStruct(new PxQueryFilterCallbackVTable
- L199 C64: new PxQueryFilterCallback :: _queryFilterCallbackSource = DataSource.FromStruct(new PxQueryFilterCallback()
- L229 C31: new List :: var controllers = new List<PhysxController>((int)count);
- L265 C30: new PhysxBoxController :: var controller = new PhysxBoxController();
- L287 C23: new Exception :: throw new Exception("Invalid box controller description");
- L317 C30: new PhysxCapsuleController :: var controller = new PhysxCapsuleController();
- L347 C23: new Exception :: throw new Exception("Invalid capsule controller description");
- L419 C42: new ObstacleContext :: ObstacleContext[] contexts = new ObstacleContext[count];
- L429 C35: new ObstacleContext :: var obstacleContext = new ObstacleContext(context);


## XRENGINE/Scene/Physics/Physx/Geometry/IPhysicsGeometry.cs
- L22 C52: new SphereShape :: public readonly Shape AsJoltShape() => new SphereShape(Radius);
- L30 C52: new BoxShape :: public readonly Shape AsJoltShape() => new BoxShape(HalfExtents);
- L39 C52: new CapsuleShape :: public readonly Shape AsJoltShape() => new CapsuleShape(HalfHeight, Radius);
- L57 C58: new NotImplementedException :: public readonly Shape AsJoltShape() => throw new NotImplementedException(); //new ConvexMeshShape(Mesh, Scale, Rotation, TightBounds);
- L57 C91: new ConvexMeshShape :: public readonly Shape AsJoltShape() => throw new NotImplementedException(); //new ConvexMeshShape(Mesh, Scale, Rotation, TightBounds);
- L76 C58: new NotImplementedException :: public readonly Shape AsJoltShape() => throw new NotImplementedException(); //new TriangleMeshShape(Mesh, Scale, Rotation, TightBounds, DoubleSided);
- L76 C91: new TriangleMeshShape :: public readonly Shape AsJoltShape() => throw new NotImplementedException(); //new TriangleMeshShape(Mesh, Scale, Rotation, TightBounds, DoubleSided);
- L93 C58: new NotImplementedException :: public readonly Shape AsJoltShape() => throw new NotImplementedException(); //new HeightFieldShape(Field, HeightScale, RowScale, ColumnScale, TightBounds, DoubleSided);
- L93 C91: new HeightFieldShape :: public readonly Shape AsJoltShape() => throw new NotImplementedException(); //new HeightFieldShape(Field, HeightScale, RowScale, ColumnScale, TightBounds, DoubleSided);
- L97 C54: new System.Numerics.Plane :: public System.Numerics.Plane PlaneData = new System.Numerics.Plane(Globals.Up, 0.0f);
- L101 C52: new PlaneShape :: public readonly Shape AsJoltShape() => new PlaneShape(PlaneData);
- L114 C58: new NotImplementedException :: public readonly Shape AsJoltShape() => throw new NotImplementedException(); //new ParticleSystemShape(Solver);
- L114 C91: new ParticleSystemShape :: public readonly Shape AsJoltShape() => throw new NotImplementedException(); //new ParticleSystemShape(Solver);
- L124 C58: new NotImplementedException :: public readonly Shape AsJoltShape() => throw new NotImplementedException(); //new TetrahedronMeshShape(Mesh);
- L124 C91: new TetrahedronMeshShape :: public readonly Shape AsJoltShape() => throw new NotImplementedException(); //new TetrahedronMeshShape(Mesh);


## XRENGINE/Scene/Physics/Physx/InstancedDebugVisualizer.cs
- L100 C49: new Task :: private readonly Task?[] _renderTasks = new Task?[3];
- L374 C38: new XRDataBuffer :: _debugPointsBuffer = new XRDataBuffer(
- L414 C37: new XRDataBuffer :: _debugLinesBuffer = new XRDataBuffer(
- L454 C41: new XRDataBuffer :: _debugTrianglesBuffer = new XRDataBuffer(
- L482 C24: new XRMeshRenderer :: var rend = new XRMeshRenderer(CreateDebugMesh(), CreateDebugPointMaterial());
- L488 C24: new XRMeshRenderer :: var rend = new XRMeshRenderer(CreateDebugMesh(), CreateDebugLineMaterial());
- L494 C24: new XRMeshRenderer :: var rend = new XRMeshRenderer(CreateDebugMesh(), CreateDebugTriangleMaterial());
- L523 C21: new Vertex :: => new([new Vertex(Vector3.Zero)]);
- L540 C21: new ShaderFloat :: new ShaderFloat(PointSize, "PointSize"),
- L541 C21: new ShaderInt :: new ShaderInt(0, "TotalPoints"),
- L543 C27: new XRMaterial :: var mat = new XRMaterial(vars, vertShader, stereoMV2VertShader, stereoNVVertShader, geomShader, fragShader);
- L573 C21: new ShaderFloat :: new ShaderFloat(LineWidth, "LineWidth"),
- L574 C21: new ShaderInt :: new ShaderInt(0, "TotalLines"),
- L576 C27: new XRMaterial :: var mat = new XRMaterial(vars, vertShader, stereoMV2VertShader, stereoNVVertShader, geomShader, fragShader);
- L606 C21: new ShaderInt :: new ShaderInt(0, "TotalTriangles"),
- L608 C27: new XRMaterial :: var mat = new XRMaterial(vars, vertShader, stereoMV2VertShader, stereoNVVertShader, geomShader, fragShader);


## XRENGINE/Scene/Physics/Physx/Joints/PhysxJoint.cs
- L87 C33: new() :: PxTransform v = new() { p = value.position, q = value.rotation };
- L100 C33: new() :: PxTransform v = new() { p = value.position, q = value.rotation };


## XRENGINE/Scene/Physics/Physx/Joints/PhysxJoint_D6.cs
- L25 C44: new() :: PxJointLinearLimit limit = new() { value = value.@value, restitution = value.restitution, bounceThreshold = value.bounceThreshold, stiffness = value.stiffness, damping = value.damping };
- L39 C48: new() :: PxJointLinearLimitPair limit = new() { lower = value.lower, upper = value.upper, restitution = value.restitution, bounceThreshold = value.bounceThreshold, stiffness = value.stiffness, damping = value.damping };
- L52 C48: new() :: PxJointLinearLimitPair limit = new() { lower = value.lower, upper = value.upper, restitution = value.restitution, bounceThreshold = value.bounceThreshold, stiffness = value.stiffness, damping = value.damping };
- L65 C48: new() :: PxJointLinearLimitPair limit = new() { lower = value.lower, upper = value.upper, restitution = value.restitution, bounceThreshold = value.bounceThreshold, stiffness = value.stiffness, damping = value.damping };
- L78 C49: new() :: PxJointAngularLimitPair limit = new() { lower = value.lower, upper = value.upper, restitution = value.restitution, bounceThreshold = value.bounceThreshold, stiffness = value.stiffness, damping = value.damping };
- L91 C48: new() :: PxJointLinearLimitPair limit = new() { lower = value.lower, upper = value.upper, restitution = value.restitution, bounceThreshold = value.bounceThreshold, stiffness = value.stiffness, damping = value.damping };
- L104 C48: new() :: PxJointLinearLimitPair limit = new() { lower = value.lower, upper = value.upper, restitution = value.restitution, bounceThreshold = value.bounceThreshold, stiffness = value.stiffness, damping = value.damping };
- L118 C40: new() :: PxD6JointDrive drive = new() { stiffness = value.stiffness, damping = value.damping, forceLimit = value.forceLimit, flags = value.flags };
- L131 C40: new() :: PxD6JointDrive drive = new() { stiffness = value.stiffness, damping = value.damping, forceLimit = value.forceLimit, flags = value.flags };
- L144 C40: new() :: PxD6JointDrive drive = new() { stiffness = value.stiffness, damping = value.damping, forceLimit = value.forceLimit, flags = value.flags };
- L157 C40: new() :: PxD6JointDrive drive = new() { stiffness = value.stiffness, damping = value.damping, forceLimit = value.forceLimit, flags = value.flags };
- L170 C40: new() :: PxD6JointDrive drive = new() { stiffness = value.stiffness, damping = value.damping, forceLimit = value.forceLimit, flags = value.flags };
- L183 C40: new() :: PxD6JointDrive drive = new() { stiffness = value.stiffness, damping = value.damping, forceLimit = value.forceLimit, flags = value.flags };
- L197 C33: new() :: PxTransform t = new() { p = value.position, q = value.Quaternion };
- L204 C29: new() :: PxTransform t = new() { p = position, q = Quaternion };
- L252 C45: new() :: PxJointLimitPyramid limit = new() { yAngleMin = value.yAngleMin, yAngleMax = value.yAngleMax, zAngleMin = value.zAngleMin, zAngleMax = value.zAngleMax, restitution = value.restitution, bounceThreshold = value.bounceThreshold, stiffness = value.stiffness, damping = value.damping };
- L266 C42: new() :: PxJointLimitCone limit = new() { yAngle = value.yAngle, zAngle = value.zAngle, restitution = value.restitution, bounceThreshold = value.bounceThreshold, stiffness = value.stiffness, damping = value.damping };


## XRENGINE/Scene/Physics/Physx/Joints/PhysxJoint_Prismatic.cs
- L22 C47: new() :: PxJointLinearLimitPair pair = new()


## XRENGINE/Scene/Physics/Physx/Joints/PhysxJoint_Revolute.cs
- L24 C28: new PxJointAngularLimitPair :: var pair = new PxJointAngularLimitPair


## XRENGINE/Scene/Physics/Physx/Joints/PhysxJoint_Spherical.cs
- L20 C28: new PxJointLimitCone :: var cone = new PxJointLimitCone() { zAngle = value.zAngle, yAngle = value.yAngle, restitution = value.restitution, bounceThreshold = value.bounceThreshold, stiffness = value.stiffness, damping = value.damping };


## XRENGINE/Scene/Physics/Physx/PhysxActor.cs
- L16 C83: new() :: public static ConcurrentDictionary<nint, PhysxActor> AllActors { get; } = new();
- L153 C20: new AABB :: return new AABB(bounds.minimum, bounds.maximum);
- L161 C24: new string :: return new string((sbyte*)name);


## XRENGINE/Scene/Physics/Physx/PhysxControllerActorProxy.cs
- L48 C31: new Vector3 :: var newPosition = new Vector3((float)pos->x, (float)pos->y, (float)pos->z);
- L68 C70: new Vector3 :: rotation = XRMath.RotationBetweenVectors(Globals.Up, new Vector3(up.x, up.y, up.z));


## XRENGINE/Scene/Physics/Physx/PhysxConvexHullCooker.cs
- L14 C52: new() :: private static readonly object _initLock = new();
- L48 C23: new InvalidOperationException :: throw new InvalidOperationException("PhysxConvexHullCooker.Initialize must be called before cooking convex meshes.");
- L57 C23: new ArgumentException :: throw new ArgumentException("Convex hull requires at least four vertices.", nameof(hull));
- L73 C37: new PxVec3 :: PxVec3[] vertexBuffer = new PxVec3[hull.Vertices.Length];
- L77 C35: new PxVec3 :: vertexBuffer[i] = new PxVec3 { x = v.X, y = v.Y, z = v.Z };
- L89 C27: new InvalidOperationException :: throw new InvalidOperationException("PxConvexMeshDesc validation failed for supplied hull.");
- L94 C27: new InvalidOperationException :: throw new InvalidOperationException("PhysX must be initialized before cooking convex meshes.");
- L98 C27: new InvalidOperationException :: throw new InvalidOperationException($"PxCreateConvexMesh failed with result {result}.");
- L100 C24: new PhysxConvexMesh :: return new PhysxConvexMesh(meshPtr);


## XRENGINE/Scene/Physics/Physx/PhysxConvexMesh.cs
- L30 C26: new Vector3 :: var result = new Vector3[count];
- L40 C20: new AABB :: return new AABB(bounds.minimum, bounds.maximum);
- L54 C28: new Matrix4x4 :: localInertia = new Matrix4x4(


## XRENGINE/Scene/Physics/Physx/PhysxDynamicRigidBody.cs
- L14 C95: new() :: public static ConcurrentDictionary<nint, PhysxDynamicRigidBody> AllDynamic { get; } = new();


## XRENGINE/Scene/Physics/Physx/PhysxHeightField.cs
- L32 C65: new Exception :: var values = image.GetPixels().GetValues() ?? throw new Exception("Image does not contain pixel values.");
- L34 C23: new Exception :: throw new Exception("Image size does not match heightfield size.");


## XRENGINE/Scene/Physics/Physx/PhysxMaterial.cs
- L15 C80: new() :: public static ConcurrentDictionary<nint, PhysxMaterial> All { get; } = new();
- L197 C28: new ArgumentOutOfRangeException :: _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
- L207 C28: new ArgumentOutOfRangeException :: _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),


## XRENGINE/Scene/Physics/Physx/PhysxObjectLog.cs
- L49 C43: new KeyValuePair :: bool removed = dict.TryRemove(new KeyValuePair<nint, T>(ptr, value));


## XRENGINE/Scene/Physics/Physx/PhysxPlane.cs
- L21 C20: new PhysxPlane :: return new PhysxPlane(p);
- L55 C20: new PhysxPlane :: return new PhysxPlane(InternalPlane.Transform(&tfm));
- L60 C20: new PhysxPlane :: return new PhysxPlane(InternalPlane.InverseTransform(&tfm));


## XRENGINE/Scene/Physics/Physx/PhysxRigidActor.cs
- L19 C93: new() :: public static ConcurrentDictionary<nint, PhysxRigidActor> AllRigidActors { get; } = new();
- L62 C33: new() :: set => SetTransform(new() { p = value.position, q = value.rotation }, true);
- L68 C24: new Vector3 :: position = new Vector3(pose.p.x, pose.p.y, pose.p.z);
- L69 C24: new Quaternion :: rotation = new Quaternion(pose.q.x, pose.q.y, pose.q.z, pose.q.w);
- L84 C29: new() :: => SetTransform(new() { p = position, q = rotation }, wake);
- L98 C31: new PxConstraint :: var constraints = new PxConstraint*[ConstraintCount];
- L108 C26: new PxShape :: var shapes = new PxShape*[ShapeCount];
- L111 C27: new PhysxShape :: var shapes2 = new PhysxShape[ShapeCount];


## XRENGINE/Scene/Physics/Physx/PhysxRigidBody.cs
- L51 C25: new PxTransform :: var x = new PxTransform { q = value.Item1, p = value.Item2 };


## XRENGINE/Scene/Physics/Physx/PhysxScene.cs
- L28 C80: new() :: public static ConcurrentDictionary<nint, PhysxScene> Scenes { get; } = new();
- L50 C56: new() :: public static readonly PxVec3 DefaultGravity = new() { x = 0.0f, y = -9.81f, z = 0.0f };
- L103 C34: new KeyValuePair :: Scenes.TryRemove(new KeyValuePair<nint, PhysxScene>((nint)_scene, this));
- L189 C43: new[] :: [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
- L200 C43: new[] :: [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
- L212 C43: new[] :: [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
- L224 C43: new[] :: [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
- L236 C43: new[] :: [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
- L300 C25: new List :: var owned = new List<PhysxRigidActor>(PhysxDynamicRigidBody.AllDynamic.Count + PhysxStaticRigidBody.AllStaticRigidBodies.Count);
- L379 C43: new[] :: [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
- L431 C88: new() :: private readonly ConcurrentQueue<PhysxController> _pendingControllerReleases = new();
- L523 C28: new PxContactPairHeader :: contactPairs = new PxContactPairHeader[numPairs];
- L581 C24: new FilterShader :: return new FilterShader { data = data, dataSize = dataSize };
- L641 C27: new List :: var addList = new List<PhysxActor>(actors.Length);
- L709 C30: new List :: var removeList = new List<PhysxActor>(actors.Length);
- L802 C73: new() :: public ConcurrentDictionary<nint, PhysxShape> Shapes { get; } = new();
- L810 C48: new PhysxShape :: shape = PhysxShape.Get(ptr) ?? new PhysxShape(ptr);
- L830 C73: new() :: public ConcurrentDictionary<nint, PhysxJoint> Joints { get; } = new();
- L834 C88: new() :: public ConcurrentDictionary<nint, PhysxJoint_Contact> ContactJoints { get; } = new();
- L839 C41: new() :: PxTransform pxlocalFrame0 = new() { p = localFrame0.position, q = localFrame0.rotation };
- L840 C41: new() :: PxTransform pxlocalFrame1 = new() { p = localFrame1.position, q = localFrame1.rotation };
- L842 C28: new PhysxJoint_Contact :: var jointObj = new PhysxJoint_Contact(joint);
- L849 C90: new() :: public ConcurrentDictionary<nint, PhysxJoint_Distance> DistanceJoints { get; } = new();
- L854 C41: new() :: PxTransform pxlocalFrame0 = new() { p = localFrame0.position, q = localFrame0.rotation };
- L855 C41: new() :: PxTransform pxlocalFrame1 = new() { p = localFrame1.position, q = localFrame1.rotation };
- L857 C28: new PhysxJoint_Distance :: var jointObj = new PhysxJoint_Distance(joint);
- L864 C78: new() :: public ConcurrentDictionary<nint, PhysxJoint_D6> D6Joints { get; } = new();
- L869 C41: new() :: PxTransform pxlocalFrame0 = new() { p = localFrame0.position, q = localFrame0.rotation };
- L870 C41: new() :: PxTransform pxlocalFrame1 = new() { p = localFrame1.position, q = localFrame1.rotation };
- L872 C28: new PhysxJoint_D6 :: var jointObj = new PhysxJoint_D6(joint);
- L879 C84: new() :: public ConcurrentDictionary<nint, PhysxJoint_Fixed> FixedJoints { get; } = new();
- L884 C41: new() :: PxTransform pxlocalFrame0 = new() { p = localFrame0.position, q = localFrame0.rotation };
- L885 C41: new() :: PxTransform pxlocalFrame1 = new() { p = localFrame1.position, q = localFrame1.rotation };
- L887 C28: new PhysxJoint_Fixed :: var jointObj = new PhysxJoint_Fixed(joint);
- L894 C92: new() :: public ConcurrentDictionary<nint, PhysxJoint_Prismatic> PrismaticJoints { get; } = new();
- L899 C41: new() :: PxTransform pxlocalFrame0 = new() { p = localFrame0.position, q = localFrame0.rotation };
- L900 C41: new() :: PxTransform pxlocalFrame1 = new() { p = localFrame1.position, q = localFrame1.rotation };
- L902 C28: new PhysxJoint_Prismatic :: var jointObj = new PhysxJoint_Prismatic(joint);
- L909 C90: new() :: public ConcurrentDictionary<nint, PhysxJoint_Revolute> RevoluteJoints { get; } = new();
- L914 C41: new() :: PxTransform pxlocalFrame0 = new() { p = localFrame0.position, q = localFrame0.rotation };
- L915 C41: new() :: PxTransform pxlocalFrame1 = new() { p = localFrame1.position, q = localFrame1.rotation };
- L917 C28: new PhysxJoint_Revolute :: var jointObj = new PhysxJoint_Revolute(joint);
- L924 C92: new() :: public ConcurrentDictionary<nint, PhysxJoint_Spherical> SphericalJoints { get; } = new();
- L929 C41: new() :: PxTransform pxlocalFrame0 = new() { p = localFrame0.position, q = localFrame0.rotation };
- L930 C41: new() :: PxTransform pxlocalFrame1 = new() { p = localFrame1.position, q = localFrame1.rotation };
- L932 C28: new PhysxJoint_Spherical :: var jointObj = new PhysxJoint_Spherical(joint);
- L945 C26: new() :: PxVec3 pos = new() { x = p.X, y = p.Y, z = p.Z };
- L946 C26: new() :: PxQuat rot = new() { x = q.X, y = q.Y, z = q.Z, w = q.W };
- L980 C35: new PhysxActor :: PhysxActor[] actors = new PhysxActor[count];
- L994 C35: new PhysxActor :: PhysxActor[] actors = new PhysxActor[count];
- L1007 C64: new PxArticulationReducedCoordinate :: PxArticulationReducedCoordinate*[] articulations = new PxArticulationReducedCoordinate*[count];
- L1020 C43: new PxConstraint :: PxConstraint*[] constraints = new PxConstraint*[count];
- L1033 C41: new PxAggregate :: PxAggregate*[] aggregates = new PxAggregate*[count];
- L1121 C24: new AABB :: return new AABB { Min = b.minimum, Max = b.maximum };
- L1125 C31: new() :: PxBounds3 b = new() { minimum = value.Min, maximum = value.Max };
- L1173 C48: new PxBroadPhaseRegionInfo :: PxBroadPhaseRegionInfo[] regions = new PxBroadPhaseRegionInfo[count];
- L1277 C34: new ControllerManager :: _controllerManager = new ControllerManager(mgrPtr);
- L1412 C35: new PxRaycastHit :: PxRaycastHit[] hits = new PxRaycastHit[hitCount];
- L1526 C33: new PxSweepHit :: PxSweepHit[] hits = new PxSweepHit[hitCount];
- L1553 C35: new PxOverlapHit :: PxOverlapHit[] hits = new PxOverlapHit[hitCount];
- L1598 C20: new PhysxBatchQuery :: return new PhysxBatchQuery(ptr);
- L1630 C20: new PhysxBatchQuery :: return new PhysxBatchQuery(ptr);
- L1705 C30: new() :: filterCallback = new() { vtable_ = PhysxScene.Native.CreateVTable(PreFilter, PostFilter, Destructor) };
- L1736 C16: new() :: => new()
- L1742 C22: new Vector2 :: UV = new Vector2(hit.u, hit.v),
- L1747 C20: new SweepHit :: return new SweepHit
- L1758 C20: new OverlapHit :: return new OverlapHit
- L2434 C94: new() :: private static Queue<(Vector3 delta, float minDist, float elapsedTime)> _moveQueue = new();


## XRENGINE/Scene/Physics/Physx/PhysxShape.cs
- L34 C77: new() :: public static ConcurrentDictionary<nint, PhysxShape> All { get; } = new();
- L213 C39: new PxMaterial :: PxMaterial*[] materials = new PxMaterial*[MaterialCount];
- L216 C36: new PhysxMaterial :: PhysxMaterial[] mats = new PhysxMaterial[MaterialCount];
- L271 C38: new PxRaycastHit :: PxRaycastHit[] rayHits = new PxRaycastHit[maxHits];
- L275 C39: new PxRaycastHit :: PxRaycastHit[] hits = new PxRaycastHit[num];
- L302 C20: new AABB :: return new AABB(bounds.minimum, bounds.maximum);


## XRENGINE/Scene/Physics/Physx/PhysxStaticRigidBody.cs
- L15 C104: new() :: public static ConcurrentDictionary<nint, PhysxStaticRigidBody> AllStaticRigidBodies { get; } = new();
- L77 C20: new PhysxStaticRigidBody :: return new PhysxStaticRigidBody(stat);


## XRENGINE/Scene/Physics/Physx/PhysxTetrahedronMesh.cs
- L17 C34: new Vector3 :: Vector3[] vertices = new Vector3[VertexCount];
- L27 C30: new uint :: uint[] indices = new uint[TetrahedronCount * 4];
- L51 C28: new uint :: uint[] remap = new uint[TetrahedronCount];
- L61 C20: new AABB :: return new AABB(bounds.minimum, bounds.maximum);


## XRENGINE/Scene/Physics/Physx/PhysxTriangleMesh.cs
- L27 C32: new Vector3 :: Vector3[] result = new Vector3[numVertices];
- L37 C32: new Vector3 :: Vector3[] result = new Vector3[numVertices];
- L46 C20: new AABB :: return new AABB(bounds.minimum, bounds.maximum);
- L57 C33: new uint :: uint[] result = new uint[numTriangleIndices];
- L65 C33: new uint :: uint[] result = new uint[numTriangleIndices];
- L76 C29: new uint :: uint[] result = new uint[numTriangles];
- L88 C20: new AABB :: return new AABB(bounds.minimum, bounds.maximum);
- L96 C30: new float :: float[] result = new float[size];
- L112 C20: new UVector3 :: return new UVector3(numX, numY, numZ);


## XRENGINE/Scene/Prefabs/SceneNodePrefabService.cs
- L24 C37: new() :: XRPrefabSource prefab = new()
- L50 C39: new() :: XRPrefabVariant variant = new()


## XRENGINE/Scene/Prefabs/SceneNodePrefabUtility.cs
- L27 C33: new SceneNodePrefabLink :: node.Prefab ??= new SceneNodePrefabLink();
- L63 C26: new InvalidOperationException :: ?? throw new InvalidOperationException("Failed to deserialize prefab hierarchy");
- L104 C33: new SceneNodePrefabLink :: node.Prefab ??= new SceneNodePrefabLink();
- L122 C29: new SceneNodePrefabLink :: node.Prefab ??= new SceneNodePrefabLink();
- L127 C59: new SceneNodePrefabPropertyOverride :: node.Prefab.PropertyOverrides[propertyPath] = new SceneNodePrefabPropertyOverride
- L178 C47: new() :: Dictionary<Guid, SceneNode> map = new();
- L336 C36: new StringReader :: using var reader = new StringReader(overrideData.SerializedValue);
- L368 C52: new() :: SceneNodePrefabNodeOverride snapshot = new()
- L371 C30: new Dictionary :: Properties = new Dictionary<string, SceneNodePrefabPropertyOverride>(link.PropertyOverrides.Count, StringComparer.Ordinal)
- L381 C16: new() :: => new()


## XRENGINE/Scene/Prefabs/XRPrefabSource.cs
- L113 C23: new InvalidOperationException :: throw new InvalidOperationException("Cannot instantiate an empty prefab.");
- L147 C63: new ModelImportOptions :: var opts = importOptions as ModelImportOptions ?? new ModelImportOptions();
- L149 C34: new ModelImporter :: using var importer = new ModelImporter(filePath, onCompleted: null, materialFactory: null);
- L246 C17: new ShaderVector3 :: new ShaderVector3(new Vector3(1.0f, 1.0f, 1.0f), "BaseColor"),
- L246 C35: new Vector3 :: new ShaderVector3(new Vector3(1.0f, 1.0f, 1.0f), "BaseColor"),
- L247 C17: new ShaderFloat :: new ShaderFloat(1.0f, "Opacity"),
- L248 C17: new ShaderFloat :: new ShaderFloat(1.0f, "Roughness"),
- L249 C17: new ShaderFloat :: new ShaderFloat(0.0f, "Metallic"),
- L250 C17: new ShaderFloat :: new ShaderFloat(0.0f, "Specular"),
- L251 C17: new ShaderFloat :: new ShaderFloat(0.0f, "Emission"),


## XRENGINE/Scene/Prefabs/XRPrefabVariant.cs
- L46 C80: new() :: public List<SceneNodePrefabNodeOverride> NodeOverrides { get; set; } = new();
- L57 C26: new InvalidOperationException :: ?? throw new InvalidOperationException("Prefab variant is missing a base prefab reference.");


## XRENGINE/Scene/SceneNode.cs
- L40 C38: new Transform :: Transform = transform ?? new Transform();
- L51 C38: new Transform :: Transform = transform ?? new Transform();
- L73 C38: new Transform :: Transform = transform ?? new Transform();
- L98 C63: new() :: private readonly EventList<XRComponent> _components = new() { ThreadSafe = true };
- L338 C169: new() :: public T? GetTransformAs<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(bool forceConvert = false) where T : TransformBase, new()
- L424 C194: new() :: public T SetTransform<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(ETransformSetFlags flags = ETransformSetFlags.Default) where T : TransformBase, new()
- L426 C23: new() :: T value = new();
- L456 C29: new List :: var nodes = new List<SceneNode>();
- L1263 C34: new SceneNode :: SceneNode?[] nodes = new SceneNode?[predicates.Length];
- L1304 C45: new SceneNode :: var node = parentNode is null ? new SceneNode(name) : new SceneNode(parentNode, name);
- L1304 C67: new SceneNode :: var node = parentNode is null ? new SceneNode(name) : new SceneNode(parentNode, name);
- L1311 C45: new SceneNode :: var node = parentNode is null ? new SceneNode(name) : new SceneNode(parentNode, name);
- L1311 C67: new SceneNode :: var node = parentNode is null ? new SceneNode(name) : new SceneNode(parentNode, name);
- L1319 C45: new SceneNode :: var node = parentNode is null ? new SceneNode(name) : new SceneNode(parentNode, name);
- L1319 C67: new SceneNode :: var node = parentNode is null ? new SceneNode(name) : new SceneNode(parentNode, name);
- L1328 C45: new SceneNode :: var node = parentNode is null ? new SceneNode(name) : new SceneNode(parentNode, name);
- L1328 C67: new SceneNode :: var node = parentNode is null ? new SceneNode(name) : new SceneNode(parentNode, name);
- L1338 C45: new SceneNode :: var node = parentNode is null ? new SceneNode(name) : new SceneNode(parentNode, name);
- L1338 C67: new SceneNode :: var node = parentNode is null ? new SceneNode(name) : new SceneNode(parentNode, name);
- L1349 C45: new SceneNode :: var node = parentNode is null ? new SceneNode(name) : new SceneNode(parentNode, name);
- L1349 C67: new SceneNode :: var node = parentNode is null ? new SceneNode(name) : new SceneNode(parentNode, name);
- L1374 C215: new() :: public SceneNode NewChildWithTransform<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTransform>(out TTransform tfm, string? name = null) where TTransform : TransformBase, new()
- L1375 C29: new SceneNode :: => SetTransform(new SceneNode(this) { Name = name }, out tfm);
- L1376 C256: new() :: public SceneNode NewChildWithTransform<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTransform, T1>(out TTransform tfm, out T1 comp1, string? name = null) where T1 : XRComponent where TTransform : TransformBase, new()
- L1378 C297: new() :: public SceneNode NewChildWithTransform<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTransform, T1, T2>(out TTransform tfm, out T1 comp1, out T2 comp2, string? name = null) where T1 : XRComponent where T2 : XRComponent where TTransform : TransformBase, new()
- L1380 C338: new() :: public SceneNode NewChildWithTransform<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTransform, T1, T2, T3>(out TTransform tfm, out T1 comp1, out T2 comp2, out T3 comp3, string? name = null) where T1 : XRComponent where T2 : XRComponent where T3 : XRComponent where TTransform : TransformBase, new()
- L1382 C379: new() :: public SceneNode NewChildWithTransform<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTransform, T1, T2, T3, T4>(out TTransform tfm, out T1 comp1, out T2 comp2, out T3 comp3, out T4 comp4, string? name = null) where T1 : XRComponent where T2 : XRComponent where T3 : XRComponent where T4 : XRComponent where TTransform : TransformBase, new()
- L1384 C420: new() :: public SceneNode NewChildWithTransform<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTransform, T1, T2, T3, T4, T5>(out TTransform tfm, out T1 comp1, out T2 comp2, out T3 comp3, out T4 comp4, out T5 comp5, string? name = null) where T1 : XRComponent where T2 : XRComponent where T3 : XRComponent where T4 : XRComponent where T5 : XRComponent where TTransform : TransformBase, new()
- L1386 C461: new() :: public SceneNode NewChildWithTransform<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTransform, T1, T2, T3, T4, T5, T6>(out TTransform tfm, out T1 comp1, out T2 comp2, out T3 comp3, out T4 comp4, out T5 comp5, out T6 comp6, string? name = null) where T1 : XRComponent where T2 : XRComponent where T3 : XRComponent where T4 : XRComponent where T5 : XRComponent where T6 : XRComponent where TTransform : TransformBase, new()
- L1389 C214: new() :: private static SceneNode SetTransform<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TTransform>(SceneNode sceneNode, out TTransform tfm) where TTransform : TransformBase, new()


## XRENGINE/Scene/Transforms/Misc/Boom.cs
- L87 C60: new() :: private PhysxScene.PhysxQueryFilter _queryFilter = new()


## XRENGINE/Scene/Transforms/Misc/DefaultLayers.cs
- L14 C62: new() :: public static Dictionary<int, string> All { get; } = new()


## XRENGINE/Scene/Transforms/Misc/OrbitTransform.cs
- L62 C51: new Vector3 :: var mtx = Matrix4x4.CreateTranslation(new Vector3(0, 0, Radius)) * Matrix4x4.CreateRotationY(float.DegreesToRadians(AngleDegrees));


## XRENGINE/Scene/Transforms/Misc/RectTransform.cs
- L55 C32: new Vector2 :: set => SizeDelta = new Vector2(value, SizeDelta.Y);
- L60 C32: new Vector2 :: set => SizeDelta = new Vector2(SizeDelta.X, value);
- L67 C25: new Vector2 :: AnchorMin = new Vector2(0.5f, 0.5f);
- L68 C25: new Vector2 :: AnchorMax = new Vector2(0.5f, 0.5f);
- L71 C21: new Vector2 :: Pivot = new Vector2(0.5f, 0.5f);
- L77 C33: new Vector3 :: Vector3[] corners = new Vector3[4];
- L82 C26: new Vector3 :: corners[0] = new Vector3(AnchoredPosition.X + OffsetMin.X, AnchoredPosition.Y + OffsetMin.Y, 0); // bottom left
- L83 C26: new Vector3 :: corners[1] = new Vector3(AnchoredPosition.X + OffsetMin.X, AnchoredPosition.Y + height, 0); // top left
- L84 C26: new Vector3 :: corners[2] = new Vector3(AnchoredPosition.X + width, AnchoredPosition.Y + height, 0); // top right
- L85 C26: new Vector3 :: corners[3] = new Vector3(AnchoredPosition.X + width, AnchoredPosition.Y + OffsetMin.Y, 0); // bottom right


## XRENGINE/Scene/Transforms/Noise/NoiseRotationTransform.cs
- L59 C46: new() :: private readonly Rotator _rotation = new();
- L60 C45: new() :: private readonly FastNoise _noise = new();


## XRENGINE/Scene/Transforms/Transform.cs
- L65 C30: new Vector3 :: => Translation = new Vector3(x, Translation.Y, Translation.Z);
- L67 C30: new Vector3 :: => Translation = new Vector3(Translation.X, y, Translation.Z);
- L69 C30: new Vector3 :: => Translation = new Vector3(Translation.X, Translation.Y, z);
- L89 C30: new Vector3 :: => Translation = new Vector3(x + _bindState.Translation.X, Translation.Y, Translation.Z);
- L91 C30: new Vector3 :: => Translation = new Vector3(Translation.X, y + _bindState.Translation.Y, Translation.Z);
- L93 C30: new Vector3 :: => Translation = new Vector3(Translation.X, Translation.Y, z + _bindState.Translation.Z);
- L97 C46: new() :: private TransformState _frameState = new();
- L98 C45: new() :: private TransformState _bindState = new();
- L106 C26: new TransformState :: _bindState = new TransformState { Translation = Translation, Rotation = Rotation, Scale = Scale, Order = Order };
- L331 C49: new Vector3 :: => Translation += Vector3.Transform(new Vector3(x, y, z), Matrix4x4.CreateFromQuaternion(Rotation));
- L406 C26: new byte :: byte[] all = new byte[4 + scale?.Length ?? 0 + translation?.Length ?? 0 + rotation?.Length ?? 0];
- L480 C28: new byte :: byte[] bytes = new byte[6];


## XRENGINE/Scene/Transforms/TransformBase.cs
- L71 C93: new() :: private static readonly ConcurrentQueue<ParentReassignRequest> _parentsToReassign = new();
- L121 C30: new HashSet :: var ancestorsA = new HashSet<TransformBase>();
- L517 C53: new() :: private readonly object _renderMatrixLock = new();
- L533 C60: new() :: private readonly object _inverseRenderMatrixLock = new();
- L573 C52: new() :: private readonly object _localMatrixLock = new();
- L589 C52: new() :: private readonly object _worldMatrixLock = new();
- L603 C59: new() :: private readonly object _inverseLocalMatrixLock = new();
- L618 C59: new() :: private readonly object _inverseWorldMatrixLock = new();
- L638 C25: new EventList :: _children = new EventList<TransformBase>() { ThreadSafe = true };
- L647 C49: new RenderCommandMethod3D :: RenderInfo = RenderInfo3D.New(this, new RenderCommandMethod3D((int)EDefaultRenderPass.OnTopForward, RenderDebug));
- L784 C48: new ParentReassignRequest :: _parentsToReassign.Enqueue(new ParentReassignRequest(this, newParent, preserveWorldTransform, onApplied));
- L1267 C20: new Capsule :: return new Capsule(center, dir, SelectionRadius, halfHeight);
- L1333 C29: new Task :: var tasks = new Task[count];
- L1400 C29: new Task :: var tasks = new Task[count];


## XRENGINE/Scene/Transforms/TransformBase.MatrixInfo.cs
- L97 C38: new MemoryStream :: using var memoryStream = new MemoryStream();
- L98 C37: new GZipStream :: using (var gzipStream = new GZipStream(memoryStream, CompressionLevel.Optimal))
- L129 C38: new MemoryStream :: using var memoryStream = new MemoryStream(arr);
- L130 C36: new GZipStream :: using var gzipStream = new GZipStream(memoryStream, CompressionMode.Decompress);
- L131 C30: new byte :: var deltaBytes = new byte[sizeof(bool)];
- L135 C31: new byte :: var matrixBytes = new byte[16 * sizeof(float)];
- L147 C25: new byte :: var bytes = new byte[16 * sizeof(float)];
- L148 C30: new[] :: Buffer.BlockCopy(new[]
- L160 C26: new float :: var values = new float[16];
- L162 C20: new Matrix4x4 :: return new Matrix4x4(


## XRENGINE/Scene/Transforms/VR/VREyeTransform.cs
- L76 C58: new Vector3 :: _ipdOffset = Matrix4x4.CreateTranslation(new Vector3(IsLeftEye ? -diff : diff, 0.0f, 0.0f));


## XRENGINE/Scene/WorldSettings.cs
- L575 C18: new FogSettings :: return new FogSettings
- L609 C9: new WorldSettings :: return new WorldSettings
- L676 C20: new WorldSettings :: return new WorldSettings
- L721 C14: new ColorF3 :: return new ColorF3(
- L729 C10: new ColorF4 :: return new ColorF4(


---
Total matches: 8189
