# Scene System

XRENGINE uses a hierarchical scene system with SceneNodes that contain components and transforms, providing a flexible and efficient way to organize and manage game objects.

## Overview

The scene system is built around a tree-based hierarchy where SceneNodes can contain components, transforms, and child nodes. This provides a natural way to organize game objects while maintaining efficient spatial queries and rendering.

## Core Classes

### SceneNode
The fundamental building block of the scene hierarchy:

```csharp
public sealed class SceneNode : XRWorldObjectBase
{
    public string Name { get; set; }
    public SceneNode? Parent { get; set; }
    public TransformBase Transform { get; }
    public IEventListReadOnly<XRComponent> Components { get; }
    public bool IsActiveSelf { get; set; }
    public bool IsActiveInHierarchy { get; }
    public XRWorldInstance? World { get; }
    
    // Child management
    public SceneNode? FirstChild { get; }
    public SceneNode? LastChild { get; }
    public SceneNode? GetChild(int index);
    public void AddChild(SceneNode node);
    public void RemoveChild(SceneNode node);
    
    // Component management
    public T? AddComponent<T>(string? name = null) where T : XRComponent;
    public T? GetComponent<T>() where T : XRComponent;
    public T[] GetComponents<T>() where T : XRComponent;
    public bool HasComponent(Type requiredType);
    
    // Hierarchy traversal
    public void IterateHierarchy(Action<SceneNode> nodeAction);
    public void IterateComponents(Action<XRComponent> componentAction, bool iterateChildHierarchy);
    public SceneNode? FindDescendantByName(string name, StringComparison comp = StringComparison.Ordinal);
    public SceneNode? FindDescendant(string path, string pathSplitter = "/");
    
    // Events
    public event Action<SceneNode>? Activated;
    public event Action<SceneNode>? Deactivated;
    public XREvent<(SceneNode node, XRComponent comp)>? ComponentAdded;
    public XREvent<(SceneNode node, XRComponent comp)>? ComponentRemoved;
}
```

### TransformBase
Base class for all transform types:

```csharp
public abstract partial class TransformBase : XRWorldObjectBase, IRenderable
{
    public SceneNode? SceneNode { get; set; }
    public TransformBase? Parent { get; set; }
    public EventList<TransformBase> Children { get; }
    public int Depth { get; }
    public string? Name { get; set; }
    
    // Matrix properties
    public Matrix4x4 LocalMatrix { get; }
    public Matrix4x4 WorldMatrix { get; }
    public Matrix4x4 InverseLocalMatrix { get; }
    public Matrix4x4 InverseWorldMatrix { get; }
    public Matrix4x4 RenderMatrix { get; }
    
    // Transform components
    public Vector3 WorldTranslation { get; }
    public Quaternion WorldRotation { get; }
    public Vector3 WorldScale { get; }
    public Vector3 WorldUp { get; }
    public Vector3 WorldRight { get; }
    public Vector3 WorldForward { get; }
    
    // Events
    public event DelLocalMatrixChanged? LocalMatrixChanged;
    public event DelWorldMatrixChanged? WorldMatrixChanged;
    public event DelRenderMatrixChanged? RenderMatrixChanged;
    
    // Matrix operations
    public bool RecalculateMatrices(bool forceWorldRecalc = false, bool setRenderMatrixNow = false);
    public Task RecalculateMatrixHeirarchy(bool recalcAllChildren, bool setRenderMatrixNow, ELoopType childRecalcType);
    public Task SetRenderMatrix(Matrix4x4 matrix, bool recalcAllChildRenderMatrices = true);
    
    // Child management
    public void AddChild(TransformBase child, bool childPreservesWorldTransform, bool now);
    public void RemoveChild(TransformBase child, bool now);
    public TransformBase? GetChild(int index);
    public TransformBase? FindChild(string name, StringComparison comp = StringComparison.Ordinal);
    public TransformBase? FindDescendant(string name);
}
```

## Transform Types

