# Overrideable Settings

A cascading configuration system where settings flow from **Engine → Project → User**, with each level optionally overriding the one below. The final resolved value at runtime is called the **effective value**.

---

## Overview

The overrideable settings system solves a common configuration problem: how to provide sensible defaults while allowing customization at multiple levels without duplicating entire config files.

**Key principles:**
- **Cascading precedence** – User overrides beat project overrides, which beat engine defaults
- **Explicit opt-in** – A setting only overrides when `HasOverride` is explicitly enabled
- **Safe fallbacks** – Disabled overrides transparently fall through to lower layers
- **Automatic dirty tracking** – Any change marks the owning asset dirty for save operations

---

## Cascade Resolution

```
┌─────────────────┐
│  User Override  │  ← Highest priority (per-user preferences)
│  HasOverride?   │
└────────┬────────┘
         │ No
         ▼
┌─────────────────┐
│ Project Override│  ← Middle priority (per-project settings)
│  HasOverride?   │
└────────┬────────┘
         │ No
         ▼
┌─────────────────┐
│ Engine Default  │  ← Lowest priority (global defaults)
└─────────────────┘
         │
         ▼
   Effective Value
```

The **effective value** is the final result after walking this chain. It's what the application actually uses at runtime.

### Resolution Rules

1. Check user override → if `HasOverride = true`, return its `Value`
2. Check project override → if `HasOverride = true`, return its `Value`
3. Return engine default

This means:
- A user can always override any setting for themselves
- A project can set defaults that differ from engine defaults
- If nothing overrides, the engine default is used

---

## Core Types

### `OverrideableSetting<T>`

The fundamental building block. Each instance stores:

| Property | Type | Description |
|----------|------|-------------|
| `HasOverride` | `bool` | Whether this level actively overrides the fallback |
| `Value` | `T` | The override value (only used when `HasOverride = true`) |

**Behaviors:**
- Setting `Value` to a non-null value automatically enables `HasOverride`
- `ClearOverride()` sets `HasOverride = false` and resets `Value` to default
- `SetOverride(value)` explicitly sets both `HasOverride = true` and `Value`
- `Resolve(fallback)` returns `Value` if `HasOverride`, otherwise returns `fallback`
- `ResolveNullable(fallback)` same as above but handles nullable types

### `IOverrideableSetting`

Non-generic interface for reflection-based tooling:
- `HasOverride` – read/write
- `ValueType` – the `typeof(T)`
- `BoxedValue` – get/set `Value` as `object`

### Cascade Extension Methods

```csharp
// For value types
T ResolveCascade<T>(T engineDefault, OverrideableSetting<T> projectOverride, OverrideableSetting<T> userOverride)

// For nullable/reference types
T? ResolveCascadeNullable<T>(T? engineDefault, OverrideableSetting<T>? projectOverride, OverrideableSetting<T>? userOverride)
```

These walk the cascade in correct priority order and return the effective value.

---

## Setting Layers

| Layer | Example Classes | Scope | Typical Location |
|-------|-----------------|-------|------------------|
| **Engine** | `EngineSettings` | Global defaults for all projects | Engine installation |
| **Project** | `GameStartupSettings`, `EditorPreferencesOverrides` | Per-project configuration | Project assets folder |
| **User** | `UserSettings` | Per-user preferences | User's app data |

### Layer Details

**Engine defaults** – The baseline configuration shipped with the engine. These provide reasonable out-of-the-box behavior.

**Project overrides** – Stored in the project's assets. A game project can:
- Override startup settings via `GameStartupSettings`
- Override editor preferences via `EditorPreferencesOverrides` (theme, debug options, etc.)
- Define project-specific defaults that all team members share

**User overrides** – Personal preferences stored outside the project. Not checked into source control. Examples: preferred theme, window positions, recently opened files.

---

## Effective Settings

The **effective value** is what the application actually uses after resolving all override layers. Understanding this is crucial:

```csharp
// Engine default: VSync = true
// Project override: VSync.HasOverride = true, VSync.Value = false  
// User override: VSync.HasOverride = false

// Effective value: false (project override wins, user didn't override)
```

```csharp
// Engine default: VSync = true
// Project override: VSync.HasOverride = true, VSync.Value = false  
// User override: VSync.HasOverride = true, VSync.Value = true

// Effective value: true (user override wins)
```

```csharp
// Engine default: VSync = true
// Project override: VSync.HasOverride = false
// User override: VSync.HasOverride = false

// Effective value: true (engine default, nothing overrode it)
```

### When to Use Effective Values

