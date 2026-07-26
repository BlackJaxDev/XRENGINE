using Microsoft.Win32;
using System.Runtime.Versioning;
using System.Security;
using System.Text.Json;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Discovers Windows Vulkan implicit-layer registrations without loading or calling the Vulkan loader.
/// </summary>
internal static class VulkanImplicitLayerDiscovery
{
    private const string ImplicitLayersRegistryPath = @"SOFTWARE\Khronos\Vulkan\ImplicitLayers";

    /// <summary>
    /// Attempts to find an enabled implicit-layer manifest that declares <paramref name="layerName"/>.
    /// </summary>
    internal static bool TryFindRegisteredLayer(string layerName, out string? manifestPath)
    {
        manifestPath = null;
        if (!OperatingSystem.IsWindows())
            return false;

        return TryFindRegisteredWindowsLayer(layerName, out manifestPath);
    }

    /// <summary>
    /// Returns whether a Vulkan layer-manifest document declares <paramref name="layerName"/>.
    /// </summary>
    internal static bool ManifestDefinesLayer(string json, string layerName)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(layerName))
            return false;

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });

            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            if (root.TryGetProperty("layer", out JsonElement layer) &&
                LayerElementHasName(layer, layerName))
            {
                return true;
            }

            if (!root.TryGetProperty("layers", out JsonElement layers) ||
                layers.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (JsonElement entry in layers.EnumerateArray())
            {
                if (LayerElementHasName(entry, layerName))
                    return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    [SupportedOSPlatform("windows")]
    private static bool TryFindRegisteredWindowsLayer(string layerName, out string? manifestPath)
    {
        manifestPath = null;
        RegistryHive[] hives = [RegistryHive.LocalMachine, RegistryHive.CurrentUser];
        RegistryView[] views = [RegistryView.Registry64, RegistryView.Registry32];

        foreach (RegistryHive hive in hives)
        {
            foreach (RegistryView view in views)
            {
                if (TryFindRegisteredWindowsLayer(hive, view, layerName, out manifestPath))
                    return true;
            }
        }

        return false;
    }

    [SupportedOSPlatform("windows")]
    private static bool TryFindRegisteredWindowsLayer(
        RegistryHive hive,
        RegistryView view,
        string layerName,
        out string? manifestPath)
    {
        manifestPath = null;

        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
            using RegistryKey? implicitLayers = baseKey.OpenSubKey(ImplicitLayersRegistryPath);
            if (implicitLayers is null)
                return false;

            foreach (string registeredManifestPath in implicitLayers.GetValueNames())
            {
                if (implicitLayers.GetValue(registeredManifestPath) is not int registrationState ||
                    registrationState != 0)
                {
                    continue;
                }

                string candidatePath = Environment.ExpandEnvironmentVariables(
                    registeredManifestPath.Trim().Trim('"'));
                if (!File.Exists(candidatePath))
                    continue;

                string json = File.ReadAllText(candidatePath);
                if (!ManifestDefinesLayer(json, layerName))
                    continue;

                manifestPath = candidatePath;
                return true;
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }

        return false;
    }

    private static bool LayerElementHasName(JsonElement layer, string expectedName)
        => layer.ValueKind == JsonValueKind.Object &&
            layer.TryGetProperty("name", out JsonElement name) &&
            name.ValueKind == JsonValueKind.String &&
            string.Equals(name.GetString(), expectedName, StringComparison.Ordinal);
}
