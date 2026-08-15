using XREngine.Data.Geometry;

namespace XREngine.Rendering;

public struct LayeredShadowUniformState : IEquatable<LayeredShadowUniformState>
{
    public bool IsShadowPass;
    public bool DirectionalCascadeLayeredShadowPass;
    public bool DirectionalCascadeInstancedLayeredShadowPass;
    public bool DirectionalCascadeAtlasGroupedShadowPass;
    public int DirectionalCascadeShadowLayerCount;
    public int DirectionalCascadeTargetMask;
    public bool PointLightLayeredShadowPass;
    public bool PointLightInstancedLayeredShadowPass;
    public bool PointLightAtlasGroupedShadowPass;
    public int PointLightShadowFaceCount;

    private Matrix4x4 _directionalCascadeShadowMatrix0;
    private Matrix4x4 _directionalCascadeShadowMatrix1;
    private Matrix4x4 _directionalCascadeShadowMatrix2;
    private Matrix4x4 _directionalCascadeShadowMatrix3;
    private Matrix4x4 _directionalCascadeShadowMatrix4;
    private Matrix4x4 _directionalCascadeShadowMatrix5;
    private Matrix4x4 _directionalCascadeShadowMatrix6;
    private Matrix4x4 _directionalCascadeShadowMatrix7;
    private Matrix4x4 _pointLightShadowFaceMatrix0;
    private Matrix4x4 _pointLightShadowFaceMatrix1;
    private Matrix4x4 _pointLightShadowFaceMatrix2;
    private Matrix4x4 _pointLightShadowFaceMatrix3;
    private Matrix4x4 _pointLightShadowFaceMatrix4;
    private Matrix4x4 _pointLightShadowFaceMatrix5;
    private int _pointLightShadowFaceIndex0;
    private int _pointLightShadowFaceIndex1;
    private int _pointLightShadowFaceIndex2;
    private int _pointLightShadowFaceIndex3;
    private int _pointLightShadowFaceIndex4;
    private int _pointLightShadowFaceIndex5;
    private int _pointLightShadowFaceMask;

    public static LayeredShadowUniformState CaptureFromCurrentRenderingState()
    {
        var state = RuntimeEngine.Rendering.State.RenderingPipelineState;
        if (state?.ShadowPass != true)
            return default;

        return CaptureFromRenderingState(state);
    }

    internal static LayeredShadowUniformState CaptureFromRenderingState(
        XRRenderPipelineInstance.RenderingState state)
    {
        LayeredShadowUniformState snapshot = new()
        {
            IsShadowPass = state.ShadowPass,
            DirectionalCascadeLayeredShadowPass = state.DirectionalCascadeLayeredShadowPass,
            DirectionalCascadeInstancedLayeredShadowPass = state.DirectionalCascadeInstancedLayeredShadowPass,
            DirectionalCascadeAtlasGroupedShadowPass = state.DirectionalCascadeAtlasGroupedShadowPass,
            DirectionalCascadeShadowLayerCount = Math.Clamp(state.DirectionalCascadeShadowLayerCount, 0, 8),
            PointLightLayeredShadowPass = state.PointLightLayeredShadowPass,
            PointLightInstancedLayeredShadowPass = state.PointLightInstancedLayeredShadowPass,
            PointLightAtlasGroupedShadowPass = state.PointLightAtlasGroupedShadowPass,
            PointLightShadowFaceCount = Math.Clamp(state.PointLightShadowFaceCount, 0, 6),
        };

        snapshot.DirectionalCascadeTargetMask =
            CreateCountMask(snapshot.DirectionalCascadeShadowLayerCount);

        for (int i = 0; i < snapshot.DirectionalCascadeShadowLayerCount; i++)
            if (state.TryGetDirectionalCascadeShadowMatrix(i, out Matrix4x4 matrix))
                snapshot.SetDirectionalCascadeShadowMatrix(i, matrix);

        for (int i = 0; i < snapshot.PointLightShadowFaceCount; i++)
        {
            if (state.TryGetPointLightShadowFaceMatrix(i, out Matrix4x4 matrix))
                snapshot.SetPointLightShadowFaceMatrix(i, matrix);
            if (state.TryGetPointLightShadowFaceIndex(i, out int faceIndex))
                snapshot.SetPointLightShadowFaceIndex(i, faceIndex);
            else
                snapshot.SetPointLightShadowFaceIndex(i, i);
        }

        snapshot._pointLightShadowFaceMask =
            snapshot.BuildPointLightShadowFaceMask();

        return snapshot;
    }

