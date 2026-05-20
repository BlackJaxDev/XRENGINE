# Subdivision And OpenSubdiv TODO

Last Updated: 2026-05-19
Owner: Modeling
Status: Planned child tracker for [GPU-Accelerated Modeling Roadmap](00-gpu-accelerated-modeling-roadmap.md) Phase 5
Target Branch: `modeling-gpu-accelerated-tools`

Source design:

- [GPU-Accelerated Modeling Tools Design](../../design/modeling/gpu-accelerated-modeling-tools-design.md)

Related docs:

- [GPU-Accelerated Modeling Roadmap TODO](00-gpu-accelerated-modeling-roadmap.md)
- [Modeling Topology Foundation TODO](01-modeling-topology-foundation-todo.md)
- [Geometry Nodes Foundation TODO](02-geometry-nodes-foundation-todo.md)

## Parent Roadmap Contract

This tracker owns subdivision evaluator architecture and optional OpenSubdiv evaluation. It must not add OpenSubdiv, submodules, native binaries, or dependency upgrades without explicit approval and the repository dependency process.

## Goal

Introduce subdivision as an evaluator interface over authored control topology. Use an internal/CPU fallback first, then decide whether to integrate OpenSubdiv as an optional backend for Catmull-Clark/Loop subdivision, patch tables, limit surface preview, and final evaluated surface generation.

## Non-Negotiable Rules

- [ ] Authored control topology remains in `XREngine.Modeling`.
- [ ] Subdivision evaluation is derived/disposable until explicitly baked.
- [ ] `ISubdivisionEvaluator` exists before any OpenSubdiv integration.
- [ ] CPU fallback exists before optional native dependency work.
- [ ] Stop for approval before adding OpenSubdiv as a submodule, native binary, or package.
- [ ] Reconfirm OpenSubdiv license text and notice obligations at integration time.
- [ ] Run `pwsh Tools/Generate-Dependencies.ps1` after adding any dependency.
- [ ] Preserve third-party license and notice files if OpenSubdiv is distributed.

## Success Criteria

- [ ] Modeling documents can store subdivision settings, creases, boundary interpolation settings, and preview/bake policy.
- [ ] `ISubdivisionEvaluator` can prepare topology, update control points, evaluate preview, and bake output.
- [ ] A baseline evaluator works without OpenSubdiv.
- [ ] OpenSubdiv is either integrated through the approved dependency flow or deferred with a documented reason.
- [ ] Subdivision preview/render/bake preserves attributes according to policy.
- [ ] Tests cover evaluator interface behavior without requiring native dependencies.

## Primary Code Areas

- `XREngine.Modeling/`
- `XREngine.Runtime.ModelingBridge/`
- `XREngine.Runtime.Rendering/Objects/Meshes/`
- `XREngine.UnitTests/Modeling/`
- `docs/DEPENDENCIES.md`
- `docs/licenses/`
- `Build/Submodules/` if OpenSubdiv is approved as a submodule

## Phase 0: Subdivision Settings And Boundary Model

**Goal:** define subdivision data on the authored topology.

### Tasks

- [ ] Add subdivision settings model:
  - [ ] scheme: Catmull-Clark, Loop, none
  - [ ] preview level
  - [ ] render level
  - [ ] bake level
  - [ ] boundary interpolation
  - [ ] crease policy
  - [ ] face-varying interpolation policy
- [ ] Add edge crease weights.
- [ ] Add vertex crease/corner tags if needed.
- [ ] Add sharp flags integration.
- [ ] Add attribute interpolation policy hooks.
- [ ] Add serialization tests.

### Exit Criteria

- [ ] Control topology can store subdivision settings without requiring an evaluator backend.

## Phase 1: Evaluator Interface

**Goal:** define subdivision behind a small backend boundary.

### Tasks

- [ ] Define `ISubdivisionEvaluator`.
- [ ] Define topology preparation input.
- [ ] Define control point update input.
- [ ] Define preview output buffers.
- [ ] Define bake output buffers or document conversion.
- [ ] Define evaluator capability flags.
- [ ] Define evaluator diagnostics.
- [ ] Add tests with a fake evaluator backend.

