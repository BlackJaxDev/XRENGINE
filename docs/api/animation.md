# Animation API Reference

XRENGINE's animation API provides comprehensive character animation capabilities including skeletal animation, inverse kinematics, state machines, and GPU acceleration.

## Core Animation Classes

### BaseAnimation
Base class for all animation types.

```csharp
public abstract class BaseAnimation : XRAsset
{
    public float LengthInSeconds { get; set; }
    public float Speed { get; set; }
    public float CurrentTime { get; set; }
    public bool Looped { get; set; }
    public EAnimationState State { get; set; }
    
    public event Action<BaseAnimation>? AnimationStarted;
    public event Action<BaseAnimation>? AnimationEnded;
    public event Action<BaseAnimation>? AnimationPaused;
    
    public abstract void Update(float deltaTime);
    public abstract void Play();
    public abstract void Stop();
    public abstract void Pause();
    public abstract void Reset();
}
```

### AnimationClip
Represents a single animation clip.

```csharp
public class AnimationClip : MotionBase
{
    public EAnimTreeTraversalMethod TraversalMethod { get; set; }
    public AnimationMember RootMember { get; set; }
    public float FrameRate { get; set; }
    public bool IsLooping { get; set; }
    
    public void AddEvent(float time, Action callback);
    public void RemoveEvent(float time);
    public void ClearEvents();
}
```

## Skeletal Animation

### SkeletalAnimation
Handles bone-based animation for skeletal meshes.

```csharp
public class SkeletalAnimation : BaseAnimation
{
    public Dictionary<string, BoneAnimation> BoneAnimations { get; set; }
    public List<AnimationEvent> Events { get; set; }
    
    public void UpdateSkeleton(HumanoidComponent skeleton);
    public void UpdateSkeletonBlended(HumanoidComponent skeleton, SkeletalAnimation other, float weight, EAnimBlendType blendType);
    public void AddBoneAnimation(string boneName, BoneAnimation animation);
    public BoneAnimation? GetBoneAnimation(string boneName);
}
```

### BoneAnimation
Individual bone animation data.

```csharp
public class BoneAnimation : XRBase
{
    public string Name { get; set; }
    public bool UseKeyframes { get; set; }
    public float LengthInSeconds { get; }
    
    // Transform tracks
    public PropAnimFloat TranslationX { get; }
    public PropAnimFloat TranslationY { get; }
    public PropAnimFloat TranslationZ { get; }
    public PropAnimFloat RotationX { get; }
    public PropAnimFloat RotationY { get; }
    public PropAnimFloat RotationZ { get; }
    public PropAnimFloat ScaleX { get; }
    public PropAnimFloat ScaleY { get; }
    public PropAnimFloat ScaleZ { get; }
    
    public void AddKeyframe(float time, Vector3 translation, Quaternion rotation, Vector3 scale);
    public void ClearKeyframes();
    public void OptimizeKeyframes(float tolerance);
}
```

## Humanoid System

### HumanoidComponent
Complete humanoid character system with full body IK.

```csharp
public class HumanoidComponent : XRComponent, IRenderable
{
    public HumanoidSettings Settings { get; set; }
    public bool SolveIK { get; set; }
    
    // Body parts
    public BodySide Left { get; }
    public BodySide Right { get; }
    
    // IK targets
    public Transform? HeadTarget { get; set; }
    public Transform? HipsTarget { get; set; }
    public Transform? LeftHandTarget { get; set; }
    public Transform? RightHandTarget { get; set; }
    public Transform? LeftFootTarget { get; set; }
    public Transform? RightFootTarget { get; set; }
    
    // Bone references
    public Transform? Hips { get; set; }
    public Transform? Spine { get; set; }
    public Transform? Head { get; set; }
    public Transform? LeftShoulder { get; set; }
    public Transform? RightShoulder { get; set; }
    public Transform? LeftArm { get; set; }
    public Transform? RightArm { get; set; }
    public Transform? LeftForeArm { get; set; }
    public Transform? RightForeArm { get; set; }
    public Transform? LeftHand { get; set; }
    public Transform? RightHand { get; set; }
    public Transform? LeftUpLeg { get; set; }
    public Transform? RightUpLeg { get; set; }
    public Transform? LeftLeg { get; set; }
    public Transform? RightLeg { get; set; }
    public Transform? LeftFoot { get; set; }
    public Transform? RightFoot { get; set; }
    
    public void SetBoneReference(EHumanoidBone bone, Transform transform);
    public Transform? GetBoneReference(EHumanoidBone bone);
    public void UpdateIK();
}
```

