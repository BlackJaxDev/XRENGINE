# Browser output source and companion ownership

The earlier eight-object canvas-composed registry observation was already closed
by the [unified browser checkpoint](../platform/unified-browser-checkpoint-2026-10-01.md#reviewed-lifetime-repair-and-lit-renderer-continuation).
The following defects were found separately in qualified source tree
`390a21595accad2b5fee4dc785880e692f00c9c5`.

Auxiliary material factories allocated replacement `RenderingParameters` without
an owner. Material destruction correctly released the companion and its original
constructor-owned options, while preserving replacement options as borrowed.
The lit-color, lit-texture, authored-texture, textured-alpha, unlit and Uber
factories now configure each new companion's existing owned options. Parameters,
textures, authored replacement options and cached shaders retain their existing
owners. Variant invalidation and shader ownership are unchanged.

An unassigned camera can request its default pipeline after browser startup's
construction scopes have closed. The scoped browser factory now retains its exact
new source allocations in session ownership, including explicit Advanced
offscreen sources. Existing shutdown ordering stops outputs before releasing
these owners. Explicit authored sources bypass this factory, and stopping one
session does not destroy another session's sources.

Command containers retained terminated pipeline instances as strong dictionary
keys after ordinary source membership was removed. Each instance now weakly tracks
the exact containers it uses and removes only its own bookkeeping at terminal
teardown, including externally retained containers from earlier command graphs.
The reverse tracker does not keep otherwise unowned graphs alive. Cleanup does
not call shared branch-resource release hooks or destroy the authored pipeline;
another live output retains its state. The terminal instance also clears its
last viewport reference.

## Validation

The baseline production-assembly probe reproduced four registered survivors for
one lit-color normal/shadow pair in each of three cycles: two replacement options
and their two asset metadata lists. An unlit normal companion left two survivors.
Companion materials were already destroyed, and pending destruction was drained.
Two real pipeline instances retained both root and detached-container keys after
both terminal teardowns. Actual browser factory and `StopCore` calls also showed
unowned Default and Advanced sources surviving their session cleanup.

Exact implementation tree `e8af67957b15bc42d642e159a643b417c64a54e4` passes the native
Browser build, including its Rendering, WebGPU, Host and Core dependencies, with
zero warnings and errors. The same probe against that complete Browser app DLL
set passes 23 checks. All measured material cycles return to the exact registered
baseline; both container key counts move from two to one to zero without changing
the other instance. Each session destroys its generated sources, drains its owner
list and preserves unrelated authored sources. Borrowed material options survive
their borrower, and a detached graph remains collectible while its instance lives.

Source manifests, exact assembly paths and SHA-256 hashes, baseline/fixed logs and
the small reflection probe are under
`Build/_AgentValidation/20261001-225000-lit-surface/scratch/canvas-ownership-20261004/`.
The validated app DLLs are under the same run's
`temp-build/output-lifetime-browser-0816/bin/XREngine.Browser/release/`.
The probe invokes production factory, destruction and terminal-state methods. It
uses a real pending WebGPU renderer owner without device initialization, JavaScript
imports or world startup. These results establish the specified managed ownership
mechanisms, not complete browser start/stop acceptance, GPU completion, physical
pixels or a whole-session heap budget.

Direct-new desktop fallback fragment shaders and their source text still require
a separate ownership repair. Shared `ShaderHelper` variants must remain borrowed;
that desktop behavior is outside this change.
