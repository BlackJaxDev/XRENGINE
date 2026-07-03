using Silk.NET.Core;
using Silk.NET.OpenXR;
using Silk.NET.Core.Native;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Rendering;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    private const int XrViewConfigurationTypePrimaryQuadVarjoValue = 1000037000;
    private static readonly ViewConfigurationType PrimaryQuadVarjoViewConfigurationType =
        (ViewConfigurationType)XrViewConfigurationTypePrimaryQuadVarjoValue;

    private delegate Result XrGetVisibilityMaskKhrDelegate(
        Session session,
        ViewConfigurationType viewConfigurationType,
        uint viewIndex,
        VisibilityMaskTypeKHR visibilityMaskType,
        VisibilityMaskKHR* visibilityMask);

    private bool IsOpenXrQuadViewConfigurationActive
        => _activeViewConfigurationType == PrimaryQuadVarjoViewConfigurationType;

    private void InitializeOpenXrViewsForActiveConfiguration(string backendLabel)
    {
        ViewConfigurationType viewConfigType = SelectOpenXrViewConfigurationType();
        CacheOpenXrViewConfigurationSnapshots(backendLabel);
        _viewCount = 0;
        Result countResult = Api.EnumerateViewConfigurationView(_instance, _systemId, viewConfigType, 0, ref _viewCount, null);
        if (countResult != Result.Success)
            throw new InvalidOperationException($"{backendLabel} failed to enumerate OpenXR view count for {viewConfigType}: {countResult}");

        if (_viewCount == 0 || _viewCount > RenderFrameViewSet.MaxViewCount)
            throw new InvalidOperationException($"{backendLabel} reported unsupported OpenXR view count {_viewCount} for {viewConfigType}. Max supported is {RenderFrameViewSet.MaxViewCount}.");

        _views = new View[_viewCount];
        for (int i = 0; i < _views.Length; i++)
            _views[i].Type = StructureType.View;

        for (int i = 0; i < _viewConfigViews.Length; i++)
            _viewConfigViews[i] = new ViewConfigurationView { Type = StructureType.ViewConfigurationView };

        uint viewCountInputOutput = _viewCount;
        fixed (ViewConfigurationView* viewConfigViewsPtr = _viewConfigViews)
        {
            Result viewResult = Api.EnumerateViewConfigurationView(
                _instance,
                _systemId,
                viewConfigType,
                viewCountInputOutput,
                ref viewCountInputOutput,
                viewConfigViewsPtr);

            if (viewResult != Result.Success)
                throw new InvalidOperationException($"{backendLabel} failed to enumerate OpenXR view configuration data for {viewConfigType}: {viewResult}");
        }

        _viewCount = viewCountInputOutput;
        for (int i = 0; i < _viewCount; i++)
        {
            uint rw = _viewConfigViews[i].RecommendedImageRectWidth;
            uint rh = _viewConfigViews[i].RecommendedImageRectHeight;
            Debug.Out($"{backendLabel} view[{i}] config={viewConfigType} recommended size: {rw}x{rh}, max={_viewConfigViews[i].MaxImageRectWidth}x{_viewConfigViews[i].MaxImageRectHeight}, samples={_viewConfigViews[i].RecommendedSwapchainSampleCount}");

            if (rw == 0 || rh == 0)
                throw new InvalidOperationException($"{backendLabel} runtime reported an invalid recommended image rect size for view {i}: {rw}x{rh}. Cannot create swapchains.");
        }

        Debug.Out($"[OpenXR] Active view configuration={_activeViewConfigurationType} viewCount={_viewCount} quadActive={IsOpenXrQuadViewConfigurationActive} fallback={_activeViewConfigurationFallbackReason}: {_activeViewConfigurationDiagnostic}");
        InitializeOpenXrRvcVisibilityMaskStates();
    }

    private void CacheOpenXrViewConfigurationSnapshots(string backendLabel)
    {
        if (TryCacheOpenXrViewConfigurationViews(
            ViewConfigurationType.PrimaryStereo,
            _nonFoveatedStereoViewConfigViews,
            out _nonFoveatedStereoViewConfigViewCount,
            out Result stereoResult))
        {
            Debug.Out($"[OpenXR] Cached non-foveated stereo view configuration count={_nonFoveatedStereoViewConfigViewCount} for {backendLabel}.");
        }
        else
        {
            _nonFoveatedStereoViewConfigViewCount = 0;
            Debug.RenderingWarningEvery(
                "OpenXR.ViewConfiguration.StereoSnapshotFailed",
                TimeSpan.FromSeconds(5),
                "[OpenXR] Failed to cache stereo view configuration data for {0}: {1}.",
                backendLabel,
                stereoResult);
        }

        if (!IsInstanceExtensionEnabled(VarjoQuadViewsExtensionName))
        {
            _foveatedQuadViewConfigViewCount = 0;
            return;
        }

        if (TryCacheOpenXrViewConfigurationViews(
            PrimaryQuadVarjoViewConfigurationType,
            _foveatedQuadViewConfigViews,
            out _foveatedQuadViewConfigViewCount,
            out Result quadResult))
        {
            Debug.Out($"[OpenXR] Cached foveated quad-view configuration count={_foveatedQuadViewConfigViewCount} for {backendLabel}.");
        }
        else
        {
            _foveatedQuadViewConfigViewCount = 0;
            Debug.RenderingWarningEvery(
                "OpenXR.ViewConfiguration.QuadSnapshotFailed",
                TimeSpan.FromSeconds(5),
                "[OpenXR] Failed to cache quad-view configuration data for {0}: {1}.",
                backendLabel,
                quadResult);
        }
    }

    private bool TryCacheOpenXrViewConfigurationViews(
        ViewConfigurationType viewConfigurationType,
        ViewConfigurationView[] target,
        out uint viewCount,
        out Result result)
    {
        viewCount = 0;
        result = Api.EnumerateViewConfigurationView(_instance, _systemId, viewConfigurationType, 0, ref viewCount, null);
        if (result != Result.Success || viewCount == 0 || viewCount > RenderFrameViewSet.MaxViewCount)
            return false;

        for (int i = 0; i < target.Length; i++)
            target[i] = new ViewConfigurationView { Type = StructureType.ViewConfigurationView };

        uint capacity = viewCount;
        fixed (ViewConfigurationView* targetPtr = target)
        {
            result = Api.EnumerateViewConfigurationView(
                _instance,
                _systemId,
                viewConfigurationType,
                capacity,
                ref viewCount,
                targetPtr);
        }

        return result == Result.Success && viewCount > 0 && viewCount <= RenderFrameViewSet.MaxViewCount;
    }

    private ViewConfigurationType SelectOpenXrViewConfigurationType()
    {
        _activeViewConfigurationType = ViewConfigurationType.PrimaryStereo;
        _activeViewConfigurationFallbackReason = ERvcFallbackReason.None;
        _activeViewConfigurationDiagnostic = "Primary stereo view configuration selected.";

        if (!ShouldRequestOpenXrQuadViews())
            return _activeViewConfigurationType;

        if (!IsInstanceExtensionEnabled(VarjoQuadViewsExtensionName))
        {
            RecordOpenXrViewConfigurationFallback(
                ERvcFallbackReason.MissingQuadViewRuntime,
                $"{VarjoQuadViewsExtensionName} is not enabled by the OpenXR runtime.");
            return _activeViewConfigurationType;
        }

        if (!TryGetOpenXrViewConfigurationViewCount(PrimaryQuadVarjoViewConfigurationType, out uint quadViewCount, out Result result))
        {
            RecordOpenXrViewConfigurationFallback(
                ERvcFallbackReason.MissingQuadViewRuntime,
                $"OpenXR runtime did not enumerate {PrimaryQuadVarjoViewConfigurationType}; result={result}, viewCount={quadViewCount}.");
            return _activeViewConfigurationType;
        }

        if (quadViewCount != 4)
        {
            RecordOpenXrViewConfigurationFallback(
                ERvcFallbackReason.MissingQuadViewRuntime,
                $"OpenXR quad-view configuration reported {quadViewCount} views; expected 4.");
            return _activeViewConfigurationType;
        }

        _activeViewConfigurationType = PrimaryQuadVarjoViewConfigurationType;
        _activeViewConfigurationFallbackReason = ERvcFallbackReason.None;
        _activeViewConfigurationDiagnostic = "OpenXR quad-view configuration selected.";
        return _activeViewConfigurationType;
    }

    private bool ShouldRequestOpenXrQuadViews()
    {
        IRuntimeRenderingHostServices host = RuntimeRenderingHostServices.Current;
        return host.RvcQuadViewEnabled ||
            (host.RvcPipelineMode != ERvcPipelineMode.Off && host.RvcQuadViewEnabled);
    }

    private static bool IsLeftEyeLikeOpenXrView(uint viewIndex)
        => (viewIndex & 1u) == 0u;

    private EVrOutputViewKind ResolveOpenXrRvcViewKind(uint viewIndex)
    {
        if (IsOpenXrQuadViewConfigurationActive)
        {
            return viewIndex switch
            {
                0u => EVrOutputViewKind.LeftWide,
                1u => EVrOutputViewKind.RightWide,
                2u => EVrOutputViewKind.LeftInset,
                3u => EVrOutputViewKind.RightInset,
                _ => EVrOutputViewKind.Debug,
            };
        }

        return IsLeftEyeLikeOpenXrView(viewIndex)
            ? EVrOutputViewKind.LeftEye
            : EVrOutputViewKind.RightEye;
    }

    private void InitializeOpenXrRvcVisibilityMaskStates()
    {
        bool enabled = IsRvcOpenXrVisibilityMaskExtensionEnabled;
        ERvcOpenXrVisibilityMaskStatus status = enabled
            ? ERvcOpenXrVisibilityMaskStatus.AwaitingRuntimeMesh
            : ERvcOpenXrVisibilityMaskStatus.ExtensionMissing;
        string diagnostic = enabled
            ? "XR_KHR_visibility_mask is enabled; waiting for backend mesh acquisition."
            : "XR_KHR_visibility_mask is not enabled for this OpenXR instance.";

        _openXrRvcVisibilityMaskRevision++;
        for (uint i = 0; i < _openXrRvcVisibilityMaskStates.Length; i++)
        {
            _openXrRvcVisibilityMaskStates[i] = new(
                i,
                i < _viewCount ? ResolveOpenXrRvcViewKind(i) : EVrOutputViewKind.Debug,
                HiddenAreaMeshAvailable: false,
                VisibleAreaMeshAvailable: false,
                HiddenAreaVertexCount: 0u,
                HiddenAreaIndexCount: 0u,
                VisibleAreaVertexCount: 0u,
                VisibleAreaIndexCount: 0u,
                _openXrRvcVisibilityMaskRevision,
                status,
                diagnostic);
            _openXrRvcHiddenAreaMaskVertices[i] = Array.Empty<Vector2>();
            _openXrRvcHiddenAreaMaskIndices[i] = Array.Empty<uint>();
            _openXrRvcVisibleAreaMaskVertices[i] = Array.Empty<Vector2>();
            _openXrRvcVisibleAreaMaskIndices[i] = Array.Empty<uint>();
            _openXrRvcPreviousViewProjectionMatrices[i] = Matrix4x4.Identity;
        }

        RefreshOpenXrRvcVisibilityMasks();
    }

    private void InvalidateOpenXrRvcVisibilityMasks(string diagnostic)
    {
        _openXrRvcVisibilityMaskRevision++;
        uint count = Math.Min(_viewCount, (uint)_openXrRvcVisibilityMaskStates.Length);
        for (uint i = 0; i < count; i++)
        {
            _openXrRvcVisibilityMaskStates[i] =
                _openXrRvcVisibilityMaskStates[i].MarkInvalidated(diagnostic) with
                {
                    Revision = _openXrRvcVisibilityMaskRevision,
                };
        }

        RefreshOpenXrRvcVisibilityMasks();
    }

    private void RefreshOpenXrRvcVisibilityMasks()
    {
        if (!IsRvcOpenXrVisibilityMaskExtensionEnabled)
            return;

        uint count = Math.Min(_viewCount, (uint)_openXrRvcVisibilityMaskStates.Length);
        if (count == 0)
            return;

        if (_session.Handle == 0)
        {
            MarkOpenXrRvcVisibilityMasksUnavailable(
                ERvcOpenXrVisibilityMaskStatus.AwaitingRuntimeMesh,
                "OpenXR session is not available yet; RVC visibility masks will be fetched after session creation.");
            return;
        }

        if (!TryResolveOpenXrRvcVisibilityMaskFunction(out XrGetVisibilityMaskKhrDelegate getVisibilityMask, out string functionDiagnostic))
        {
            MarkOpenXrRvcVisibilityMasksUnavailable(ERvcOpenXrVisibilityMaskStatus.NativeFunctionMissing, functionDiagnostic);
            return;
        }

        for (uint viewIndex = 0; viewIndex < count; viewIndex++)
        {
            bool hiddenFetched = TryFetchOpenXrRvcVisibilityMaskMesh(
                getVisibilityMask,
                viewIndex,
                VisibilityMaskTypeKHR.HiddenTriangleMeshKhr,
                out Vector2[] hiddenVertices,
                out uint[] hiddenIndices,
                out string hiddenDiagnostic);

            bool visibleFetched = TryFetchOpenXrRvcVisibilityMaskMesh(
                getVisibilityMask,
                viewIndex,
                VisibilityMaskTypeKHR.VisibleTriangleMeshKhr,
                out Vector2[] visibleVertices,
                out uint[] visibleIndices,
                out string visibleDiagnostic);

            _openXrRvcHiddenAreaMaskVertices[viewIndex] = hiddenVertices;
            _openXrRvcHiddenAreaMaskIndices[viewIndex] = hiddenIndices;
            _openXrRvcVisibleAreaMaskVertices[viewIndex] = visibleVertices;
            _openXrRvcVisibleAreaMaskIndices[viewIndex] = visibleIndices;

            bool hasHiddenMesh = hiddenFetched && hiddenVertices.Length > 0 && hiddenIndices.Length > 0;
            bool hasVisibleMesh = visibleFetched && visibleVertices.Length > 0 && visibleIndices.Length > 0;
            ERvcOpenXrVisibilityMaskStatus state = hasHiddenMesh
                ? ERvcOpenXrVisibilityMaskStatus.ReadyForStencilPrepass
                : ERvcOpenXrVisibilityMaskStatus.RuntimeMeshUnavailable;

            string diagnostic = hasHiddenMesh
                ? $"XR_KHR_visibility_mask hidden mesh ready for view {viewIndex}: vertices={hiddenVertices.Length}, indices={hiddenIndices.Length}; visible vertices={visibleVertices.Length}, indices={visibleIndices.Length}."
                : $"XR_KHR_visibility_mask mesh unavailable for view {viewIndex}. Hidden: {hiddenDiagnostic} Visible: {visibleDiagnostic}";

            _openXrRvcVisibilityMaskStates[viewIndex] = _openXrRvcVisibilityMaskStates[viewIndex] with
            {
                HiddenAreaMeshAvailable = hasHiddenMesh,
                VisibleAreaMeshAvailable = hasVisibleMesh,
                HiddenAreaVertexCount = checked((uint)hiddenVertices.Length),
                HiddenAreaIndexCount = checked((uint)hiddenIndices.Length),
                VisibleAreaVertexCount = checked((uint)visibleVertices.Length),
                VisibleAreaIndexCount = checked((uint)visibleIndices.Length),
                Revision = _openXrRvcVisibilityMaskRevision,
                Status = state,
                Diagnostic = diagnostic,
            };
        }
    }

    private bool TryResolveOpenXrRvcVisibilityMaskFunction(
        out XrGetVisibilityMaskKhrDelegate getVisibilityMask,
        out string diagnostic)
    {
        if (_xrGetVisibilityMaskKhr is not null)
        {
            getVisibilityMask = _xrGetVisibilityMaskKhr;
            diagnostic = "xrGetVisibilityMaskKHR is cached.";
            return true;
        }

        PfnVoidFunction function = default;
        Result result = Api.GetInstanceProcAddr(_instance, "xrGetVisibilityMaskKHR", ref function);
        nint functionPointer = (nint)function;
        if (result != Result.Success || functionPointer == 0)
        {
            getVisibilityMask = null!;
            diagnostic = $"xrGetVisibilityMaskKHR lookup failed: result={result}, pointer=0x{functionPointer:X}.";
            return false;
        }

        _xrGetVisibilityMaskKhr = Marshal.GetDelegateForFunctionPointer<XrGetVisibilityMaskKhrDelegate>(functionPointer);
        getVisibilityMask = _xrGetVisibilityMaskKhr;
        diagnostic = "xrGetVisibilityMaskKHR resolved.";
        return true;
    }

    private bool TryFetchOpenXrRvcVisibilityMaskMesh(
        XrGetVisibilityMaskKhrDelegate getVisibilityMask,
        uint viewIndex,
        VisibilityMaskTypeKHR maskType,
        out Vector2[] vertices,
        out uint[] indices,
        out string diagnostic)
    {
        vertices = Array.Empty<Vector2>();
        indices = Array.Empty<uint>();

        VisibilityMaskKHR countQuery = new()
        {
            Type = StructureType.VisibilityMaskKhr,
            VertexCapacityInput = 0u,
            IndexCapacityInput = 0u,
        };

        Result countResult = getVisibilityMask(_session, _activeViewConfigurationType, viewIndex, maskType, &countQuery);
        if (countResult != Result.Success)
        {
            diagnostic = $"{maskType} count query failed: {countResult}.";
            return false;
        }

        if (countQuery.VertexCountOutput == 0u || countQuery.IndexCountOutput == 0u)
        {
            diagnostic = $"{maskType} count query returned vertices={countQuery.VertexCountOutput}, indices={countQuery.IndexCountOutput}.";
            return true;
        }

        int vertexCount = checked((int)countQuery.VertexCountOutput);
        int indexCount = checked((int)countQuery.IndexCountOutput);
        Vector2f[] nativeVertices = new Vector2f[vertexCount];
        uint[] fetchedIndices = new uint[indexCount];

        fixed (Vector2f* vertexPtr = nativeVertices)
        fixed (uint* indexPtr = fetchedIndices)
        {
            VisibilityMaskKHR meshQuery = new()
            {
                Type = StructureType.VisibilityMaskKhr,
                VertexCapacityInput = countQuery.VertexCountOutput,
                VertexCountOutput = 0u,
                Vertices = vertexPtr,
                IndexCapacityInput = countQuery.IndexCountOutput,
                IndexCountOutput = 0u,
                Indices = indexPtr,
            };

            Result meshResult = getVisibilityMask(_session, _activeViewConfigurationType, viewIndex, maskType, &meshQuery);
            if (meshResult != Result.Success)
            {
                diagnostic = $"{maskType} mesh query failed: {meshResult}.";
                return false;
            }

            int fetchedVertexCount = Math.Min(vertexCount, checked((int)meshQuery.VertexCountOutput));
            int fetchedIndexCount = Math.Min(indexCount, checked((int)meshQuery.IndexCountOutput));
            vertices = new Vector2[fetchedVertexCount];
            for (int i = 0; i < fetchedVertexCount; i++)
                vertices[i] = new Vector2(nativeVertices[i].X, nativeVertices[i].Y);

            if (fetchedIndexCount == fetchedIndices.Length)
            {
                indices = fetchedIndices;
            }
            else
            {
                indices = new uint[fetchedIndexCount];
                Array.Copy(fetchedIndices, indices, fetchedIndexCount);
            }

            diagnostic = $"{maskType} mesh fetched: vertices={vertices.Length}, indices={indices.Length}.";
            return true;
        }
    }

    private void MarkOpenXrRvcVisibilityMasksUnavailable(ERvcOpenXrVisibilityMaskStatus status, string diagnostic)
    {
        uint count = Math.Min(_viewCount, (uint)_openXrRvcVisibilityMaskStates.Length);
        for (uint i = 0; i < count; i++)
        {
            _openXrRvcHiddenAreaMaskVertices[i] = Array.Empty<Vector2>();
            _openXrRvcHiddenAreaMaskIndices[i] = Array.Empty<uint>();
            _openXrRvcVisibleAreaMaskVertices[i] = Array.Empty<Vector2>();
            _openXrRvcVisibleAreaMaskIndices[i] = Array.Empty<uint>();
            _openXrRvcVisibilityMaskStates[i] = _openXrRvcVisibilityMaskStates[i] with
            {
                HiddenAreaMeshAvailable = false,
                VisibleAreaMeshAvailable = false,
                HiddenAreaVertexCount = 0u,
                HiddenAreaIndexCount = 0u,
                VisibleAreaVertexCount = 0u,
                VisibleAreaIndexCount = 0u,
                Revision = _openXrRvcVisibilityMaskRevision,
                Status = status,
                Diagnostic = diagnostic,
            };
        }
    }

    private XRViewport? GetOpenXrEyeViewport(uint viewIndex)
        => IsLeftEyeLikeOpenXrView(viewIndex)
            ? _openXrLeftViewport
            : _openXrRightViewport;

    private XRCamera? GetOpenXrEyeCamera(uint viewIndex)
        => IsLeftEyeLikeOpenXrView(viewIndex)
            ? _openXrLeftEyeCamera
            : _openXrRightEyeCamera;

    private XRTexture2D? GetOpenXrPreviewTexture(uint viewIndex)
        => IsLeftEyeLikeOpenXrView(viewIndex)
            ? _previewLeftEyeTexture
            : _previewRightEyeTexture;

    private bool TryGetOpenXrViewConfigurationViewCount(
        ViewConfigurationType viewConfigurationType,
        out uint viewCount,
        out Result result)
    {
        viewCount = 0;
        result = Api.EnumerateViewConfigurationView(_instance, _systemId, viewConfigurationType, 0, ref viewCount, null);
        return result == Result.Success && viewCount > 0 && viewCount <= RenderFrameViewSet.MaxViewCount;
    }

    private void RecordOpenXrViewConfigurationFallback(ERvcFallbackReason reason, string diagnostic)
    {
        _activeViewConfigurationFallbackReason = reason;
        _activeViewConfigurationDiagnostic = diagnostic;
        Debug.RenderingWarningEvery(
            "OpenXR.ViewConfiguration.Fallback",
            TimeSpan.FromSeconds(5),
            "[OpenXR] Quad-view request fell back to stereo: {0}",
            diagnostic);
    }
}
