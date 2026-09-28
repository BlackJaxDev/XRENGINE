# Portable scene host

This standalone browser application runs the engine's shared scene, component,
and transform lifecycle in WebAssembly. It does not create a GPU device, canvas
renderer, desktop window, physics/audio service, or XR session.

## Build and run

Install the .NET 10 SDK and its `wasm-tools` workload, then run from the repository
root:

```sh
dotnet workload install wasm-tools
dotnet publish XREngine.Browser/XREngine.Browser.csproj -c Release -p:XREnginePortableRuntime=true -m:1
```

Use the portable property on restore and build commands as well. It is a global
property so every project in the graph selects the same source profile. The
browser project intentionally stays outside the default desktop solution, so a
desktop build does not require the WebAssembly workload.

The publish is untrimmed and interpreted (`PublishTrimmed=false`,
`RunAOTCompilation=false`). Trimming, AOT, cooked asset loading, and generated
component registration need separate qualification before enabling them.

Serve `XREngine.Browser/bin/portable/Release/net10.0/publish/wwwroot` with a static HTTP server supporting
`.wasm` as `application/wasm`. Open its localhost URL in a browser; do not open
`index.html` through `file://`. The page runs 120 fixed simulation steps, validates
parent/child transform propagation and component begin/end callbacks, then
checks that stopped scenes no longer tick and destroyed objects leave the
engine object cache. **Run again** repeats the full construction/teardown cycle.
Failures are shown on the page and logged to the browser console.

The frame loop uses `requestAnimationFrame`, bounds catch-up to four steps,
pauses scheduling while hidden, and discards elapsed hidden time on resume.
The runtime remains loaded between runs; each scene owns its nodes/components.

## Shared engine boundary

`Build/Portable/*.items` explicitly select existing source files in Data,
Extensions, Runtime.Core, and Runtime.Rendering. Namespaces and assembly names
remain unchanged; portable outputs use `bin/portable` and `obj/portable` so they
cannot overwrite desktop outputs. The selected rendering files currently expose
backend and presentation capabilities only, not a rendering implementation.

`RuntimeSceneHost` advances `RuntimeWorldLifecycle` and real `SceneNode` objects
on the caller's thread. Its transform traversal uses pooled child snapshots and
synchronous matrix publication without task waits or worker dispatch. It does
not initialize the desktop bootstrap. Desktop-only asset archive reading and
rigid-body transform registration remain in desktop partial files.

The MSBuild guard rejects unsupported projects, Windows target frameworks,
unreviewed direct packages, and native runtime assets. The official .NET browser
runtime is an explicit exception. This is a build-boundary guard, not proof that
every API in an included source file works in a browser. Expanding the source
profile requires runtime validation of the new paths.

See [implementation and validation notes](../docs/work/progress/rendering/portable-browser-scene-boot.md)
for the verified environment and remaining work.
