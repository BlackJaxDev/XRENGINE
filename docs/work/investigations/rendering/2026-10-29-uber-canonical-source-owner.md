# Uber canonical shader source ownership

Status: source recovery and publication fixes passed the focused runtime probe
(42 checks). Successful-generation retirement remains open.
The original defects mixed a shader-file probe with a replacement byte owner
and accepted an unloaded text object after a file disappeared. The recovery
path now captures the byte owner before probing, retries stale reads, and
reserves the complete source installation for commit.

## Evidence

At `0771d056bf30b037922d32d2d3ad315260d3dad7`, a disposable executable calls
`XRMaterial.ResolveCanonicalUberFragmentShader` with a generated shader and a
real source path. A fake `IShaderSourceFileBackend` returns a successful probe
and changes `DirectStorageIO.Source` during that callback. The returned
canonical shader contains the replacement runtime source's text, while the
probe belonged to the earlier shader provider.

A second call deletes the physical file after returning a positive probe. The
subsequent `TextFile.LoadText` returns false, but the method assigns and returns
a canonical shader whose `Source.Text` is null. Both cases were reproduced on
Linux without a renderer or network. The executable and output are disposable
under `Build/_AgentValidation/20261028-120000-webgpu-io/` in
`scratch/ShaderBoundaryProbe/` and `logs/shader-boundary-probe.log`.

## Original boundary findings

`ShaderSourceResolver.CaptureOwner` tracks the shader file backend and asset
filesystem generations. `TextFile.LoadText` separately captures a
`RuntimeAssetReadLease`, detects encoding from bytes, and publishes text with
its own source lifetime and disk provenance. A source swap in the gap can select
a different byte owner. A positive probe is not a promise that the later read
will succeed.

A fix must retain BOM and provenance behavior, avoid assigning a canonical
shader when the read returns false, and define how stale synchronous callers
retry or fall back. It must also avoid retaining temporary `TextFile` or
`XRShader` objects in the global object cache on rejected attempts. A raw-read
publication lease alone does not pin the shader backend or asset filesystem;
point-in-time checks still allow a concurrent replacement before assignment.
The host must keep path identities consistent across its shader and asset-read
providers. No shader change is included in this batch.

`RenderObjectPublicationScope` shares its batch with a parent scope. Disposing
an incomplete nested scope aborts that batch, so a failed probe cannot simply
use a nested scope and then continue with the fallback. A recovery design must
also preserve the returned shader's lifetime if an enclosing publication fails.

The shader backend and asset filesystem setters have no publication reservation.
Adding a rejection to service setters requires a review of their teardown paths.
For example, the runtime shader service installation disposes its installed
service in a `finally` block after it attempts to restore the previous service.
A new rejection there could leave the disposed service installed. Host-file
admission also reads mutable caller-thread and shader-service state. The fix
needs a coordinated lifetime and retry contract for these inputs, not only a
second generation check.

## Implemented boundary

The implementation and host retry contract are specified in
[Canonical Source Recovery](../../../architecture/rendering/uber-shader-varianting.md#canonical-source-recovery).
The fix includes independent render publication for recovered and shared
fallback shaders, numeric cache-owner stamps, a retained asset-cache epoch,
and retry-safe disposal of the runtime shader service installation.

The disposable probe is under
`Build/_AgentValidation/20261029-205003-implementation/scratch/phase2/canonical-probe/`.
It covers read-time replacements, BOM and provenance, missing files, owner
cache invalidation, outer-scope failure, rejected-attempt cache counts, and
installation/asset-owner retry behavior during commit.

The corrected focused build passed with zero warnings and errors. The complete
runtime probe passed 42 checks and exited with code zero. Its first run caught
a missing `Source.FilePath` assignment after recovery switched to the shader's
existing source object. The correction assigns the path before loading the
bytes; BOM decoding and disk refresh provenance then passed together.

The probe confirmed that source replacement can run during a blocked read and
that recovery discards the old bytes. During the short commit it confirmed
rejection and retry for the raw source, shader-file backend, asset filesystem,
shader service, and caller-thread admission. A rejected real shader-service
lease disposal left the installed owner alive; retry restored its predecessor.
The real asset cache rejected teardown while its epoch was reserved. Independent
recovery and shared fallback survived an outer abort. Failed recovery left its
parent usable and added no object or render cache entries.

Disposable evidence is in the same run root:
`logs/phase2/canonical-probe-build-corrected.log`,
`logs/phase2/canonical-probe-runtime-corrected.log`, and
`reports/phase2/canonical-source/runtime-results.md`.

## Remaining lifetime requirement

Repeated successful recovery retains each old shader and source `TextFile` in
the global caches. Each new generation now allocates exactly these two objects;
it no longer abandons the shader constructor's default `TextFile`. A local
material ownership list cannot safely destroy old generations because another
material can borrow the canonical shader directly or through a generated
variant. `ObjectCacheOwnership.Dispose` requires its caller to stop all users;
the shader path has no shared borrower lease. Keep borrowed live shaders intact
until a shared lifetime contract can prove retirement is safe. Material teardown
and revival do not close this gap. The probe measures retained successful
generations separately from rejected attempts, which must add no cache entries.
Four successful replacements retained eight object-cache entries and four
render-cache entries. The borrowed prior shader stayed alive. This measurement
confirms the open lifetime gap; it is not a retirement acceptance result.
