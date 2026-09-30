using System.Numerics;
using System.Runtime.InteropServices;
using OpenVR.NET.Devices;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine;

/// <summary>Copies OpenVR render-model buffers before publishing managed mesh data.</summary>
public static class OpenVrModelLoader
{
    public static async Task<RuntimeVrModelComponentData[]> LoadAsync(string modelName)
    {
        DeviceModel model = new(modelName);
        List<RuntimeVrModelComponentData> components = [];
        foreach (ComponentModel component in model.Components)
        {
            try
            {
                RuntimeVrModelComponentData? data = await LoadComponentAsync(component);
                if (data is not null)
                    components.Add(data);
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
        return [.. components];
    }

    private static async Task<RuntimeVrModelComponentData?> LoadComponentAsync(ComponentModel component)
    {
        if (!component.ModelName.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
            return null;

        List<Vertex> vertices = [];
        List<ushort> indices = [];
        List<Task<RuntimeVrTextureData?>> textureLoads = [];

        void AddTexture(ComponentModel.Texture texture)
            => textureLoads.Add(LoadTextureAsync(texture));
        void AddTriangle(short index0, short index1, short index2)
        {
            indices.Add((ushort)index0);
            indices.Add((ushort)index2);
            indices.Add((ushort)index1);
        }
        void AddVertex(Vector3 position, Vector3 normal, Vector2 uv)
            => vertices.Add(new Vertex(position, normal, uv));

        await component.LoadAsync(
            _ => true, null, AddVertex, AddTriangle, AddTexture,
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
