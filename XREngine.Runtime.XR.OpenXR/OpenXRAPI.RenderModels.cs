using Silk.NET.OpenXR;
using Silk.NET.OpenXR.Extensions.MSFT;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using XREngine.Rendering;
using Debug = XREngine.Debug;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    private MsftControllerModel? _msftControllerModel;
    private int _msftControllerModelExtensionChecked;
    private int _msftControllerModelUnavailableLogged;
    private readonly Dictionary<string, RuntimeVrRenderModelDescriptor> _openXrControllerModelCache = new(StringComparer.Ordinal);

    public bool TryGetControllerRenderModel(bool leftHand, [NotNullWhen(true)] out RuntimeVrRenderModelDescriptor? renderModel)
    {
        renderModel = null;

        if (_instance.Handle == 0 || _session.Handle == 0)
            return false;

        EnsureInputCreated();

        ulong userPath = leftHand ? _leftHandPath : _rightHandPath;
        if (userPath == 0)
            return false;

        if (!TryGetMsftControllerModelExtension(out MsftControllerModel? extension))
            return false;

        var keyState = new ControllerModelKeyStateMSFT
        {
            Type = StructureType.ControllerModelKeyStateMsft,
        };

        Result keyResult = extension.GetControllerModelKeyMsft(_session, userPath, ref keyState);
        if (keyResult != Result.Success || keyState.ModelKey == 0)
            return false;

        string cacheKey = $"openxr-msft-controller:{(leftHand ? "left" : "right")}:{keyState.ModelKey}";
        lock (_openXrControllerModelCache)
        {
            if (_openXrControllerModelCache.TryGetValue(cacheKey, out renderModel))
                return true;
        }

        if (!TryLoadMsftControllerModel(extension, _session, keyState.ModelKey, out byte[]? glbData))
            return false;

        renderModel = RuntimeVrRenderModelDescriptor.FromOpenXrControllerModel(
            glbData,
            cacheKey,
            $"{(leftHand ? "Left" : "Right")} OpenXR controller model");

        lock (_openXrControllerModelCache)
            _openXrControllerModelCache[cacheKey] = renderModel;

        return true;
    }

    public string DescribeControllerRenderModelAvailability()
        => IsInstanceExtensionEnabled(MsftControllerModelExtensionName)
            ? $"{MsftControllerModelExtensionName} enabled"
            : DescribeInstanceExtensionState(MsftControllerModelExtensionName);

    private bool TryGetMsftControllerModelExtension([NotNullWhen(true)] out MsftControllerModel? extension)
    {
        extension = _msftControllerModel;
        if (extension is not null)
            return true;

        if (!IsInstanceExtensionEnabled(MsftControllerModelExtensionName))
        {
            LogMsftControllerModelUnavailable(DescribeInstanceExtensionState(MsftControllerModelExtensionName));
            return false;
        }

        if (Interlocked.Exchange(ref _msftControllerModelExtensionChecked, 1) != 0)
        {
            extension = _msftControllerModel;
            return extension is not null;
        }

        try
        {
            if (!Api.TryGetInstanceExtension<MsftControllerModel>(string.Empty, _instance, out _msftControllerModel) ||
                _msftControllerModel is null)
            {
                LogMsftControllerModelUnavailable("extension advertised but delegate loading failed");
                return false;
            }
        }
        catch (Exception ex)
        {
            LogMsftControllerModelUnavailable($"extension delegate load failed: {ex.Message}");
            return false;
        }

        extension = _msftControllerModel;
        return true;
    }

    private static bool TryLoadMsftControllerModel(MsftControllerModel extension, Session session, ulong modelKey, [NotNullWhen(true)] out byte[]? glbData)
    {
        glbData = null;

        uint bufferCount = 0;
        Result sizeResult = extension.LoadControllerModelMsft(session, modelKey, 0, ref bufferCount, (byte*)null);
        if (sizeResult != Result.Success || bufferCount == 0)
            return false;

        byte[] bytes = new byte[bufferCount];
        fixed (byte* bytesPtr = bytes)
        {
            Result loadResult = extension.LoadControllerModelMsft(session, modelKey, bufferCount, ref bufferCount, bytesPtr);
            if (loadResult != Result.Success || bufferCount == 0)
                return false;
        }

        if (bufferCount < bytes.Length)
            Array.Resize(ref bytes, (int)bufferCount);

        glbData = bytes;
        return true;
    }

    private void LogMsftControllerModelUnavailable(string reason)
    {
        if (Interlocked.Exchange(ref _msftControllerModelUnavailableLogged, 1) != 0)
            return;

        Debug.LogWarning($"OpenXR controller render models unavailable; {MsftControllerModelExtensionName} is required for runtime-supplied OpenXR controller GLBs. Reason={reason}");
    }
}
