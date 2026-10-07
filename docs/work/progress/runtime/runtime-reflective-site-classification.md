# Runtime reflective-site classification

This inventory classifies the existing runtime reflection boundaries by owner.
It accompanies the [implementation ledger](runtime-data-layout-and-generated-contracts-progress.md)
and the [NativeAOT hardening work](../../todo/runtime/runtime-regression-and-nativeaot-hardening-todo.md).
It is a source audit, not a claim of zero runtime diagnostics or refreshed warning counts.

Player work is entered through explicit published-reader, cooked-snapshot,
begin/end-play, tick-dispatch and active-world component-construction scopes.
A different editor call chain does not become player work merely because a
world is playing. Synchronous scopes use thread-local counters; awaited
begin-play and content work use logical execution-context scopes. `error` rejects
every attempted fallback; log/inventory deduplication never permits a later
attempt to succeed. Published builds skip development diagnostics and continue
to enforce their own registration/codec requirements.

| Owner / reflective boundary | Classification and disposition |
|---|---|
| `CookedAssetTypeReference.TryResolve` / type scan | Player reachable; published resolution uses metadata/registered asset types, development fallback reports type resolution. |
| `AotRuntimeMetadataStore.ResolveType` / `Type.GetType`, assembly scan | Player reachable; generated known-type table and registered resolution precede reflection; fallback diagnostics owned by the metadata store. |
| `RuntimeCookedBinarySerializer` / type resolution and object construction | Player reachable; registered runtime factories and generated codecs precede reflection; fallback reports type resolution/factory categories. |
| `CookedBinarySerializer` and `CookedBinary/Modules/Core/Registry` / BinaryV1, generic events, tuples, sets and type resolution | Authoring serializer also reachable through cooked snapshots; reflective deserialization/factory/type diagnostics apply inside explicit snapshot scopes. Generated runtime codecs are the publication route. |
| `AnimationMember` / property, field and method binding | Player reachable; typed bindings precede reflective member fallback. |
| `AnimationPropertySerialization` / `Keyframes`, keyframe constructors and runtime animation types | `CreateModel` is cooking; `CreateRuntimeAnimation` is player reachable. Member fallback reports; type resolution uses the metadata owner. Generated animation codec coverage remains a validation obligation. |
| `XRComponent.New(Type)` / uninitialized object plus reflected constructor | Player reachable through `SceneNode.AddComponent`; the legacy path remains active and reports a factory violation. Generated registrations exist, but migration is pending a construction context that preserves scene-node access before derived constructor bodies. |
| `RequiresTransformAttribute` / `Activator.CreateInstance` | Player reachable during component creation; transform factories run first, fallback reports. |
| `RuntimePlayerControllerServices.ResolveControllerType` / `Type.GetType` | Player reachable; registered local/remote controller defaults remove name resolution; dynamic fallback reports. |
| `RuntimeVRIKCalibrator.ResolveRuntimeType` / assembly-qualified type lookup | Player reachable; dynamic host fallback reports; the typed calibration service is the intended registration boundary. |
| `SceneNodePrefabUtility.ResolveOverrideType` / `Type.GetType` | Player reachable through prefab override restoration; dynamic fallback reports. |
| `AssetManager.Loading.SerializationAndCache` / asset type scan, `Type.GetType`, cached third-party constructor | Authoring/import normally; potentially player reachable and instrumented when invoked inside a player scope. |
| `RuntimeThirdPartyAssetLoadingServices` / constructor fallback | Authoring/import normally; player invocation reports a missing factory. |
| `AssetLoading.Contexts` / placeholder constructor | Potentially player reachable during deferred reference resolution; factory fallback reports. |
| `XRPersistentCall.ResolveMethod` / parameter type lookup, method discovery | Player reachable; member fallback reports before method enumeration. |
| `SessionSettingsOverlay.ApplyValue` / null child construction | Potentially player reachable when applying session settings; missing typed child construction reports a factory violation. |
| `DelegateBuilder` / expression adapter and default parameter construction | Player reachable; direct delegates are attempted first; dynamic adapter reports member fallback. Value-type defaults now use `Expression.Default`, removing the `Activator` site. |
| `PolymorphicYamlNodeDeserializer` / type lookup and assembly scan | Authoring YAML normally; runtime invocation is instrumented rather than assumed unreachable. |
| `OverrideableSettingYamlTypeConverter` / generic settings construction | Authoring YAML normally; player invocation reports a factory violation and directs users to cooked settings. |
| `XRAssetYamlTypeConverter` / untracked text, missing reference constructors and type hints | Authoring YAML/reference recovery normally; constructor fallback reports on the player path. |
| `AssetManager.Serialization` / discovery and construction of YAML converters | Authoring serializer initialization; published asset loads use the separate published reader and generated registrations. |
| `TransformBase.GetAllTransformTypes`, friendly selector / assembly and attribute discovery | Authoring transform picker; published selection reads the generated runtime metadata list. |
| `GameCSProjLoader` / path and stream assembly loads | Editor/game-project authoring only. Published CoreCLR and AOT execution is rejected before loading; protected content roots also reject file-backed stream loads. |

No allowlist or suppression was introduced to make a parity run appear clean.
The table records possible player reachability conservatively; live parity smoke,
warning refresh and generated contract coverage must provide the acceptance
evidence before the associated tracker closes.
