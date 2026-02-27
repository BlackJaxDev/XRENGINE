# Cyclopean “Middle View” Reconstruction (Stereo → Single 2D) — OpenGL / Vulkan
We synthesize a convincing **cyclopean (middle) view** from **left/right eye color + depth** without rendering a third camera. This is meant for a **2D spectator / preview window**.

With per-eye depth available, we can do a much stronger approach than “shift + blend”: we **reconstruct world-space points from each eye depth**, then **reproject into the middle camera**, and resolve conflicts (occlusion / ghosting) with depth-aware rules.

---

## 0) Assumptions & What You Provide

### Required
- Head pose:
  - `headPos_ws : vec3`
  - `headRot_ws : quat` (or matrix)
- Eye tracking (for fixation depth):
  - `gazePos_ws[2] : vec3` (Left, Right)
  - `gazeDir_ws[2] : vec3` (unit)
- Stereo render targets:
  - `eyeColor : sampler2DArray` (layer 0 = L, 1 = R)
  - `eyeDepth : sampler2DArray` (layer 0 = L, 1 = R)
- Stereo parameters:
  - `IPD : float`
- Projection data used to render the eyes:
  - `V_L, P_L, invV_L, invP_L`
  - `V_R, P_R, invV_R, invP_R`
- Preview (middle) camera projection:
  - `P_M, invP_M` (choose your spectator window FOV/aspect)
- Toggle:
  - `reversedZ : bool` (true = near=1, far=0 in device depth)

### Output
- `middleColor` (RGBA)
- Optional: `middleDepth` (linear or middle view-space Z), if you want DOF later.

---

## 1) Fixation Point From Eye Tracking (vergence intersection)
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
Cyclopean origin:
- `E_M_ws = 0.5*(gazePosL + gazePosR)`

Orientation:
- use head orientation for stable viewing:
  - `R_M_ws = headRot_ws`

View:
- `V_M = inverse( T(E_M_ws) * R(R_M_ws) )`
- `invV_M = inverse(V_M)`

Fixation depth in middle view space:
- `zFix = (V_M * vec4(F_ws,1)).z`

---

## 3) Depth Conventions: OpenGL vs Vulkan + Reversed-Z
We sample **device depth** `zDev = texture(eyeDepth, ...)`.

### 3.1 Validity test (far plane)
We need to decide if sampled depth is “background/far”.

For **normal Z**:
- far is near **1.0**
- invalid if `zDev >= 1 - eps`

For **reversed Z**:
- far is near **0.0**
- invalid if `zDev <= eps`

Use:
- `eps = 1e-6` (or a slightly bigger epsilon if your depth is noisy)

### 3.2 Device depth → NDC z
We need `ndc.z` for reconstructing with `invP`.

- Vulkan/D3D-style NDC z is **[0..1]**
- OpenGL NDC z is **[-1..1]**

So:
- If **Vulkan**: `ndcZ = zDev`
- If **OpenGL**: `ndcZ = zDev * 2 - 1`

> Reversed-Z does **not** change this mapping; it changes how zDev corresponds to distance, but the matrix inversion still works as long as `P` matches the render path that generated that depth.

---

## 4) Core Method (Per-pixel “Gather + 1-step Refinement”)
We run a fullscreen pass over the **middle preview viewport**.

For each output pixel:
1) Build a middle-view ray through that pixel.
2) Use fixation depth `zFix` as an initial depth guess to generate a world-space guess point.
3) Project that guess into each eye to get `uvL0`, `uvR0`.
4) Sample eye depth at those UVs; reconstruct true world-space points from each eye.
5) Reproject those reconstructed points into the middle camera and sample eye colors at the refined UVs.
6) Resolve L/R conflicts via depth difference threshold + near selection.

This is fast, stable, and avoids atomics.

---

## 5) GLSL Shader (OpenGL or Vulkan)

