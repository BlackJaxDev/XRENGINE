using XREngine.Components;
using XREngine.Components.Mesh.Shapes;
using XREngine.Components.Scene.Mesh;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.Resources;
using XREngine.Rendering.UI;
using XREngine.Scene.Components.Landscape;

namespace XREngine.Editor.Publishing;

internal sealed partial class BrowserMaterialCookProjection
{
    private readonly Dictionary<XRMaterial, HashSet<UIMaterialComponent>> _uiSolidConsumers = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<XRMaterial, string> _otherMaterialConsumers = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Inventories serialized render consumers before projecting any material, including aliases
    /// reached through game fields. Opaque custom payloads keep their existing independent graph contract.
    /// </summary>
    internal void PrepareMaterialConsumers(object root, string assetPath, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Registered roots bypass the projection in CreateCookedBlob. Their owner remains
        // authoritative; inspecting a different reflection graph would invent consumer edges.
        if (PublishedCookedAssetRegistry.IsRegistered(root.GetType()))
            return;
        HashSet<object> inspected = new(ReferenceEqualityComparer.Instance);
        RenderPipelineResourceProfile output = BrowserRenderPipelineOutputProfile.FromStartup(Engine.PersistentGameSettings);
        CookedBinarySerializationCallbacks callbacks = new() { OnSerializingValue = Inspect };
        // Use the same module/member selection as the actual ordinary asset cook. A tree-only
        // scan misses model LODs, pipeline references, and material consumers in game fields.
        _ = CookedBinarySerializer.ExecuteWithMemoryPackSuppressed(() => CookedBinarySerializer.CalculateSize(root, callbacks));
        foreach ((XRMaterial material, HashSet<UIMaterialComponent> consumers) in _uiSolidConsumers)
            if (material.Textures.Count == 0 && material.Shaders.Count != 0 &&
                _otherMaterialConsumers.TryGetValue(material, out string? other))
                throw UiSolidConsumerConflict(material, consumers, other);

        object? Inspect(object? value)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (value is not (UIRenderableComponent or RenderableComponent or SubMeshLOD or RenderableMesh or
                IRenderCommandMesh or XRMeshRenderer or DeferredDecalComponent or LandscapeComponent or
                CameraComponent or XRCamera or RenderPipeline or RenderPipelineRequirementsDeclaration or
                VPRC_PushMaterialOverride or XRMaterialFrameBuffer or IRenderable) || !inspected.Add(value))
                return value;
            string label = value is XRComponent component
                ? $"{assetPath}:{component.SceneNode?.GetPath() ?? component.GetType().Name}"
                : $"{assetPath}:{value.GetType().Name}";
            switch (value)
            {
                case UIMaterialComponent { Material: { } material } quad:
                    if (!_uiSolidConsumers.TryGetValue(material, out HashSet<UIMaterialComponent>? consumers))
                        _uiSolidConsumers.Add(material, consumers = new(ReferenceEqualityComparer.Instance));
                    consumers.Add(quad);
                    break;
                case UIRenderableComponent { Material: { } material }:
                    Other(material, label);
                    break;
                case ShapeMeshComponent shape:
                    Other(shape.Material, label);
                    InspectRenderable(shape, label);
                    break;
                case RenderableComponent renderable:
                    InspectRenderable(renderable, label);
                    break;
                case SubMeshLOD lod:
                    Other(lod.Material, label);
                    break;
                case RenderableMesh mesh:
                    Other(mesh.MaterialOverride, label + "/override");
                    foreach (RenderableMesh.RenderableLOD lod in mesh.LODs)
                        Other(lod.Renderer.Material, label);
                    break;
                case IRenderCommandMesh command:
                    Other(command.MaterialOverride, label + "/override");
                    Other(command.Mesh?.Material, label);
                    break;
                case XRMeshRenderer renderer:
                    Other(renderer.Material, label);
                    break;
                case DeferredDecalComponent decal:
                    Other(decal.Material, label);
                    break;
                case LandscapeComponent landscape:
                    Other(landscape.Material, label);
                    break;
                case CameraComponent camera:
                    if (camera.TryGetCreatedCamera(out XRCamera? created))
                        Other(created?.PostProcessMaterial, label + "/postprocess");
                    RenderPipeline? assigned = camera.RenderPipelineSource;
                    PipelinePostProcessState? authored;
                    if (assigned is not null)
                        camera.PostProcessStates.TryGetState(assigned.ID, out authored);
                    else
                        authored = camera.PostProcessStates.DefaultState;
                    RenderPipelineResourceProfile profile = output with
                    {
                        AntiAliasingMode = camera.AntiAliasingModeOverride ?? output.AntiAliasingMode,
                        MsaaSampleCount = camera.MsaaSampleCountOverride ?? output.MsaaSampleCount,
                        OutputHDR = camera.OutputHDROverride ?? output.OutputHDR,
                    };
                    Requirements(assigned?.CreateRequirements(RendererBackendId.WebGPU, profile, authored)
                        ?? DefaultRenderPipeline.CreateWebDefaultRequirements(profile, authored), label + "/pipeline");
                    break;
                case XRCamera camera:
                    Other(camera.PostProcessMaterial, label + "/postprocess");
                    break;
                case RenderPipeline pipeline:
                    Requirements(pipeline.CreateRequirements(RendererBackendId.WebGPU, output), label);
                    break;
                case RenderPipelineRequirementsDeclaration declaration:
                    foreach (XRMaterial material in declaration.Materials)
                        Other(material, label);
                    break;
                case VPRC_PushMaterialOverride command:
                    Other(command.Material, label);
                    break;
                case XRMaterialFrameBuffer framebuffer:
                    Other(framebuffer.Material, label);
                    break;
            }
            if (value is IRenderable renderableOwner && value is not UIRenderableComponent)
                foreach (var info in renderableOwner.RenderedObjects)
                    foreach (RenderCommand command in info.RenderCommands)
                        if (command is IRenderCommandMesh meshCommand)
                        {
                            Other(meshCommand.MaterialOverride, label + "/override");
                            Other(meshCommand.Mesh?.Material, label);
                        }
            return value;
        }

