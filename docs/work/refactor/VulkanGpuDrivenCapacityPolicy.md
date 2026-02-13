# Vulkan GPU-Driven Capacity and Overflow Policy

## Scope

This policy covers GPU-driven command capacity handling for Vulkan render passes in XRENGINE.

## Buffers covered

- Command buffers (`AllLoadedCommandsBuffer`, culled command buffers)
- Indirect draw buffer (`DrawElementsIndirectCommand[]`)
- Count/flag buffers (visible count, overflow/truncation flags)

## Capacity model

- Command capacity is tracked per `GPUScene` and exposed via `AllocatedMaxCommandCount`.
- Growth requests use bounded doubling via `ComputeBoundedDoublingCapacity(current, minimumRequired)`.
- Growth is applied through `scene.EnsureCommandCapacity(requestedCapacity)`.
- Capacity growth is bounded to `int.MaxValue` and never unbounded.

## Overflow behavior

When GPU overflow/truncation flags are raised:

1. Overflow is logged with bounded warning budgets.
2. Rendering degrades gracefully for the frame (no crash path).
3. Next-frame capacity growth is requested through bounded doubling.
4. Existing work continues with truncation semantics until larger buffers are active.

## Shipping constraints

- `ShippingFast` does not silently switch to CPU rescue paths.
- Forbidden fallback attempts are counted in `Engine.Rendering.Stats.ForbiddenGpuFallbackEvents`.
- Golden-scene CI expects zero forbidden fallback attempts.

## Telemetry

Per-frame telemetry records:

- Requested/cull/emitted/consumed draw counts
- Overflow counters
- Forbidden fallback counters
- Stage timing (reset/cull/occlusion/indirect/draw)
