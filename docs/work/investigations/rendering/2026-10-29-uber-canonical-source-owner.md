# Uber canonical shader source ownership

Status: open. A generated Uber shader can be rehydrated from a shader-file
probe and a separate runtime asset-read installation. These owners can change
between the probe and the text read. The current path can also accept an empty
text object after a file disappears.

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

## Boundary and next work

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
