using XREngine.Components;
using XREngine.Components.Capture;
using XREngine.Components.Lights;
using XREngine.Components.Scene.Environment;
using XREngine.Components.Scene.Mesh;
using XREngine.Components.Mesh.Shapes;
using XREngine.Components.VR;
using XREngine.Data.Components.Scene;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor.Publishing;

/// <summary>Rejects unsupported authored behavior before browser content is activated.</summary>
internal static class BrowserWorldCapabilityAudit
{
    internal static IReadOnlyList<ShaderProgramArtifact> Inspect(XRWorld world, IShaderProgramArtifactResolver? resolver, CancellationToken cancellationToken)
    {
        Dictionary<string, ShaderProgramArtifact> artifacts = new(StringComparer.Ordinal);
        BrowserShadowCapabilityAudit shadows = new(resolver);
        HashSet<SceneNode> visited = new(ReferenceEqualityComparer.Instance);
        foreach (XRScene scene in world.Scenes)
            foreach (SceneNode root in scene.RootNodes)
                Visit(root, 0);
        shadows.Complete();
        if (resolver is BrowserShaderArtifactSource shaderSource)
        {
            foreach (EngineMaterialVariantEntry variant in shaderSource.MaterialVariants)
            {
                if (!shaderSource.TryResolve(variant.DescriptorIdentity, variant.Key.Target, out ShaderProgramArtifact? artifact))
                    throw new InvalidDataException($"BrowserCook.VariantMissing: '{variant.Key}'.");
                artifacts.TryAdd(artifact.Identity, artifact);
            }
            foreach ((string pass, string identity) in shaderSource.PipelineArtifacts)
            {
                if (!shaderSource.TryResolve(identity, ShaderCompileTarget.WebGPUWgsl, out ShaderProgramArtifact? artifact))
                    throw new InvalidDataException($"BrowserCook.PipelineArtifactMissing: '{pass}' descriptor is unavailable.");
                artifacts.TryAdd(artifact.Identity, artifact);
            }
            foreach ((string kernel, string identity) in shaderSource.ComputeArtifacts)
            {
                if (!shaderSource.TryResolve(identity, ShaderCompileTarget.WebGPUWgsl, out ShaderProgramArtifact? artifact))
                    throw new InvalidDataException($"BrowserCook.ComputeArtifactMissing: '{kernel}' descriptor is unavailable.");
                artifacts.TryAdd(artifact.Identity, artifact);
            }
        }
        return artifacts.Values.OrderBy(artifact => artifact.Identity, StringComparer.Ordinal).ToArray();

        void Visit(SceneNode node, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = node.GetPath();
            if (depth >= 128 || !visited.Add(node) || visited.Count > 8192)
                throw new NotSupportedException($"BrowserCook.SceneGraphUnsupported: '{path}' exceeds the hierarchy budget or shares a scene node.");
            if (RequiresVrTransform(node.Transform.GetType()))
                throw new NotSupportedException($"BrowserCook.XrUnsupported: '{path}' transform '{node.Transform.GetType().FullName}' requires a browser XR service that is not enabled.");
            foreach (XRComponent component in node.Components)
            {
                BrowserPhysicsCapabilityAudit.Inspect(component, path);
                shadows.Inspect(component, path);
                if (component is SceneCaptureComponentBase or AdvancedOffscreenTextureCaptureComponent or
                    MirrorCaptureComponent or LightProbeGridSpawnerComponent)
                    throw new NotSupportedException($"BrowserCook.EnvironmentCaptureUnsupported: '{path}' component '{component.GetType().FullName}' requires an explicit cooked capture and probe/IBL path.");
                if (component is AtmosphericScatteringComponent)
                    throw new NotSupportedException($"BrowserCook.AtmosphereUnsupported: '{path}' requires the planetary-atmosphere and aerial-perspective pass family; the procedural SkyboxComponent is a separate admitted profile.");
                if (component is VRHeadsetComponent or VRDeviceModelComponent or VRPlayerCharacterComponent
                    or VRTrackerCollectionComponent or VRHeightScaleComponent or VRPlayerInputSet)
                    throw new NotSupportedException($"BrowserCook.XrUnsupported: '{path}' component '{component.GetType().FullName}' requires a browser XR service that is not enabled.");
                string? assembly = component.GetType().Assembly.GetName().Name;
                if (assembly is "XREngine.Runtime.Physics.PhysX" or "XREngine.Runtime.Platform.Desktop"
                    or "XREngine.Runtime.VR" or "XREngine.Runtime.UI.Skia" or "XREngine.Runtime.UI.Rive"
                    or "XREngine.Audio.SteamAudio" or "XREngine.Audio.Audio2Face")
                    throw new NotSupportedException($"BrowserCook.ComponentUnsupported: '{path}' component '{component.GetType().FullName}' requires desktop service '{assembly}'.");
                if (component is ModelComponent { Model: { } model })
                    foreach (var mesh in model.Meshes)
                        foreach (var lod in mesh.LODs)
                        {
                            if (lod.Mesh is { } geometry && (geometry.HasSkinning || geometry.HasBlendshapes) &&
                                (resolver is not BrowserShaderArtifactSource computeSource ||
                                !computeSource.ComputeArtifacts.ContainsKey(WebComputeArtifactCatalog.PackedSkinningKernel)))
                                throw new NotSupportedException($"BrowserCook.ComputeArtifactMissing: '{path}' mesh '{mesh.Name}' requires packed-skinning in the project shader manifest.");
                            InspectMaterial(lod.Material, path, mesh.Name);
                        }
                else if (component is SkyboxComponent sky)
                {
                    try { sky.ValidateWebGpuProfile(); }
                    catch (NotSupportedException error)
                    {
                        throw new NotSupportedException($"BrowserCook.SkyboxUnsupported: '{path}': {error.Message}", error);
                    }
                    EngineMaterialVariantKey key = new(sky.GetWebGpuSemantic(), ShaderCompileTarget.WebGPUWgsl,
                        "background", "fullscreen-sky-v1", "linear-hdr-v1");
                    if (resolver is not BrowserShaderArtifactSource source ||
                        !source.MaterialVariants.Any(variant => variant.Key == key))
                        throw new NotSupportedException($"BrowserCook.SkyboxVariantMissing: '{path}' requires '{key}' in the project shader manifest.");
                }
                else if (component is ShapeMeshComponent shape)
                    InspectMaterial(shape.Material, path, shape.GetType().Name);
                else if (component is UICanvasComponent canvas)
                {
                    if (canvas.CanvasTransform.DrawSpace != ECanvasDrawSpace.Screen || canvas.StrictOneByOneRenderCalls)
                        throw new NotSupportedException($"BrowserCook.UiCanvasUnsupported: '{path}' requires a batched screen-space canvas.");
                }
                else if (component is UITextComponent text)
                {
                    if (!text.SupportsBatchedRendering || text.Font is { AtlasType: not EFontAtlasType.Bitmap })
                        throw new NotSupportedException($"BrowserCook.UiTextUnsupported: '{path}' requires unclipped bitmap text without custom stages or glyph rotation.");
                    RequireUiVariant(EngineMaterialSemanticIdentity.UITextBatchedBitmapV1,
                        "instanced-ui-bitmap-text-v1", path);
                }
                else if (component is UIMaterialComponent quad)
                {
                    if (!quad.SupportsBatchedRendering)
                        throw new NotSupportedException($"BrowserCook.UiMaterialUnsupported: '{path}' requires the source-free solid-color screen UI profile.");
                    RequireUiVariant(EngineMaterialSemanticIdentity.UIQuadBatchedV1,
                        "instanced-ui-quad-v1", path);
                }
                else if (component is UIRenderableComponent)
                    throw new NotSupportedException($"BrowserCook.UiComponentUnsupported: '{path}' component '{component.GetType().FullName}' has no cooked screen UI profile.");
            }
            foreach (var transform in node.Transform.Children)
                if (transform.SceneNode is SceneNode child)
                    Visit(child, depth + 1);
        }

        void RequireUiVariant(EngineMaterialSemanticIdentity semantic, string profile, string path)
        {
            EngineMaterialVariantKey key = new(semantic, ShaderCompileTarget.WebGPUWgsl,
                "screen-ui", profile, "display-rgba-v1");
            if (resolver is not BrowserShaderArtifactSource source ||
                !source.MaterialVariants.Any(variant => variant.Key == key))
                throw new NotSupportedException($"BrowserCook.UiVariantMissing: '{path}' requires '{key}' in the project shader manifest.");
        }

        void InspectMaterial(XRMaterial? material, string path, string? meshName)
        {
            shadows.InspectMaterial(material);
            if (material is null)
                throw new InvalidDataException($"BrowserCook.MaterialMissing: '{path}' mesh '{meshName}'.");
            if (material.EngineSemantic == EngineMaterialSemanticIdentity.StandardLitColorV1 ||
                material.EngineSemantic == EngineMaterialSemanticIdentity.StandardLitColorV2)
            {
                if (material.Shaders.Count != 0)
                    throw new NotSupportedException($"BrowserCook.SurfaceUnsupported: '{path}' material '{material.Name}' retains authored shader stages.");
                if (!StandardLitColorSurfaceBinding.TryCreate(material, out _, out string? reason))
                    throw new NotSupportedException($"BrowserCook.SurfaceUnsupported: '{path}' material '{material.Name}': {reason}.");
                if (resolver is not BrowserShaderArtifactSource source ||
                    !source.MaterialVariants.Any(variant =>
                        variant.Key.Semantic == material.EngineSemantic &&
                        variant.Key.Target == ShaderCompileTarget.WebGPUWgsl &&
                        variant.Key.Pass == (material.EngineSemantic.Version == 2 ? "forward-coverage" : "opaque-forward") &&
                        variant.Key.VertexProfile == "static-position-normal-v1" &&
                        variant.Key.OutputProfile is "linear-hdr-v1" or "linear-hdr-directional-shadow-v1" or "linear-hdr-local-shadows-v1"))
                    throw new NotSupportedException($"BrowserCook.VariantMissing: '{path}' material '{material.Name}' requires its exact lit-color WebGPU forward variant in the project shader manifest.");
                if (material.EngineSemantic.Version == 2 && !material.IsTransparentLike())
                {
                    EngineMaterialVariantKey normal = new(material.EngineSemantic, ShaderCompileTarget.WebGPUWgsl,
                        "depth-normal", "static-position-normal-v1", "normal-rgba16f-v1");
                    EngineMaterialVariantKey depth = new(material.EngineSemantic, ShaderCompileTarget.WebGPUWgsl,
                        "depth", "static-position-v1", "depth-normal-v1");
                    if (!source.MaterialVariants.Any(variant => variant.Key == normal) ||
                        !source.MaterialVariants.Any(variant => variant.Key == depth))
                        throw new NotSupportedException($"BrowserCook.CoverageVariantMissing: '{path}' material '{material.Name}' requires matching depth-normal and shadow-depth variants.");
                }
                return;
            }
            if (material.Shaders.Count == 0)
                throw new NotSupportedException($"BrowserCook.ShaderMissing: '{path}' material '{material.Name}' has no selected cooked WebGPU stages.");
            foreach (XRShader shader in material.Shaders)
            {
                if (!shader.TryGetCookedArtifact(ShaderCompileTarget.WebGPUWgsl, resolver, out ShaderProgramArtifact? artifact))
                    throw new NotSupportedException($"BrowserCook.ShaderUnsupported: '{path}' material '{material.Name}', shader '{shader.Name ?? shader.Source?.FilePath}' has no cooked WebGPU companion. Cook the authored material's web target and configure BrowserShaderArtifactManifestPath before publishing.");
                if (artifact.Target != ShaderCompileTarget.WebGPUWgsl || artifact.DescriptorBytes.IsDefaultOrEmpty)
                    throw new NotSupportedException($"BrowserCook.ShaderArtifactUnsupported: '{path}' material '{material.Name}', pass '{artifact.Pass}', source '{artifact.SourcePath}' has no verified WGSL descriptor.");
                ShaderProgramArtifact verified = ShaderProgramArtifactReader.Read(artifact.DescriptorBytes.AsSpan(), artifact.Artifact.Bytes);
                if (verified.Identity != artifact.Identity || shader.CookedArtifactIdentity != artifact.Identity)
                    throw new InvalidDataException($"BrowserCook.ShaderIdentityMismatch: '{path}' material '{material.Name}', source '{artifact.SourcePath}'.");
                artifacts.TryAdd(verified.Identity, verified);
            }
        }
    }

    private static bool RequiresVrTransform(Type type)
    {
        if (typeof(VRDeviceTransformBase).IsAssignableFrom(type) || typeof(VREyeTransform).IsAssignableFrom(type))
            return true;
        for (Type? current = type; current is not null; current = current.BaseType)
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(VRActionTransformBase<,>))
                return true;
        return false;
    }
}
