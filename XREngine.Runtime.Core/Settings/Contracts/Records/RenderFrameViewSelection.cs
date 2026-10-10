using System.Numerics;

namespace XREngine;

/// <summary>One immutable pass selection shared by visibility, LOD, raster and auxiliary geometry.</summary>
public readonly record struct RenderFrameViewSelection(
    RenderFrameViewDescriptor View,
    bool UseUnjitteredProjection,
    Vector2 ViewportSize,
    float ElapsedTime,
    bool ShadowPass)
{
    /// <summary>Uses the resolved projection when an independent unjittered projection is unavailable.</summary>
    public Matrix4x4 ProjectionMatrix => UseUnjitteredProjection && View.ProjectionMatrixUnjittered != default
        ? View.ProjectionMatrixUnjittered : View.ProjectionMatrix;

    public Matrix4x4 ViewProjectionMatrix => View.ViewMatrix * ProjectionMatrix;

    /// <summary>Never pairs a current unjittered projection with jittered history, or vice versa.</summary>
    public Matrix4x4 PreviousViewProjectionMatrix
    {
        get
        {
            if (!View.HasValidTemporalHistory ||
                UseUnjitteredProjection && View.ProjectionMatrixUnjittered == default)
                return ViewProjectionMatrix;
            Matrix4x4 previous = UseUnjitteredProjection
                ? View.PreviousViewProjectionMatrixUnjittered : View.PreviousViewProjectionMatrix;
            return previous != default ? previous : ViewProjectionMatrix;
        }
    }

    /// <summary>Matches authored orthographic camera uniforms; other cameras use frozen draw pixels.</summary>
    public Vector2 ScreenSize => View.CameraOrthographicSize ?? ViewportSize;
}
