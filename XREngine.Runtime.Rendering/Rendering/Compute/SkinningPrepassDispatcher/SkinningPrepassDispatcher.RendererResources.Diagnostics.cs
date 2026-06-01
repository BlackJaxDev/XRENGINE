using System;
using Silk.NET.OpenGL;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.OpenGL;

namespace XREngine.Rendering.Compute;

internal sealed partial class SkinningPrepassDispatcher
{
    private sealed partial class RendererResources
    {
        private sealed class ReadbackState
        {
            public int LoggedCount;
            public int DispatchCount;
            public float LastMinX = float.NaN, LastMaxX, LastMinY, LastMaxY, LastMinZ, LastMaxZ;
            public bool HasLast;
        }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<XRMesh, ReadbackState> _readbackByMesh = new();
        private const int ReadbackMaxLogsPerMesh = 60;
        private const float ReadbackSignificantDelta = 3f;

        /// <summary>
        /// Decisive evidence step: reads the ACTUAL compute-skinned position output back from the
        /// GPU right after a dispatch and compares it against the GPU INPUT positions. The user
        /// reports a manual bone move FIXES an exploded mesh, so routine identical frames are NOT
        /// logged (they exhausted the cap during load before): we log the first few dispatches and
        /// then only when bounds move significantly (a real break or fix), up to a high cap, so the
        /// post-move state is always captured. Keyed per mesh instance (mesh.Name is null here).
        /// </summary>
        public unsafe void DebugReadbackSkinnedOutput(XRMesh mesh)
        {
            var st = _readbackByMesh.GetValue(mesh, static _ => new ReadbackState());
            st.DispatchCount++;
            if (st.LoggedCount >= ReadbackMaxLogsPerMesh)
                return;
            var buf = _renderer.SkinnedPositionsBuffer;
            if (buf is null)
            {
                if (st.LoggedCount < 3)
                {
                    st.LoggedCount++;
                    Debug.LogWarning(
                        $"[SkinReadback] *** NO-OUTPUT-BUFFER *** verts={mesh.VertexCount} SkinnedPositionsBuffer is null " +
                        $"(skinned draw will read stale/unskinned data -> may render exploded).");
                }
                return;
            }
            if (AbstractRenderer.Current is not OpenGLRenderer glRenderer)
                return;

            uint id = 0u;
            foreach (var wrapper in buf.APIWrappers)
                if (wrapper is OpenGLRenderer.GLDataBuffer gl && gl.TryGetBindingId(out id))
                    break;
            if (id == 0u)
            {
                if (st.LoggedCount < 3)
                {
                    st.LoggedCount++;
                    Debug.LogWarning(
                        $"[SkinReadback] *** NO-OUTPUT-GLID *** verts={mesh.VertexCount} SkinnedPositionsBuffer has no GL binding id " +
                        $"(output not yet GPU-resident; skinned draw may read garbage).");
                }
                return;
            }

            var rawGl = glRenderer.RawGL;
            // Ensure the compute shader's writes are visible to this client read.
            rawGl.MemoryBarrier((uint)(GLEnum.BufferUpdateBarrierBit | GLEnum.ShaderStorageBarrierBit));

            rawGl.BindBuffer(GLEnum.ShaderStorageBuffer, id);
            // Query the ACTUAL allocated GPU byte size. If this is smaller than vertexCount*16,
            // the output buffer is under-allocated -> compute writes/draw reads run out of range.
            rawGl.GetBufferParameter(GLEnum.ShaderStorageBuffer, GLEnum.BufferSize, out int gpuBytes);
            long expectedBytes = (long)mesh.VertexCount * 4L * sizeof(float);

            int maxSampleVerts = gpuBytes > 0 ? (int)(gpuBytes / (4 * sizeof(float))) : 0;
            int sample = Math.Min(Math.Min(mesh.VertexCount, 512), maxSampleVerts);
            if (sample <= 0)
            {
                rawGl.BindBuffer(GLEnum.ShaderStorageBuffer, 0);
                st.LoggedCount++;
                Debug.LogWarning(
                    $"[SkinReadback] *** NO-GPU-DATA *** verts={mesh.VertexCount} gpuBytes={gpuBytes} expectedBytes={expectedBytes}.");
                return;
            }

            float[] data = new float[sample * 4];
            // GetBufferSubData(target) reads the buffer bound to target; bind explicitly first.
            fixed (float* p = data)
                rawGl.GetBufferSubData(GLEnum.ShaderStorageBuffer, IntPtr.Zero, (nuint)(sample * 4 * sizeof(float)), p);
            rawGl.BindBuffer(GLEnum.ShaderStorageBuffer, 0);

            bool underAllocated = gpuBytes < expectedBytes;

            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            int nan = 0;
            for (int i = 0; i < sample; i++)
            {
                float x = data[i * 4], y = data[i * 4 + 1], z = data[i * 4 + 2];
                if (float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z) || float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z))
                {
                    nan++;
                    continue;
                }
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
                if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
            }

