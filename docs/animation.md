# Animation System

XRENGINE features a comprehensive animation system with skeletal animation, inverse kinematics, state machines, and GPU acceleration optimized for VR and real-time applications.

## Overview

The animation system provides advanced character animation capabilities including skeletal animation, IK solvers, animation state machines, and GPU-accelerated skinning for high performance.

## Core Animation Classes

### BaseAnimation
The foundation for all animation types:

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
}
```

### AnimationClip
Represents a single animation clip that can be played:

```csharp
public class AnimationClip : MotionBase
{
    public EAnimTreeTraversalMethod TraversalMethod { get; set; }
    public AnimationMember RootMember { get; set; }
}
```

## Skeletal Animation

### SkeletalAnimation
Handles bone-based animation for skeletal meshes:

```csharp
public class SkeletalAnimation : BaseAnimation
{
    public Dictionary<string, BoneAnimation> BoneAnimations { get; set; }
    
    public void UpdateSkeleton(HumanoidComponent skeleton);
    public void UpdateSkeletonBlended(HumanoidComponent skeleton, SkeletalAnimation other, float weight, EAnimBlendType blendType);
}
```

### BoneAnimation
Individual bone animation data:

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
}
```

## Humanoid System

### HumanoidComponent
Complete humanoid character system with full body IK:

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
}
```

### HumanoidSettings
Configuration for humanoid characters:

```csharp
public class HumanoidSettings
{
    public Vector2 LeftEyeDownUpRange { get; set; }
    public Vector2 LeftEyeInOutRange { get; set; }
    public Vector2 RightEyeDownUpRange { get; set; }
    public Vector2 RightEyeInOutRange { get; set; }
    
    public void SetValue(EHumanoidValue value, float amount);
    public float GetValue(EHumanoidValue value);
}
```

## Inverse Kinematics

### IKSolverComponent
Base class for IK solvers:

```csharp
public abstract class IKSolverComponent : XRComponent, IRenderable
{
    protected abstract IKSolver GetIKSolver();
    protected virtual void InitializeSolver() { }
    protected virtual void UpdateSolver() { }
    public abstract void Visualize();
}
```

### VRIKSolverComponent
VR-specific IK solver for realistic VR character movement:

```csharp
public class VRIKSolverComponent : IKSolverComponent
{
    public IKSolverVR Solver { get; }
    public HumanoidComponent Humanoid { get; }
    
    public void GuessHandOrientations();
}
```

### IKSolverVR
Advanced VR IK solver with full body support:

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
}
```

## Animation State Machine

### AnimStateMachine
Complex animation blending and state management:

```csharp
public class AnimStateMachine : XRAsset
{
    public bool AnimatePhysics { get; set; }
    public EventList<AnimLayer> Layers { get; set; }
    public Dictionary<string, object?> Variables { get; set; }
    
    public void Initialize(object? rootObject);
    public void EvaluationTick(object? rootObject, float delta);
}
```

### AnimLayer
Individual layer in the state machine:

```csharp
public class AnimLayer : XRBase
{
    public EApplyType ApplyType { get; set; }
    public EventList<AnimState> States { get; set; }
    public float Weight { get; set; }
    public int InitialStateIndex { get; set; }
    public AnimState? CurrentState { get; set; }
}
```

### AnimState
Individual animation state:

```csharp
public class AnimState : XRBase
{
    public string Name { get; set; }
    public float Speed { get; set; }
    public bool Loop { get; set; }
    public EventList<AnimTransition> Transitions { get; set; }
    public AnimationClip? Animation { get; set; }
}
```

## Animation Components

### AnimStateMachineComponent
Manages animation state machines:

```csharp
public class AnimStateMachineComponent : XRComponent
{
    public AnimStateMachine StateMachine { get; set; }
    public HumanoidComponent? Humanoid { get; set; }
    
    protected internal void EvaluationTick();
}
```

### AnimationClipComponent
Plays individual animation clips:

```csharp
public class AnimationClipComponent : XRComponent
{
    public AnimationClip? Animation { get; set; }
    public bool StartOnActivate { get; set; }
    public float Weight { get; set; }
    public float Speed { get; set; }
    
    public void Start();
    public void Stop();
    public void Pause();
}
```

## GPU Acceleration

### GPU Skinning
Compute shader-based skeletal animation for high performance:

