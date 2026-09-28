# Browser GPU command resources

The WebGPU host exposes `IBrowserCommandCapability` for cold resource creation and reusable ordered command submission. Handles are positive, generation-stamped identities owned by one renderer session. `DestroyResource` rejects resources retained by binding groups, pipelines, or command sequences: release the dependent sequence/group/pipeline first. Browser objects remain private to the executor.

`CreateShaderModuleAsync(wgsl, debugName)` compiles a WGSL module and reports compilation messages with the supplied name, line and column. Shader and pipeline preparation has a 45-second deadline and at most 64 pending operations per renderer; teardown rejects pending waits. Entries are published only after compilation succeeds and the owning device remains current. These low-level modules are separate from the hash-verified cooked material startup route.

The remaining creation methods accept JSON with exact, case-sensitive field names. Descriptions are limited to 262144 characters; unknown fields are rejected. This is a cold control API: prepare descriptions outside frame execution, retain the returned handle, and call `SubmitPreparedCommands(handle)` for replay. JSON is never parsed during replay. Descriptor changes require preparing a replacement sequence. Canvas resize or surface generation changes require re-preparing sequences that use the canvas.

## Bindings

`CreateBindingLayout` accepts a label and up to 32 entries, constrained further by device limits. Each entry has `binding`, `visibility` (WebGPU vertex=1, fragment=2, compute=4), and exactly one of:

- `buffer`: `{ "type": "uniform" | "storage" | "read-only-storage", "hasDynamicOffset": false, "minBindingSize": 16 }`
- `sampler`: `{ "type": "filtering" | "non-filtering" }`
- `texture`: `{ "sampleType": "float" | "unfilterable-float" | "depth", "viewDimension": "2d", "multisampled": false }`

`CreateBindingGroup` accepts `{ "label": "...", "layout": layoutHandle, "entries": [...] }`. Each entry is `{ "binding": 0, "resource": resourceHandle }`, with optional `offset` and `size` for buffers. Buffers use the device's uniform or storage binding alignment and size limits. Texture resources must be texture-view handles. Groups fill the layout exactly. Dynamic offsets are supplied in ascending binding order when the sequence is prepared; their alignment and effective ranges are checked.

## Pipelines

`CreateComputePipelineAsync` accepts:

```json
{
  "label": "Update particles",
  "layouts": [65537],
  "compute": { "shader": 65538, "entryPoint": "computeMain" }
}
```

`CreateRenderPipelineAsync` accepts explicit layouts and fixed-function state:

```json
{
  "label": "Opaque mesh",
  "layouts": [],
  "vertex": {
    "shader": 65537,
    "entryPoint": "vertexMain",
    "buffers": [{
      "arrayStride": 12,
      "stepMode": "vertex",
      "attributes": [{ "format": "float32x3", "offset": 0, "shaderLocation": 0 }]
    }]
  },
  "fragment": {
    "shader": 65537,
    "entryPoint": "fragmentMain",
    "targets": [{ "format": "bgra8unorm", "writeMask": 15 }]
  },
  "primitive": { "topology": "triangle-list", "frontFace": "ccw", "cullMode": "back" },
  "depthStencil": { "format": "depth24plus", "depthWriteEnabled": true, "depthCompare": "less" },
  "multisample": { "count": 1 }
}
```

Example handles are placeholders for values returned by the renderer. The target format must match the selected canvas format or offscreen attachment. Supported vertex formats are 32-bit float/signed/unsigned scalar or vectors of two to four components. Step mode is `vertex` or `instance`; topology is triangle-list, line-list or point-list. Color targets use baseline RGBA8/BGRA8 linear or sRGB formats, optional explicit color/alpha blend components, and write masks. Each blend component specifies `operation`, `srcFactor`, and `dstFactor`; factors use ordinary source/destination color/alpha values, excluding blend constants and dual-source blending. Depth formats are depth16unorm, depth24plus, depth24plus-stencil8 and depth32float. Optional finite depth bias fields are accepted. Combined depth/stencil pipelines accept `stencilFront` and `stencilBack` objects with explicit `compare`, `failOp`, `depthFailOp` and `passOp`, plus unsigned `stencilReadMask` and `stencilWriteMask`; attachment plans preserve read-only aspect policy. Samples are 1 or 4. Shader overrides, strips, storage textures and optional-format expansion are outside this initial command profile.

