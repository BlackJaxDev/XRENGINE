# GPU Mesh BVH

Known issue: live GPU skinned bounds preview is still under investigation. See
`docs/architecture/rendering/gpu-skinned-bounds-live-preview-issue.md` for the
current symptom summary, attempted fixes, and next debugging steps.

Mesh-level GPU BVHs reuse the GPUScene BVH builder for per-triangle acceleration data. A renderable mesh owns a `GpuMeshBvh` instance on demand; the tree is built from a caller-owned triangle AABB buffer and then refit when the topology is stable.

## Static Meshes

Static mesh triangle AABBs are uploaded once in mesh local space, then the preview renderer applies the renderable transform on the GPU when drawing BVH boxes.

## Skinned Meshes

Skinned meshes can update their triangle AABBs from `SkinningPrepassDispatcher` output. The triangle-AABB compute pass reads skinned positions from GPU memory, transforms them into the renderable skinned-BVH basis, and refits the GPU BVH without CPU traversal or readback.

The live path uses compute skinning output. GPU BVH preview can request that output even when the main draw path is still using vertex-shader skinning, so disabling `Only Update BVH On Request` is not tied to the global compute-skinning renderer setting. If no GPU skinned position buffer is available yet, editor preview builds and draws a GPU bind-pose BVH once, then switches to live skinned refits when the compute-skinned buffers become available.

When `Calculate Skinned Bounds In Compute Shader` is enabled, collected skinned meshes refresh their CPU-visible culling bounds from the GPU reduction path every render-thread frame. The refresh first forces the compute-skinning output for that renderer when needed, then reduces either the separate skinned position buffer or the interleaved skinned vertex buffer to a small AABB readback.

The editor cyan bounds preview does not depend on that readback/culling cadence for skinned meshes. `SkinningPrepassDispatcher` maintains a per-renderer `LiveGpuSkinnedBounds` buffer by reducing the freshly written compute-skinned vertex output immediately after the skinning dispatch. The preview renders that GPU buffer with `skinned_bounds_debug_lines.comp`, emitting the 12 debug edges directly on the GPU.

The hover gate also prefers this live GPU-skinned world AABB. Per-mesh GPU BVH work stays downstream of the bounds pass: the BVH is only requested/refit when the mouse ray intersects the live GPU bounds and the submesh allows realtime skinned GPU BVH updates.

Imported/authored submesh culling bounds are still used when compute skinned bounds are disabled. When compute skinned bounds are enabled, the live GPU reduction takes precedence so editor bounds previews and hover-triggered BVH refreshes do not stay pinned to the authored bind-pose box.

The lightweight reduction path only requires the mapped two-vector bounds buffer. It does not require the standalone skinned-bounds shader's cloned non-interleaved source buffers, so interleaved meshes can still reduce from `SkinningPrepassInterleaved.comp` output.

The ModelComponent ImGui submesh editor must not push asset `SubMesh.Bounds`/`SubMesh.CullingBounds` back into the runtime render info while compute skinned bounds are enabled. Those asset bounds are edit-time defaults; forcing them during inspection would overwrite the render-thread GPU reduction result before the cyan bounds preview and hover gate can see it.

## SubMesh Opt-In

`SubMesh.UseGpuMeshBvh` enables interaction-triggered GPU BVH refreshes. Raycast/picking code only requests a refresh when the world-space ray segment intersects the renderable's current world bounds.

`SubMesh.RealtimeGpuMeshBvhForSkinnedMeshes` controls whether skinned instances refit from compute-skinned positions when such an interaction-triggered refresh is requested.

## Preview Rendering

The ImGui model BVH preview uses `GpuBvhDebugLineRenderer` exclusively. It attaches an `OnTopForward` render command to each previewed mesh and emits line segments from the GPU BVH node buffer with `bvh_debug_lines.comp`, so the preview no longer traverses every BVH node on the CPU or kicks CPU mesh BVH generation.

If the GPU shaders are still linking, that mesh skips the preview draw for the frame; if live skinned buffers are missing, it draws the bind-pose GPU BVH instead.

The preview line renderer pushes the active render camera around its GPU line-buffer draw, matching the scene-level GPU BVH preview command. This keeps the mesh preview from depending on collect-visible callback timing.

The preview's node render mode controls the GPU debug compute filter: render all nodes with one color, highlight leaf nodes, render only leaf nodes, or render only internal nodes.

Realtime skinned preview refits are controlled by `Only Update BVH On Request`, which is enabled by default. When it is enabled, skinned BVHs are only refit in realtime when the editor hover ray or runtime interaction ray intersects the submesh world bounds and `SubMesh.RealtimeGpuMeshBvhForSkinnedMeshes` requests a refresh for that frame. Disabling it makes the preview refit from compute-skinned GPU output every rendered preview frame.

`Only Render BVH On Request` gates the preview draw itself for skinned meshes and is also enabled by default. When enabled, moving the mouse off the submesh bounds hides that mesh's BVH preview instead of drawing the last prepared tree. The ImGui preview uses the editor camera's latest mouse ray directly, so it does not depend on throttled world picking to notice a hover. Skinned refresh requests remain pending until GPU BVH prepare actually uses skinned GPU output, so mesh collection and preview rendering cannot drop the request on a bind-pose fallback.

The ImGui preview hover path does not require `SubMesh.UseGpuMeshBvh`; enabling the preview is already an explicit debug request. Runtime interaction/picking still honors `UseGpuMeshBvh` so normal scene queries only pay for GPU mesh BVH refreshes on opted-in submeshes.