```glsl
#version 450
layout(local_size_x = 256) in;

layout(std430, binding = 0) buffer BoneMatricesBuffer
{
    mat4 BoneMatrices [];
};

layout(std430, binding = 1) buffer BoneInvBindMatricesBuffer
{
    mat4 BoneInvBindMatrices [];
};

layout(std430, binding = 2) buffer BoneMatrixIndicesBuffer
{
    int BoneMatrixIndices [];
};

layout(std430, binding = 3) buffer BoneMatrixWeightsBuffer
{
    float BoneMatrixWeights [];
};

void main()
{
    uint index = gl_GlobalInvocationID.x;
    
    vec4 finalPosition = vec4(0.0);
    vec3 finalNormal = vec3(0.0);
    vec3 finalTangent = vec3(0.0);
    
    for (int i = 0; i < 4; i++)
    {
        int boneIndex = BoneMatrixIndices[index * 4 + i];
        float weight = BoneMatrixWeights[index * 4 + i];
        mat4 boneMatrix = BoneInvBindMatrices[boneIndex] * BoneMatrices[boneIndex];
        
        finalPosition += (boneMatrix * basePosition) * weight;
        finalNormal += (boneMatrix3 * baseNormal) * weight;
        finalTangent += (boneMatrix3 * baseTangent) * weight;
    }
}
```

### Physics Chains
GPU-accelerated physics chains for cloth and hair:

```csharp
public class GPUPhysicsChainComponent : XRComponent, IRenderable
{
    public Transform? Root { get; set; }
    public float Damping { get; set; }
    public float Elasticity { get; set; }
    public float Stiffness { get; set; }
    public float Inert { get; set; }
}
```

## Keyframe Animation

### Keyframe System
Support for various keyframe types:

```csharp
public interface IKeyframe
{
    float Time { get; set; }
    EInterpolationType InterpolationType { get; set; }
}

public class FloatKeyframe : IKeyframe
{
    public float Time { get; set; }
    public float Value { get; set; }
    public EInterpolationType InterpolationType { get; set; }
}

public class Vector3Keyframe : IKeyframe
{
    public float Time { get; set; }
    public Vector3 Value { get; set; }
    public EInterpolationType InterpolationType { get; set; }
}
```

### Interpolation Types
- **Linear**: Straight-line interpolation
- **Bezier**: Smooth curve interpolation
- **Step**: Discrete value changes
- **EaseIn/EaseOut**: Smooth acceleration/deceleration

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
Complex animation blending with multiple inputs:

```csharp
public class BlendTree : XRBase
{
    public List<BlendTreeChild> Children { get; set; }
    public EBlendType BlendType { get; set; }
    public float BlendParameter { get; set; }
}
```

## VR-Specific Features

### VR IK Calibration
VR-specific IK calibration for accurate tracking:

```csharp
public class VRIKCalibrator
{
    public class Settings
    {
        public float HeadHeight { get; set; }
        public float ArmLength { get; set; }
        public float LegLength { get; set; }
    }
    
    public static void Calibrate(HumanoidComponent humanoid, Settings settings);
}
```

### VR Device Animation
Animation for VR devices (controllers, trackers):

```csharp
public class VRDeviceModelComponent : ModelComponent
{
    protected abstract DeviceModel? GetRenderModel(VrDevice? device);
    public void LoadModelAsync(DeviceModel? deviceModel);
}
```

## Performance Optimization

### GPU Skinning Benefits
- **Parallel Processing**: Process multiple vertices simultaneously
- **Reduced CPU Load**: Move skinning calculations to GPU
- **Memory Efficiency**: Direct GPU memory access
- **Scalability**: Handle complex skeletons efficiently

### Animation LOD
- **Distance-Based**: Reduce animation complexity at distance
- **Performance-Based**: Adapt to frame rate targets
- **Quality Presets**: Different quality levels for different hardware

## Configuration

### Animation Settings
```json
{
  "EnableGPUSkinning": true,
  "MaxBoneCount": 128,
  "AnimationLOD": true,
  "IKIterations": 10,
  "BlendTreeDepth": 8
}
```

### Performance Settings
- **GPU Skinning**: Enable compute shader skinning
- **Bone Limits**: Maximum bones per skeleton
- **IK Iterations**: Number of IK solver iterations
- **Blend Tree Depth**: Maximum blend tree complexity

## Example: Creating an Animated Character

```csharp
// Create a humanoid character
var character = new SceneNode();
var humanoid = character.AddComponent<HumanoidComponent>();
var animStateMachine = character.AddComponent<AnimStateMachineComponent>();

// Configure humanoid settings
humanoid.Settings.LeftEyeDownUpRange = new Vector2(-30, 30);
humanoid.Settings.RightEyeDownUpRange = new Vector2(-30, 30);

// Set up animation state machine
var stateMachine = new AnimStateMachine();
var idleState = new AnimState { Name = "Idle", Animation = idleClip };
var walkState = new AnimState { Name = "Walk", Animation = walkClip };

var layer = new AnimLayer();
layer.States.Add(idleState);
layer.States.Add(walkState);
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
vrIK.GuessHandOrientations();
```

## Related Documentation
- [Component System](components.md)
- [Scene System](scene.md)
- [Rendering System](rendering.md)
- [Physics System](physics.md)
- [VR Development](vr-development.md) 