    /// <summary>
    /// Gets the exact logical point-light faces represented by the compact
    /// matrix/index arrays.
    /// </summary>
    public readonly int PointLightShadowFaceMask => _pointLightShadowFaceMask;

    /// <summary>
    /// Applies a conservative per-caster target mask to the immutable pass state.
    /// A target is rejected only when every world-space AABB corner lies outside
    /// the same homogeneous clip plane. Invalid bounds or matrices retain all
    /// targets so culling cannot remove a legitimate shadow caster.
    /// </summary>
    public readonly LayeredShadowCasterRelevance CalculateCasterTargetRelevance(
        in AABB localBounds,
        in Matrix4x4 modelMatrix,
        bool retainAllTargets)
    {
        LayeredShadowCasterRelevance allTargets =
            LayeredShadowCasterRelevance.FromPassState(this);
        if (!IsShadowPass || retainAllTargets || !localBounds.IsValid ||
            !IsAffineFinite(modelMatrix))
        {
            return allTargets;
        }

        AABB worldBounds = localBounds.Transformed(modelMatrix);
        if (!worldBounds.IsValid)
            return allTargets;

        bool depthZeroToOne =
            RuntimeEngine.Rendering.EffectiveClipDepthRange ==
            ERenderClipDepthRange.ZeroToOne;
        int directionalTargetMask = allTargets.DirectionalCascadeTargetMask;
        int pointFaceMask = allTargets.PointLightShadowFaceMask;

        if (DirectionalCascadeLayeredShadowPass)
        {
            int targetMask = 0;
            int targetCount = Math.Clamp(
                DirectionalCascadeShadowLayerCount,
                0,
                8);
            for (int targetIndex = 0; targetIndex < targetCount; targetIndex++)
            {
                if (!TryGetDirectionalCascadeShadowMatrix(
                        targetIndex,
                        out Matrix4x4 viewProjection) ||
                    IntersectsHomogeneousClipVolume(
                        worldBounds,
                        viewProjection,
                        depthZeroToOne))
                {
                    targetMask |= 1 << targetIndex;
                }
            }

            directionalTargetMask = targetMask;
        }

        if (PointLightLayeredShadowPass)
        {
            int faceMask = 0;
            int targetCount = Math.Clamp(PointLightShadowFaceCount, 0, 6);
            for (int targetIndex = 0; targetIndex < targetCount; targetIndex++)
            {
                if (!TryGetPointLightShadowFaceIndex(
                        targetIndex,
                        out int faceIndex) ||
                    (uint)faceIndex >= 6u)
                {
                    continue;
                }

                if (!TryGetPointLightShadowFaceMatrix(
                        targetIndex,
                        out Matrix4x4 viewProjection) ||
                    IntersectsHomogeneousClipVolume(
                        worldBounds,
                        viewProjection,
                        depthZeroToOne))
                {
                    faceMask |= 1 << faceIndex;
                }
            }

            pointFaceMask = faceMask;
        }

        return new LayeredShadowCasterRelevance(
            directionalTargetMask,
            pointFaceMask);
    }

    public readonly bool TryGetDirectionalCascadeShadowMatrix(int index, out Matrix4x4 matrix)
    {
        if ((uint)index >= (uint)DirectionalCascadeShadowLayerCount)
        {
            matrix = Matrix4x4.Identity;
            return false;
        }

        matrix = index switch
        {
            0 => _directionalCascadeShadowMatrix0,
            1 => _directionalCascadeShadowMatrix1,
            2 => _directionalCascadeShadowMatrix2,
            3 => _directionalCascadeShadowMatrix3,
            4 => _directionalCascadeShadowMatrix4,
            5 => _directionalCascadeShadowMatrix5,
            6 => _directionalCascadeShadowMatrix6,
            7 => _directionalCascadeShadowMatrix7,
            _ => Matrix4x4.Identity,
        };
        return true;
    }

    public readonly bool TryGetPointLightShadowFaceMatrix(int index, out Matrix4x4 matrix)
    {
        if ((uint)index >= (uint)PointLightShadowFaceCount)
        {
            matrix = Matrix4x4.Identity;
            return false;
        }

        matrix = index switch
        {
            0 => _pointLightShadowFaceMatrix0,
            1 => _pointLightShadowFaceMatrix1,
            2 => _pointLightShadowFaceMatrix2,
            3 => _pointLightShadowFaceMatrix3,
            4 => _pointLightShadowFaceMatrix4,
            5 => _pointLightShadowFaceMatrix5,
            _ => Matrix4x4.Identity,
        };
        return true;
    }