### HumanoidSettings
Configuration for humanoid characters.

```csharp
public class HumanoidSettings
{
    public Vector2 LeftEyeDownUpRange { get; set; }
    public Vector2 LeftEyeInOutRange { get; set; }
    public Vector2 RightEyeDownUpRange { get; set; }
    public Vector2 RightEyeInOutRange { get; set; }
    public float ArmStretch { get; set; }
    public float LegStretch { get; set; }
    public float UpperArmTwist { get; set; }
    public float LowerArmTwist { get; set; }
    public float UpperLegTwist { get; set; }
    public float LowerLegTwist { get; set; }
    public float FeetSpacing { get; set; }
    public bool HasTranslationDoF { get; set; }
    
    public void SetValue(EHumanoidValue value, float amount);
    public float GetValue(EHumanoidValue value);
    public void ResetToDefaults();
}
```

## Inverse Kinematics

### IKSolverComponent
Base class for IK solvers.

```csharp
public abstract class IKSolverComponent : XRComponent, IRenderable
{
    protected abstract IKSolver GetIKSolver();
    protected virtual void InitializeSolver() { }
    protected virtual void UpdateSolver() { }
    public abstract void Visualize();
    
    public bool Enabled { get; set; }
    public int Iterations { get; set; }
    public float Tolerance { get; set; }
}
```

### VRIKSolverComponent
VR-specific IK solver for realistic VR character movement.

```csharp
public class VRIKSolverComponent : IKSolverComponent
{
    public IKSolverVR Solver { get; }
    public HumanoidComponent Humanoid { get; }
    
    public bool CalibrateOnStart { get; set; }
    public bool UseHandOrientations { get; set; }
    public bool UseFootTracking { get; set; }
    
    public void GuessHandOrientations();
    public void Calibrate();
    public void SetCalibrationData(VRIKCalibrator.Settings settings);
}
```

### IKSolverVR
Advanced VR IK solver with full body support.

```csharp
public class IKSolverVR : IKSolver
{
    public void SetToReferences(HumanoidComponent humanoid);
    public void SolveFullBodyIK(
        TransformChain hipToHead,
        TransformChain leftLegToAnkle,
        TransformChain rightLegToAnkle,
        TransformChain leftShoulderToWrist,
        TransformChain rightShoulderToWrist,
        Transform? headTarget,
        Transform? hipsTarget,
        Transform? leftHandTarget,
        Transform? rightHandTarget,
        Transform? leftFootTarget,
        Transform? rightFootTarget,
        int iterations);
    
    public void SetArmStretch(float stretch);
    public void SetLegStretch(float stretch);
    public void SetLocomotionWeight(float weight);
    public void SetLegsWeight(float weight);
    public void SetArmsWeight(float weight);
}
```

## Animation State Machine

### AnimStateMachine
Complex animation blending and state management.

```csharp
public class AnimStateMachine : XRAsset
{
    public bool AnimatePhysics { get; set; }
    public EventList<AnimLayer> Layers { get; set; }
    public Dictionary<string, object?> Variables { get; set; }
    public AnimState? CurrentState { get; }
    
    public void Initialize(object? rootObject);
    public void EvaluationTick(object? rootObject, float delta);
    public void SetVariable(string name, object value);
    public T? GetVariable<T>(string name);
    public void TriggerTransition(string transitionName);
}
```