        void InspectRenderable(RenderableComponent component, string label)
        {
            foreach (RenderableMesh mesh in component.Meshes)
            {
                Other(mesh.MaterialOverride, label + "/override");
                foreach (RenderableMesh.RenderableLOD lod in mesh.LODs)
                    Other(lod.Renderer.Material, label);
            }
        }

        void Requirements(RenderPipelineRequirements requirements, string label)
        {
            foreach (XRMaterial material in requirements.Materials)
                Other(material, label);
        }

        void Other(XRMaterial? material, string label)
        {
            if (material is not null)
                _otherMaterialConsumers.TryAdd(material, label);
        }
    }

    private XRMaterial ProjectUiSolid(XRMaterial source)
    {
        HashSet<UIMaterialComponent> consumers = _uiSolidConsumers[source];
        if (_otherMaterialConsumers.TryGetValue(source, out string? other))
            throw UiSolidConsumerConflict(source, consumers, other);
        if (!UIMaterialComponent.TryGetWebGpuSolidCookProfile(source, consumers, out string? reason))
            throw new NotSupportedException($"BrowserCook.UiSolidProfileUnsupported: '{source.Name}': {reason}.");
        if (source.Shaders.Count != 1 || source.Shaders[0].CookedArtifactIdentity is not null)
            throw new NotSupportedException($"BrowserCook.UiSolidStageUnsupported: '{source.Name}' requires only the canonical desktop solid fragment without a separately authored cooked companion.");
        try
        {
            VerifyUnlitDesktopStage(source, EngineMaterialSemanticIdentity.UnlitColorV1);
        }
        catch (NotSupportedException error)
        {
            throw new NotSupportedException($"BrowserCook.UiSolidStageUnsupported: '{source.Name}': {error.Message}", error);
        }

        // Later roots may add consumers or callbacks to the same source. Revalidate the
        // complete current profile and stage before reusing its identity-preserving copy.
        if (_copies.TryGetValue(source, out XRMaterial? existing))
        {
            if (existing.GetType() != typeof(XRMaterial) || existing.HasEngineSemantic || existing.Shaders.Count != 0 ||
                existing.Textures.Count != 0 || existing.SurfaceTextureBindings.Length != 0 ||
                existing.ID != source.ID || existing.Name != source.Name || existing.RenderPass != source.RenderPass ||
                existing.AlphaCutoff != source.AlphaCutoff || existing.TransparencyMode != source.TransparencyMode ||
                existing.TransparentSortPriority != source.TransparentSortPriority ||
                !ReferenceEquals(existing.Parameters, source.Parameters) || !ReferenceEquals(existing.RenderOptions, source.RenderOptions))
                throw new InvalidDataException($"BrowserCook.UiSolidProjectionMismatch: '{source.Name}' no longer matches its plain source-free UI carrier and shared mutable aliases.");
            return existing;
        }

        using IDisposable wrappers = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
        using IDisposable cache = XRObjectBase.SuppressObjectCacheRegistration();
        using IDisposable target = RuntimeEngineMaterialConstructionServices.InstallForCurrentThread(EngineMaterialConstructionTarget.WebGpuCooked);
        XRMaterial copy = new();
        try
        {
            copy.AdoptPersistentID(source.ID);
            copy.Name = source.Name;
            copy.AlphaCutoff = source.AlphaCutoff;
            copy.TransparencyMode = source.TransparencyMode;
            copy.RenderPass = source.RenderPass;
            copy.TransparentSortPriority = source.TransparentSortPriority;
            // One copy is used for every material alias. Borrow mutable data only after
            // detached construction/state setup, retaining parameter and raster aliases too.
            copy.RenderOptions = source.RenderOptions;
            copy.Parameters = source.Parameters;
            _copies.Add(source, copy);
            return copy;
        }
        catch
        {
            Release(copy);
            throw;
        }
    }

    private static NotSupportedException UiSolidConsumerConflict(XRMaterial source,
        IEnumerable<UIMaterialComponent> consumers, string other)
    {
        string ui = string.Join(", ", consumers.Select(static component => component.SceneNode?.GetPath() ?? component.GetType().Name)
            .Order(StringComparer.Ordinal));
        NotSupportedException error = new($"BrowserCook.UiSolidConsumerConflict: material '{source.Name}' ({source.ID}) is shared by UI '{ui}' and non-UI consumer '{other}'. The UI-only projection cannot remove a shared scene or pipeline stage; author distinct materials explicitly while sharing parameters where needed.");
        error.Data["BrowserCook.Material"] = source.Name;
        error.Data["BrowserCook.Pass"] = "screen-ui";
        return error;
    }
}