### Transform
Standard 3D transform with position, rotation, and scale:

```csharp
public class Transform : TransformBase
{
    public Vector3 Translation { get; set; }
    public Quaternion Rotation { get; set; }
    public Vector3 Scale { get; set; }
    public ETransformOrder Order { get; set; }
    
    // Constructors
    public Transform();
    public Transform(Vector3 translation, Quaternion rotation, TransformBase? parent = null, ETransformOrder order = ETransformOrder.TRS);
    public Transform(Vector3 scale, Vector3 translation, Quaternion rotation, TransformBase? parent = null, ETransformOrder order = ETransformOrder.TRS);
    
    // Matrix creation
    protected override Matrix4x4 CreateLocalMatrix();
    protected override Matrix4x4 CreateWorldMatrix();
}
```

### UITransform
2D UI transform for user interface elements:

```csharp
public class UITransform : TransformBase, IRenderable
{
    public Vector2 Translation { get; set; }
    public float DepthTranslation { get; set; }
    public Vector2 Scale { get; set; }
    public EVisibility Visibility { get; set; }
    public UICanvasTransform? ParentCanvas { get; set; }
    public string StylingClass { get; set; }
    public string StylingID { get; set; }
    
    // UI-specific methods
    public UICanvasTransform? GetCanvasTransform();
    public UICanvasComponent? GetCanvasComponent();
    public void InvalidateLayout();
}
```

### CopyTransform
Copies the world matrix of another transform:

```csharp
public class CopyTransform : TransformBase
{
    public TransformBase? Source { get; set; }
    
    protected override Matrix4x4 CreateWorldMatrix()
        => Source is null
            ? Parent?.WorldMatrix ?? Matrix4x4.Identity
            : Source.WorldMatrix;
}
```

### OrbitTransform
Rotates around the parent transform about the local Y axis:

```csharp
public class OrbitTransform : TransformBase
{
    public float Radius { get; set; }
    public float AngleDegrees { get; set; }
    public bool IgnoreRotation { get; set; }
    
    protected override Matrix4x4 CreateLocalMatrix();
}
```

### TransformNone
Does not transform the node:

```csharp
public class TransformNone : TransformBase
{
    protected override Matrix4x4 CreateLocalMatrix()
        => Matrix4x4.Identity;
}
```

## Scene Hierarchy

### Creating Scene Structure
```csharp
// Create root node
var rootNode = new SceneNode("Root");

// Create child nodes
var playerNode = rootNode.NewChild("Player");
var weaponNode = playerNode.NewChild("Weapon");
var effectNode = weaponNode.NewChild("MuzzleFlash");

// Add components
var playerComponent = playerNode.AddComponent<PlayerComponent>();
var weaponComponent = weaponNode.AddComponent<WeaponComponent>();
var effectComponent = effectNode.AddComponent<ParticleEffectComponent>();

// Set transforms
var playerTransform = playerNode.GetTransformAs<Transform>();
playerTransform.Translation = new Vector3(0, 1, 0);
```

### Scene Traversal
```csharp
// Iterate through all children
foreach (var child in sceneNode.Transform.Children)
{
    var childNode = child.SceneNode;
    if (childNode != null)
    {
        // Process child node
        ProcessNode(childNode);
    }
}

// Find nodes by name
var weaponNode = sceneNode.FindDescendantByName("Weapon");

// Get all components of type
sceneNode.IterateComponents<IRenderable>(renderable => 
{
    // Process renderable component
}, true);
```

## World and Scene Management

### XRWorld
Defines a collection of scenes that can be loaded together:

```csharp
public class XRWorld : XRAsset
{
    public List<XRScene> Scenes { get; set; }
    public GameMode? DefaultGameMode { get; set; }
    public WorldSettings Settings { get; set; }
    
    // Constructors
    public XRWorld();
    public XRWorld(string name);
    public XRWorld(string name, params XRScene[] scenes);
    public XRWorld(string name, WorldSettings settings, params XRScene[] scenes);
}
```