### AnimLayer
Individual layer in the state machine.

```csharp
public class AnimLayer : XRBase
{
    public EApplyType ApplyType { get; set; }
    public EventList<AnimState> States { get; set; }
    public float Weight { get; set; }
    public int InitialStateIndex { get; set; }
    public AnimState? CurrentState { get; set; }
    public bool Enabled { get; set; }
    
    public void AddState(AnimState state);
    public void RemoveState(AnimState state);
    public void SetWeight(float weight);
}
```

### AnimState
Individual animation state.

```csharp
public class AnimState : XRBase
{
    public string Name { get; set; }
    public float Speed { get; set; }
    public bool Loop { get; set; }
    public EventList<AnimTransition> Transitions { get; set; }
    public AnimationClip? Animation { get; set; }
    public float Weight { get; set; }
    
    public void AddTransition(AnimTransition transition);
    public void RemoveTransition(AnimTransition transition);
    public void SetAnimation(AnimationClip animation);
}
```

### AnimTransition
Transition between animation states.

```csharp
public class AnimTransition : XRBase
{
    public AnimState? FromState { get; set; }
    public AnimState? ToState { get; set; }
    public float Duration { get; set; }
    public ETransitionType Type { get; set; }
    public AnimCondition[] Conditions { get; set; }
    
    public bool CanTransition();
    public void Trigger();
}
```

## Animation Components

### AnimStateMachineComponent
Manages animation state machines.

```csharp
public class AnimStateMachineComponent : XRComponent
{
    public AnimStateMachine StateMachine { get; set; }
    public HumanoidComponent? Humanoid { get; set; }
    
    public bool AutoStart { get; set; }
    public bool UpdateOnTick { get; set; }
    
    protected internal void EvaluationTick();
    public void SetState(string stateName);
    public void TriggerTransition(string transitionName);
}
```

### AnimationClipComponent
Plays individual animation clips.

```csharp
public class AnimationClipComponent : XRComponent
{
    public AnimationClip? Animation { get; set; }
    public bool StartOnActivate { get; set; }
    public float Weight { get; set; }
    public float Speed { get; set; }
    public bool Loop { get; set; }
    
    public void Start();
    public void Stop();
    public void Pause();
    public void Resume();
    public void Reset();
    
    public event Action? AnimationStarted;
    public event Action? AnimationEnded;
    public event Action? AnimationPaused;
}
```

## Keyframe Animation

### IKeyframe
Base interface for keyframes.

```csharp
public interface IKeyframe
{
    float Time { get; set; }
    EInterpolationType InterpolationType { get; set; }
    EaseType EaseType { get; set; }
}
```

### Keyframe Types

#### FloatKeyframe
```csharp
public class FloatKeyframe : IKeyframe
{
    public float Time { get; set; }
    public float Value { get; set; }
    public EInterpolationType InterpolationType { get; set; }
    public EaseType EaseType { get; set; }
    
    public FloatKeyframe(float time, float value);
}
```

#### Vector3Keyframe
```csharp
public class Vector3Keyframe : IKeyframe
{
    public float Time { get; set; }
    public Vector3 Value { get; set; }
    public EInterpolationType InterpolationType { get; set; }
    public EaseType EaseType { get; set; }
    
    public Vector3Keyframe(float time, Vector3 value);
}
```

#### QuaternionKeyframe
```csharp
public class QuaternionKeyframe : IKeyframe
{
    public float Time { get; set; }
    public Quaternion Value { get; set; }
    public EInterpolationType InterpolationType { get; set; }
    public EaseType EaseType { get; set; }
    
    public QuaternionKeyframe(float time, Quaternion value);
}
```

### Interpolation Types
```csharp
public enum EInterpolationType
{
    Linear,
    Bezier,
    Step,
    Smooth
}
```

### Ease Types
```csharp
public enum EaseType
{
    None,
    EaseIn,
    EaseOut,
    EaseInOut
}
```

