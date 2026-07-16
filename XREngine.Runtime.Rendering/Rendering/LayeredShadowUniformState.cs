namespace XREngine.Rendering;

public struct LayeredShadowUniformState
{
    public bool IsShadowPass;
    public bool DirectionalCascadeInstancedLayeredShadowPass;
    public int DirectionalCascadeShadowLayerCount;
    public bool PointLightInstancedLayeredShadowPass;
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

    public static LayeredShadowUniformState CaptureFromCurrentRenderingState()
    {
        var state = RuntimeEngine.Rendering.State.RenderingPipelineState;
        if (state?.ShadowPass != true)
            return default;

        LayeredShadowUniformState snapshot = new()
        {
            IsShadowPass = true,
            DirectionalCascadeInstancedLayeredShadowPass = state.DirectionalCascadeInstancedLayeredShadowPass,
            DirectionalCascadeShadowLayerCount = Math.Clamp(state.DirectionalCascadeShadowLayerCount, 0, 8),
            PointLightInstancedLayeredShadowPass = state.PointLightInstancedLayeredShadowPass,
            PointLightShadowFaceCount = Math.Clamp(state.PointLightShadowFaceCount, 0, 6),
        };

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

        return snapshot;
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
