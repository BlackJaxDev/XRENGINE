using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

public partial class XRMaterial
{
    private XRShader RecoverCanonicalUberFragmentShader(XRShader fragmentShader, string? shaderPath)
    {
        OperationCanceledException? staleFailure = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            long generation = RuntimeSourcePublicationServices.Generation;
            using RuntimeAssetReadLease read = RuntimeAssetReadServices.Capture();
            ShaderSourceProviderOwner owner = ShaderSourceResolver.CaptureOwner();
            IRuntimeShaderServices? services = RuntimeShaderServices.Current;
            int serviceVersion = RuntimeShaderServices.ServiceVersion;
            int assetCacheVersion = services?.ShaderAssetCacheVersion ?? 0;
            try
            {
                if (!string.IsNullOrWhiteSpace(shaderPath) &&
                    ShaderSourceResolver.TryReadCanonicalSource(shaderPath, owner, read, out byte[] bytes))
                {
                    using IDisposable reservation = RuntimeSourcePublicationServices.BeginPublication(generation);
                    using IDisposable? assetPublication = services?.BeginSourcePublication(assetCacheVersion);
                    EnsureCanonicalSourceOwner(owner, read, services, serviceVersion, assetCacheVersion);

                    // Failed reads have allocated no engine objects. The successful
                    // source belongs to this cache, not an enclosing caller's batch.
                    using RenderObjectPublicationScope publication = GenericRenderObject.BeginIndependentPublication();
                    XRShader canonical = new(fragmentShader.Type)
                    {
                        Name = fragmentShader.Name,
                        GenerateAsync = fragmentShader.GenerateAsync,
                    };
                    canonical.Source.FilePath = shaderPath;
                    canonical.Source.LoadTextBytes(shaderPath, bytes, read);
                    XRShader? previous = _uberCanonicalFragmentShader;
                    publication.Complete(
                        () =>
                        {
                            ObjectDisposedException.ThrowIf(IsDestroyed || IsMaterialTeardownRequested, this);
                            ShaderSourceResolver.RecordCanonicalSourceOwner(canonical, generation,
                                serviceVersion, assetCacheVersion);
                            SetField(ref _uberCanonicalFragmentShader, canonical, publishNotifications: false);
                        },
                        () =>
                        {
                            if (ReferenceEquals(_uberCanonicalFragmentShader, canonical))
                                SetField(ref _uberCanonicalFragmentShader, previous, publishNotifications: false);
                        },
                        static () => { });
                    return canonical;
                }

                if (!RuntimeSourcePublicationServices.IsCurrent(generation))
                    throw new OperationCanceledException("ShaderSource.StaleOwner: the host changed before canonical fallback selection.");
                XRShader fallback;
                try { fallback = ShaderHelper.UberFragForward(); }
                catch (NotSupportedException error) when (!ShaderSourceResolver.CanAccessHostShaderFiles)
                {
                    throw new NotSupportedException(
                        "UberVariant.CanonicalSourceUnavailable: the canonical Uber shader must be published in the browser shader catalog before a generated material can be rehydrated.", error);
                }
                using (RuntimeSourcePublicationServices.BeginPublication(generation))
                using (services?.BeginSourcePublication(assetCacheVersion))
                {
                    EnsureCanonicalSourceOwner(owner, read, services, serviceVersion, assetCacheVersion);
                    if (fallback.IsDestroyed)
                        throw new OperationCanceledException("ShaderSource.StaleAssetOwner: the canonical fallback was destroyed before publication.");
                    ObjectDisposedException.ThrowIf(IsDestroyed || IsMaterialTeardownRequested, this);
                    ShaderSourceResolver.RecordCanonicalSourceOwner(fallback, generation,
                        serviceVersion, assetCacheVersion);
                    SetField(ref _uberCanonicalFragmentShader, fallback, publishNotifications: false);
                    return fallback;
                }
            }
            catch (OperationCanceledException error) { staleFailure = error; }
        }
        throw new OperationCanceledException(
            "UberVariant.CanonicalSourceChanged: source owners changed repeatedly; retry canonical shader recovery after host installation settles.", staleFailure);
    }

    private static void EnsureCanonicalSourceOwner(ShaderSourceProviderOwner owner, RuntimeAssetReadLease read,
        IRuntimeShaderServices? services, int serviceVersion, int assetCacheVersion)
    {
        read.EnsureCurrent();
        if (!ShaderSourceResolver.IsOwnerCurrent(owner) ||
            !ReferenceEquals(RuntimeShaderServices.Current, services) ||
            RuntimeShaderServices.ServiceVersion != serviceVersion ||
            (services?.ShaderAssetCacheVersion ?? 0) != assetCacheVersion)
            throw new OperationCanceledException("ShaderSource.StaleOwner: the canonical source participants changed during preparation.");
    }
}
