# Cyclopean “Middle View” Reconstruction (Stereo → Single 2D) — OpenGL / Vulkan

Implementation tracker: [VR Mirror Cyclopean Reconstruction TODO](../../todo/rendering/vr/vr-mirror-cyclopean-reconstruction-todo.md).

We synthesize a convincing **cyclopean (middle) view** from **left/right eye color + depth** without rendering a third camera. This is meant for a **2D spectator / preview window**.

With per-eye depth available, we can do a much stronger approach than “shift + blend”: we **reconstruct world-space points from each eye depth**, then **reproject into the middle camera**, and resolve conflicts (occlusion / ghosting) with depth-aware rules.

## Key Design Decisions (2026-07-01 revision)

- **Depth input is linear view-space Z in `R32_SFLOAT`**, resolved per eye by a small pass at the end of each eye render (§3.1). This removes all device-depth convention plumbing (OpenGL vs Vulkan NDC z, reversed-Z, clip control) from the reconstruction shader and gives MSAA eye targets a well-defined resolve point. Sampling hardware depth directly is a documented fallback only (§3.2).
- **Eye tracking is optional.** v1 uses a pure geometric midpoint camera and a per-pixel iterative depth search; no fixation point is required. Gaze/fixation is a later optimization/bias (§1).
- **Correspondence is solved per pixel with bounded iterative refinement** (2–3 fixed-point steps through the middle camera, optionally seeded by a short epipolar march), not a single projection at fixation depth. A single fixation-plane gather smears everything not at fixation depth (§4).
- **Candidates are validated by reprojection error into the middle camera.** A candidate whose reconstructed point does not land near the output pixel is rejected or down-weighted.
- **Resolve is a soft blend**: depth-consistency weight × lateral eye-dominance weight × reprojection confidence. No hard per-pixel L/R switch (hard switches flicker frame-to-frame).
- **Blending happens in linear color space** (sample eye color through sRGB views); depth is sampled with **nearest** filtering, color with linear.
- **No raw 0.5 L+R average fallback.** Pixels with no valid candidate use temporal history or nearest-valid dilation; a 50/50 average produces double images exactly at disocclusions where artifacts are most visible.

---

## 0) Assumptions & What You Provide

### Required

- Head pose:
  - `headPos_ws : vec3`
  - `headRot_ws : quat` (or matrix)
- Stereo render targets (same frame id for both eyes):
  - `eyeColor : sampler2DArray` (layer 0 = L, 1 = R; bound via sRGB view so samples are linear)
  - `eyeLinearDepth : sampler2DArray` (layer 0 = L, 1 = R; `R32_SFLOAT` view-space Z, nearest filtering)
- Stereo parameters:
  - `IPD : float`
- Projection data used to render the eyes:
  - `V_L, P_L, invV_L, invP_L`
  - `V_R, P_R, invV_R, invP_R`
- Preview (middle) camera projection:
  - `P_M, invP_M` (choose your spectator window FOV/aspect; letterbox/crop intentionally)
- Per-eye metadata (validated before composition):
  - frame id, dimensions, color space, clip-space Y direction, near/far

### Optional

- Eye tracking (fixation bias, §1):
  - `gazePos_ws[2] : vec3` (Left, Right)
  - `gazeDir_ws[2] : vec3` (unit)
- Previous-frame middle color/depth for temporal accumulation (§8) and iteration seeding (§4).

### Output

- `middleColor` (RGBA)
- Optional: `middleDepth` (middle view-space Z) for later DOF and for temporal reprojection.

---

## 1) (Optional) Fixation Point From Eye Tracking (vergence intersection)

> Optional. The iterative per-pixel depth search (§4) does not require a fixation point, so the base path works without eye tracking. When gaze data exists, fixation can bias the search seed depth and tighten the depth tolerance near fixation.

We compute a stable fixation point `F_ws` from the two gaze rays:

Left ray:  `L(t) = p + t*d1`  
Right ray: `R(u) = q + u*d2`

Where:

- `p = gazePosL`, `d1 = gazeDirL` (unit)
- `q = gazePosR`, `d2 = gazeDirR` (unit)

Compute closest points between rays (skew-line closest approach):

Let:

- `r = p - q`
- `b = dot(d1,d2)`
- `c = dot(d1,r)`
- `f = dot(d2,r)`
- `den = 1 - b*b`

If `den` is too small (nearly parallel), **fallback**:

- keep last fixation, or
- sample depth at gaze pixel in one eye and reconstruct point (often best)