            // Direct explosion signals that the span-ratio test cannot see: NaN/Inf output (NaN
            // values are excluded from min/max, and `NaN > threshold` is always false) and any
            // sampled coordinate with an absurd magnitude. Either is an unambiguous corruption.
            bool hugeAbs = (minX != float.MaxValue)
                && (MathF.Abs(minX) > 1000f || MathF.Abs(maxX) > 1000f
                    || MathF.Abs(minY) > 1000f || MathF.Abs(maxY) > 1000f
                    || MathF.Abs(minZ) > 1000f || MathF.Abs(maxZ) > 1000f);
            bool directExplosion = nan > 0 || hugeAbs;

            // Significant change vs the last LOGGED bounds = a real break or a real fix (e.g. the
            // user's bone move), as opposed to per-frame animation jitter which we suppress.
            bool significant = !st.HasLast
                || MathF.Abs(minX - st.LastMinX) > ReadbackSignificantDelta
                || MathF.Abs(maxX - st.LastMaxX) > ReadbackSignificantDelta
                || MathF.Abs(minY - st.LastMinY) > ReadbackSignificantDelta
                || MathF.Abs(maxY - st.LastMaxY) > ReadbackSignificantDelta
                || MathF.Abs(minZ - st.LastMinZ) > ReadbackSignificantDelta
                || MathF.Abs(maxZ - st.LastMaxZ) > ReadbackSignificantDelta;
            bool changed = st.HasLast && significant;
            // Log the first 3 dispatches always, then significant changes, and ALWAYS any direct
            // explosion (NaN/Inf/huge) even after the cap so a frozen-bad state keeps reporting.
            bool shouldLog = st.LoggedCount < 3 || significant || directExplosion;
            if (!shouldLog)
            {
                rawGl.BindBuffer(GLEnum.ShaderStorageBuffer, 0);
                return;
            }
            st.LoggedCount++;
            st.LastMinX = minX; st.LastMaxX = maxX; st.LastMinY = minY; st.LastMaxY = maxY; st.LastMinZ = minZ; st.LastMaxZ = maxZ;
            st.HasLast = true;

            string allocFlag = underAllocated ? " UNDER-ALLOCATED!" : "";
            string changeFlag = changed ? " <<< OUTPUT CHANGED" : "";
            string nanFlag = nan > 0 ? " NAN!" : "";

