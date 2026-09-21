using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Core;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.GI.DDGI;

namespace XREngine.Editor.Mcp;

public sealed partial class EditorMcpActions
{
    /// <summary>
    /// Captures the selected viewport's converged DDGI resources at its post-render
    /// boundary, then saves and verifies the resulting baked asset off the render thread.
    /// </summary>
    [XRMcp(Name = "bake_ddgi_volume", Permission = McpPermissionLevel.Mutate, PermissionReason = "Captures the selected viewport's DDGI GPU state and writes a baked asset file.")]
    [Description("Bake the selected viewport's converged DDGI state to output_path. The volume is not changed or automatically assigned to the new asset.")]
    public static async Task<McpToolResponse> BakeDdgiVolumeAsync(
        McpToolContext context,
        [McpName("output_path"), Description("Required destination path for the binary DDGI baked asset.")] string outputPath,
        [McpName("camera_node_id"), Description("Optional camera node ID to target.")] string? cameraNodeId = null,
        [McpName("window_index"), Description("Optional window index to target.")] int windowIndex = 0,
        [McpName("viewport_index"), Description("Optional viewport index to target.")] int viewportIndex = 0,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            return new McpToolResponse("output_path is required.", isError: true);

        string fullOutputPath;
        try
        {
            fullOutputPath = Path.GetFullPath(outputPath);
        }
        catch (Exception exception)
        {
            return new McpToolResponse($"output_path is invalid: {exception.Message}", isError: true);
        }

        XRViewport? viewport = ResolveViewport(
            context.World,
            cameraNodeId,
            null,
            windowIndex,
            viewportIndex,
            out string? viewportError);
        if (viewport is null)
            return new McpToolResponse(viewportError ?? "No viewport found for the requested DDGI bake.", isError: true);

        XRWindow? window = viewport.Window
            ?? RuntimeEngine.Windows.FirstOrDefault(candidate => candidate.Viewports.Contains(viewport))
            ?? RuntimeEngine.Windows.FirstOrDefault();
        if (window is null)
            return new McpToolResponse("No window found for the selected viewport.", isError: true);

        XRRenderPipelineInstance pipelineInstance = ResolveSelectedPipelineInstance(viewport, vrEye: null);
        string volumeName = Path.GetFileNameWithoutExtension(fullOutputPath);
        if (string.IsNullOrWhiteSpace(volumeName))
            volumeName = "DDGI";

        try
        {
            DDGIBakedAsset asset = await CaptureAfterWindowRenderAsync<DDGIBakedAsset>(
                window,
                (_, completion) =>
                {
                    if (DDGIBaking.TryCaptureFromPipeline(pipelineInstance, volumeName, out DDGIBakedAsset? captured, out string failure) &&
                        captured is not null)
                    {
                        completion.TrySetResult(captured);
                        return;
                    }

                    completion.TrySetException(new InvalidOperationException(failure));
                },
                token).ConfigureAwait(false);

            var verification = await Task.Run(() => SaveAndVerifyDdgiBake(asset, fullOutputPath, token), token).ConfigureAwait(false);
            return new McpToolResponse(
                $"Baked DDGI volume to '{fullOutputPath}' and verified the saved payload.",
                new
                {
                    path = fullOutputPath,
                    volume_name = asset.VolumeName,
                    probe_count = asset.Probes.Length,
                    irradiance_layer_count = asset.IrradianceAtlasLayers.Length,
                    visibility_layer_count = asset.VisibilityAtlasLayers.Length,
                    sha256 = verification.sha256,
                    roundtrip_verified = verification.roundtripVerified,
                    byte_length = verification.byteLength,
                });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new McpToolResponse("DDGI bake was cancelled.", isError: true);
        }
        catch (Exception exception)
        {
            return new McpToolResponse($"Failed to bake DDGI volume: {exception.Message}", isError: true);
        }
    }

    private static (string sha256, bool roundtripVerified, long byteLength) SaveAndVerifyDdgiBake(
        DDGIBakedAsset asset,
        string outputPath,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        asset.Save(outputPath);
        token.ThrowIfCancellationRequested();

        DDGIBakedAsset loaded = DDGIBakedAsset.Load(outputPath);
        bool probesMatch = MemoryMarshal.AsBytes(asset.Probes.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(loaded.Probes.AsSpan()));
        bool irradianceMatches = ByteLayersEqual(asset.IrradianceAtlasLayers, loaded.IrradianceAtlasLayers);
        bool visibilityMatches = ByteLayersEqual(asset.VisibilityAtlasLayers, loaded.VisibilityAtlasLayers);
        if (!probesMatch || !irradianceMatches || !visibilityMatches)
            throw new InvalidDataException("DDGI baked asset round-trip changed probe records or atlas payload bytes.");

        token.ThrowIfCancellationRequested();
        using FileStream serializedFile = File.OpenRead(outputPath);
        long byteLength = serializedFile.Length;
        string sha256 = Convert.ToHexString(SHA256.HashData(serializedFile));
        return (sha256, true, byteLength);
    }

    private static bool ByteLayersEqual(byte[][] expected, byte[][] actual)
    {
        if (expected.Length != actual.Length)
            return false;

        for (int index = 0; index < expected.Length; index++)
            if (!expected[index].AsSpan().SequenceEqual(actual[index]))
                return false;

        return true;
    }
}