Otherwise:

- `t = (b*f - c) / den`
- `u = (f - b*c) / den`
- `P = p + t*d1`
- `Q = q + u*d2`
- `F_ws = (P + Q) * 0.5`

**Smooth it** (recommended):

- `F_ws = lerp(F_ws_prev, F_ws_new, alpha)` (alpha ~ 0.05–0.3)

---

## 2) Define the Cyclopean (Middle) Camera

Cyclopean origin (eye poses, not gaze):

- `E_M_ws = 0.5*(eyePosL + eyePosR)`

Orientation:

- v1: use head orientation for stable viewing:
  - `R_M_ws = headRot_ws`
- Alternative for canted displays: `R_M_ws = slerp(eyeRotL, eyeRotR, 0.5)`
- Reuse the engine's existing smoothed `CyclopeanDesktop` camera pose so reconstruction matches the pose already used for combined visibility and mirror cadence.

View:

- `V_M = inverse( T(E_M_ws) * R(R_M_ws) )`
- `invV_M = inverse(V_M)`

Fixation depth in middle view space (optional, only when gaze data is available):

- `zFix = (V_M * vec4(F_ws,1)).z`

---

## 3) Depth Input: Linear View-Space Z (primary) vs Hardware Depth (fallback)

### 3.1 Primary: per-eye linear `R32_SFLOAT` view-space Z

A small per-eye pass at the end of the eye render resolves hardware depth into **positive forward distance** (`linZ = -viewPos.z` for a -Z-forward view space) stored as `R32_SFLOAT`.

Why this is the contract:

- The reconstruction shader needs no NDC-z mapping, no reversed-Z branches, and no per-API convention flags — reconstruction is just `viewPos = viewRayZNorm(uv) * linZ`.
- It is the natural **MSAA resolve point**: MSAA hardware depth cannot be meaningfully sampled or blitted for reconstruction (resolve with min / nearest-to-camera, or sample 0).
- On OpenGL it avoids all depth-texture compare-mode pitfalls: the resolved texture is a color-class `R32F` texture.

Validity test: `linZ` outside `[nearClamp, farClamp]` (or exactly the cleared value) means background/far → invalid.

### 3.2 Fallback: sampling hardware depth directly

Only for cases where the resolve pass is unavailable. Requires explicit per-eye metadata; never guess per backend.

Validity test (far plane):

- Normal Z: far is near **1.0** → invalid if `zDev >= 1 - eps`
- Reversed Z: far is near **0.0** → invalid if `zDev <= eps`
- `eps = 1e-6` (bigger if depth is noisy)

Device depth → NDC z for reconstructing with `invP`:

- `[0..1]` NDC z (Vulkan, D3D, or **OpenGL with `glClipControl(GL_ZERO_TO_ONE)`**): `ndcZ = zDev`
- `[-1..1]` NDC z (default OpenGL clip volume): `ndcZ = zDev * 2 - 1`

> **Caveat:** the classic “OpenGL = `zDev * 2 - 1`” rule is wrong when the engine renders reversed-Z with `glClipControl(GL_ZERO_TO_ONE)` (the standard reversed-Z GL setup). Key the mapping off the **actual clip-control state used to render the eye**, published as per-eye metadata — not off the graphics API. Reversed-Z itself does not change the mapping as long as `P` matches the render path that produced the depth.

---

## 4) Core Method (Per-pixel Gather With Iterative Refinement)

We run a fullscreen pass over the **middle preview viewport**.

For each output pixel:

1) Build a middle-view ray through that pixel.
2) Initialize a depth guess along the ray (previous-frame middle depth when temporal accumulation is enabled; otherwise a mid-range constant, or fixation depth if gaze data exists).
3) For each eye, run 2–3 fixed-point refinement steps:
   - Project the current guess point into the eye → `uvE`.
   - Sample eye linear depth (nearest) at `uvE`; reconstruct the world-space point `X_ws` on that eye's ray.
   - Update the guess to the middle-view forward distance of `X_ws` and repeat.
4) Validate each eye candidate by projecting its final `X_ws` into the **middle** camera and measuring reprojection error in output pixels; reject candidates above `maxReprojErrPx`.
5) Resolve L/R with soft weights: depth agreement (relative to the nearer candidate's depth) × lateral eye dominance × reprojection confidence.
6) If neither candidate is valid, fall back to temporal history / dilation — never a 50/50 eye average.

