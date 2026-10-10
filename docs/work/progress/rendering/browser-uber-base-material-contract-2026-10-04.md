# Browser canonical Uber base material contract

Date: 2026-10-04. This records the bounded base-Uber implementation and its
qualification boundaries. It does not close UR05.03 or establish rendered parity.
The [shader inventory](unified-webgpu-shader-inventory.md) and
[modular pipeline scope](../../design/platform/modular-browser-render-pipelines-2026-10-02.md)
remain the scope authorities.

## Source and authored state

`UberBaseSourcePreparation` uses the actual `UberShaderVariantBuilder` and the
canonical `Uber/UberShader.vert` and `.frag` source closure. It preserves
`UberAuthoredState`, prepared static literals, animated parameter identities,
pipeline macros, source shaders and all original texture references. It does not
assign common lit-material semantics to arbitrary property names. Desktop GLSL
and ordinary authored records are unchanged.

The admitted feature identities are `normal-map`, `alpha-masks`,
`advanced-specular`, `emission` and `render-time`, with profile bits 1, 2, 4, 8 and
16. The source has 51 numeric members in a retained 352-byte parameter block.
Static values come from the prepared canonical literals; animated values retain
typed source parameters and update the same block. Changing the source pipeline
requirements requires the corresponding exact cooked variant.

| Input | Preserved canonical interpretation |
| --- | --- |
| `_MainTex`, `_Color` | RGBA multiplication, source UV selector, ST and pan; authored vertex-color conversion is retained |
| `_BumpMap` | Scaled/optionally flipped RG, reconstructed Z or source Sobel mode, source tangent basis and thresholds |
| `_AlphaMask` | Base-subset red channel, source mode/inversion/blend/value, alpha modification and force-opaque behavior |
| `_PBRMetallicMaps` | Actual clamped RGBA channel and metallic multiplier |
| `_PBRSmoothnessMaps` | Actual channel, inversion and `clamp(1 - smoothness * multiplier, .04, 1)` roughness |
| `_SpecularMap` | Source maximum-RGBA mask; source F0 reset/mix and realistic specular model |
| `_EmissionMap` | RGB, tint, strength and source time/scroll/vertex factors |

Source UV selectors include UV0–3, reflected selector 4, world XZ selector 5,
polar selector 6 and local XY selector 8. Normal evaluation precedes color/alpha
and later reflected/polar samples. The native path obtains these derivatives
from a real fragment producer. It does not reconstruct them with a compute
finite-difference approximation.

The companion passes are color, depth-normal, depth, point-shadow depth,
spot-shadow depth and the exact final-position authored-order gate. Canonical
cutout uses the source strict-less-than cutoff. Opaque/cutout return alpha one;
fade/transparent retain canonical alpha. Optional surface-extension
premultiplication is not inferred for the base subset.

## Cooking and projection

`BrowserUberBaseCookExporter.Write` stages six ordinary Slang recipes per exact
material/variant, the admitted Slang dependencies and the original desktop source
closure. The pinned offline `Tools/ShaderCooker` compiles these recipes and checks
the semantic ABI, reflected bindings, compiler identity and source hashes. A
missing artifact reports material, pass, canonical source, variant and expected
cook identity. Runtime GLSL-to-WGSL compilation is not provided.

The browser publisher uses the target-only `PublishedUberBaseMaterial` carrier.
It preserves the inherited source graph and installs exact cooked companions;
ordinary `XRMaterial.CookedUberBaseProfile` is runtime-only and ignored by authored
serialization. Custom subclasses are rejected when their additional state cannot
be represented. The carrier is registered in the closed published type metadata.
The global raw-texture serialization format has not been migrated; target
metadata restores omitted sampling/interpretation fields after its existing codec.

## Shared runtime and native ABI

Generic raster uses the shared engine material, pass resolver, lighting publisher
and `IPbrLightingResourceProvider`. Lighting and AO retain canonical source math.
AO uses a viewport-local integer texel load, including the backend's top-left
fragment coordinates and nonzero viewport origin. Disabled AO remains neutral.
The lighting block is 1,088 bytes, including the explicit viewport member.

