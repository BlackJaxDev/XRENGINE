# Scene System

[Back to user guide](README.md)

Scenes are the authoring units that hold node hierarchies. Worlds collect one or more scenes and run them inside an `XRWorldInstance`. For the runtime architecture, see [Scene Architecture](../architecture/scene/overview.md). For code recipes, see [Scene Graph Developer Guide](../developer-guides/scene/scene-graph.md).

## Everyday Model

- A world owns scenes.
- A scene owns root nodes.
- A scene node owns one transform and zero or more components.
- Components add behavior such as rendering, physics, audio, networking, animation, and tools.

Use scenes to group content that should be loaded, hidden, shown, or streamed together. Use nodes to organize object hierarchies.

## Working With Nodes

In the editor, create nodes through the hierarchy panel or authoring tools. In code, create `SceneNode` instances, parent them under another node or a world root, then add components.

Activation is hierarchical: disabling a parent disables active children in the hierarchy without destroying them.

## Prefabs

Use prefabs for reusable node hierarchies. Prefab instances can record overrides, create variants, and be spawned at runtime.

See [Prefab Workflow](prefab-workflow.md) for current prefab authoring and runtime helpers.

## Deeper Docs

- [Scene Architecture](../architecture/scene/overview.md)
- [Scene Graph Developer Guide](../developer-guides/scene/scene-graph.md)
- [Transforms](transforms.md)
- [Components](components.md)