    public readonly bool TryGetPointLightShadowFaceIndex(int index, out int faceIndex)
    {
        if ((uint)index >= (uint)PointLightShadowFaceCount)
        {
            faceIndex = index;
            return false;
        }

        faceIndex = index switch
        {
            0 => _pointLightShadowFaceIndex0,
            1 => _pointLightShadowFaceIndex1,
            2 => _pointLightShadowFaceIndex2,
            3 => _pointLightShadowFaceIndex3,
            4 => _pointLightShadowFaceIndex4,
            5 => _pointLightShadowFaceIndex5,
            _ => index,
        };
        return true;
    }

    /// <summary>
    /// Compares the complete captured shadow state without boxing this large value type.
    /// </summary>
    public readonly bool Equals(LayeredShadowUniformState other)
        => IsShadowPass == other.IsShadowPass &&
           DirectionalCascadeLayeredShadowPass ==
               other.DirectionalCascadeLayeredShadowPass &&
           DirectionalCascadeInstancedLayeredShadowPass ==
               other.DirectionalCascadeInstancedLayeredShadowPass &&
           DirectionalCascadeAtlasGroupedShadowPass ==
               other.DirectionalCascadeAtlasGroupedShadowPass &&
           DirectionalCascadeShadowLayerCount ==
               other.DirectionalCascadeShadowLayerCount &&
           DirectionalCascadeTargetMask ==
               other.DirectionalCascadeTargetMask &&
           PointLightLayeredShadowPass ==
               other.PointLightLayeredShadowPass &&
           PointLightInstancedLayeredShadowPass ==
               other.PointLightInstancedLayeredShadowPass &&
           PointLightAtlasGroupedShadowPass ==
               other.PointLightAtlasGroupedShadowPass &&
           PointLightShadowFaceCount == other.PointLightShadowFaceCount &&
           _directionalCascadeShadowMatrix0.Equals(other._directionalCascadeShadowMatrix0) &&
           _directionalCascadeShadowMatrix1.Equals(other._directionalCascadeShadowMatrix1) &&
           _directionalCascadeShadowMatrix2.Equals(other._directionalCascadeShadowMatrix2) &&
           _directionalCascadeShadowMatrix3.Equals(other._directionalCascadeShadowMatrix3) &&
           _directionalCascadeShadowMatrix4.Equals(other._directionalCascadeShadowMatrix4) &&
           _directionalCascadeShadowMatrix5.Equals(other._directionalCascadeShadowMatrix5) &&
           _directionalCascadeShadowMatrix6.Equals(other._directionalCascadeShadowMatrix6) &&
           _directionalCascadeShadowMatrix7.Equals(other._directionalCascadeShadowMatrix7) &&
           _pointLightShadowFaceMatrix0.Equals(other._pointLightShadowFaceMatrix0) &&
           _pointLightShadowFaceMatrix1.Equals(other._pointLightShadowFaceMatrix1) &&
           _pointLightShadowFaceMatrix2.Equals(other._pointLightShadowFaceMatrix2) &&
           _pointLightShadowFaceMatrix3.Equals(other._pointLightShadowFaceMatrix3) &&
           _pointLightShadowFaceMatrix4.Equals(other._pointLightShadowFaceMatrix4) &&
           _pointLightShadowFaceMatrix5.Equals(other._pointLightShadowFaceMatrix5) &&
           _pointLightShadowFaceIndex0 == other._pointLightShadowFaceIndex0 &&
           _pointLightShadowFaceIndex1 == other._pointLightShadowFaceIndex1 &&
           _pointLightShadowFaceIndex2 == other._pointLightShadowFaceIndex2 &&
           _pointLightShadowFaceIndex3 == other._pointLightShadowFaceIndex3 &&
           _pointLightShadowFaceIndex4 == other._pointLightShadowFaceIndex4 &&
           _pointLightShadowFaceIndex5 == other._pointLightShadowFaceIndex5 &&
           _pointLightShadowFaceMask == other._pointLightShadowFaceMask;

    public override readonly bool Equals(object? obj)
        => obj is LayeredShadowUniformState other && Equals(other);