Forward probe admission is per receiver. An unrelated lit receiver in a mixed
world cannot inherit Uber's probe support. The fixed irradiance and BRDF samplers
require the source's nonmip linear contracts; other samplers retain their declared
roles. Runtime recapture and retained baked images are distinct operations.

Native materials retain existing schema 5 and its 480-byte records. Additive
directory table 34 contains the 624-byte Uber companion; table 35 contains the
48-byte raw UV/color sidecar. Table 33 remains the authored basis sidecar.
Source/LOD identity, aliases, generations and current/previous ownership are
validated. The directory now has 36 rows and a 1,152-byte header. Absolute payload
offsets move by 64 bytes; pre-existing row lookup and payload interpretation remain
unchanged. Old program directory/companion contracts reject before indexing.

The source-exact fragment producer writes four RGBA32F layers: surface color and
roughness, mapped normal and metallic, emission and specular, and composed source
direct/ambient lighting. It executes after AO. Its winner gate includes the
draw/primitive, instance, sample, view and current production identity; implicit
samples complete before a divergent identity gate. Its positions, depth and
coverage match the visibility producer.

Each output family owns one scratch image, costing 64 bytes per pixel at either
1x or 4x. At 1920×1080 this adds 132,710,400 bytes per output; three output families
add 398,131,200 bytes. This is an additional scratch cost, not the total renderer
memory budget. The existing 256 MiB per-image limit and actual texture/binding
limits remain admission requirements. At 4x, the engine records four separate
export traversals, each immediately followed by that sample's consumers, then the
existing full-float resolve. There is no measured performance claim.

One ordered WebGPU queue, complete pass boundaries and atomic frame encoding
permit reuse across samples and frames. No history/readback/later-stage consumer
retains scratch contents. Recorded physical leases survive replacement until all
referencing submissions retire. A native shading stage is complete only after
every requested sample producer and consumer has been recorded.

## Retained probe images

The target-only retained probe carrier requires authored
`AutoCaptureOnActivate=false` and `RealtimeCapture=false`, valid retained CPU mip
data and an existing source generation. It borrows source images and records
retained-data provenance; it does not fabricate a GPU completion fence. Runtime
capture/convolution requests reject before producer allocation.

Linear RGBA images retain their existing representation. For the admitted
RGB16F/Rgb/HalfFloat input, the target converter preserves all three half-float bit
patterns and appends half-float one, with separate target identity and source
provenance. Float upload, missing/malformed mips, encoded RGBM/RGBE/YCoCg, rectangle
coordinates, unsupported bias/ranges and unavailable production reject precisely.
No global runtime format mapper silently widens a texture.

The immutable witness persists through inactive periods. It observes declared
texture/mip mutation and publication events and checks identities, data addresses,
lengths and generations at retention boundaries, without per-frame pixel reads or
hashing. Unsafe in-place writers must issue the texture's invalidation/publication
notification. Borrowed images outlive the carrier when publication pins remain.
Array sampler state is independent, and incompatible per-layer sampling rejects
instead of mutating source textures.

## Evidence and remaining acceptance

The preserved combined source `4018f411c16e45ae5c8a92adaa01fdb20d43e90b`
compiled through Editor and the native-WASM Browser boundary. The final bounded
successor `d4c588839b96475abbe8c88a2ffabec7a6e4427a` adds the caller-axis and
framework-value corrections described below. Its Data/Runtime.Rendering boundary
also compiled with zero warnings/errors, and the genuine fixture used these exact
new assemblies with unchanged Editor consumers. The Editor output was
compile-only with package dependencies resolved from its exact dependency
manifest; it is not claimed as a complete runnable desktop distribution.

Scratch evidence under the active `20261001-225000-lit-surface` validation run:

- Native ABI witness: 170 checks, including all 34 existing packed table payloads
  after header relocation, old-program rejection, seven-role resource reference
  ownership, retained parameter storage and static/live values
