# UI Layout Architecture

The native UI uses a value-based measure and arrange kernel. The canvas owner
captures authored state, a bounded worker computes geometry, and the owner
validates the result before applying it. See [retained execution](05-Retained-Execution.md)
for resource limits and the [change journal](04-Change-Tracking.md) for dirty domains.

## Coordinates and sizing

Canvas coordinates start at the bottom-left. Positive X points right and positive
Y points up. Screen canvases use pixels. `UICanvasTransform.DpiScale` is an
explicit positive text scale; it does not automatically query the operating system.

Measure computes desired size under a parent constraint. Explicit width and
height override automatic content size. Minimum and maximum constraints, margins,
visibility, and operator-specific child constraints affect the result. Arrange
assigns the slot, actual size, pivot placement, and child bounds. Anchor ranges
and margins determine how a child uses its allocated slot.

Lists aggregate along their primary axis. Splits and docks allocate child regions.
Grids capture tracks and placement values. Rotation, fitting, scrolling, and tabs
have numeric operators. A custom transform supplies `IUILayoutValueAdapter` data;
a worker never calls a virtual method on the live transform.

Wrapped text must measure against the width available inside its parent. The
kernel can repeat measure and arrange when final text width changes intrinsic
size. It rejects a result that does not converge within three passes. It does not
publish partially settled geometry.

## Owner and worker boundary

`UICanvasTransform.UpdateLayout()` and `UpdateLayoutAsync()` both request worker
layout through `UILayoutCanvasSession`. `UILayoutSystem.UpdateCanvasLayout()`
uses this same entry point. `PumpLayoutResults()` accepts completed work.

1. The owner reserves a working-page lease before consuming dirty records.
2. `UILayoutSnapshot` captures values, topology, element identities, font metrics,
   and dependency versions. Scroll limits and virtual list rows synchronize on the
   owner during capture. Capture does not call `CalcAutoWidthCallback` or
   `CalcAutoHeightCallback`. Text size comes from the captured text input. Any
   other auto-size callback causes a `NotSupportedException`.
3. `UILayoutValueKernel` computes measure and arrange from those values. Serial
   reference checks use the same kernel.
4. The owner checks lifetime, topology, versions, and constraints before application.
5. Applied geometry and identities become the canvas's accepted layout generation.

Each canvas has at most one running request and one replacement pending input.
Cancellation does not release storage while a worker still reads it. Re-entrant
capture and application callbacks invalidate the attempt; they cannot apply an
older result over newer authored state.

## Invalidation and caches

Measure changes invalidate the required ancestor path. Arrange-only changes retain
valid measurements. Layout caches include identity, dependency versions, parent
constraints, child topology, and allocated slots. Visual changes do not discard
valid layout. Derived size and position updates change visual, clip, and hit data
without pretending that authored scale or position changed.

The current implementation still copies and checks full retained arrays in some
capture and publication paths. Cache hits do not establish constant-time leaf
updates or zero allocations. Sparse page work remains in the
[open TODO](../../work/todo/ui/native-ui-retained-performance-and-editor-parity-todo.md).

## Text

`FontGlyphSet` owns copies of authored glyph dictionaries and caches immutable
numeric metric pages. Reassign an edited glyph map to commit it. Text inputs carry
font and text revisions, wrapping, alignment, spacing, and DPI. The result contains
immutable glyph placements, intrinsic size, and UTF-16 grapheme caret stops.

The rune-based kernel does not establish full shaping, bidirectional layout, or
fallback-font support. These limits also apply to caret behavior. Platform IME
support is a separate adapter and validation concern.

## Legacy helpers and extensions

The public `UILayoutSystem` measure, arrange, and `FitLayout` helpers and the
transform `Measure`, `Arrange`, and `FitLayout` methods do not compute geometry.
They request canvas layout or read the last accepted result. A detached tree
measures with the same value kernel. The canvas path does not call the
`ArrangeChildren`, `MeasureChildrenWidth`, `MeasureChildrenHeight`, or
`GetActualBounds` overrides on transforms. Do not call them from a worker or use
them to apply an unvalidated canvas result. New custom layout behavior belongs in
a captured value adapter and the shared kernel.

Runtime correctness, visual parity, allocations, and performance are recorded
separately in the [validation plan](../../work/testing/ui/native-ui-validation.md).