            // Read the GPU INPUT positions (still bound at readback time since
            // ClearOpenGlComputeBindings runs later in the finally block). Because the GPU palette
            // is proven near-identity, a CORRECT skin gives output ~= input. A large |out-in| or a
            // ratio far from 1 therefore localizes the bug to the skinning math/weights or the
            // input position read itself (not the palette).
            float maxDisp = -1f, maxRatio = -1f;
            float in0x = 0f, in0y = 0f, in0z = 0f;
            int inPosId = 0, inBytes = 0;
            rawGl.GetInteger(GLEnum.ShaderStorageBufferBinding, SkinningPrepassBindings.NonInterleavedPositionInput, out inPosId);
            if (inPosId != 0)
            {
                rawGl.BindBuffer(GLEnum.ShaderStorageBuffer, (uint)inPosId);
                rawGl.GetBufferParameter(GLEnum.ShaderStorageBuffer, GLEnum.BufferSize, out inBytes);
                int inVerts = inBytes / (3 * sizeof(float)); // tight vec3
                int inSample = Math.Min(sample, inVerts);
                if (inSample > 0)
                {
                    float[] indata = new float[inSample * 3];
                    fixed (float* p = indata)
                        rawGl.GetBufferSubData(GLEnum.ShaderStorageBuffer, IntPtr.Zero, (nuint)(inSample * 3 * sizeof(float)), p);
                    in0x = indata[0]; in0y = indata[1]; in0z = indata[2];
                    for (int i = 0; i < inSample; i++)
                    {
                        float ox = data[i * 4], oy = data[i * 4 + 1], oz = data[i * 4 + 2];
                        if (float.IsNaN(ox) || float.IsNaN(oy) || float.IsNaN(oz))
                            continue;
                        float ix = indata[i * 3], iy = indata[i * 3 + 1], iz = indata[i * 3 + 2];
                        float disp = MathF.Sqrt((ox - ix) * (ox - ix) + (oy - iy) * (oy - iy) + (oz - iz) * (oz - iz));
                        if (disp > maxDisp) maxDisp = disp;
                        float inLen = MathF.Sqrt(ix * ix + iy * iy + iz * iz);
                        float outLen = MathF.Sqrt(ox * ox + oy * oy + oz * oz);
                        if (inLen > 0.01f)
                        {
                            float ratio = outLen / inLen;
                            if (ratio > maxRatio) maxRatio = ratio;
                        }
                    }
                }
                rawGl.BindBuffer(GLEnum.ShaderStorageBuffer, 0);
            }

            // CPU client-side source positions for the SAME source-position buffer. Comparing the
            // CPU bytes against the GPU bytes (in0) classifies the corruption:
            //   cpu0 sane  + gpu in0 exploded -> GPU UPLOAD/BINDING bug (source built fine, wrong bytes on GPU).
            //   cpu0 exploded                 -> SOURCE-BUILD bug (mesh.PositionsBuffer populated/compressed wrong).
            float cpu0x = float.NaN, cpu0y = float.NaN, cpu0z = float.NaN;
            uint cpuId = 0u; long cpuBytes = -1L;
            var posBuf = mesh.PositionsBuffer;
            if (posBuf is not null)
            {
                cpuBytes = (long)posBuf.ElementCount * posBuf.ElementSize;
                foreach (var w in posBuf.APIWrappers)
                    if (w is OpenGLRenderer.GLDataBuffer glp && glp.TryGetBindingId(out cpuId))
                        break;
                if (posBuf.Address.Pointer != null && posBuf.ElementCount > 0)
                {
                    var v = posBuf.GetDataRawAtIndex<System.Numerics.Vector3>(0);
                    cpu0x = v.X; cpu0y = v.Y; cpu0z = v.Z;
                }
            }