### Exit Criteria

- [ ] Callers can use subdivision through an interface without knowing the backend.

## Phase 2: Baseline CPU Evaluator

**Goal:** provide a dependency-free implementation for tests and fallback.

### Tasks

- [ ] Implement simple Catmull-Clark evaluator for supported manifold meshes, or choose a smaller v1 subset and document unsupported cases.
- [ ] Implement Loop evaluator if included in first scope, or defer explicitly.
- [ ] Preserve boundary behavior according to settings.
- [ ] Interpolate point and corner attributes according to policy.
- [ ] Generate preview mesh output.
- [ ] Generate bake output.
- [ ] Add tests for simple cube/plane/control-cage cases.

### Exit Criteria

- [ ] Subdivision works for initial supported cases without OpenSubdiv.

## Phase 3: OpenSubdiv License And Dependency Gate

**Goal:** decide whether OpenSubdiv is allowed for this repo and how it should be supplied.

### Tasks

- [ ] Reconfirm the current OpenSubdiv license text from the upstream repository.
- [ ] Reconfirm notice obligations from upstream `NOTICE.txt`.
- [ ] Review compatibility with open-source and commercial use.
- [ ] Decide supply path:
  - [ ] submodule under `Build/Submodules`
  - [ ] prebuilt native dependency
  - [ ] source build through Tools
  - [ ] defer
- [ ] Decide optional dependency flags and build settings.
- [ ] Disable examples, tutorials, docs, Ptex, OpenCL, CUDA, and optional viewer dependencies unless needed.
- [ ] Obtain owner approval before adding the dependency.

### Exit Criteria

- [ ] OpenSubdiv integration is approved with a supply path or explicitly deferred.

## Phase 4: Optional OpenSubdiv Backend

**Goal:** integrate OpenSubdiv only if Phase 3 approves it.

### Tasks

- [ ] Add OpenSubdiv dependency through approved supply path.
- [ ] Add native wrapper or interop boundary.
- [ ] Implement backend capability detection.
- [ ] Implement topology preparation.
- [ ] Implement control point updates.
- [ ] Implement CPU evaluation backend first.
- [ ] Implement GPU evaluation backend only after CPU backend is stable.
- [ ] Preserve license and notice files.
- [ ] Run dependency generation:

```powershell
pwsh Tools/Generate-Dependencies.ps1
```

- [ ] Review and commit updated `docs/DEPENDENCIES.md`.
- [ ] Review and commit updated `docs/licenses/`.

### Exit Criteria

- [ ] Optional OpenSubdiv backend can be enabled/disabled.
- [ ] Dependency metadata and license files are correct.

## Phase 5: Render, Preview, And Bake Integration

**Goal:** connect subdivision outputs to the modeling and rendering bridge.

### Tasks

- [ ] Preview evaluated subdivision without mutating authored topology.
- [ ] Render evaluated subdivision through transient or cached `XRMesh` output.
- [ ] Bake evaluated subdivision into `ModelingMeshDocument`.
- [ ] Allocate stable IDs for baked output.
- [ ] Preserve/capture attributes on bake.
- [ ] Clear derived caches after bake.
- [ ] Schedule render buffer, bounds, and meshlet refresh.
- [ ] Add tests for preview/render/bake ownership.

### Exit Criteria

- [ ] Subdivision output can preview, render, and bake through explicit paths.

## Validation

```powershell
dotnet build .\XREngine.Modeling\XREngine.Modeling.csproj
dotnet build .\XREngine.Runtime.ModelingBridge\XREngine.Runtime.ModelingBridge.csproj
dotnet test .\XREngine.UnitTests\XREngine.UnitTests.csproj --filter FullyQualifiedName~Modeling
```

If OpenSubdiv is added:

```powershell
pwsh Tools/Generate-Dependencies.ps1
dotnet build XRENGINE.slnx
```