## Animation Blending

### Blend Types
```csharp
public enum EAnimBlendType
{
    Override,    // Replace current animation
    Additive,    // Add to current animation
    Multiply,    // Multiply with current animation
    Blend        // Smooth blend between animations
}
```

### Blend Trees
Complex animation blending with multiple inputs.

```csharp
public class BlendTree : XRBase
{
    public List<BlendTreeChild> Children { get; set; }
    public EBlendType BlendType { get; set; }
    public float BlendParameter { get; set; }
    public AnimationClip? Result { get; }
    
    public void AddChild(BlendTreeChild child);
    public void RemoveChild(BlendTreeChild child);
    public void SetBlendParameter(float parameter);
}
```

### BlendTreeChild
```csharp
public class BlendTreeChild : XRBase
{
    public AnimationClip? Animation { get; set; }
    public float Weight { get; set; }
    public float Threshold { get; set; }
    public Vector2 Position { get; set; }
}
```

## GPU Acceleration

### GPU Skinning
Compute shader-based skeletal animation.

```csharp
public class GPUSkinning
{
    public ComputeShader SkinningShader { get; }
    public ComputeBuffer BoneMatricesBuffer { get; }
    public ComputeBuffer BoneWeightsBuffer { get; }
    public ComputeBuffer BoneIndicesBuffer { get; }
    
    public void UpdateBoneMatrices(Matrix4x4[] boneMatrices);
    public void DispatchSkinning(int vertexCount);
    public void BindBuffers();
    public void UnbindBuffers();
}
```

### Physics Chains
GPU-accelerated physics chains for cloth and hair.

```csharp
public class GPUPhysicsChainComponent : XRComponent, IRenderable
{
    public Transform? Root { get; set; }
    public float Damping { get; set; }
    public float Elasticity { get; set; }
    public float Stiffness { get; set; }
    public float Inert { get; set; }
    public int LinkCount { get; set; }
    
    public ComputeShader PhysicsShader { get; }
    public ComputeBuffer PositionsBuffer { get; }
    public ComputeBuffer VelocitiesBuffer { get; }
    
    protected override void UpdatePhysics();
    public void SetChainParameters(float damping, float elasticity, float stiffness, float inert);
}
```

## VR-Specific Features

### VR IK Calibration
VR-specific IK calibration for accurate tracking.

```csharp
public class VRIKCalibrator
{
    public class Settings
    {
        public float HeadHeight { get; set; }
        public float ArmLength { get; set; }
        public float LegLength { get; set; }
        public float ShoulderWidth { get; set; }
        public float HipWidth { get; set; }
        public float FootLength { get; set; }
    }
    
    public static void Calibrate(HumanoidComponent humanoid, Settings settings);
    public static Settings GetDefaultSettings();
    public static void AutoCalibrate(HumanoidComponent humanoid);
}
```

### VR Device Animation
Animation for VR devices (controllers, trackers).

```csharp
public class VRDeviceModelComponent : ModelComponent
{
    protected abstract DeviceModel? GetRenderModel(VrDevice? device);
    public void LoadModelAsync(DeviceModel? deviceModel);
    public void SetDevice(VrDevice? device);
    public void UpdateModel();
    
    public bool ShowDeviceModel { get; set; }
    public bool AnimateDevice { get; set; }
}
```

## Example: Creating an Animated Character

