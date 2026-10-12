using System.Numerics;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Data.Core;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    private const long MaximumSceneCaptureShadowBytes = 64L * 1024 * 1024;

    private sealed class SceneShadowCopy(LightComponent light, int lightIndex)
    {
        internal readonly LightComponent Light = light;
        internal readonly int LightIndex = lightIndex;
        internal XRTexture? Source, Texture;
        internal int SourceHandle, Handle;
        internal WebGpuTextureResource Destination;
        internal ObjectCacheOwnership? Ownership;
        internal int[] Commands = [];
        internal AdvancedShadowRecord Record;
        internal Matrix4x4 Projection;
    }

    private sealed class PendingSceneLighting(IRuntimeRenderWorld world, Func<bool> isCurrent,
        LightComponent[] lights, SceneShadowCopy[] copies, int session, long generation, CancellationToken token)
    {
        internal readonly IRuntimeRenderWorld World = world;
        internal readonly Func<bool> IsCurrent = isCurrent;
        internal readonly LightComponent[] Lights = lights;
        internal readonly SceneShadowCopy[] Copies = copies;
        internal readonly int Session = session;
        internal readonly long Generation = generation;
        internal readonly CancellationToken Token = token;
        internal readonly TaskCompletionSource<SceneCaptureLightingSnapshot> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenRegistration Cancellation;
        internal uint RecordedFrame;
        internal float ElapsedTime;
        internal bool Submitted, CopiesDisposed;
        internal Exception? Failure;
    }

    private PendingSceneLighting? _sceneLighting;

    public Task<SceneCaptureLightingSnapshot> CaptureSceneLightingAsync(IRuntimeRenderWorld world, Func<bool> isCurrent,
        CancellationToken cancellationToken = default)
    {
        RequireReady();
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(isCurrent);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(Current, this) || _sceneLighting is not null)
            throw new InvalidOperationException("WebGPU.SceneCapture.LightingOwner: one shadow snapshot requires the renderer owner scope.");
        if (!isCurrent()) throw new OperationCanceledException("WebGPU.SceneCapture.LightingSourceChanged");
        var lights = world.Lights;
        LightComponent[] identities = new LightComponent[checked(lights.DynamicDirectionalLights.Count +
            lights.DynamicPointLights.Count + lights.DynamicSpotLights.Count)];
        List<SceneShadowCopy> copies = new(3);
        int lightIndex = 0;
        foreach (DirectionalLightComponent light in lights.DynamicDirectionalLights) CaptureLight(light);
        foreach (PointLightComponent light in lights.DynamicPointLights) CaptureLight(light);
        foreach (SpotLightComponent light in lights.DynamicSpotLights) CaptureLight(light);
        if (copies.Count == 0)
            return Task.FromResult(new SceneCaptureLightingSnapshot(this, _session, BackendGeneration, [], static () => { }, RuntimeEngine.ElapsedTime));
        PendingSceneLighting pending = new(world, isCurrent, identities, [.. copies], _session, BackendGeneration, cancellationToken);
        SetField(ref _sceneLighting, pending, publishNotifications: false);
        pending.Cancellation = cancellationToken.Register(() =>
        {
            if (_engineRecording || pending.Submitted) return;
            pending.Completion.TrySetCanceled(cancellationToken);
            ReleaseSceneLighting(pending, destroyCopies: true);
        });
        if (pending.Completion.Task.IsCompleted) pending.Cancellation.Dispose();
        return pending.Completion.Task;

        void CaptureLight(LightComponent light)
        {
            identities[lightIndex] = light;
            if (light.CastsShadows && light is not SpotLightComponent { ShadowFrustumRelevant: false })
            {
                if (copies.Count == 3)
                    throw new NotSupportedException("WebGPU.SceneCapture.ShadowCount: frozen lighting admits at most three authored shadow textures.");
                copies.Add(new(light, lightIndex));
            }
            lightIndex++;
        }
    }

    private bool RecordPendingSceneLighting()
    {
        PendingSceneLighting? pending = _sceneLighting;
        if (pending is null || pending.Submitted || pending.Completion.Task.IsCompleted) return true;
        try
        {
            RequireCurrentSceneLighting(pending);
            ulong outputGeneration = CurrentFrameOutput?.TargetGeneration
                ?? throw new InvalidOperationException("WebGPU.SceneCapture.ShadowOutputMissing");
            long bytes = 0;
            foreach (SceneShadowCopy copy in pending.Copies)
            {
                if (!copy.Light.TryGetBrowserShadowSnapshot(outputGeneration, this, out AdvancedShadowRecord record, out XRTexture? source) ||
                    source is null || !(WasShadowProducedInCurrentFrame(source) || CanPublishReusedShadow(copy.Light, source)))
                    return false;
                WebGpuTextureResource physical = WebGpuTextureResource.Resolve(this, source);
                if (physical.Samples != 1 || physical.Mips != 1 || physical.BaseMip != 0 || physical.BaseLayer != 0 ||
                    source is not (XRTexture2D or XRTextureCube) || physical.Layers != (source is XRTextureCube ? 6 : 1))
                    throw new NotSupportedException("WebGPU.SceneCapture.ShadowShape: copies require an exact single-mip 2D shadow or six-face cube.");
                int pixelBytes = physical.Format switch
                {
                    "r16float" or "depth16unorm" => 2,
                    "r32float" or "depth24plus" or "depth32float" => 4,
                    _ => throw new NotSupportedException("WebGPU.SceneCapture.ShadowFormat: the shadow encoding has no exact retained copy profile."),
                };
                bytes = checked(bytes + (long)physical.Width * physical.Height * physical.Layers * pixelBytes);
                if (bytes > MaximumSceneCaptureShadowBytes)
                    throw new NotSupportedException("WebGPU.SceneCapture.ShadowCapacity: frozen shadow textures exceed the 64 MiB request budget.");
                if (copy.SourceHandle != physical.Handle || !ReferenceEquals(copy.Source, source))
                {
                    DestroySceneShadowCopy(copy);
                    CreateSceneShadowCopy(copy, source, physical);
                }
                WebGpuTextureResource destination = WebGpuTextureResource.Resolve(this, copy.Texture!);
                if (destination.Width != physical.Width || destination.Height != physical.Height ||
                    destination.Layers != physical.Layers || destination.Format != physical.Format || destination.Samples != 1 || destination.Mips != 1)
                    throw new InvalidOperationException("WebGPU.SceneCapture.ShadowDestinationChanged");
                copy.Destination = destination;
                copy.Handle = destination.Handle;
                for (int layer = 0; layer < copy.Commands.Length; layer++)
                    if (copy.Commands[layer] == 0)
                        copy.Commands[layer] = PrepareEngineTextureCopy(copy, physical.Handle, destination.Handle,
                            0, 0, layer, checked((int)physical.Width), checked((int)physical.Height), sourceLayer: layer);
                copy.Record = record;
                copy.Projection = copy.Light switch
                {
                    DirectionalLightComponent directional => directional.ShadowCamera?.ViewProjectionMatrix
                        ?? throw new InvalidOperationException("WebGPU.SceneCapture.ShadowProjectionMissing"),
                    SpotLightComponent spot => spot.ShadowCamera?.ViewProjectionMatrix
                        ?? throw new InvalidOperationException("WebGPU.SceneCapture.ShadowProjectionMissing"),
                    PointLightComponent => Matrix4x4.Identity,
                    _ => throw new NotSupportedException("WebGPU.SceneCapture.ShadowLightUnsupported"),
                };
            }
            // No copy operation is recorded until every source and destination is
            // prepared. The normal packet orders these copies after source producers.
            foreach (SceneShadowCopy copy in pending.Copies)
            {
                foreach (int command in copy.Commands) RecordEngineCommands(command, []);
                MarkEngineTextureRecorded(copy.SourceHandle);
                ((IWebGpuProducedTexture)copy.Destination.Owner).MarkProduced();
            }
            pending.ElapsedTime = RuntimeEngine.ElapsedTime;
            pending.RecordedFrame = _engineFrameSequence;
            return true;
        }
        catch (RenderResourcePreparationPendingException) { return false; }
        catch (Exception error)
        {
            pending.Failure = error;
            MarkEngineDrawPending();
            return false;
        }
    }

    private void CreateSceneShadowCopy(SceneShadowCopy copy, XRTexture source, WebGpuTextureResource physical)
    {
        (EPixelInternalFormat Internal, EPixelFormat Pixel, EPixelType Type) encoding = physical.Format switch
        {
            "r16float" => (EPixelInternalFormat.R16f, EPixelFormat.Red, EPixelType.HalfFloat),
            "r32float" => (EPixelInternalFormat.R32f, EPixelFormat.Red, EPixelType.Float),
            "depth16unorm" => (EPixelInternalFormat.DepthComponent16, EPixelFormat.DepthComponent, EPixelType.UnsignedShort),
            "depth24plus" => (EPixelInternalFormat.DepthComponent24, EPixelFormat.DepthComponent, EPixelType.UnsignedInt),
            "depth32float" => (EPixelInternalFormat.DepthComponent32f, EPixelFormat.DepthComponent, EPixelType.Float),
            _ => throw new NotSupportedException("WebGPU.SceneCapture.ShadowFormat"),
        };
        using ObjectCachePublicationScope publication = XRObjectBase.BeginIndependentObjectCachePublication();
        using IDisposable suppression = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
        XRTexture texture;
        if (source is XRTexture2D image)
            texture = new XRTexture2D(physical.Width, physical.Height, encoding.Internal, encoding.Pixel, encoding.Type, allocateData: false)
            {
                SizedInternalFormat = image.SizedInternalFormat, Resizable = false, AutoGenerateMipmaps = false,
                MinFilter = image.MinFilter, MagFilter = image.MagFilter, UWrap = image.UWrap, VWrap = image.VWrap,
                EnableComparison = image.EnableComparison, CompareFunc = image.CompareFunc,
                MaxAnisotropy = image.MaxAnisotropy, MinLOD = 0, MaxLOD = 0,
            };
        else if (source is XRTextureCube cube)
            texture = new XRTextureCube(physical.Width, encoding.Internal, encoding.Pixel, encoding.Type, allocateData: false)
            {
                SizedInternalFormat = cube.SizedInternalFormat, Resizable = false, AutoGenerateMipmaps = false,
                MinFilter = cube.MinFilter, MagFilter = cube.MagFilter, UWrap = cube.UWrap, VWrap = cube.VWrap, WWrap = cube.WWrap,
                MinLOD = 0, MaxLOD = 0,
            };
        else throw new NotSupportedException("WebGPU.SceneCapture.ShadowTextureUnsupported");
        texture.Name = "Frozen scene capture shadow";
        copy.Ownership = publication.CompleteWithOwnership();
        copy.Texture = texture;
        copy.Source = source;
        copy.SourceHandle = physical.Handle;
        copy.Commands = new int[physical.Layers];
    }

    private void CompleteAcceptedSceneLighting(bool accepted)
    {
        PendingSceneLighting? pending = _sceneLighting;
        if (pending is null || pending.Submitted) return;
        if (pending.Failure is { } failure)
        {
            pending.Completion.TrySetException(failure);
            ReleaseSceneLighting(pending, destroyCopies: true);
            return;
        }
        if (pending.Token.IsCancellationRequested)
        {
            pending.Completion.TrySetCanceled(pending.Token);
            ReleaseSceneLighting(pending, destroyCopies: true);
            return;
        }
        if (!accepted || pending.RecordedFrame != _engineFrameSequence) return;
        pending.Submitted = true;
        _ = CompleteSceneLightingAsync(pending);
    }

    private async Task CompleteSceneLightingAsync(PendingSceneLighting pending)
    {
        bool transferred = false;
        try
        {
            await CompleteSubmittedWorkTicketAsync(pending.Token, pending.RecordedFrame);
            RequireCurrentSceneLighting(pending);
            SceneCaptureShadowSnapshot[] shadows = new SceneCaptureShadowSnapshot[pending.Copies.Length];
            for (int index = 0; index < shadows.Length; index++)
            {
                SceneShadowCopy copy = pending.Copies[index];
                WebGpuTextureResource physical = WebGpuTextureResource.Resolve(this, copy.Texture!);
                if (physical.Handle != copy.Handle || !((IWebGpuProducedTexture)physical.Owner).HasCommittedProduction)
                    throw new InvalidOperationException("WebGPU.SceneCapture.ShadowCopyObsolete: the copied shadow generation was replaced or not committed.");
                shadows[index] = new(copy.Light, copy.LightIndex, copy.Texture!, copy.Projection, copy.Record,
                    copy.Handle, GetShadowProductionTicket(copy.Texture!));
                ReleaseSceneShadowCommands(copy);
            }
            SceneCaptureLightingSnapshot snapshot = new(this, pending.Session, pending.Generation, shadows,
                () => DestroySceneLightingCopies(pending), pending.ElapsedTime);
            transferred = pending.Completion.TrySetResult(snapshot);
            if (!transferred) snapshot.Dispose();
        }
        catch (OperationCanceledException) { pending.Completion.TrySetCanceled(); }
        catch (Exception error) { pending.Completion.TrySetException(error); }
        finally { ReleaseSceneLighting(pending, destroyCopies: !transferred); }
    }

    private void RequireCurrentSceneLighting(PendingSceneLighting pending)
    {
        if (pending.Failure is { } failure) throw failure;
        pending.Token.ThrowIfCancellationRequested();
        RequireReadbackSession(pending.Session);
        if (pending.Generation != BackendGeneration || !pending.IsCurrent())
            throw new OperationCanceledException("WebGPU.SceneCapture.LightingSourceChanged");
        var lights = pending.World.Lights;
        if (pending.Lights.Length != lights.DynamicDirectionalLights.Count + lights.DynamicPointLights.Count + lights.DynamicSpotLights.Count)
            throw new OperationCanceledException("WebGPU.SceneCapture.LightingListChanged");
        int index = 0;
        for (int i = 0; i < lights.DynamicDirectionalLights.Count; i++)
            RequireSceneLightingIdentity(pending, index++, lights.DynamicDirectionalLights[i]);
        for (int i = 0; i < lights.DynamicPointLights.Count; i++)
            RequireSceneLightingIdentity(pending, index++, lights.DynamicPointLights[i]);
        for (int i = 0; i < lights.DynamicSpotLights.Count; i++)
            RequireSceneLightingIdentity(pending, index++, lights.DynamicSpotLights[i]);
        foreach (SceneShadowCopy copy in pending.Copies)
            if (!copy.Light.CastsShadows) throw new OperationCanceledException("WebGPU.SceneCapture.ShadowLightChanged");
    }

    private static void RequireSceneLightingIdentity(PendingSceneLighting pending, int index, LightComponent light)
    {
        if (light.IsDestroyed || !ReferenceEquals(pending.Lights[index], light))
            throw new OperationCanceledException("WebGPU.SceneCapture.LightingListChanged");
    }

    private void ReleaseSceneShadowCommands(SceneShadowCopy copy)
    {
        List<Exception>? failures = null;
        for (int index = _engineResourceRequests.Count - 1; index >= 0; index--)
        {
            if (index >= _engineResourceRequests.Count) continue;
            WebGpuResourceRequest request = _engineResourceRequests[index];
            if (!ReferenceEquals(request.Owner, copy)) continue;
            try { CancelEngineResourceRequest(request); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        for (int index = 0; index < copy.Commands.Length; index++)
        {
            int command = copy.Commands[index];
            copy.Commands[index] = 0;
            try { RetireEngineResourceAfterFrame(command); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        if (failures is not null)
            throw new AggregateException("WebGPU.SceneCapture.ShadowCommandReleaseFailed", failures);
    }

    private void DestroySceneShadowCopy(SceneShadowCopy copy)
    {
        List<Exception>? failures = null;
        try { ReleaseSceneShadowCommands(copy); }
        catch (Exception error) { (failures ??= []).Add(error); }
        IDisposable? ownership = copy.Ownership;
        copy.Ownership = null;
        copy.Source = copy.Texture = null;
        copy.SourceHandle = copy.Handle = 0;
        try { ownership?.Dispose(); }
        catch (Exception error) { (failures ??= []).Add(error); }
        if (failures is not null)
            throw new AggregateException("WebGPU.SceneCapture.ShadowCopyReleaseFailed", failures);
    }

    private void DestroySceneLightingCopies(PendingSceneLighting pending)
    {
        if (pending.CopiesDisposed) return;
        pending.CopiesDisposed = true;
        using var owner = EnterThreadCurrentScope(this);
        List<Exception>? failures = null;
        foreach (SceneShadowCopy copy in pending.Copies)
            try { DestroySceneShadowCopy(copy); }
            catch (Exception error) { (failures ??= []).Add(error); }
        if (failures is not null)
            throw new AggregateException("WebGPU.SceneCapture.LightingCopyReleaseFailed", failures);
    }

    private void ReleaseSceneLighting(PendingSceneLighting pending, bool destroyCopies)
    {
        pending.Cancellation.Dispose();
        if (ReferenceEquals(_sceneLighting, pending)) SetField(ref _sceneLighting, null, publishNotifications: false);
        if (destroyCopies) DestroySceneLightingCopies(pending);
    }

    private void CancelSceneLighting()
    {
        if (_sceneLighting is not { } pending) return;
        Exception failure = new InvalidOperationException("WebGPU.SceneCapture.LightingSessionEnded");
        pending.Failure ??= failure;
        if (_engineRecording || pending.Submitted) return;
        pending.Completion.TrySetException(failure);
        ReleaseSceneLighting(pending, destroyCopies: true);
    }
}