            // Authoritative imported vertex positions (Vertices[].Position). Non-canonical meshes
            // (like the exploding one) still carry the Vertices[] array. Comparing its bounds to the
            // cooked PositionsBuffer bounds splits the corruption stage definitively:
            //   Vertices[] sane + PositionsBuffer exploded -> COOKING corrupts positions (CookedBinary/Core).
            //   Vertices[] ALSO exploded                   -> IMPORT builds wrong positions (geometryTransform).
            float vMinX = float.NaN, vMaxX = float.NaN, vMinY = float.NaN, vMaxY = float.NaN, vMinZ = float.NaN, vMaxZ = float.NaN;
            float vert0x = float.NaN, vert0y = float.NaN, vert0z = float.NaN;
            int vertCountArr = -1;
            var verts = mesh.Vertices;
            if (verts is { Length: > 0 })
            {
                vertCountArr = verts.Length;
                vMinX = vMinY = vMinZ = float.PositiveInfinity;
                vMaxX = vMaxY = vMaxZ = float.NegativeInfinity;
                var v0 = verts[0].Position;
                vert0x = v0.X; vert0y = v0.Y; vert0z = v0.Z;
                for (int vi = 0; vi < verts.Length; vi++)
                {
                    var p = verts[vi].Position;
                    if (p.X < vMinX) vMinX = p.X; if (p.X > vMaxX) vMaxX = p.X;
                    if (p.Y < vMinY) vMinY = p.Y; if (p.Y > vMaxY) vMaxY = p.Y;
                    if (p.Z < vMinZ) vMinZ = p.Z; if (p.Z > vMaxZ) vMaxZ = p.Z;
                }
            }

            // TRUE explosion test (replaces the false-positive-prone absolute-translation trigger):
            // a mesh is genuinely exploded only when its skinned OUTPUT span vastly exceeds its
            // AUTHORITATIVE source span. A large-but-correct mesh (e.g. verts=12852, ~86u Y-span) has
            // output span ~= authoritative span (ratio ~1) and is NOT exploded; a real explosion blows
            // the output span far past the source. Requires the authoritative Vertices[] array.
            string explodeFlag = string.Empty;
            if (directExplosion)
            {
                // NaN/Inf or absurd-magnitude output: the span-ratio test below cannot detect this
                // (NaN excluded from bounds, NaN comparisons are false), so flag it explicitly.
                explodeFlag = $" *** EXPLODED direct nan={nan} hugeAbs={hugeAbs} settled={_seedInputsSettled} reseed#{_renderer.SkinPaletteReseedCount} ***";
            }
            else if (vertCountArr > 0)
            {
                float outSpanX = maxX - minX, outSpanY = maxY - minY, outSpanZ = maxZ - minZ;
                float authSpanX = vMaxX - vMinX, authSpanY = vMaxY - vMinY, authSpanZ = vMaxZ - vMinZ;
                float rX = authSpanX > 0.01f ? outSpanX / authSpanX : 0f;
                float rY = authSpanY > 0.01f ? outSpanY / authSpanY : 0f;
                float rZ = authSpanZ > 0.01f ? outSpanZ / authSpanZ : 0f;
                float worstRatio = MathF.Max(rX, MathF.Max(rY, rZ));
                // 2.5x is well above any legitimate pose (animation rotates limbs but does not multiply
                // the whole-mesh extent); a stale/garbage first dispatch blows it up by 5-50x.
                if (worstRatio > 2.5f)
                    explodeFlag = $" *** EXPLODED outVsAuth ratio={worstRatio:F1} (rX={rX:F1} rY={rY:F1} rZ={rZ:F1}) settled={_seedInputsSettled} reseed#{_renderer.SkinPaletteReseedCount} ***";
            }

            Debug.LogWarning(
                $"[SkinReadback] verts={mesh.VertexCount} sample={sample} reseed#{_renderer.SkinPaletteReseedCount} settled={_seedInputsSettled} " +
                $"gpuBytes={gpuBytes} expectedBytes={expectedBytes}{allocFlag}{nanFlag}{changeFlag}{explodeFlag} " +
                $"X[{minX:F2},{maxX:F2}] Y[{minY:F2},{maxY:F2}] Z[{minZ:F2},{maxZ:F2}] out0=({data[0]:F2},{data[1]:F2},{data[2]:F2}) " +
                $"in0=({in0x:F2},{in0y:F2},{in0z:F2}) maxDisp={maxDisp:F2} maxRatio={maxRatio:F2} " +
                $"bind8Id={inPosId} bind8Bytes={inBytes} posBufId={cpuId} posBufBytes={cpuBytes} cpu0=({cpu0x:F2},{cpu0y:F2},{cpu0z:F2}) " +
                $"vertsArr={vertCountArr} vert0=({vert0x:F2},{vert0y:F2},{vert0z:F2}) " +
                $"vertX[{vMinX:F2},{vMaxX:F2}] vertY[{vMinY:F2},{vMaxY:F2}] vertZ[{vMinZ:F2},{vMaxZ:F2}].");