```csharp
// Create a humanoid character
var character = new SceneNode();
var humanoid = character.AddComponent<HumanoidComponent>();
var animStateMachine = character.AddComponent<AnimStateMachineComponent>();

// Configure humanoid settings
humanoid.Settings.LeftEyeDownUpRange = new Vector2(-30, 30);
humanoid.Settings.RightEyeDownUpRange = new Vector2(-30, 30);
humanoid.Settings.ArmStretch = 0.05f;
humanoid.Settings.LegStretch = 0.05f;

// Set up animation state machine
var stateMachine = new AnimStateMachine();
var idleState = new AnimState { Name = "Idle", Animation = idleClip };
var walkState = new AnimState { Name = "Walk", Animation = walkClip };
var runState = new AnimState { Name = "Run", Animation = runClip };

// Create transitions
var idleToWalk = new AnimTransition
{
    FromState = idleState,
    ToState = walkState,
    Duration = 0.25f,
    Type = ETransitionType.Blend
};

var walkToRun = new AnimTransition
{
    FromState = walkState,
    ToState = runState,
    Duration = 0.15f,
    Type = ETransitionType.Blend
};

// Set up layer
var layer = new AnimLayer();
layer.States.Add(idleState);
layer.States.Add(walkState);
layer.States.Add(runState);
layer.InitialStateIndex = 0;

stateMachine.Layers.Add(layer);
animStateMachine.StateMachine = stateMachine;
```

## Example: VR IK Setup

```csharp
// Add VR IK solver
var vrIK = character.AddComponent<VRIKSolverComponent>();

// Set up IK targets
humanoid.HeadTarget = headsetTransform;
humanoid.LeftHandTarget = leftControllerTransform;
humanoid.RightHandTarget = rightControllerTransform;

// Configure IK settings
vrIK.Solver.SetToReferences(humanoid);
vrIK.Iterations = 10;
vrIK.Tolerance = 0.001f;
vrIK.UseHandOrientations = true;

// Calibrate VR IK
var calibrationSettings = new VRIKCalibrator.Settings
{
    HeadHeight = 1.7f,
    ArmLength = 0.7f,
    LegLength = 0.9f,
    ShoulderWidth = 0.4f
};

VRIKCalibrator.Calibrate(humanoid, calibrationSettings);
```

## Example: Animation Events

```csharp
// Create animation clip with events
var attackClip = new AnimationClip();
attackClip.LengthInSeconds = 1.5f;
attackClip.FrameRate = 30.0f;

// Add animation events
attackClip.AddEvent(0.2f, () => PlaySwordSwingSound());
attackClip.AddEvent(0.5f, () => EnableSwordCollision());
attackClip.AddEvent(0.8f, () => DisableSwordCollision());
attackClip.AddEvent(1.2f, () => PlayImpactEffect());

// Create animation component
var animComponent = character.AddComponent<AnimationClipComponent>();
animComponent.Animation = attackClip;
animComponent.StartOnActivate = false;

// Subscribe to events
animComponent.AnimationStarted += () => Debug.Out("Attack started");
animComponent.AnimationEnded += () => Debug.Out("Attack ended");
```

## Example: GPU Skinning

```csharp
// Set up GPU skinning
var gpuSkinning = new GPUSkinning();
gpuSkinning.SkinningShader = new ComputeShader(skinningShaderSource);

// Create buffers
gpuSkinning.BoneMatricesBuffer = new ComputeBuffer<Matrix4x4>(boneCount);
gpuSkinning.BoneWeightsBuffer = new ComputeBuffer<Vector4>(vertexCount);
gpuSkinning.BoneIndicesBuffer = new ComputeBuffer<Vector4>(vertexCount);

// Update bone matrices
var boneMatrices = humanoid.GetBoneMatrices();
gpuSkinning.UpdateBoneMatrices(boneMatrices);

// Dispatch skinning
gpuSkinning.BindBuffers();
gpuSkinning.DispatchSkinning(vertexCount);
gpuSkinning.UnbindBuffers();
```

## Configuration

### Animation Settings
```json
{
  "Animation": {
    "EnableGPUSkinning": true,
    "MaxBoneCount": 128,
    "AnimationLOD": true,
    "IKIterations": 10,
    "BlendTreeDepth": 8,
    "KeyframeOptimization": true,
    "EventPrecision": 0.001
  }
}
```

## Related Documentation
- [Component System](../components.md)
- [Scene System](../scene.md)
- [Rendering System](../rendering.md)
- [Physics System](../physics.md)
- [VR Development](../vr-development.md) 