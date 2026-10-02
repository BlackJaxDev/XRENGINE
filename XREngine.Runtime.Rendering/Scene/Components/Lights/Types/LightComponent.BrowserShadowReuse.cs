using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Components.Lights;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Components.Capture.Lights.Types;

public abstract partial class LightComponent
{
    private XRMaterialFrameBuffer? _browserCachedShadowMap;
    private XRTexture? _browserCachedShadowReceiver;
    private IBrowserShadowReuseCapability? _browserCachedShadowRenderer;
    private ulong _browserCachedShadowProductionTicket;
    private ulong _browserCachedShadowSignature;
    private ulong _browserCachedShadowOutputGeneration;
    private ulong _browserCachedShadowFrame;

    private void ResetBrowserShadowReuse()
    {
        _browserCachedShadowMap = null;
        _browserCachedShadowReceiver = null;
        _browserCachedShadowRenderer = null;
        _browserCachedShadowProductionTicket = 0;
        _browserCachedShadowSignature = 0;
        _browserCachedShadowOutputGeneration = 0;
        _browserCachedShadowFrame = 0;
    }

    /// <summary>Returns the currently owned receiver image, if this light has an admitted browser shadow.</summary>
    private XRTexture? GetBrowserShadowReceiver()
        => this switch
        {
            DirectionalLightComponent directional => directional.PrimaryShadowReceiverTexture,
            SpotLightComponent spot => spot.CookedShadowReceiverTexture,
            PointLightComponent point => point.CookedShadowReceiverTexture,
            _ => null,
        };

    /// <summary>Describes the published caster content and whether its silhouette can change without a revision.</summary>
    internal readonly record struct BrowserShadowCasterState(ulong Signature, bool RequiresRefresh);

    internal virtual BrowserShadowCasterState GetBrowserShadowCasterState() => new(0, false);

    internal static BrowserShadowCasterState GetBrowserShadowViewportCasterState(XRViewport? viewport)
    {
        if (viewport is null)
            return new(0, false);

        RenderCommandCollection commands = viewport.RenderPipelineInstance.MeshRenderCommands;
        ulong hash = commands.ShadowCasterCommandSetSignature;
        bool requiresRefresh = false;
        ReadOnlySpan<int> passes = stackalloc int[]
        {
            (int)EDefaultRenderPass.PreRender,
            (int)EDefaultRenderPass.OpaqueDeferred,
            (int)EDefaultRenderPass.OpaqueForward,
            (int)EDefaultRenderPass.MaskedForward,
            (int)EDefaultRenderPass.PostRender,
        };
        for (int passIndex = 0; passIndex < passes.Length; passIndex++)
        {
            if (!commands.TryGetRenderingPassCommands(passes[passIndex], out IReadOnlyCollection<RenderCommand>? published) ||
                published is null)
                continue;
            if (published is not ICollection<RenderCommand> indexed)
                return new(hash, true);
            for (int commandIndex = 0; commandIndex < indexed.Count; commandIndex++)
            {
                RenderCommand command = RenderCommandCollection.GetCommandAt(indexed, commandIndex);
                if (command is not IRenderCommandMesh)
                {
                    requiresRefresh = true;
                    continue;
                }
                if (command is not RenderCommandMesh3D meshCommand)
                    return new(hash, true);

                AdvancedMeshRenderSnapshot snapshot = meshCommand.CaptureAdvancedPreparationSnapshot();
                XRMeshRenderer? renderer = snapshot.Renderer;
                if (snapshot.RenderOptionsOverride is not null)
                    requiresRefresh = true;
                if (renderer is null)
                {
                    requiresRefresh = true;
                    continue;
                }
                if (renderer.MeshDeformEnabled || renderer.Bones is { Length: > 0 } ||
                    renderer.HasActiveBlendshapes || snapshot.Instances != 1)
                {
                    requiresRefresh = true;
                    continue;
                }

                int primitiveCount = Math.Max(1, renderer.Submeshes.Count);
                for (int primitive = 0; primitive < primitiveCount; primitive++)
                {
                    if (!renderer.TryGetMesh(primitive, out XRMesh? mesh, out XRMaterial? material) || mesh is null)
                    {
                        requiresRefresh = true;
                        continue;
                    }
                    hash = MixBrowserShadowValue(hash, unchecked((uint)RuntimeHelpers.GetHashCode(mesh)));
                    hash = MixBrowserShadowValue(hash, unchecked((ulong)mesh.GeometryRevision));
                    if (mesh.HasSkinning || mesh.BlendshapeCount != 0)
                        requiresRefresh = true;

                    material = snapshot.MaterialOverride ?? material;
                    if (material is null)
                    {
                        requiresRefresh = true;
                        continue;
                    }
                    hash = MixBrowserShadowValue(hash, unchecked((uint)RuntimeHelpers.GetHashCode(material)));
                    hash = MixBrowserShadowValue(hash, material.BindingValueVersion);
                    hash = MixBrowserShadowValue(hash, material.BindingResourceVersion);
                    hash = MixBrowserShadowValue(hash, unchecked((ulong)material.ShaderStateRevision));
                    hash = MixBrowserShadowValue(hash, unchecked((ulong)material.UberStateRevision));
                    hash = MixBrowserShadowValue(hash, unchecked((uint)material.EngineSemantic.Semantic));
                    hash = MixBrowserShadowValue(hash, unchecked((uint)material.EngineSemantic.Version));
                    hash = MixBrowserShadowValue(hash, unchecked((uint)material.GetEffectiveTransparencyMode()));
                    hash = MixBrowserShadowValue(hash, BitConverter.SingleToUInt32Bits(material.AlphaCutoff));
                    if (material.RenderOptions is { } options)
                    {
                        hash = MixBrowserShadowValue(hash, unchecked((uint)options.CullMode));
                        hash = MixBrowserShadowValue(hash, unchecked((uint)options.Winding));
                        hash = MixBrowserShadowValue(hash, unchecked((uint)options.AlphaToCoverage));
                        if (options.DepthTest is { } depth)
                        {
                            hash = MixBrowserShadowValue(hash, unchecked((uint)depth.Enabled));
                            hash = MixBrowserShadowValue(hash, unchecked((uint)depth.Function));
                            hash = MixBrowserShadowValue(hash, depth.UpdateDepth ? 1u : 0u);
                        }
                    }
                    if (material.EngineSemantic != EngineMaterialSemanticIdentity.StandardLitColorV1 ||
                        material.GetEffectiveTransparencyMode() != ETransparencyMode.Opaque)
                        requiresRefresh = true;
                }
            }
        }
        return new(hash, requiresRefresh);
    }