> **Why iterate:** a single gather at a global guess depth (e.g. fixation depth) samples the wrong surface for every pixel whose true depth differs from the guess. And the refinement must close the loop through the **middle** camera — reprojecting the reconstructed point back into the *same* eye returns the same UV and refines nothing (this was a bug in the original one-step sketch).

Because the middle camera sits exactly between the eyes with (nearly) identical orientation, correspondence is a bounded 1D disparity problem (≤ ±IPD/2 of lateral reprojection). If fixed-point iteration fails to converge at depth discontinuities, seed it with a short epipolar march (8–16 taps between the depth clamps, one secant refinement), similar to parallax occlusion mapping.

This is fast, stable, avoids atomics, and requires no gaze data.

---

## 5) GLSL Shader (OpenGL or Vulkan)

### 5.1 UBO / Push constants

```glsl
layout(std140, binding = 0) uniform CyclopeanUBO {
    mat4 V_L, P_L, invV_L, invP_L;
    mat4 V_R, P_R, invV_R, invP_R;

    mat4 V_M, P_M, invV_M, invP_M;

    vec4 viewport;         // x=width, y=height, z=1/width, w=1/height
    vec4 depthClamp;       // x=near clamp, y=far clamp, z=seed depth, w=unused

    float ipd;             // debug / epipolar bounds
    float zTolerance;      // relative depth agreement tolerance (e.g. 0.02 = 2%)
    float maxReprojErrPx;  // reject candidates landing further than this from the output pixel
    float dominanceEdge;   // lateral dominance transition width in uv.x (e.g. 0.2)
} u;

// eyeColor: bound through an sRGB view so samples arrive linear; linear filtering.
layout(binding = 1) uniform sampler2DArray eyeColor;
// eyeLinearDepth: R32_SFLOAT positive view-space forward distance; NEAREST filtering.
layout(binding = 2) uniform sampler2DArray eyeLinearDepth;

layout(location = 0) out vec4 outColor;
// Optional second target: middle view-space depth for temporal reprojection / DOF.
layout(location = 1) out float outMiddleDepth;
```

### 5.2 Helpers

```glsl
vec2 ndcFromUV(vec2 uv) { return uv * 2.0 - 1.0; }

bool inBounds(vec2 uv) { return all(greaterThanEqual(uv, vec2(0))) && all(lessThanEqual(uv, vec2(1))); }

bool depthValid(float linZ) {
    return linZ > u.depthClamp.x && linZ < u.depthClamp.y;
}

// Reconstruct world-space position from linear view-space forward distance.
// viewRay is the eye-space ray direction normalized so its forward component is 1.
vec3 reconstructWS_fromLinearDepth(vec2 uv, float linZ, mat4 invP, mat4 invV) {
    vec4 clip = vec4(ndcFromUV(uv), 1.0, 1.0);
    vec3 viewDir = (invP * clip).xyz;
    viewDir /= abs(viewDir.z);              // forward component -> 1 (-Z-forward view space)
    vec3 viewPos = viewDir * linZ;
    return (invV * vec4(viewPos, 1.0)).xyz;
}

vec2 projectToUV(vec3 ws, mat4 V, mat4 P) {
    vec4 clip = P * (V * vec4(ws, 1.0));
    vec3 ndc = clip.xyz / clip.w;

    // Clip-space Y direction must come from per-eye published metadata, not be
    // guessed per API. If the eye was rendered with a Y-flipped projection,
    // either flip here from metadata or bake the flip into P_* consistently.

    return ndc.xy * 0.5 + 0.5;
}

// Positive forward distance of a world point in the middle view.
float middleLinZ(vec3 ws) {
    return -(u.V_M * vec4(ws, 1.0)).z;
}
```

### 5.3 Main pass

The refinement loop must close through the **middle** camera: reprojecting a
reconstructed point back into the *same* eye returns the same UV and refines
nothing. Each iteration updates the depth guess along the middle ray from the
eye's observed surface, then re-gathers.

