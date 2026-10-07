# Asset Import Validation

[Work docs index](../../README.md) · [Testing docs](../README.md)

Scope: runtime, editor, and visual checks for third-party asset import into native XRENGINE assets: Unity prefabs, FBX, USD, glTF, prefab sub-asset externalization, and the model import binary cache.

Architecture: [Model Import](../../../developer-guides/assets/model-import.md), [Unity Conversion Integrations](../../../developer-guides/assets/unity-conversion-integrations.md), [Native FBX Import And Export](../../../developer-guides/assets/native-fbx-import-export.md).

Code todos: [Native FBX import/export](../../todo/assets/fbx-import-export-todo.md), [Model import binary cache](../../todo/assets/model-import-binary-cache-todo.md), [USD import/export](../../todo/assets/usd-import-export-todo.md).

Related validation: [fastgltf glTF Import](../gltf-import.md).

## Setup

- Use a named isolated MCP editor session. Follow the [editor and tooling workflow](../../../developer-guides/ai/agent-editor-workflows.md).
- Import through the same external-file UI path that users use. Place prefabs by drag and drop so that the ordinary `XRPrefabSource` instantiation path runs.
- Validate OpenGL first. Then run the narrowest useful Vulkan check.
- Private fixtures, such as the Unity avatar corpus, stay outside the repository. Opt-in tests skip with a clear reason when the corpus path is not available.

## Imported Checks

### From unity-prefab-avatar-import-todo.md

| Check | Procedure | Expected | Status | Last evidence |
|---|---|---|---|---|
| Unity avatar on Vulkan | Import the avatar fixture, place it with a static camera, and capture it from front, oblique, and rear positions on Vulkan. | The capture is upright and matches the OpenGL result, including forward-masked materials. | Open | 2026-07-31: placement and texture streaming worked, but the screenshot readback was vertically inverted and forward-masked passes were wrong. See the [import investigation](../../investigations/assets/unity-prefab-avatar-import-2026-07-29.md). |

The OpenGL acceptance for the Unity avatar import passed on 2026-08-04. The [import investigation](../../investigations/assets/unity-prefab-avatar-import-2026-07-29.md) has the evidence.

### From prefab-asset-externalization-twopass-todo.md

No manual checks remain. The externalization acceptance (no inline sub-asset blocks in the root prefab, each sub-asset on disk once, references resolve on load, and the visual prefab smoke) passed. The missing regression tests are code items in the [FBX todo](../../todo/assets/fbx-import-export-todo.md#open-code-items-moved-from-prefab-asset-externalization-twopass-todomd).

## Failures

| Check | Symptom | Investigation or code item |
|---|---|---|
| Unity avatar on Vulkan | Vertically inverted screenshot readback; incorrect forward-masked rendering. | [Unity prefab avatar import investigation](../../investigations/assets/unity-prefab-avatar-import-2026-07-29.md) |
