# Poiyomi Toon 9.3.64 Parity

This report is generated from the pinned source catalog and the engine-owned widget registry. Do not edit it by hand.

- Source commit: `c5aaeeb3a67782b7e8a26e184d5e0a1970792294`
- Shader SHA-256: `7efb9176022291a041ecf332bf999f68ba33591d6f446e60757be83e968e61d8`
- Properties: 3736
- Passes: 5
- Active annotation kinds: 41
- Reachable workflows: 62

## Property Support Summary

| Catalog state | Runtime support statement | Count |
| --- | --- | ---: |
| animationLocking / missing | Preserved inactive; `POI0006` reports the absent runtime mapping | 5 |
| compatibilityAlias / notApplicable | Catalog/editor data; no runtime conversion required | 1 |
| inspectorOnly / notApplicable | Catalog/editor data; no runtime conversion required | 87 |
| integration / preservedInactive | Preserved inactive; unavailable integration is reported | 194 |
| internalData / notApplicable | Catalog/editor data; no runtime conversion required | 758 |
| renderState / missing | Preserved inactive; `POI0006` reports the absent runtime mapping | 216 |
| renderState / nativeEquivalent | Exact or reviewed native mapping | 7 |
| runtime / missing | Preserved inactive; `POI0006` reports the absent runtime mapping | 2319 |
| runtime / nativeEquivalent | Exact or reviewed native mapping | 149 |

Every runtime-visible source value is retained in the versioned descriptor even when its runtime mapping is inactive.

## Active Annotation Parity

| Annotation | Active uses | Classification | XRENGINE equivalent |
| --- | ---: | --- | --- |
| `ButtonVector` | 4 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `Curve` | 4 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `DoNotAnimate` | 41 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `DoNotLock` | 4 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `Enum` | 358 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `Gamma` | 5 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `Gradient` | 12 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `HDR` | 19 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `Header` | 22 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `Helpbox` | 15 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `HideInInspector` | 1578 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `IntRange` | 13 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `KeywordEnum` | 5 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `lilToggleLeft` | 1 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `MaterialToggle` | 1 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `MultiSlider` | 48 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `NonModifiableTextureData` | 1 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `Normal` | 10 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `NoScaleOffset` | 9 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `PowerSlider` | 2 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `Space` | 76 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `sRGBWarning` | 106 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `TextureArray` | 1 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `TextureKeyword` | 18 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `ThryDecalPositioning` | 4 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `ThryHeaderLabel` | 21 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `ThryMask` | 1 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `ThryMultiFloatButtons` | 8 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `ThryRGBAPacker` | 31 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `ThryRichLabel` | 3 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `ThryShaderOptimizerLockButton` | 1 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `ThryTexture` | 7 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `ThryToggle` | 70 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `ThryToggleUI` | 91 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `ThryWideEnum` | 516 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `Toggle` | 4 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `ToggleUI` | 341 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `Vector2` | 164 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `Vector3` | 42 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `VectorLabel` | 133 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |
| `VectorToSliders` | 7 | Native | Typed `ShaderAuthoringWidgetRegistry` capability |

## Reachable Workflow Parity