### XRScene
Defines a collection of root scene nodes:

```csharp
public class XRScene : XRAsset
{
    public bool IsVisible { get; internal set; }
    public List<SceneNode> RootNodes { get; set; }
    
    // Constructors
    public XRScene();
    public XRScene(string name);
    public XRScene(params SceneNode[] rootNodes);
    public XRScene(string name, params SceneNode[] rootNodes);
}
```

### XRWorldInstance
Handles the runtime instance of a world:

```csharp
public partial class XRWorldInstance : XRObjectBase
{
    public XRWorld? TargetWorld { get; set; }
    public RootNodeCollection RootNodes { get; }
    public VisualScene3D VisualScene { get; }
    public AbstractPhysicsScene PhysicsScene { get; }
    public Lights3DCollection Lights { get; }
    
    // World lifecycle
    public async Task BeginPlay();
    public void EndPlay();
    
    // Scene management
    public void LoadScene(XRScene scene);
    public void UnloadScene(XRScene scene);
    
    // Events
    public XREvent<XRWorldInstance>? PreBeginPlay;
    public XREvent<XRWorldInstance>? PostBeginPlay;
    public XREvent<XRWorldInstance>? PreEndPlay;
    public XREvent<XRWorldInstance>? PostEndPlay;
}
```

## Spatial Queries

### Octree
3D spatial partitioning for efficient queries:

```csharp
public class Octree<T> : I3DRenderTree where T : IOctreeItem
{
    public void Insert(T item);
    public void Remove(T item);
    public List<T> Query(AABB bounds);
    public List<T> Query(Sphere sphere);
    public T? FindNearest(Vector3 point);
    public void Remake(AABB bounds);
}
```

### Quadtree
2D spatial partitioning for UI and 2D elements:

```csharp
public class Quadtree<T> : I2DRenderTree where T : IQuadtreeItem
{
    public void Insert(T item);
    public void Remove(T item);
    public List<T> Query(BoundingRectangle bounds);
    public List<T> Query(BoundingRectangleF bounds);
}
```

### BVH (Bounding Volume Hierarchy)
Hierarchical bounding volumes for complex spatial queries:

```csharp
public class BVH<T> where T : IBVHItem
{
    public void Build(List<T> items);
    public List<T> Query(AABB bounds);
    public List<T> Query(Ray ray);
    public void Update();
}
```

## Scene Events

### Node Events
```csharp
public class SceneNode
{
    public event Action<SceneNode>? Activated;
    public event Action<SceneNode>? Deactivated;
    public XREvent<(SceneNode node, XRComponent comp)>? ComponentAdded;
    public XREvent<(SceneNode node, XRComponent comp)>? ComponentRemoved;
}
```

### Component Events
```csharp
public class XRComponent
{
    public event Action<XRComponent>? ComponentActivated;
    public event Action<XRComponent>? ComponentDeactivated;
    
    // Internal events
    protected internal virtual void AddedToSceneNode(SceneNode sceneNode);
    protected internal virtual void RemovedFromSceneNode(SceneNode sceneNode);
}
```

### Transform Events
```csharp
public class TransformBase
{
    public event DelLocalMatrixChanged? LocalMatrixChanged;
    public event DelWorldMatrixChanged? WorldMatrixChanged;
    public event DelInverseLocalMatrixChanged? InverseLocalMatrixChanged;
    public event DelInverseWorldMatrixChanged? InverseWorldMatrixChanged;
    public event DelRenderMatrixChanged? RenderMatrixChanged;
}
```

## Scene Debugging

### Debug Visualization
```csharp
public class TransformBase
{
    public bool DebugRender { get; set; }
    public float SelectionRadius { get; set; }
    public Capsule? Capsule { get; set; }
    
    protected virtual void RenderDebug();
    protected virtual RenderInfo[] GetDebugRenderInfo();
}
```