    /// <summary>Returns the producer projection; receiver uniforms must agree before an image is reused.</summary>
    internal virtual ulong GetBrowserShadowProjectionSignature() => 0;

    protected static ulong MixBrowserShadowMatrix(ulong hash, in Matrix4x4 matrix)
    {
        Matrix4x4 copy = matrix;
        ReadOnlySpan<float> values = System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(ref copy.M11, 16);
        for (int i = 0; i < values.Length; i++)
            hash = MixBrowserShadowValue(hash, BitConverter.SingleToUInt32Bits(values[i]));
        return hash;
    }

    protected static ulong MixBrowserShadowValue(ulong hash, ulong value)
        => unchecked((hash ^ value) * 1099511628211UL);

    private ulong GetBrowserShadowSignature(ulong casterMembershipRevision, out bool requiresRefresh)
    {
        BrowserShadowCasterState caster = GetBrowserShadowCasterState();
        requiresRefresh = caster.RequiresRefresh;
        ulong hash = 14695981039346656037UL;
        hash = MixBrowserShadowValue(hash, BindingGeneration);
        hash = MixBrowserShadowValue(hash, casterMembershipRevision);
        hash = MixBrowserShadowValue(hash, caster.Signature);
        hash = MixBrowserShadowValue(hash, GetBrowserShadowProjectionSignature());
        hash = MixBrowserShadowValue(hash, ShadowMapResolutionWidth);
        hash = MixBrowserShadowValue(hash, ShadowMapResolutionHeight);
        return hash;
    }

    /// <summary>Refreshes on ownership, scene, projection or output changes; otherwise observes the selected cadence.</summary>
    internal bool ShouldRenderBrowserShadow(ulong frameId, ulong outputGeneration, ulong casterMembershipRevision,
        int updateInterval, IBrowserShadowReuseCapability reuse)
    {
        if (updateInterval <= 1 || outputGeneration == 0 || frameId < _browserCachedShadowFrame ||
            frameId - _browserCachedShadowFrame >= (ulong)updateInterval)
            return true;

        XRTexture? receiver = GetBrowserShadowReceiver();
        ulong signature = GetBrowserShadowSignature(casterMembershipRevision, out bool requiresRefresh);
        bool refresh = ShadowMap is null || receiver is null ||
            requiresRefresh ||
            !ReferenceEquals(_browserCachedShadowMap, ShadowMap) ||
            !ReferenceEquals(_browserCachedShadowReceiver, receiver) ||
            !ReferenceEquals(_browserCachedShadowRenderer, reuse) ||
            _browserCachedShadowOutputGeneration != outputGeneration ||
            _browserCachedShadowSignature != signature ||
            !reuse.CanReuseCommittedShadow(receiver) ||
            _browserCachedShadowProductionTicket != reuse.GetShadowProductionTicket(receiver);
        if (!refresh)
            reuse.AuthorizeShadowReuse(this, receiver!);
        return refresh;
    }

    /// <summary>Records the attempted producer state; uncommitted images remain ineligible for reuse.</summary>
    internal void RecordBrowserShadowRender(ulong frameId, ulong outputGeneration, ulong casterMembershipRevision,
        IBrowserShadowReuseCapability reuse)
    {
        XRTexture? receiver = GetBrowserShadowReceiver();
        if (ShadowMap is null || receiver is null || !reuse.WasShadowProducedInCurrentFrame(receiver))
        {
            ResetBrowserShadowReuse();
            return;
        }
        ulong signature = GetBrowserShadowSignature(casterMembershipRevision, out bool requiresRefresh);
        if (requiresRefresh)
        {
            ResetBrowserShadowReuse();
            return;
        }
        _browserCachedShadowMap = ShadowMap;
        _browserCachedShadowReceiver = receiver;
        _browserCachedShadowRenderer = reuse;
        _browserCachedShadowProductionTicket = reuse.GetShadowProductionTicket(receiver);
        _browserCachedShadowSignature = signature;
        _browserCachedShadowOutputGeneration = outputGeneration;
        _browserCachedShadowFrame = frameId;
    }
}