```glsl
struct EyeCandidate {
    vec3 color;
    float zM;        // middle view-space forward distance
    float reprojErr; // pixels, in middle output space
    bool ok;
};

EyeCandidate gatherEye(int layer, mat4 V_E, mat4 P_E, mat4 invV_E, mat4 invP_E,
                       vec3 rayDir_M_ws, vec3 eyeOrigin_M_ws, vec2 outUV, float seedZ) {
    EyeCandidate c;
    c.ok = false;

    float zGuess = seedZ;
    vec3 X_ws = vec3(0.0);

    // 2-3 fixed-point refinement steps through the middle camera.
    for (int i = 0; i < 3; ++i) {
        vec3 guess_ws = eyeOrigin_M_ws + rayDir_M_ws * zGuess;
        vec2 uvE = projectToUV(guess_ws, V_E, P_E);
        if (!inBounds(uvE))
            return c;

        float linZ = texture(eyeLinearDepth, vec3(uvE, layer)).r; // NEAREST
        if (!depthValid(linZ))
            return c;

        X_ws = reconstructWS_fromLinearDepth(uvE, linZ, invP_E, invV_E);
        float zNew = middleLinZ(X_ws);
        if (abs(zNew - zGuess) < 1e-4 * zNew)
        {
            zGuess = zNew;
            break;
        }
        zGuess = zNew;
    }

    // Validate by reprojection error into the middle camera.
    vec2 uvM = projectToUV(X_ws, u.V_M, u.P_M);
    float errPx = length((uvM - outUV) * u.viewport.xy);
    if (errPx > u.maxReprojErrPx)
        return c;

    vec2 uvE = projectToUV(X_ws, V_E, P_E);
    if (!inBounds(uvE))
        return c;

    c.color = texture(eyeColor, vec3(uvE, layer)).rgb; // linear (sRGB view)
    c.zM = zGuess;
    c.reprojErr = errPx;
    c.ok = true;
    return c;
}

void main() {
    vec2 uv = gl_FragCoord.xy * u.viewport.zw; // [0..1]

    // Middle-view ray, normalized so forward component is 1.
    vec4 clip = vec4(ndcFromUV(uv), 1.0, 1.0);
    vec3 rayDir_vs = (u.invP_M * clip).xyz;
    rayDir_vs /= abs(rayDir_vs.z);
    vec3 rayDir_ws = mat3(u.invV_M) * rayDir_vs;
    vec3 origin_ws = u.invV_M[3].xyz;

    // Seed: previous-frame middle depth when temporal accumulation is enabled;
    // otherwise depthClamp.z (mid-range constant or fixation depth if gaze exists).
    float seedZ = u.depthClamp.z;

    EyeCandidate L = gatherEye(0, u.V_L, u.P_L, u.invV_L, u.invP_L, rayDir_ws, origin_ws, uv, seedZ);
    EyeCandidate R = gatherEye(1, u.V_R, u.P_R, u.invV_R, u.invP_R, rayDir_ws, origin_ws, uv, seedZ);

    // No candidate: temporal history / dilation fallback.
    // NEVER average raw L+R at the output UV; a 50/50 average double-images
    // exactly at disocclusions where artifacts are most visible.
    if (!L.ok && !R.ok) {
        outColor = vec4(0.0);        // marks invalid; resolved by history/dilate pass
        outMiddleDepth = 0.0;
        return;
    }
    if (L.ok != R.ok) {
        EyeCandidate c = L.ok ? L : R;
        outColor = vec4(c.color, 1.0);
        outMiddleDepth = c.zM;
        return;
    }

    // Both valid: soft resolve.
    // 1) Depth agreement scaled by the NEARER candidate's own depth (not fixation).
    float zNear = min(L.zM, R.zM);
    float dz = abs(L.zM - R.zM);
    float agree = clamp(1.0 - dz / (u.zTolerance * zNear), 0.0, 1.0);

    // 2) Lateral eye dominance: left half of the output prefers the left eye.
    //    Hides per-eye specular/reflection disagreement and makes the seam invisible.
    float wR = smoothstep(0.5 - u.dominanceEdge, 0.5 + u.dominanceEdge, uv.x);
    vec3 blended = mix(L.color, R.color, wR);

    // 3) When depths disagree, favor the nearer (occluding) candidate.
    vec3 nearColor = (L.zM < R.zM) ? L.color : R.color;
    vec3 outRGB = mix(nearColor, blended, agree);

    outColor = vec4(outRGB, 1.0);
    outMiddleDepth = zNear;
}
```

---

## 6) Recommended Settings / Tuning

### zTolerance (relative depth agreement)

Scale the agreement test by the **nearer candidate's own depth per pixel**, not
by a global fixation depth, so tolerance is correct across the whole range:

- start with `zTolerance = 0.02` (2%)
- optionally clamp the absolute tolerance: `clamp(zTolerance * zNear, 0.02, 0.25)` (world units; adjust for scene scale)

### maxReprojErrPx

