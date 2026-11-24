# Runtime Environment Settings

Every environment variable declared by XRENGINE is available in the ImGui
editor in the **Runtime Environment** window. Open it from **View > Runtime
Environment** or **Settings > Runtime Environment**. **Global Editor
Preferences > Runtime Environment** shows the same table and has a button that
opens the window.

Each row shows the category, the variable name, the launch value, the value or
override, the effective value, and how quickly a change can take effect.
Boolean variables have direct On, Off, and Inherit controls. Variables with a
fixed set of values show a list of those values. Other values can be edited as
text.

Use the controls above the table to find variables:

- **Search** filters by variable name.
- **Category** shows one category only.
- **Only set/overridden** shows variables that have a value or a runtime
  override.
- **Only overrides** shows variables that have a runtime override.
- **Only unsupported** shows variables whose launch or effective value is not
  a value that the variable accepts.
- **Refresh Process Values** reads values again that other code changed
  outside the editor.

A value that the variable does not accept is marked **[unsupported]** or
**[invalid]** in red. The panel still applies it, and the status line shows
the accepted values. Sensitive values are masked.

Runtime overrides affect only the current process and are not written to the
user or machine environment. Inherit removes the temporary override and
restores the explicit launch value, or the editor preference value when no
launch value was supplied.

Apply modes are:

- **Immediate**: subscribers update without restarting a renderer.
- **Next operation**: the next relevant operation reads the new value.
- **Renderer restart**: restart active renderers from the panel.
- **OpenXR restart**: restart the OpenXR session from the panel.
- **Process restart**: the value is exposed and applied to the process, but the
  owning bootstrap path cannot safely be reconstructed in place.

Validation, tracing, diagnostic, and debugging variables are opt-in and remain
off when unset. Feature paths use their best available implementation by
default and fall back based on capability checks. Environment variables that
disable or downgrade those paths use explicit `XRE_DISABLE_*`, `XRE_BYPASS_*`,
or similarly narrow compatibility names and also remain off when unset.

Explicit launch values remain authoritative until a runtime override is
created. VS Code launch and runnable tasks set environment variables only when
their names and descriptions identify the specific world, role, validation,
profiling, or compatibility scenario being requested.

## Reading And Writing Variables In Code

Engine code reads and writes environment variables through `XREnvironment`
(`XREngine.Data/Environment/XREnvironment.cs`). Do not call
`Environment.GetEnvironmentVariable` or `Environment.SetEnvironmentVariable`
directly, because the panel cannot see or override those values.

- `XREnvironment.GetValue(name)` returns the effective value. When no runtime
  override exists, it reads the process environment, so values that other code
  writes stay visible.
- `XREnvironment.SetProcessValue(name, value)` writes a process value for
  bootstrap or subsystem setup. It does not create a runtime override.
- `XREnvironment.VariableChanged` tells subscribers that a value changed. Use it
  for variables with the **Immediate** apply mode. For example, `Engine.Time`
  applies the timer settings again when `XRE_DESKTOP_RENDER_UNCAPPED` changes.

To add a variable:

1. Add a constant to `XREngineEnvironmentVariables`. Use the constant in code,
   not the string literal.
2. `XREngineEnvironmentVariableCatalog` finds every constant by reflection.
   It sets the category, value kind, and apply mode from the field name and
   value. Look at the new row in the panel. If a result is wrong, change the
   rules in the catalog. For example, add the field to `SensitiveFields` or
   `TextOrEnumFields`.
3. If the variable accepts only a fixed set of values, add an entry to
   `EnumValueSpecs` in the catalog. Give the allowed values and any aliases
   that the parser also accepts. The panel then shows a list and marks other
   values as unsupported.

RenderBench owns two opt-in Vulkan device diagnostics. Both are off when
unset, require renderer recreation, and are applied from the recipe before
device creation and restored after teardown:

| Variable | Recipe input | Device behavior |
| --- | --- | --- |
| `XRE_VK_RENDER_BENCH_GPU_CALIBRATION` | `gpu_profiling.calibrated_timestamps` | Requests `VK_EXT_calibrated_timestamps` for bounded host/device clock correlation. |
| `XRE_VK_RENDER_BENCH_PERFORMANCE_QUERY` | `hardware_counter_policy` / `HardwareCounters` instrumentation | Requests `VK_KHR_performance_query` and its query-pool feature for intrusive counter replay. |

Unsupported required diagnostics fail preparation. Optional diagnostics publish
unsupported status; they do not synthesize calibration or hardware counters.
Use the [profiler recipes](../diagnostics/profiler.md#dedicated-vulkan-renderbench)
to select these observers rather than inheriting editor environment settings.

## AOT Parity Diagnostics

| Variable | Values | Behavior |
| --- | --- | --- |
| `XRE_AOT_PARITY` | `off`, `warn`, `error` | Development-only. Reports player-path resolutions that succeed only through a reflective fallback NativeAOT cannot provide. Defaults to `off` in interactive editor sessions and `error` in the unit-test lane and headless validation. Published builds ignore it. See [AOT Final Game Builds](aot-final-game-builds.md#development-parity-diagnostics). |