### 5.1 UBO / Push constants
```glsl
layout(std140, binding = 0) uniform CyclopeanUBO {
    mat4 V_L, P_L, invV_L, invP_L;
    mat4 V_R, P_R, invV_R, invP_R;

    mat4 V_M, P_M, invV_M, invP_M;

    vec4 fixation_ws;      // xyz = F_ws
    vec4 viewport;         // x=width, y=height, z=1/width, w=1/height

    float ipd;             // not required for Tier A, but keep for debug
    float zThreshold;      // middle-space depth threshold for ghosting resolve
    int apiConvention;     // 0=OpenGL, 1=Vulkan
    int reversedZ;         // 0=normal, 1=reversed
} u;

layout(binding = 1) uniform sampler2DArray eyeColor;
layout(binding = 2) uniform sampler2DArray eyeDepth;

layout(location = 0) out vec4 outColor;
5.2 Helpers
vec2 ndcFromUV(vec2 uv) { return uv * 2.0 - 1.0; }

float ndcZFromDeviceZ(float zDev, int apiConvention) {
    return (apiConvention == 0) ? (zDev * 2.0 - 1.0) : zDev;
}

bool inBounds(vec2 uv) { return all(greaterThanEqual(uv, vec2(0))) && all(lessThanEqual(uv, vec2(1))); }

bool depthValid(float zDev, int reversedZ) {
    const float eps = 1e-6;
    return (reversedZ == 0) ? (zDev < 1.0 - eps) : (zDev > eps);
}

vec3 reconstructWS_fromDeviceDepth(vec2 uv, float zDev, mat4 invP, mat4 invV, int apiConvention) {
    vec2 ndcXY = ndcFromUV(uv);
    float ndcZ = ndcZFromDeviceZ(zDev, apiConvention);

    vec4 clip = vec4(ndcXY, ndcZ, 1.0);
    vec4 view = invP * clip;
    view.xyz /= view.w;

    vec4 world = invV * vec4(view.xyz, 1.0);
    return world.xyz;
}

vec2 projectToUV(vec3 ws, mat4 V, mat4 P) {
    vec4 clip = P * (V * vec4(ws, 1.0));
    vec3 ndc = clip.xyz / clip.w;

    // If your Vulkan path uses a Y-flipped projection, handle it consistently:
    // ndc.y = -ndc.y;  (only if needed in your engine)

    return ndc.xy * 0.5 + 0.5;
}

float middleViewZ(vec3 ws) {
    return (u.V_M * vec4(ws, 1.0)).z;
}
5.3 Main pass
void main() {
    vec2 uv = gl_FragCoord.xy * u.viewport.zw; // [0..1]
    vec2 ndcXY = ndcFromUV(uv);

    // Fixation depth in middle view space
    float zFix = (u.V_M * vec4(u.fixation_ws.xyz, 1.0)).z;

    // Build a ray in middle view space (z=1 direction from invP)
    vec4 clip = vec4(ndcXY, 1.0, 1.0);
    vec3 ray_M = normalize((u.invP_M * clip).xyz);

    // Initial guess point at fixation depth (middle view space)
    vec3 X_M_vs_guess = ray_M * zFix;
    vec3 X_ws_guess = (u.invV_M * vec4(X_M_vs_guess, 1.0)).xyz;

    vec3 colL = vec3(0); float zML = 1e30; bool okL = false;
    vec3 colR = vec3(0); float zMR = 1e30; bool okR = false;

    // LEFT candidate
    {
        vec2 uvL0 = projectToUV(X_ws_guess, u.V_L, u.P_L);
        if (inBounds(uvL0)) {
            float zDev = texture(eyeDepth, vec3(uvL0, 0)).r;
            if (depthValid(zDev, u.reversedZ)) {
                vec3 X_ws = reconstructWS_fromDeviceDepth(uvL0, zDev, u.invP_L, u.invV_L, u.apiConvention);
                vec2 uvL = projectToUV(X_ws, u.V_L, u.P_L);
                if (inBounds(uvL)) {
                    colL = texture(eyeColor, vec3(uvL, 0)).rgb;
                    zML = middleViewZ(X_ws);
                    okL = true;
                }
            }
        }
    }

    // RIGHT candidate
    {
        vec2 uvR0 = projectToUV(X_ws_guess, u.V_R, u.P_R);
        if (inBounds(uvR0)) {
            float zDev = texture(eyeDepth, vec3(uvR0, 1)).r;
            if (depthValid(zDev, u.reversedZ)) {
                vec3 X_ws = reconstructWS_fromDeviceDepth(uvR0, zDev, u.invP_R, u.invV_R, u.apiConvention);
                vec2 uvR = projectToUV(X_ws, u.V_R, u.P_R);
                if (inBounds(uvR)) {
                    colR = texture(eyeColor, vec3(uvR, 1)).rgb;
                    zMR = middleViewZ(X_ws);
                    okR = true;
                }
            }
        }
    }

    // Fallbacks
    if (!okL && !okR) {
        vec3 c0 = texture(eyeColor, vec3(uv, 0)).rgb;
        vec3 c1 = texture(eyeColor, vec3(uv, 1)).rgb;
        outColor = vec4(0.5*(c0+c1), 1.0);
        return;
    }
    if (okL && !okR) { outColor = vec4(colL, 1.0); return; }
    if (!okL && okR) { outColor = vec4(colR, 1.0); return; }

    // Both valid: resolve ghosting using middle-space depth disagreement
    float dz = abs(zML - zMR);

    // Hard decision when they disagree strongly, otherwise blend
    vec3 outRGB;
    if (dz > u.zThreshold) {
        outRGB = (zML < zMR) ? colL : colR;
    } else {
        outRGB = 0.5 * (colL + colR);
    }

    outColor = vec4(outRGB, 1.0);
}
6) Recommended Settings / Tuning
zThreshold

You want a threshold in middle view space depth units (same units as your view matrices).
A good starting point:

zThreshold = 0.02 * abs(zFix) (2% of fixation depth)
Or clamp:

zThreshold = clamp(0.02*abs(zFix), 0.02, 0.25) (adjust for your scale)

Fixation smoothing

Smooth in world space, then compute zFix each frame.

Also clamp fixation depth to a safe range (avoid wild values during saccades).

Reversed-Z & precision

Reversed-Z improves far precision, but your validity test must match:

Normal: invalid near 1

Reversed: invalid near 0

If you store linear depth instead of device depth, you can skip the OpenGL/Vulkan z mapping entirely and reconstruct via ray * linearZ.

7) Vulkan / OpenGL Integration Notes
Vulkan

Prefer eyeDepth stored as R32_SFLOAT linear Z if you can (simpler).

If sampling actual depth images, ensure image is created with VK_IMAGE_USAGE_SAMPLED_BIT and correct layout transitions.

OpenGL

If sampling a depth texture:

disable compare mode

ensure texture format supports sampling

Texture arrays:

layer 0 = Left, layer 1 = Right

Projection flips

If your Vulkan projection uses the common “negative Y” trick, you must make projectToUV match your convention:

either flip ndc.y in projectToUV,

or bake it into P_* consistently across all projections.

8) Quality Upgrades

Soft blending vs hard threshold
Instead of a hard switch:

t = saturate(1 - dz / zThreshold)

out = mix(nearEye, 0.5*(L+R), t)

Dominant eye bias
If edges shimmer, prefer a dominant eye when gradients disagree.

Temporal accumulation
Reproject last middle view and accumulate (TAA-lite). This helps disocclusion holes a lot.

Output middle depth
Write min(zML,zMR) (or the chosen eye’s z) to a depth target so later you can add DOF.

9) What This Produces

A stable, convincing “middle” viewpoint that usually feels like a centered spectator camera.

Depth-based selection suppresses most double-edges and stereo ghosting.

It won’t be perfect at heavy disocclusions (no way around that without true geometry), but it’s very solid for preview.

- eyeDepth is hardware depth or linear view-space Z?