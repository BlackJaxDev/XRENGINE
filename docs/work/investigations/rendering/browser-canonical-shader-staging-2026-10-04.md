# Canonical browser shader source staging

## Failure and cause

Windows CI run `37196549762`, job `111419543941`, built and published
RollingBall, then failed while preparing the canonical RenderingParity browser
inputs on source commit `2f61fd7ea927cc09d6f8f11a76cc037f98109ace`. The first
failing recipe was `engine-octahedral-impostor-authored-order.recipe.json`.
The diagnostic named the missing `Scene3D` directory under the temporary
engine shader staging root.

`Tools/Cook-EngineBrowserShaders.ps1` copied only `WebGPU` and the backend WGSL
kernels. The impostor contract deliberately verifies the original authored
`Scene3D/OctahedralImposterBillboard.vs`, its fragment, and
`Common/OctahedralImposter.glsl` relative to the parent of `WebGPU`. Ordinary
unlit programs similarly require canonical `Common` fragments and `Snippets`.
Those sibling directories were absent. The path stopped at `Scene3D` because
the cooker's link-safety check queries each path component and failed on the
first missing one. Argument parsing and spaces were not responsible.

The helper now copies `Common`, `Scene3D`, and `Snippets` beside `WebGPU` without
changing their bytes or their logical dependency paths. All source hash,
provenance, link-safety, recipe inventory, and publisher failure checks remain
active. No desktop GLSL, recipes, or emitted shader semantics were changed.

## Checkout line endings

The authored desktop shader files use Git's automatic text handling; WebGPU
sources already require LF. A disposable CRLF copy of
`Common/UnlitColoredForward.fs` exposed a second defect: the ordinary and
companion unlit cooker verifiers compared raw desktop bytes to LF canonical
pins. The Editor's `VerifyUnlitDesktopStage` and the impostor cooker already
use the source's canonical LF text identity.

Both unlit desktop verification routes now strictly decode UTF-8 and use that
same `EngineTexturedAlphaShaderGenerator.NormalizedHash` contract. Slang
hashing and all canonical pins remain unchanged. The Editor still requires the
loaded source text to equal its canonical file and checks resolved snippets;
runtime unlit admission checks the pinned descriptor closure through
`EngineUnlitShaderProvenance`. Canonical dependency paths and hashes retain their
existing identity contract; per-material provenance continues through its
existing Editor/cooker path.

## Manifest capacity

After source staging was repaired, the genuine full cook prepared all 172
canonical recipes but rejected its final manifest at 64 KiB. The reconstructed
manifest is 71,904 bytes including its terminal LF, with 172 artifacts, 93
material variants, 72 pipeline bindings, and four compute bindings. Every
reconstructed recipe hash was checked against its emitted descriptor. This
diagnostic reconstruction was not published as a successful cook.

The schema-three publisher already admits a 1 MiB manifest and 256 artifacts.
`ShaderProgramArtifactCatalog.MaximumManifestBytes` now shares that existing
bound between its reader and the engine cooker writer. Recipes, descriptors,
and legacy schema-one/two manifests retain their 64 KiB limits. Network and
runtime content budgets, array counts, wire schema, and canonical byte
serialization are unchanged. The manifest remains the final atomic commit
point; a failed cook does not publish a new manifest.

## Validation

- The final ShaderCooker build passed with zero warnings and errors using
  .NET SDK 10.0.401; compilation uses the pinned Slang 2026.8 toolchain.
- Direct invocation of the actual provenance verifier reproduced missing
  `Scene3D` before staging and accepted the exact impostor closure afterward.
  All five unlit closures accepted unchanged LF source copies. A disposable
  CRLF unlit fragment reproduced the pre-fix hash rejection.
- The initial full staged cook prepared and wrote all 172 verified descriptor
  files, then failed its old manifest capacity check without a manifest.
- Real four-recipe LF and CRLF cooks passed for ordinary color and alpha-texture
  receivers and their authored-order/depth companions. Their complete output
  directories, including manifests, descriptors, and WGSL, were byte-identical.
  A non-newline edit to a disposable authored color source was rejected on both
  the ordinary and companion route, without publishing a manifest.
- The final genuine production helper cook exited successfully and reported
  `Packaged 172 shader artifact(s).` at 11:22 UTC on 2026-10-04. The subsequent
  final artifact/hash comparison and manifest-size confirmation were interrupted
  when the execution workspace reset. They were not performed. Actual Windows
  CI and browser GPU pixel acceptance remain separate validation.

Before the execution workspace reset, disposable logs and source copies were
under `Build/_AgentValidation/20261001-225000-lit-surface/`. Relevant
logs are `logs/canonical-staging-provenance-1111.log`,
`logs/canonical-cook-staging-1111.log`, and
`logs/windows-cook-final-build-1118.log`. Final validation is recorded in
`logs/unlit-line-endings-final-1119.log` and
`logs/canonical-cook-final-1119.log`.

The final successful output directory was `reports/canonical-cook-final-1119/`.
Those disposable outputs are unavailable in the recovered workspace; validation
results above distinguish retained command evidence from checks that never ran.
The repair was reconstructed against the same published base after the reset,
and its four production source hashes were checked against the pre-reset values.
