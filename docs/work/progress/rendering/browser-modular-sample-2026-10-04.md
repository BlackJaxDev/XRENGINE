# Saved modular browser sample

Status: source authored, pending normal shader cook, Editor publication and live
WebGPU inspection. This is preparatory work for UR06.11a and does not close it.

The pinned Slang 2026.8 frontend compiled the sample's two raster entries with
the ShaderCooker argument order and flags, with no diagnostics. Reflection has
the position input and no resource parameters. The resulting WGSL hash is
`88bed9cd371573a47fd8ed5bd52897b3931e8bae38aa16e71e49981ace593aa7`;
the reflection JSON hash is
`0349a35e2a464cf4652f333e5e6e7fbd7bf53ab640a6ae764e3e4218322d6ed1`.
Disposable outputs and a 17-file authored-source checksum list are under
`Build/_AgentValidation/20261001-225000-lit-surface/scratch/modular-pipeline/frontend/`.
This checks Slang frontend syntax and stage reflection only. The normal C#
ShaderCooker recipe, immutable catalog and Editor publication have not run.

`Samples/ModularPipelineParity` contains a project-owned portable game
assembly, saved startup world, local game bootstrap, explicit camera selection,
and an isolated Slang whole-program recipe. The first two saved camera nodes
reference one `ModularClearRenderPipeline` YAML object. The third references
`ModularQuadRenderPipeline`, which declares only `custom::custom-pass` and
materializes one output quad from that exact cooked program. The game-defined
pipelines generate their normal scoped command chains from saved color and
program identity. No browser pipeline type registry, Default replacement,
desktop GLSL edit, new dependency, packaging behavior or output host was added.

The Editor publisher separately loads and audits the saved world and its cooked
graph. On browser launch, the game bootstrap inspects a loaded asset graph;
the runtime game mode inspects the actual hydrated world and selects the two
shared-source cameras, then the independent quad camera, through the ordinary
local player using keys 1, 2 and 3. These assertions are useful failures if serialization changes,
but they are not evidence that the currently authored YAML has passed the
actual Editor cook. The shipping browser host exposes one player viewport, so
sequential switching does not prove simultaneous physical output isolation.

The first qualification should run the sample's `Prepare-BrowserShaders.ps1`,
then the documented Editor `--build-project` BrowserWebGPU command. Confirm the
activated `Build/BrowserWebGPU/index.html` and immutable content catalog, then
load that exact player in WebGPU Chromium. Record startup and switch logs,
pixel samples of the clear color and quad gradient, shader/module failures,
source IDs, scoped artifact identity, and any operation-level rejection. A
software adapter is labeled API/shader evidence; physical-device acceptance
needs a device run. This sample requests AA None. The x4 framebuffer, blend,
indirect and resize cohort of UR06.11f3 remains open.

The existing portable-browser CI workflow now stages only this sample's saved
world, startup settings and portable game scripts; its pinned Windows Slang
compiler cooks the one custom recipe. The ordinary Editor CLI activates the
shipping player, and the existing Linux Chromium matrix runs the named
`modular-pipeline-parity` BrowserSmoke case against that exact bundle. The case
uses the focused canvas's 1/2/3 input bindings, verifies each Release marker's
source ID and AA mode, waits for a later acquisition of the player canvas and
submission on its configured device queue, and checks clear/gradient pixels
across two fresh starts and resize. The two shared-source clear cameras have
identical output; this is evidence of sequential camera selection and frame
publication, while simultaneous multi-viewport isolation remains open. The
new CI route has not yet run.