    /// <summary>
    /// Hashes the complete captured shadow state without falling through
    /// <see cref="ValueType.GetHashCode"/>, which boxes this large struct.
    /// </summary>
    public override readonly int GetHashCode()
    {
        // A non-shadow pass semantically consumes none of the fourteen matrix
        // fields. They are normally all zero, but hashing them for every visible
        // mesh made ordinary Vulkan frame-data publication scale like a shadow
        // pass even when every light was disabled.
        if (!IsShadowPass)
            return 0;

        HashCode hash = new();
        hash.Add(IsShadowPass);
        hash.Add(DirectionalCascadeLayeredShadowPass);
        hash.Add(DirectionalCascadeInstancedLayeredShadowPass);
        hash.Add(DirectionalCascadeAtlasGroupedShadowPass);
        hash.Add(DirectionalCascadeShadowLayerCount);
        hash.Add(DirectionalCascadeTargetMask);
        hash.Add(PointLightLayeredShadowPass);
        hash.Add(PointLightInstancedLayeredShadowPass);
        hash.Add(PointLightAtlasGroupedShadowPass);
        hash.Add(PointLightShadowFaceCount);
        hash.Add(_directionalCascadeShadowMatrix0);
        hash.Add(_directionalCascadeShadowMatrix1);
        hash.Add(_directionalCascadeShadowMatrix2);
        hash.Add(_directionalCascadeShadowMatrix3);
        hash.Add(_directionalCascadeShadowMatrix4);
        hash.Add(_directionalCascadeShadowMatrix5);
        hash.Add(_directionalCascadeShadowMatrix6);
        hash.Add(_directionalCascadeShadowMatrix7);
        hash.Add(_pointLightShadowFaceMatrix0);
        hash.Add(_pointLightShadowFaceMatrix1);
        hash.Add(_pointLightShadowFaceMatrix2);
        hash.Add(_pointLightShadowFaceMatrix3);
        hash.Add(_pointLightShadowFaceMatrix4);
        hash.Add(_pointLightShadowFaceMatrix5);
        hash.Add(_pointLightShadowFaceIndex0);
        hash.Add(_pointLightShadowFaceIndex1);
        hash.Add(_pointLightShadowFaceIndex2);
        hash.Add(_pointLightShadowFaceIndex3);
        hash.Add(_pointLightShadowFaceIndex4);
        hash.Add(_pointLightShadowFaceIndex5);
        hash.Add(_pointLightShadowFaceMask);
        return hash.ToHashCode();
    }

    private readonly int BuildPointLightShadowFaceMask()
    {
        int mask = 0;
        for (int i = 0; i < PointLightShadowFaceCount; i++)
        {
            if (!TryGetPointLightShadowFaceIndex(i, out int faceIndex) ||
                (uint)faceIndex >= 6u)
            {
                continue;
            }

            mask |= 1 << faceIndex;
        }

        return mask;
    }

    private static int CreateCountMask(int count)
        => count <= 0 ? 0 : (1 << Math.Min(count, 30)) - 1;

    private static bool IsAffineFinite(in Matrix4x4 matrix)
        => float.IsFinite(matrix.M11) && float.IsFinite(matrix.M12) &&
           float.IsFinite(matrix.M13) && float.IsFinite(matrix.M14) &&
           float.IsFinite(matrix.M21) && float.IsFinite(matrix.M22) &&
           float.IsFinite(matrix.M23) && float.IsFinite(matrix.M24) &&
           float.IsFinite(matrix.M31) && float.IsFinite(matrix.M32) &&
           float.IsFinite(matrix.M33) && float.IsFinite(matrix.M34) &&
           float.IsFinite(matrix.M41) && float.IsFinite(matrix.M42) &&
           float.IsFinite(matrix.M43) && float.IsFinite(matrix.M44) &&
           MathF.Abs(matrix.M14) <= 1.0e-6f &&
           MathF.Abs(matrix.M24) <= 1.0e-6f &&
           MathF.Abs(matrix.M34) <= 1.0e-6f &&
           MathF.Abs(matrix.M44 - 1.0f) <= 1.0e-6f;

    private static bool IntersectsHomogeneousClipVolume(
        in AABB bounds,
        in Matrix4x4 viewProjection,
        bool depthZeroToOne)
    {
        if (!IsFinite(viewProjection))
            return true;

        bounds.GetCorners(
            out Vector3 topBackLeft,
            out Vector3 topBackRight,
            out Vector3 topFrontLeft,
            out Vector3 topFrontRight,
            out Vector3 bottomBackLeft,
            out Vector3 bottomBackRight,
            out Vector3 bottomFrontLeft,
            out Vector3 bottomFrontRight);

        int commonOutsidePlanes = ClassifyOutsideClipPlanes(
            topBackLeft,
            viewProjection,
            depthZeroToOne);
        commonOutsidePlanes &= ClassifyOutsideClipPlanes(
            topBackRight,
            viewProjection,
            depthZeroToOne);
        commonOutsidePlanes &= ClassifyOutsideClipPlanes(
            topFrontLeft,
            viewProjection,
            depthZeroToOne);
        commonOutsidePlanes &= ClassifyOutsideClipPlanes(
            topFrontRight,
            viewProjection,
            depthZeroToOne);
        commonOutsidePlanes &= ClassifyOutsideClipPlanes(
            bottomBackLeft,
            viewProjection,
            depthZeroToOne);
        commonOutsidePlanes &= ClassifyOutsideClipPlanes(
            bottomBackRight,
            viewProjection,
            depthZeroToOne);
        commonOutsidePlanes &= ClassifyOutsideClipPlanes(
            bottomFrontLeft,
            viewProjection,
            depthZeroToOne);
        commonOutsidePlanes &= ClassifyOutsideClipPlanes(
            bottomFrontRight,
            viewProjection,
            depthZeroToOne);
        return commonOutsidePlanes == 0;
    }

