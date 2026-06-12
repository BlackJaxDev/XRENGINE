# Transforms

[Back to user guide](README.md)

Every scene node has a transform. Transforms describe where an object is, how it is rotated, how it is scaled, and how it relates to its parent. For the full matrix and threading model, see [Transform Architecture](../architecture/scene/transforms.md).

## Local And World Space

Use local values when you want an object positioned relative to its parent. Use world values when you want an object positioned in the global scene.

When reparenting, choose whether to preserve world transform. Preserving world transform keeps the object visually in place while changing its parent.

## Common Transform Types

- `Transform` for ordinary 3D objects.
- `UITransform` and related UI transforms for native UI layout.
- `CopyTransform` for following another target.
- `OrbitTransform` for orbital camera or object behavior.
- `TransformNone` when a node should not contribute ordinary spatial state.

## Troubleshooting

If an object appears in the wrong place:

- check whether you are editing local or world values,
- check parent scale and rotation,
- check whether a custom transform recalculates its local matrix,
- check whether physics or animation writes the transform later in the frame.

## Deeper Docs

- [Transform Architecture](../architecture/scene/transforms.md)
- [Scene System](scene.md)
- [Component System](components.md)