- Retained-probe runtime witness: 193 checks through the real raw texture codec,
  target hydration, mutation rejection and borrowed publication retirement
- Retained-probe Editor witness: 85 checks through actual target projection,
  bit-exact conversion, world/camera/material aliases, private/influence state,
  unchanged ordinary encodings and exceptional cleanup
- Canonical material author fixture: real source export and unchanged YAML and
  cooked source bytes, including 15 final checks covering the ordinary MemoryPack
  asset envelope with the runtime-only profile absent/present; direct
  `MemoryPackSerializer<XRMaterial>` remains unregistered exactly as before
- Final strict cook: 34 artifacts, including 12 native Uber recipes, 10 existing
  native/visibility recipes and 12 genuinely authored material pass recipes
- Actual target projection/hydration: 56 checks against the YAML-loaded source
  graph and 58 against a graph with shared cross-material image references,
  including borrowed-owner preservation after projection disposal
- Real browser publisher and package builder: unchanged authored world bytes,
  both target carriers registered, and 121 shader identities with all 245 cooked
  payloads loaded and hash-checked by the production JavaScript loader in Node
- Actual feature-zero author export and six strict companion cooks, retaining
  one active role and all seven original source-image entries
- Fresh Published-mode hydration from the original metadata: 40 checks covering
  exact source/pass identity, all seven settings, raw and HDR/non-byte-aligned
  Vector4 values, live parameters and the closed framework assembly guard

The genuine material fixture exposed a live PBR requirement change being accepted
against an older prepared profile. The corrected final predicate passes nine
checks for PBR/AO/shadow rejection and restoration, with zero managed allocations
across 1,000 retained reads under default tiered execution. Its two hot enum tests
use bitwise operations so the interpreter does not depend on JIT elimination of
enum boxing.

Fresh Published-mode hydration also exposed `ShaderVector4.Color`'s existing
`System.Drawing.Color` value outside the framework-data allowlist. The precise
addition is implemented and passes original-metadata fresh hydration; its
shipped-assembly identity guard and all authored bytes remain unchanged. The
Vector4 convenience Color property does not alter the restored raw/HDR values.

The ordinary authored YAML raw-texture carrier creates separate same-ID images
in different material occurrences. Projection preserves that actual input
topology. The independent shared-reference witness exercises a source graph
which really contains those aliases; universal alias preservation across
unrelated custom codec scopes is not claimed.

No live browser pixel/parity acceptance or GPU execution is claimed by these
offline checks. Independent review of the exact integrated source found no
concrete ABI, resource-lifetime or sample-indexing blocker in the companion
tables, raster/compute bindings, scratch retirement or ordered MSAA traversals.
That review does not replace GPU execution. The existing software-CI native shader compile promise exceeded
45 seconds at roughly 289 KiB WGSL. The fresh cooked dedicated Uber consumers are
353,286–359,574 bytes; existing native consumers are 385,605–390,961 bytes; Uber
raster exports are 107,878–108,263 bytes; the generic material companions are
21,457–95,457 bytes. These are separate shader identities. Successful offline
cooking does not resolve the known compile-latency limitation. Publication remains
paused.

Optional inventory-D Uber features remain named exclusions. Toon/anisotropic
specular, contact shadows, additional authored passes, Uber/decal receiver
composition and decomposed-light debug views are not qualified by this profile.
Previously admitted materials keep their existing decal/debug behavior.

Canonical Uber uses the forward pass. Ordinary authored deferred decals modify
GBuffer albedo before forward receivers and therefore do not affect that Uber
receiver. Forcing canonical Uber into the deferred pass does not give its final
RGBA output a GBuffer albedo/normal/RMS contract. The named rejection preserves
this distinction; blending an already-lit export as albedo would invent material
behavior. The separate forward weighted-OIT decal is an inventory-D pass with its
own depth and accumulation contract. None of these profile limits establishes
rendered acceptance or closes the aggregate material coverage item.
