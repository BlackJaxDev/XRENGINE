# Synthetic Unity Avatar Project Fixture

This is a minimal, hand-authored Unity 2022.3 project used to validate
XRENGINE's external Unity prefab importer. It contains no Unity, VRChat SDK, or
Poiyomi package source.

Coverage:

- `Assets/SyntheticAvatar.prefab` composes an ASCII FBX and a nested prefab.
- The model instance has stripped GameObject, Transform, and MeshRenderer
  correspondence records using Unity generation-2 file IDs.
- `SyntheticAvatar.fbx.meta` remaps the FBX `Stone` material.
- A prefab override replaces that remap with a synthetic locked-style
  Poiyomi-Pro-authored material. The material exists only to test the lossy
  downgrade classifier and contains no third-party shader implementation.
- The avatar descriptor references an intentionally absent expression asset;
  this must remain a non-fatal avatar-behavior dependency.

The fixture is original XRENGINE test data and is covered by `LICENSE.txt`.