Pipeline and layout cache keys include the entire admitted descriptor and opaque shader/layout identities. Cache eviction releases its reference; live resource handles retain their pipeline objects.

## Ordered sequences

`PrepareCommands` accepts `{ "label": "Frame", "commands": [...] }`. At most 4096 commands and 4096 indexed draws are admitted per sequence. Commands execute in supplied order in one encoder and one queue submission.

A render command has:

```json
{
  "type": "render",
  "pass": {
    "colors": [{ "viewHandle": 0, "loadOp": "clear", "storeOp": "store", "clearValue": [0, 0, 0, 1] }],
    "depthStencil": { "viewHandle": -1, "depthReadOnly": false, "depthLoadOp": "clear", "depthStoreOp": "discard", "depthClearValue": 1 }
  },
  "pipeline": 65538,
  "bindings": [],
  "vertexBuffers": [{ "buffer": 65539, "offset": 0, "size": 36 }],
  "indexBuffer": { "buffer": 65540, "format": "uint32", "offset": 0, "size": 12 },
  "draws": [{ "indexCount": 3, "instanceCount": 1, "firstIndex": 0, "baseVertex": 0, "firstInstance": 0 }]
}
```

Pass view handle 0 denotes the current canvas color output; -1 denotes its depth attachment. Positive handles identify texture views. Color attachments may include a `resolveTargetHandle`; unused color slots are null. Attachment extents, formats, sample counts, load/store policy, clear values, read-only depth/stencil policy and incompatible aliasing are checked by the pass plan. `BrowserFrameBufferPlan.ToJson()` supplies the pass description from the managed attachment model. A fresh canvas texture view is acquired once per submission, and transient views are cleared after encoding.

Render commands contain one pipeline, a complete ordered vertex-buffer list, one uint16/uint32 index buffer, and bounded direct indexed draws. Optional `stencilReference` supplies an unsigned reference value; its default is zero. They retain CPU-direct submission semantics. All pipeline groups must be supplied as `{ "index": 0, "group": groupHandle, "dynamicOffsets": [] }`. Render commands can use separate passes to express pipeline changes. GPU validation still governs the shader's actual index/vertex accesses; preparation checks index-buffer ranges, instance-buffer ranges, alignment and usages.

A compute command is `{ "type": "compute", "pipeline": pipelineHandle, "bindings": [...], "workgroups": [x, y, z] }`, with positive workgroup dimensions bounded by the selected device limit.

A buffer-copy command is `{ "type": "copyBuffer", "source": sourceHandle, "destination": destinationHandle, "sourceOffset": 0, "destinationOffset": 0, "size": 256 }`. Source and destination must differ, have compatible copy usage and contain the four-byte-aligned ranges.

A texture-copy command is `{ "type": "copyTexture", "source": sourceHandle, "destination": destinationHandle, "sourceMip": 0, "destinationMip": 0, "sourceX": 0, "sourceY": 0, "destinationX": 0, "destinationY": 0, "width": 64, "height": 64 }`. Generic texture resources must differ, use the same baseline RGBA8 linear or sRGB format, have one sample and compatible copy usage, and contain the supplied mip rectangles. Texture uploads and readback also have explicit resource APIs.

Replay traverses retained arrays without new per-draw objects. WebGPU encoder, pass encoder, command buffer and current-canvas-view creation remain required browser operations. Compute-only and copy-only submissions do not mark a replacement renderer's first presentation frame ready. Submission errors stop the renderer; asynchronous uncaptured device errors follow its existing terminal error path.

## Status

Source implementation is present. Builds, browser execution, GPU validation, shader execution, screenshots and performance checks were deliberately deferred at the user's request. These APIs do not establish hardware acceptance or the broader GPU-indirect rendering baseline.