    private static int ClassifyOutsideClipPlanes(
        in Vector3 point,
        in Matrix4x4 viewProjection,
        bool depthZeroToOne)
    {
        Vector4 clip = Vector4.Transform(
            new Vector4(point, 1.0f),
            viewProjection);
        if (!float.IsFinite(clip.X) || !float.IsFinite(clip.Y) ||
            !float.IsFinite(clip.Z) || !float.IsFinite(clip.W))
        {
            return 0;
        }

        int outside = 0;
        if (clip.X < -clip.W) outside |= 1 << 0;
        if (clip.X > clip.W) outside |= 1 << 1;
        if (clip.Y < -clip.W) outside |= 1 << 2;
        if (clip.Y > clip.W) outside |= 1 << 3;
        if (clip.Z < (depthZeroToOne ? 0.0f : -clip.W)) outside |= 1 << 4;
        if (clip.Z > clip.W) outside |= 1 << 5;
        return outside;
    }

    private static bool IsFinite(in Matrix4x4 matrix)
        => float.IsFinite(matrix.M11) && float.IsFinite(matrix.M12) &&
           float.IsFinite(matrix.M13) && float.IsFinite(matrix.M14) &&
           float.IsFinite(matrix.M21) && float.IsFinite(matrix.M22) &&
           float.IsFinite(matrix.M23) && float.IsFinite(matrix.M24) &&
           float.IsFinite(matrix.M31) && float.IsFinite(matrix.M32) &&
           float.IsFinite(matrix.M33) && float.IsFinite(matrix.M34) &&
           float.IsFinite(matrix.M41) && float.IsFinite(matrix.M42) &&
           float.IsFinite(matrix.M43) && float.IsFinite(matrix.M44);

    private void SetDirectionalCascadeShadowMatrix(int index, Matrix4x4 matrix)
    {
        switch (index)
        {
            case 0: _directionalCascadeShadowMatrix0 = matrix; break;
            case 1: _directionalCascadeShadowMatrix1 = matrix; break;
            case 2: _directionalCascadeShadowMatrix2 = matrix; break;
            case 3: _directionalCascadeShadowMatrix3 = matrix; break;
            case 4: _directionalCascadeShadowMatrix4 = matrix; break;
            case 5: _directionalCascadeShadowMatrix5 = matrix; break;
            case 6: _directionalCascadeShadowMatrix6 = matrix; break;
            case 7: _directionalCascadeShadowMatrix7 = matrix; break;
        }
    }

    private void SetPointLightShadowFaceMatrix(int index, Matrix4x4 matrix)
    {
        switch (index)
        {
            case 0: _pointLightShadowFaceMatrix0 = matrix; break;
            case 1: _pointLightShadowFaceMatrix1 = matrix; break;
            case 2: _pointLightShadowFaceMatrix2 = matrix; break;
            case 3: _pointLightShadowFaceMatrix3 = matrix; break;
            case 4: _pointLightShadowFaceMatrix4 = matrix; break;
            case 5: _pointLightShadowFaceMatrix5 = matrix; break;
        }
    }

    private void SetPointLightShadowFaceIndex(int index, int faceIndex)
    {
        switch (index)
        {
            case 0: _pointLightShadowFaceIndex0 = faceIndex; break;
            case 1: _pointLightShadowFaceIndex1 = faceIndex; break;
            case 2: _pointLightShadowFaceIndex2 = faceIndex; break;
            case 3: _pointLightShadowFaceIndex3 = faceIndex; break;
            case 4: _pointLightShadowFaceIndex4 = faceIndex; break;
            case 5: _pointLightShadowFaceIndex5 = faceIndex; break;
        }
    }
}
