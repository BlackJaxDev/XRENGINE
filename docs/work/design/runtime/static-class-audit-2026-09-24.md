# Static Class Placement Review

The generated [static class inventory](../../audit/static-classes.md) contains 964 distinct entries: 873 top-level and 91 nested. It was regenerated on 2026-09-24 with `Tools/Reports/Find-StaticClasses.ps1`.

The report is a source-text inventory, not a compiler symbol index. It groups partial declarations by project, namespace, name, scope, and immediate parent. Declarations split across lines, generated files, excluded directories, and conditional compilation can affect the count. Placement findings below were checked against source and repository callers; API consumers outside this repository still need consideration before public types are removed.

## Recommended changes

| Priority | Classes | Recommendation | Evidence and reason |
| --- | --- | --- | --- |
| High | `ThirdPartyCachePathPolicies` in `XREngine.Runtime.Core/Assets/Caching/` and `XREngine.Data/Core/Assets/Caching/` | Choose one cache path policy contract and registry. Keep the registry used by `AssetManager` as the authority, migrate any real consumers of the other contract, then remove the duplicate. | Both projects define separate `IThirdPartyCachePathPolicy`, `ThirdPartyCachePathRequest`, and registry types. `AssetManager.Loading.SerializationAndCache.cs` resolves policies through the Runtime.Core registry. Repository search found no call site explicitly using the Data registry. The two implementations also differ in duplicate-policy handling, so they should not remain interchangeable by name alone. |
| High | `CameraParameterEditorRegistry` in `XREngine.Runtime.Rendering/Rendering/Camera/ICameraParameterCustomEditor.cs` | Move the registry and ImGui editor interface into `XREngine.Editor`; retain camera parameter metadata in Rendering. | The only repository caller of the registry is `XREngine.Editor/ComponentEditors/CameraComponentEditor.cs`. Its interface describes ImGui inspector drawing, while the runtime rendering assembly owns the cache and editor construction today. The `CameraParameterEditorAttribute.CustomEditorType` contract and XML documentation will need updating with the move. |
| Medium | `ImGuiTextFieldHelper` in `XREngine.Runtime.Rendering/Rendering/UI/` | Move to an editor UI namespace and project. | Every repository call found is in `XREngine.Editor`; the helper implements inspector and settings field context menus. The move reduces editor controls in the runtime rendering surface without changing rendering behavior. |
| Medium | `AnimationClipMemoryPackRegistration`, `AnimStateMachineMemoryPackRegistration`, and `BlendTreeMemoryPackRegistration` in `XREngine.Animation/Serialization/` | Fold their one-purpose registration methods into `AnimationSerializationRegistration`, or make them private implementation details of the corresponding serializer. Keep the cooked serializers separate because their codecs call them directly. | Each registration class is called only by `AnimationSerializationRegistration.Install()`. They add three top-level types without providing independent ownership or reuse. |
| Medium | `Utility` in `XREngine.Data/HelperMethods.cs` | Split the unrelated directory helper and byte color scaling constant into purpose-named homes. Remove `Swap<T>` if API review confirms it has no consumers. | `EnsureDirPathExists` is used by editor, input integration, and rendering; `ByteToFloat` is used by shader color parameters. The generic class name hides these separate responsibilities. |
| Investigate | `GameCSProjLoader` in `XREngine.Runtime.Core/Scene/Components/Scripting/` | Separate editor project build/reload orchestration from the runtime assembly-loading service. Move the whole loader only if runtime gameplay loading is intentionally unsupported. | Current repository call sites are editor code, but the loader explicitly supports runtime assembly loading and rejects NativeAOT. Moving the entire type now would prematurely decide that runtime capability. |

## Existing boundaries to keep

- `RuntimePhysicsChainRendering` in Runtime.Core is an intentional registration boundary: the Core component calls its bridge, and the concrete implementation lives in Runtime.Rendering. Moving it into Rendering would reverse the dependency direction.
- Feature-owned resource-name classes such as `AdvancedVisibilityResourceNames` and `AdvancedReconstructionResourceNames` describe separate rendering resources. Their similar names alone do not justify a global constants class.
- Large partial static classes such as `Engine` and `EditorImGuiUI` account for many declaration rows. Their partial count is not evidence that independent classes should be merged into them.

## Validation

The report script completed and `git diff --check` passed for the script and inventory. Known `static unsafe class` declarations, including `RendererNativeCallbackBridge` and `PhysxConversions`, appear in the regenerated report. No source classes were moved or changed as part of this audit.