### Scene Profiling
```csharp
public class SceneProfiler
{
    public int TotalNodes { get; }
    public int ActiveNodes { get; }
    public int VisibleNodes { get; }
    public float CullingTime { get; }
    public float UpdateTime { get; }
    
    public void BeginFrame();
    public void EndFrame();
    public void GenerateReport();
}
```

## Performance Optimization

### Scene Optimization Techniques
- **Spatial Partitioning**: Use octrees and quadtrees for efficient queries
- **Frustum Culling**: Only process visible objects
- **LOD Systems**: Reduce detail for distant objects
- **Object Pooling**: Reuse frequently created/destroyed objects
- **Batch Processing**: Process similar objects together
- **Parallel Transform Updates**: Asynchronous matrix calculations
- **Thread-Safe Operations**: Concurrent scene modifications

### Memory Management
- **Efficient Transforms**: Use appropriate transform types
- **Component Caching**: Cache frequently accessed components
- **Lazy Loading**: Load scene data on demand
- **Memory Pools**: Use object pools for dynamic objects
- **EventList Thread Safety**: Thread-safe component and child collections

## Example: Creating a Complex Scene

```csharp
// Create main scene
var scene = new XRScene("MainScene");

// Create environment
var environment = new SceneNode("Environment");
var terrain = environment.NewChild("Terrain");
var skybox = environment.NewChild("Skybox");

// Create player
var player = new SceneNode("Player");
var playerTransform = player.GetTransformAs<Transform>();
playerTransform.Translation = new Vector3(0, 1, 0);

var playerComponent = player.AddComponent<PlayerComponent>();
var humanoid = player.AddComponent<HumanoidComponent>();
var movement = player.AddComponent<CharacterMovement3DComponent>();

// Create player equipment
var equipment = player.NewChild("Equipment");
var weapon = equipment.NewChild("Weapon");
var weaponComponent = weapon.AddComponent<WeaponComponent>();

// Create UI
var ui = new SceneNode("UI");
var hud = ui.NewChild("HUD");
var hudComponent = hud.AddComponent<HUDComponent>();

// Add to scene
scene.RootNodes.Add(environment);
scene.RootNodes.Add(player);
scene.RootNodes.Add(ui);

// Create world
var world = new XRWorld("GameWorld", scene);
```

## Example: Scene Querying

```csharp
public class SceneQuerySystem : XRComponent
{
    protected internal override void OnComponentActivated()
    {
        base.OnComponentActivated();
        RegisterTick(ETickGroup.Normal, ETickOrder.Scene, UpdateQueries);
    }
    
    private void UpdateQueries()
    {
        var camera = Camera.Main;
        if (camera == null) return;
        
        // Get all renderable components in hierarchy
        var renderables = new List<IRenderable>();
        SceneNode.IterateComponents<IRenderable>(renderable => 
        {
            renderables.Add(renderable);
        }, true);
        
        // Frustum culling (simplified)
        var visibleRenderables = renderables.Where(r => 
        {
            // Check if renderable is visible to camera
            return r.RenderedObjects.Any(obj => obj.IsVisible);
        }).ToList();
        
        // Process results
        ProcessVisibleObjects(visibleRenderables);
    }
}
```

## Configuration

### World Settings
```json
{
  "Bounds": {
    "Min": [0, 0, 0],
    "Max": [1000, 1000, 1000]
  },
  "PhysicsSettings": {
    "Gravity": [0, -9.81, 0],
    "MaxSubSteps": 10
  },
  "RenderingSettings": {
    "EnableFrustumCulling": true,
    "EnableLOD": true,
    "MaxLODLevel": 3
  }
}
```

### Performance Settings
- **Bounds**: World boundaries for spatial partitioning
- **Physics Settings**: Physics simulation configuration
- **Rendering Settings**: Rendering optimization options
- **Transform Settings**: Matrix calculation preferences

## Related Documentation
- [Component System](components.md)
- [Rendering System](rendering.md)
- [Physics System](physics.md)
- [Animation System](animation.md) 