| Workflow | Kind | Classification | XRENGINE equivalent |
| --- | --- | --- | --- |
| `menu:Assets/Thry/Copy GUID` | menu | Native | Copy stable XRENGINE asset identity |
| `menu:Assets/Thry/Flipbooks/Gif 2 TextureArray` | menu | Native | Versioned texture-array recipe workspace |
| `menu:Assets/Thry/Flipbooks/Images 2 TextureArray` | menu | Native | Versioned texture-array recipe workspace |
| `menu:Assets/Thry/Materials/Add to Cross Shader Editor` | menu | Native | Semantic cross-shader material editor |
| `menu:Assets/Thry/Materials/Cleaner/List Unbound Properties` | menu | Native | Protected material-cleanup report |
| `menu:Assets/Thry/Materials/Cleaner/Remove Unbound Textures` | menu | Native | Protected material-cleanup report |
| `menu:Assets/Thry/Materials/Lock All` | menu | Native | Optimize/prepare variant manager |
| `menu:Assets/Thry/Materials/Lock Folder` | menu | Native | Optimize/prepare variant manager |
| `menu:Assets/Thry/Materials/Unlock All` | menu | Native | Optimize/prepare variant manager |
| `menu:Assets/Thry/Materials/Unlock Folder` | menu | Native | Optimize/prepare variant manager |
| `menu:Assets/Thry/Shaders/Create Locale File` | menu | Native | Versioned locale/preferences workspace |
| `menu:Assets/Thry/Shaders/Ifex Indenting` | menu | Developer only | Existing engine test/developer tooling |
| `menu:Assets/Thry/Shaders/Locale Property` | menu | Native | Versioned locale/preferences workspace |
| `menu:Assets/Thry/Shaders/New Translator Definition` | menu | Native | Semantic shader conversion preview |
| `menu:Assets/Thry/Textures/Find Uses` | menu | Native | Texture packer, usage, and array workspace |
| `menu:Assets/Thry/Textures/Open in Texture Packer` | menu | Native | Texture packer, usage, and array workspace |
| `menu:GameObject/Thry/Materials/Lock All` | menu | Native | Optimize/prepare variant manager |
| `menu:GameObject/Thry/Materials/Open All in Cross Shader Editor` | menu | Native | Semantic cross-shader material editor |
| `menu:GameObject/Thry/Materials/Translate Avatar` | menu | Native | Semantic shader conversion preview |
| `menu:GameObject/Thry/Materials/Unlock All` | menu | Native | Optimize/prepare variant manager |
| `menu:Thry/Cross Shader Editor` | menu | Native | Semantic cross-shader material editor |
| `menu:Thry/Material Lock Manager` | menu | Native | Optimize/prepare variant manager |
| `menu:Thry/Texture Packer` | menu | Native | Texture packer, usage, and array workspace |
| `menu:Thry/ThryEditor/Dev Test/Run Parser Tests` | menu | Developer only | Existing engine test/developer tooling |
| `menu:Thry/ThryEditor/Dev Test/Run Timed Tests` | menu | Developer only | Existing engine test/developer tooling |
| `menu:Thry/ThryEditor/Fix Keywords for All Materials (Slow)` | menu | Native | Variant and animation semantic repair |
| `menu:Thry/ThryEditor/Rebuild Presets Cache` | menu | Native | Versioned preset library and preview |
| `menu:Thry/ThryEditor/Settings` | menu | Native | Versioned locale/preferences workspace |
| `menu:Thry/ThryEditor/Upgraded Animated Properties` | menu | Native | Variant and animation semantic repair |
| `menu:Thry/Twitter` | menu | Preserved inactive | Unsafe social action is inert text |
| `window:AvatarMaterialTranslator` | auxiliaryWindow | Native | Semantic shader conversion preview |
| `window:CrossEditor` | auxiliaryWindow | Native | Semantic cross-shader material editor |
| `window:DecalTool` | auxiliaryWindow | Native | Viewport decal controller |
| `window:GradientEditor` | auxiliaryWindow | Native | Gradient and curve workspace |
| `window:GradientEditor2` | auxiliaryWindow | Native | Gradient and curve workspace |
| `window:ListTextureUsesPopup` | auxiliaryWindow | Native | Texture packer, usage, and array workspace |
| `window:MaterialLinkerPopupWindow` | auxiliaryWindow | Native | Cycle-safe semantic material links |
| `window:NodeGUI` | auxiliaryWindow | Native | Native ImGui authoring workspace |
| `window:PasteSpecialPopup` | auxiliaryWindow | Native | Versioned hierarchical Paste Special |
| `window:PresetsPopupGUI` | auxiliaryWindow | Native | Versioned preset library and preview |
| `window:SearchableEnumPopup` | auxiliaryWindow | Native | Typed searchable enum widget |
| `window:SetNotePopup` | auxiliaryWindow | Native | Persistent local notes |
| `window:Settings` | auxiliaryWindow | Native | Versioned locale/preferences workspace |
| `window:TextPopup` | auxiliaryWindow | Native | Persistent local notes |
| `window:TranslatorPropertySearchPopup` | auxiliaryWindow | Native | Semantic shader conversion preview |
| `window:UnlockedMaterialsList` | auxiliaryWindow | Native | Optimize/prepare variant manager |
| `workflow:crossMaterialEditor` | inspectorWorkflow | Native | Semantic cross-shader material editor |
| `workflow:decalSceneTool` | inspectorWorkflow | Native | Viewport decal controller |
| `workflow:gradientEditor` | inspectorWorkflow | Native | Gradient and curve workspace |
| `workflow:inspectorHierarchy` | inspectorWorkflow | Native | Schema-driven inspector interaction |
| `workflow:localization` | inspectorWorkflow | Native | Versioned locale/preferences workspace |
| `workflow:materialCleanup` | inspectorWorkflow | Native | Protected material-cleanup report |
| `workflow:materialLinking` | inspectorWorkflow | Native | Cycle-safe semantic material links |
| `workflow:materialNotes` | inspectorWorkflow | Native | Persistent local notes |
| `workflow:materialPresets` | inspectorWorkflow | Native | Versioned preset library and preview |
| `workflow:pasteSpecial` | inspectorWorkflow | Native | Versioned hierarchical Paste Special |
| `workflow:propertyContextMenu` | inspectorWorkflow | Native | Schema-driven inspector interaction |
| `workflow:shaderLocking` | inspectorWorkflow | Native | Optimize/prepare variant manager |
| `workflow:shaderTranslator` | inspectorWorkflow | Native | Semantic shader conversion preview |
| `workflow:texturePacker` | inspectorWorkflow | Native | Texture packer, usage, and array workspace |
| `workflow:textureUseLookup` | inspectorWorkflow | Native | Texture packer, usage, and array workspace |
| `workflow:unpreparedMaterialManager` | inspectorWorkflow | Native | Optimize/prepare variant manager |

## Review Contract

- Native entries are exercised by schema, widget, interaction, undo, persistence, and security tests.
- Preserved-inactive entries never execute arbitrary code, reflection, remote fetches, or external commands.
- A source update must regenerate this report and include the reviewed diff with updated fixtures.