- start with 1.5 output pixels; loosen to ~3 px at half-resolution composition.

### dominanceEdge

- start with 0.2 (transition band across the middle 40% of the output).

### Soft resolve, not hard switch

A hard per-pixel L/R switch flickers frame-to-frame as depths jitter across the
threshold. The resolve above is fully continuous; keep it that way.

### Iteration count

- 2 iterations handle smooth surfaces; 3 covers most discontinuities.
- If convergence fails at depth edges, seed with a short epipolar march
  (8-16 taps between the depth clamps + one secant refinement) before the
  fixed-point steps.

### Fixation smoothing (only when gaze is used)

- Smooth `F_ws` in world space, then compute `zFix` each frame.
- Clamp fixation depth to a safe range (avoid wild values during saccades).

### Sampling rules

- `eyeLinearDepth`: **nearest** filtering. Bilinear-filtered depth invents
  phantom surfaces at silhouettes.
- `eyeColor`: linear filtering, sampled through an sRGB view so blending
  happens in linear space (otherwise edges fringe).

---

## 7) Vulkan / OpenGL Integration Notes

### Depth resolve pass (both backends)

- Resolve hardware depth to linear `R32_SFLOAT` view-space Z per eye at the end
  of the eye render (see section 3.1). This pass is also the MSAA resolve point:
  resolve depth with min (nearest-to-camera) or sample 0; never bilinear-resolve
  MSAA depth.

### Vulkan

- Eye color and resolved depth need `VK_IMAGE_USAGE_SAMPLED_BIT` and explicit
  transitions to a sampled-read layout before the mirror pass samples them.
- Declare the sampled inputs and mirror output through the render graph /
  resource planner so layouts and hazards are tracked, not assumed.

### OpenGL

- The resolved `R32F` depth is a color-class texture: no compare-mode concerns.
- If the hardware-depth fallback (section 3.2) is ever used: disable compare
  mode, verify sampling support, and read the clip-control state used to render
  the eye from metadata.

### Texture arrays

- layer 0 = Left, layer 1 = Right, same frame id required for both layers.

### Projection flips

- Y-flip handling must come from the per-eye published metadata (section 0),
  applied consistently in `projectToUV` for `P_L`, `P_R`, and `P_M`.

### Foveated rendering interaction

- If fragment shading rate / fragment density map foveation is active, eye
  texture periphery is low-detail and the mirror will expose it. Accept it for
  preview quality and note it in troubleshooting docs; optionally reduce
  peripheral foveation strength when the mirror is active.

### Cost controls

- The mirror is a preview: compose at half resolution and upscale with a linear
  blit, and honor the existing cyclopean desktop cadence
  (`VrCyclopeanDesktopTargetRateHz`), holding the last composed image between
  updates (`HeldLastImage`).
- On frame-id mismatch between eyes, hold the last composed mirror image rather
  than composing from mixed frames or dropping to a stretched eye; a strobing
  mirror is worse than a slightly stale one.

---

## 8) Quality Upgrades

- **Temporal accumulation (recommended early, biggest win):** reproject last
  frame's middle color using `outMiddleDepth` and blend ~0.9 history / 0.1
  current. Fills disocclusion holes, stabilizes the L/R resolve, and provides
  the per-pixel iteration seed. Mild accumulation ghosting is far less
  objectionable in a spectator view than shimmer.
- **Hole fill:** resolve pixels marked invalid with nearest-valid dilation when
  history is also unavailable.
- **Dominant eye bias:** if edges still shimmer, strengthen `dominanceEdge`
  toward a winner-take-most lateral split.
- **Middle depth output:** already written (`outMiddleDepth`); enables DOF and
  temporal reprojection.
- **Debug modes:** left-only contribution, right-only contribution,
  reprojection error heatmap, invalid-pixel mask, iteration count, final color.

---

## 9) What This Produces

- A stable, convincing "middle" viewpoint that usually feels like a centered spectator camera.
- Depth-based soft selection suppresses most double-edges and stereo ghosting.
- It won't be perfect at heavy disocclusions (no way around that without true geometry), but temporal accumulation covers most of the remainder; it's very solid for preview.

---

## 10) Resolved Open Questions

- **Is `eyeDepth` hardware depth or linear view-space Z?** Linear view-space Z
  (`R32_SFLOAT`), produced by a per-eye resolve pass (section 3.1). Hardware
  depth is a fallback only and requires explicit per-eye convention metadata
  (section 3.2).
