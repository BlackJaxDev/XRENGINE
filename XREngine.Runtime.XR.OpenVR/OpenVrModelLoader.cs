using System.Numerics;
using System.Runtime.InteropServices;
using OpenVR.NET;
using OpenVR.NET.Devices;
using Valve.VR;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine;

/// <summary>Copies OpenVR render-model buffers before publishing managed mesh data.</summary>
public static class OpenVrModelLoader
{
    public static async Task<RuntimeVrModelComponentData[]> LoadAsync(string modelName, bool alignToGripPose = false)
    {
        Matrix4x4? vertexTransform = alignToGripPose ? TryResolveOpenXrGripAlignment(modelName) : null;
        DeviceModel model = new(modelName);
        // Prefer the complete joined render model so multi-part controllers (such as Valve Index
        // Knuckles) and single-mesh trackers (Vive/Tundra) render at their exact SteamVR pose origin
        // without unposed sub-components stacking at the local origin.
        RuntimeVrModelComponentData? joined = await LoadSingleComponentAsync(model.JoinedModel, vertexTransform);
        if (joined is not null)
        {
            LogLoadedModelBounds(modelName, joined);
            return [joined];
        }

        List<RuntimeVrModelComponentData> components = [];
        foreach (ComponentModel component in model.Components)
        {
            RuntimeVrModelComponentData? data = await LoadSingleComponentAsync(component, vertexTransform);
            if (data is not null)
                components.Add(data);
        }
        if (components.Count > 0)
            LogLoadedModelBounds(modelName, components[0]);
        return [.. components];
    }

    private static void LogLoadedModelBounds(string modelName, RuntimeVrModelComponentData data)
    {
        if (data.Vertices.Length == 0)
            return;

        Vector3 min = data.Vertices[0].Position;
        Vector3 max = min;
        for (int i = 1; i < data.Vertices.Length; i++)
        {
            Vector3 p = data.Vertices[i].Position;
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
        Debug.WriteAuxiliaryLog(
            "openxr-trackers.log",
            $"OpenVrModelLoaded model={modelName} verts={data.Vertices.Length} tris={data.TriangleIndices.Count / 3} hasTex={data.Texture is not null} min=({min.X:F4},{min.Y:F4},{min.Z:F4}) max=({max.X:F4},{max.Y:F4},{max.Z:F4})");
    }

    /// <summary>
    /// Computes the inverse of the OpenVR handgrip component transform so a raw-device-space
    /// SteamVR controller mesh aligns with OpenXR <c>/input/grip/pose</c>.
    /// </summary>
    private static Matrix4x4? TryResolveOpenXrGripAlignment(string modelName)
    {
        CVRRenderModels? renderModels = Valve.VR.OpenVR.RenderModels;
        if (renderModels is null || string.IsNullOrWhiteSpace(modelName))
            return null;

        ReadOnlySpan<string> candidates = ["grip", "handgrip"];
        foreach (string componentName in candidates)
        {
            if (!renderModels.RenderModelHasComponent(modelName, componentName))
                continue;

            VRControllerState_t controllerState = default;
            RenderModel_ControllerMode_State_t modeState = default;
            RenderModel_ComponentState_t componentState = default;
            if (!renderModels.GetComponentState(
                    modelName,
                    componentName,
                    ref controllerState,
                    ref modeState,
                    ref componentState))
            {
                continue;
            }

            Matrix4x4 gripToTracking = componentState.mTrackingToComponentLocal.ToMatrix4x4();
            if (!Matrix4x4.Invert(gripToTracking, out Matrix4x4 trackingToGrip))
                continue;

            Vector3 shift = trackingToGrip.Translation;
            Debug.WriteAuxiliaryLog(
                "openxr-trackers.log",
                $"OpenVrGripAlign model={modelName} component={componentName} shift=({shift.X:F4},{shift.Y:F4},{shift.Z:F4})");
            return trackingToGrip;
        }

        return null;
    }

    private static async Task<RuntimeVrModelComponentData?> LoadSingleComponentAsync(
        ComponentModel component,
        Matrix4x4? vertexTransform)
    {
        if (string.IsNullOrWhiteSpace(component.ModelName))
            return null;

        try
        {
            return await LoadComponentAsync(component, vertexTransform);
        }
        finally
        {
            component.FreeResources();
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (component.HasLoadedResources && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            if (component.HasLoadedResources)
                throw new InvalidOperationException(
                    $"OpenVR render-model buffers for '{component.ModelName}' were not released.");
        }
    }

    private static async Task<RuntimeVrModelComponentData?> LoadComponentAsync(
        ComponentModel component,
        Matrix4x4? vertexTransform)
    {
        List<Vertex> vertices = [];
        List<ushort> indices = [];
        List<Task<RuntimeVrTextureData?>> textureLoads = [];
        Matrix4x4 transform = vertexTransform ?? Matrix4x4.Identity;
        bool applyTransform = vertexTransform.HasValue;

        void AddTexture(ComponentModel.Texture texture)
            => textureLoads.Add(LoadTextureAsync(texture));
        void AddTriangle(short index0, short index1, short index2)
        {
            indices.Add((ushort)index0);
            indices.Add((ushort)index2);
            indices.Add((ushort)index1);
        }
        void AddVertex(Vector3 position, Vector3 normal, Vector2 uv)
        {
            if (applyTransform)
            {
                position = Vector3.Transform(position, transform);
                Vector3 transformedNormal = Vector3.TransformNormal(normal, transform);
                normal = transformedNormal.LengthSquared() > 1e-8f
                    ? Vector3.Normalize(transformedNormal)
                    : Vector3.UnitY;
            }
            vertices.Add(new Vertex(position, normal, uv));
        }

        await component.LoadAsync(
            type => type == ComponentModel.ComponentType.Component,
            null, AddVertex, AddTriangle, AddTexture,
            static (_, _) => { });

        RuntimeVrTextureData? textureData = null;
        foreach (Task<RuntimeVrTextureData?> load in textureLoads)
        {
            RuntimeVrTextureData? data = await load;
            textureData ??= data;
        }

        if (vertices.Count == 0 || indices.Count == 0)
            return null;
        for (int i = 0; i < indices.Count; i++)
        {
            if (indices[i] >= vertices.Count)
            {
                Debug.VRWarning("Invalid triangle index detected in model component.");
                return null;
            }
        }

        return new RuntimeVrModelComponentData([.. vertices], indices, textureData);
    }

    private static async Task<RuntimeVrTextureData?> LoadTextureAsync(ComponentModel.Texture texture)
    {
        try
        {
            (int width, int height, IntPtr data) = await texture.LoadParams();
            if (width <= 0 || height <= 0 || data == IntPtr.Zero)
                return null;

            int rowByteCount = checked(width * 4);
            byte[] pixels = new byte[checked(rowByteCount * height)];
            for (int sourceY = 0; sourceY < height; sourceY++)
            {
                IntPtr sourceRow = IntPtr.Add(data, sourceY * rowByteCount);
                int destinationOffset = (height - sourceY - 1) * rowByteCount;
                Marshal.Copy(sourceRow, pixels, destinationOffset, rowByteCount);
            }
            return new RuntimeVrTextureData((uint)width, (uint)height, pixels);
        }
        catch (Exception ex)
        {
            Debug.VRWarning($"Failed to load OpenVR render-model texture {texture.ID}: {ex.Message}");
            return null;
        }
    }
}