            // If the OUTPUT is displaced, read the ACTUAL GPU palette content to determine whether
            // the corruption is in the uploaded palette (GPU palette has a bad bone entry) or
            // downstream (palette sane on GPU but geometry/index/shader wrong). Client-side palette
            // was already proven sane (DetectSkinPaletteExplosion never fired), so a bad GPU entry
            // here means the GPU palette buffer content diverges from the client buffer.
            DebugReadbackSkinPalette(mesh, rawGl, st.DispatchCount);
        }

        /// <summary>
        /// Reads the actual GPU SkinPaletteBuffer content (3 vec4 rows per bone; translation is the
        /// W component of each row) and reports the bone with the largest translation magnitude.
        /// </summary>
        private unsafe void DebugReadbackSkinPalette(XRMesh mesh, Silk.NET.OpenGL.GL rawGl, int dispatchIndex)
        {
            // Read the buffer ACTUALLY bound at the palette SSBO slot the shader used, not a
            // C# property that may be null/divergent. This is still live at readback because
            // ClearOpenGlComputeBindings runs later in the finally block.
            rawGl.GetInteger(GLEnum.ShaderStorageBufferBinding, SkinningPrepassBindings.SkinPalette, out int paletteId);
            uint pid = (uint)paletteId;
            if (pid == 0u)
            {
                Debug.LogWarning($"[SkinPaletteGpu] verts={mesh.VertexCount} dispatch#{dispatchIndex} *** NO PALETTE BOUND AT BINDING 0 ***.");
                return;
            }

            rawGl.BindBuffer(GLEnum.ShaderStorageBuffer, pid);
            rawGl.GetBufferParameter(GLEnum.ShaderStorageBuffer, GLEnum.BufferSize, out int pBytes);
            int boneEntries = pBytes / (12 * sizeof(float));
            if (boneEntries <= 0)
            {
                rawGl.BindBuffer(GLEnum.ShaderStorageBuffer, 0);
                return;
            }

            float[] pdata = new float[boneEntries * 12];
            fixed (float* p = pdata)
                rawGl.GetBufferSubData(GLEnum.ShaderStorageBuffer, IntPtr.Zero, (nuint)(boneEntries * 12 * sizeof(float)), p);
            rawGl.BindBuffer(GLEnum.ShaderStorageBuffer, 0);

            float worstTrans = 0f;
            int worstBone = -1;
            int badBones = 0;
            int nanBones = 0;
            int identityBones = 0;      // bones whose palette == identity (passthrough => no skinning applied)
            float worstScaleDev = 0f;   // max |rowLength - 1| across the 3x3 (detects blown-up scale/rotation)
            int worstScaleBone = -1;
            for (int b = 0; b < boneEntries; b++)
            {
                // Translation = (Row0.W, Row1.W, Row2.W) = floats [3],[7],[11] of the 12-float block.
                float tx = pdata[b * 12 + 3], ty = pdata[b * 12 + 7], tz = pdata[b * 12 + 11];
                if (float.IsNaN(tx) || float.IsNaN(ty) || float.IsNaN(tz) || float.IsInfinity(tx) || float.IsInfinity(ty) || float.IsInfinity(tz))
                {
                    nanBones++;
                    continue;
                }
                float mag = MathF.Sqrt(tx * tx + ty * ty + tz * tz);
                if (mag > 50f)
                    badBones++;
                if (mag > worstTrans)
                {
                    worstTrans = mag;
                    worstBone = b;
                }

                // 3x3 row lengths: floats [0,1,2]=row0.xyz, [4,5,6]=row1.xyz, [8,9,10]=row2.xyz.
                // For a rigid/bind palette each row length is ~1. A blown-up scale here explodes
                // every vertex this bone touches WITHOUT showing up in the translation check above.
                float r0 = MathF.Sqrt(pdata[b * 12 + 0] * pdata[b * 12 + 0] + pdata[b * 12 + 1] * pdata[b * 12 + 1] + pdata[b * 12 + 2] * pdata[b * 12 + 2]);
                float r1 = MathF.Sqrt(pdata[b * 12 + 4] * pdata[b * 12 + 4] + pdata[b * 12 + 5] * pdata[b * 12 + 5] + pdata[b * 12 + 6] * pdata[b * 12 + 6]);
                float r2 = MathF.Sqrt(pdata[b * 12 + 8] * pdata[b * 12 + 8] + pdata[b * 12 + 9] * pdata[b * 12 + 9] + pdata[b * 12 + 10] * pdata[b * 12 + 10]);
                float dev = MathF.Max(MathF.Abs(r0 - 1f), MathF.Max(MathF.Abs(r1 - 1f), MathF.Abs(r2 - 1f)));
                if (dev > worstScaleDev)
                {
                    worstScaleDev = dev;
                    worstScaleBone = b;
                }

                // Identity detection: 3x3 == I (diagonal ~1, off-diagonal ~0) AND translation ~0.
                // An identity palette means the shader passes the source position through unchanged.
                // If the mesh is exploded with an identity palette, the SOURCE positions are not in a
                // directly-displayable space (they need the real bind/bone transform) -> the palette
                // was composed wrong (bone matrices not ready at load).
                const float idEps = 0.01f;
                bool isIdentity =
                    MathF.Abs(pdata[b * 12 + 0] - 1f) < idEps && MathF.Abs(pdata[b * 12 + 1]) < idEps && MathF.Abs(pdata[b * 12 + 2]) < idEps && MathF.Abs(tx) < idEps &&
                    MathF.Abs(pdata[b * 12 + 4]) < idEps && MathF.Abs(pdata[b * 12 + 5] - 1f) < idEps && MathF.Abs(pdata[b * 12 + 6]) < idEps && MathF.Abs(ty) < idEps &&
                    MathF.Abs(pdata[b * 12 + 8]) < idEps && MathF.Abs(pdata[b * 12 + 9]) < idEps && MathF.Abs(pdata[b * 12 + 10] - 1f) < idEps && MathF.Abs(tz) < idEps;
                if (isIdentity)
                    identityBones++;
            }

            string wtx = worstBone >= 0 ? $"({pdata[worstBone * 12 + 3]:F2},{pdata[worstBone * 12 + 7]:F2},{pdata[worstBone * 12 + 11]:F2})" : "n/a";
            bool mostlyIdentity = identityBones >= boneEntries - 1; // all bones (sans the unused slot 0) are identity
            string flag = (badBones > 0 || nanBones > 0 || worstScaleDev > 0.5f) ? " *** GPU PALETTE BAD ***"
                : mostlyIdentity ? " *** PALETTE ALL-IDENTITY (passthrough) ***" : "";
            Debug.LogWarning(
                $"[SkinPaletteGpu]{flag} verts={mesh.VertexCount} dispatch#{dispatchIndex} bones={boneEntries} " +
                $"identityBones={identityBones} badTrans(>50)={badBones} nanBones={nanBones} worstBone={worstBone} worstTransMag={worstTrans:F2} worstTrans={wtx} " +
                $"worstScaleBone={worstScaleBone} worstScaleDev={worstScaleDev:F2}.");
        }
    }
}
