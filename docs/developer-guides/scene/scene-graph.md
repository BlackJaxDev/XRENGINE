# Scene Graph Developer Guide

[Back to developer guides](../README.md)

This guide covers code-facing scene graph usage. For the runtime architecture and threading model, see [Scene Architecture](../../architecture/scene/overview.md).

## Creating Nodes

Create nodes directly and attach them to a parent or world root:

```csharp
var node = new SceneNode("Player");
var camera = node.AddComponent<CameraComponent>();
worldInstance.RootNodes.Add(node);
```

Prefer clear node names and stable hierarchy paths when editor tooling or serialized references need to find objects later.

## Adding Components

Use `AddComponent<T>()`, `GetOrAddComponent<T>()`, or `TryAddComponent<T>()` depending on whether duplicate initialization is acceptable.

```csharp
var mesh = node.GetOrAddComponent<ModelComponent>();
var body = node.GetOrAddComponent<DynamicRigidBodyComponent>();
```

Required component attributes may add dependencies automatically. See [Component API](../components/component-api.md).

## Traversal And Lookup

Use the built-in traversal helpers rather than ad-hoc recursion when possible:

- `IterateHierarchy`
- `IterateComponents<T>`
- `FindDescendant`
- `FindDescendantByName`
- `GetPath`

Root systems should subscribe to scene or root collection events when they maintain mirrors of the scene graph.

## Activation

Node activation cascades through children. Component activation follows both the component's `IsActive` flag and the owning node's hierarchy state.

Use activation hooks for resource registration and teardown. Avoid assuming construction means the component is ready to touch world services.

## Prefabs

For reusable hierarchies, use the prefab helpers described in [Prefab Workflow](../../user-guide/prefab-workflow.md). Runtime spawning should go through `AssetManager` or `XRWorldInstance` prefab helpers so links and overrides remain coherent.

## Related Docs

- [Scene Architecture](../../architecture/scene/overview.md)
- [Transform Architecture](../../architecture/scene/transforms.md)
- [Component API](../components/component-api.md)