- **Runtime logic** – Always use the effective value when the setting actually affects behavior
- **UI display** – Show the effective value so users understand what's actually active
- **Serialization** – Save the override state, not the effective value (overrides are relative)

---

## Dirty Tracking

Changes to any `OverrideableSetting` automatically mark the owning asset dirty. This ensures:

- Modified settings appear in "unsaved changes" lists
- Save/Save All captures all override changes
- Undo/redo works correctly

### How It Works

1. Base classes use reflection to discover all `IOverrideableSetting` properties
2. They subscribe to `PropertyChanged` on each setting
3. When `HasOverride` or `Value` changes, the event bubbles up
4. The owning asset raises its own `PropertyChanged` for the containing property
5. The asset is marked dirty

### Base Classes

| Class | Inherits From | Use When |
|-------|---------------|----------|
| `OverrideableSettingsOwnerBase` | `XRBase` | Non-asset classes that own overrideable settings |
| `OverrideableSettingsAssetBase` | `XRAsset` | Asset classes (saveable to disk) |

Both delegate to `OverrideableSettingsTracker` for the actual tracking logic.

**To use:**
```csharp
public class MySettings : OverrideableSettingsAssetBase
{
    private OverrideableSetting<float> _volumeOverride = new();
    public OverrideableSetting<float> VolumeOverride => _volumeOverride;
    
    // Tracking is automatic - changes to VolumeOverride mark this asset dirty
}
```

---

## Inspector UI

The editor renders overrideable settings with four components:

| Component | Description |
|-----------|-------------|
| **Base** | The fallback value from the lower layer (read-only display) |
| **Override** | Toggle checkbox to enable/disable the override |
| **Override Value** | Editable field (grayed out when override is disabled) |
| **Effective** | The final resolved value after cascade (read-only display) |

This UI makes it immediately clear:
- What value would be used if you didn't override
- Whether you're currently overriding
- What the actual runtime value will be

---

## Usage Examples

### Defining an Overrideable Setting

```csharp
public class GameStartupSettings : OverrideableSettingsAssetBase
{
    // Private backing field
    private OverrideableSetting<bool> _vsyncOverride = new();
    private OverrideableSetting<int> _targetFrameRateOverride = new();
    
    // Public property (read-only to prevent replacing the instance)
    public OverrideableSetting<bool> VSyncOverride => _vsyncOverride;
    public OverrideableSetting<int> TargetFrameRateOverride => _targetFrameRateOverride;
}
```

### Resolving the Effective Value

```csharp
public bool GetEffectiveVSync()
{
    return OverrideableSettingExtensions.ResolveCascade(
        engineDefault: Engine.Rendering.EngineSettings.VSync,
        projectOverride: _projectSettings.VSyncOverride,
        userOverride: _userSettings.VSyncOverride);
}
```

### Setting an Override

```csharp
// Option 1: Set Value (auto-enables HasOverride)
settings.VSyncOverride.Value = false;

// Option 2: Explicit
settings.VSyncOverride.SetOverride(false);

// Option 3: Enable override but keep current value
settings.VSyncOverride.HasOverride = true;
```

### Clearing an Override

```csharp
// Reverts to using the fallback from lower layers
settings.VSyncOverride.ClearOverride();
```

---

## Adding Overrideable Settings to New Classes

1. **Choose the right base class:**
   - `OverrideableSettingsAssetBase` for saveable assets
   - `OverrideableSettingsOwnerBase` for non-asset objects

2. **Add fields and properties:**
   ```csharp
   private OverrideableSetting<T> _mySettingOverride = new();
   public OverrideableSetting<T> MySettingOverride => _mySettingOverride;
   ```

3. **Implement resolution logic** where the setting is consumed:
   ```csharp
   var effective = ResolveCascade(engineDefault, projectOverride, userOverride);
   ```

4. **Ensure serialization** – The base classes handle dirty tracking; ensure your asset type is properly serialized.

---

## Related Files

| File | Purpose |
|------|---------|
| [OverrideableSetting.cs](../../XREngine.Data/Core/OverrideableSetting.cs) | Core `OverrideableSetting<T>` and interfaces |
| [OverrideableSettingsOwnerBase.cs](../../XREngine.Data/Core/Objects/OverrideableSettingsOwnerBase.cs) | Base for non-asset owners |
| [OverrideableSettingsAssetBase.cs](../../XREngine.Data/Core/Assets/OverrideableSettingsAssetBase.cs) | Base for asset owners |
| [OverrideableSettingsTracker.cs](../../XREngine.Data/Core/Objects/OverrideableSettingsTracker.cs) | Shared tracking logic |
