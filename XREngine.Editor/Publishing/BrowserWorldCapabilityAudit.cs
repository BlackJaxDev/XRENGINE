using XREngine.Components;
using XREngine.Components.Capture;
using XREngine.Components.Lights;
using XREngine.Components.Scene.Environment;
using XREngine.Components.Scene.Mesh;
using XREngine.Components.Mesh.Shapes;
using XREngine.Components.VR;
using XREngine.Data.Components.Scene;
using XREngine.Rendering;
using XREngine.Rendering.Resources;
using XREngine.Rendering.UI;
using XREngine.Rendering.Shaders.Compilation;
using XREngine.Rendering.Shaders.Generation;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor.Publishing;

/// <summary>Rejects unsupported authored behavior before browser content is activated.</summary>
internal static class BrowserWorldCapabilityAudit
{
    internal static IReadOnlyList<ShaderProgramArtifact> Inspect(XRWorld world, IShaderProgramArtifactResolver? resolver,
        CancellationToken cancellationToken, RenderPipelineResourceProfile? outputProfile = null,
        IReadOnlySet<int>? inheritedScenePasses = null, ISet<int>? admittedScenePasses = null,
        BrowserCapabilityReport? report = null, IReadOnlyList<RenderPipelineRequirements>? inheritedPipelineRequirements = null,
        ICollection<RenderPipelineRequirements>? admittedPipelineRequirements = null,
        BrowserNativeSceneCapabilityAudit? nativeAdmission = null)
    {
        Dictionary<string, ShaderProgramArtifact> artifacts = new(StringComparer.Ordinal);
        BrowserShadowCapabilityAudit shadows = new(resolver);
        BrowserRenderingCapabilityAudit rendering = new(resolver, outputProfile, inheritedScenePasses, inheritedPipelineRequirements, nativeAdmission);
        HashSet<SceneNode> visited = new(ReferenceEqualityComparer.Instance);
        int sceneIndex = 0;
        foreach (XRScene scene in world.Scenes)
        {
            string sceneName = string.IsNullOrWhiteSpace(scene.Name) ? $"Scenes[{sceneIndex}]" : scene.Name;
            string scenePath = $"{world.Name ?? "startup-world"}/{sceneName}";
            foreach (SceneNode root in scene.RootNodes)
                Visit(root, 0, scenePath);
            sceneIndex++;
        }
        Collect(() => rendering.Complete(world.Name ?? "startup-world", report), world.Name ?? "startup-world",
            "camera-pipeline", pass: "camera-pipeline");
        rendering.InspectNativeScenes(report, cancellationToken);
        if (admittedScenePasses is not null)
            rendering.CopyScenePassesTo(admittedScenePasses);
        if (admittedPipelineRequirements is not null)
            foreach (RenderPipelineRequirements requirements in rendering.PipelineRequirements)
                admittedPipelineRequirements.Add(requirements);
        foreach (RenderPipelineRequirements requirements in rendering.PipelineRequirements)
        {
            foreach (XRMaterial material in requirements.Materials)
                Collect(() => InspectMaterial(material, "pipeline-material", material.Name,
                    world.Name ?? "startup-world", sceneRoute: false), world.Name ?? "startup-world",
                    "pipeline-material", material: material.Name, pass: material.RenderPass.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (XRRenderProgram program in requirements.RenderPrograms)
                Collect(() => InspectPipelineProgram(program, requirements.ComputeRenderPrograms.Contains(program)),
                    world.Name ?? "startup-world", "pipeline-program", pass: program.Name);
            foreach (string identity in requirements.ProgramIdentities)
            {
                Collect(() =>
                {
                    if (resolver is null || !resolver.TryResolve(identity, ShaderCompileTarget.WebGPUWgsl, out ShaderProgramArtifact? artifact) || artifact is null ||
                        artifact.Identity != identity || artifact.Target != ShaderCompileTarget.WebGPUWgsl || artifact.DescriptorBytes.IsDefaultOrEmpty)
                        throw new NotSupportedException($"BrowserCook.PipelineProgramMissing: exact declared descriptor '{identity}' is unavailable.");
                    ShaderProgramArtifact verified = ShaderProgramArtifactReader.Read(artifact.DescriptorBytes.AsSpan(), artifact.Artifact.Bytes);
                    if (verified.Identity != identity)
                        throw new InvalidDataException($"BrowserCook.PipelineProgramIdentityMismatch: '{identity}'.");
                    WebPipelineArtifactCatalog.ValidateEngineBindings(verified);
                    artifacts.TryAdd(verified.Identity, verified);
                }, world.Name ?? "startup-world", "pipeline-program", pass: identity);
            }
        }
        Collect(() => shadows.Complete(report, world.Name ?? "startup-world"), world.Name ?? "startup-world",
            "lighting", pass: "forward-lighting");
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

        void Collect(Action action, string scenePath, string nodePath, string? component = null,
            string? material = null, string? pass = null)
        {
            if (report is null)
                action();
            else
                report.Inspect(action, scenePath, nodePath, component, material, pass);
        }

        void InspectPipelineProgram(XRRenderProgram program, bool requiresCompute)
        {
            if (!program.TryGetCookedArtifact(ShaderCompileTarget.WebGPUWgsl, resolver, out ShaderProgramArtifact? selected) ||
                selected.DescriptorBytes.IsDefaultOrEmpty)
                throw new NotSupportedException($"BrowserCook.PipelineProgramMissing: '{program.Name}' requires its exact verified WebGPU descriptor.");
            ShaderProgramArtifact verified = ShaderProgramArtifactReader.Read(selected.DescriptorBytes.AsSpan(), selected.Artifact.Bytes);
            if (verified.Identity != selected.Identity)
                throw new InvalidDataException($"BrowserCook.PipelineProgramIdentityMismatch: '{program.Name}'.");
            if (requiresCompute && !WebPipelineArtifactCatalog.IsCompleteComputeProgram(verified))
                throw new NotSupportedException($"BrowserCook.PipelineProgramShapeMismatch: '{program.Name}' requires a complete WebGPU compute program.");
            WebPipelineArtifactCatalog.ValidateEngineBindings(verified);
            artifacts.TryAdd(verified.Identity, verified);
        }

        void Visit(SceneNode node, int depth, string scenePath)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = node.GetPath();
            if (depth >= 128 || !visited.Add(node) || visited.Count > 8192)
            {
                Collect(() => throw new NotSupportedException($"BrowserCook.SceneGraphUnsupported: '{path}' exceeds the hierarchy budget or shares a scene node."),
                    scenePath, path);
                return;
            }
            if (RequiresVrTransform(node.Transform.GetType()))
                Collect(() => throw new NotSupportedException($"BrowserCook.XrUnsupported: '{path}' transform '{node.Transform.GetType().FullName}' requires a browser XR service that is not enabled."),
                    scenePath, path, node.Transform.GetType().FullName);
            foreach (XRComponent component in node.Components)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? componentName = component.GetType().FullName;
                Collect(() => BrowserPhysicsCapabilityAudit.Inspect(component, path, report, scenePath),
                    scenePath, path, componentName);
                Collect(() => shadows.Inspect(component, path), scenePath, path, componentName);
                if (component is CameraComponent camera)
                    Collect(() => rendering.Inspect(camera, path, report, scenePath), scenePath, path, componentName,
                        pass: "camera-pipeline");
                if (component is SceneCaptureComponentBase or AdvancedOffscreenTextureCaptureComponent or
                    MirrorCaptureComponent or LightProbeGridSpawnerComponent)
                    Collect(() => throw new NotSupportedException($"BrowserCook.EnvironmentCaptureUnsupported: '{path}' component '{componentName}' requires an explicit cooked capture and probe/IBL path."),
                        scenePath, path, componentName);
                if (component is AtmosphericScatteringComponent)
                    Collect(() => throw new NotSupportedException($"BrowserCook.AtmosphereUnsupported: '{path}' requires the planetary-atmosphere and aerial-perspective pass family; the procedural SkyboxComponent is a separate admitted profile."),
                        scenePath, path, componentName);
                if (component is VRHeadsetComponent or VRDeviceModelComponent or VRPlayerCharacterComponent
                    or VRTrackerCollectionComponent or VRHeightScaleComponent or VRPlayerInputSet)
                    Collect(() => throw new NotSupportedException($"BrowserCook.XrUnsupported: '{path}' component '{componentName}' requires a browser XR service that is not enabled."),
                        scenePath, path, componentName);
                string? assembly = component.GetType().Assembly.GetName().Name;
                if (assembly is "XREngine.Runtime.Physics.PhysX" or "XREngine.Runtime.Platform.Desktop"
                    or "XREngine.Runtime.VR" or "XREngine.Runtime.UI.Skia" or "XREngine.Runtime.UI.Rive"
                    or "XREngine.Audio.SteamAudio" or "XREngine.Audio.Audio2Face")
                    Collect(() => throw new NotSupportedException($"BrowserCook.ComponentUnsupported: '{path}' component '{componentName}' requires desktop service '{assembly}'."),
                        scenePath, path, componentName);
                if (component is ModelComponent { Model: { } model })
                    foreach (var mesh in model.Meshes)
                        foreach (var lod in mesh.LODs)
                        {
                            if (lod.Material is { } nativeMaterial)
                                rendering.InspectGeometry(lod.Mesh, nativeMaterial, scenePath, path, mesh.Name, cancellationToken);
                            if (lod.Mesh is { } geometry && (geometry.HasSkinning || geometry.HasBlendshapes) &&
                                (resolver is not BrowserShaderArtifactSource computeSource ||
                                !computeSource.ComputeArtifacts.ContainsKey(WebComputeArtifactCatalog.PackedSkinningKernel)))
                                Collect(() => throw new NotSupportedException($"BrowserCook.ComputeArtifactMissing: '{path}' mesh '{mesh.Name}' requires packed-skinning in the project shader manifest."),
                                    scenePath, path, componentName, lod.Material?.Name, "packed-skinning");
                            Collect(() => InspectMaterial(lod.Material, path, mesh.Name, scenePath, lod.Mesh),
                                scenePath, path, componentName, lod.Material?.Name,
                                lod.Material?.RenderPass.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        }
                else if (component is SkyboxComponent sky)
                {
                    Collect(() =>
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
                    }, scenePath, path, componentName, pass: "background");
                }
                else if (component is ShapeMeshComponent shape)
                {
                    foreach (RenderableMesh mesh in shape.Meshes)
                        foreach (RenderableMesh.RenderableLOD lod in mesh.LODs)
                            if (lod.Renderer.Material is { } nativeMaterial)
                                rendering.InspectGeometry(lod.Renderer.Mesh, nativeMaterial, scenePath, path, shape.GetType().Name, cancellationToken);
                    Collect(() => InspectMaterial(shape.Material, path, shape.GetType().Name, scenePath),
                        scenePath, path, componentName, shape.Material?.Name,
                        shape.Material?.RenderPass.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                else if (component is UICanvasComponent canvas)
                {
                    if (canvas.CanvasTransform.DrawSpace != ECanvasDrawSpace.Screen || canvas.StrictOneByOneRenderCalls)
                        Collect(() => throw new NotSupportedException($"BrowserCook.UiCanvasUnsupported: '{path}' requires a batched screen-space canvas."),
                            scenePath, path, componentName, pass: "screen-ui");
                }
                else if (component is UITextComponent text)
                {
                    if (!text.SupportsBatchedRendering || text.Font is { AtlasType: not EFontAtlasType.Bitmap })
                        Collect(() => throw new NotSupportedException($"BrowserCook.UiTextUnsupported: '{path}' requires unclipped bitmap text without custom stages or glyph rotation."),
                            scenePath, path, componentName, pass: "screen-ui");
                    Collect(() => RequireUiVariant(EngineMaterialSemanticIdentity.UITextBatchedBitmapV1,
                        "instanced-ui-bitmap-text-v1", path), scenePath, path, componentName, pass: "screen-ui");
                }
                else if (component is UIMaterialComponent quad)
                {
                    bool textured = quad.Material?.Textures.Count == 1;
                    if (textured && !UIMaterialComponent.HasCanonicalImageShader(quad.Material!))
                        Collect(() => throw new NotSupportedException($"BrowserCook.UiImageShaderUnsupported: '{path}' image material requires the canonical engine fragment stage or its source-free cooked companion."),
                            scenePath, path, componentName, quad.Material?.Name, "screen-ui");
                    if (textured && quad.Material!.Textures[0] is XRTexture2D image &&
                        !UIMaterialComponent.TryGetWebGpuImageProfile(image, out string? reason))
                        Collect(() => throw new NotSupportedException($"BrowserCook.UiImageTextureUnsupported: '{path}': {reason}."),
                            scenePath, path, componentName, quad.Material?.Name, "screen-ui");
                    if (!quad.SupportsBatchedRendering)
                        Collect(() => throw new NotSupportedException($"BrowserCook.UiMaterialUnsupported: '{path}' requires a source-free solid-color or single-image screen UI profile with MatColor and the exact raster state."),
                            scenePath, path, componentName, quad.Material?.Name, "screen-ui");
                    Collect(() => RequireUiVariant(textured ? EngineMaterialSemanticIdentity.UIQuadBatchedTextureV1 :
                                EngineMaterialSemanticIdentity.UIQuadBatchedV1,
                            textured ? "instanced-ui-quad-texture-v1" : "instanced-ui-quad-v1", path),
                        scenePath, path, componentName, quad.Material?.Name, "screen-ui");
                }
                else if (component is UIRenderableComponent)
                    Collect(() => throw new NotSupportedException($"BrowserCook.UiComponentUnsupported: '{path}' component '{componentName}' has no cooked screen UI profile."),
                        scenePath, path, componentName, pass: "screen-ui");
            }
            foreach (var transform in node.Transform.Children)
                if (transform.SceneNode is SceneNode child)
                    Visit(child, depth + 1, scenePath);
        }

        void RequireUiVariant(EngineMaterialSemanticIdentity semantic, string profile, string path)
        {
            EngineMaterialVariantKey key = new(semantic, ShaderCompileTarget.WebGPUWgsl,
                "screen-ui", profile, "display-rgba-v1");
            if (resolver is not BrowserShaderArtifactSource source ||
                !source.MaterialVariants.Any(variant => variant.Key == key))
                throw new NotSupportedException($"BrowserCook.UiVariantMissing: '{path}' requires '{key}' in the project shader manifest.");
        }

        void InspectMaterial(XRMaterial? material, string path, string? meshName, string scenePath,
            XRMesh? geometry = null, bool sceneRoute = true)
        {
            shadows.InspectMaterial(material, path, meshName, scenePath);
            if (material is null)
                throw new InvalidDataException($"BrowserCook.MaterialMissing: '{path}' mesh '{meshName}'.");
            Collect(() => rendering.InspectMaterial(material, path, meshName, sceneRoute, report, scenePath), scenePath, path,
                material: material.Name, pass: material.RenderPass.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (material.EngineSemantic == EngineMaterialSemanticIdentity.StandardLitTextureV1)
            {
                if (material.Shaders.Count != 0)
                    throw new NotSupportedException($"BrowserCook.TexturedSourceUnsupported: '{path}' material '{material.Name}' requires a serializer-owned source-free semantic companion; authored stages cannot be stripped implicitly.");
                if (!StandardLitTextureSurfaceBinding.TryCreate(material, out StandardLitTextureSurfaceBinding? binding, out string? reason) ||
                    !binding!.TryRead(out StandardLitTextureSurface surface, out reason))
                    throw new NotSupportedException($"BrowserCook.TexturedSurfaceUnsupported: '{path}' material '{material.Name}': {reason}.");
                if (geometry is not null && (!geometry.HasNormals || geometry.TexCoordCount == 0 || surface.Normal is not null && !geometry.HasTangents))
                    throw new NotSupportedException($"BrowserCook.TexturedVertexUnsupported: '{path}' mesh '{meshName}' requires normals, UV0, and float4 authored tangents when normal mapped.");
                if (resolver is not BrowserShaderArtifactSource textureSource ||
                    !textureSource.MaterialVariants.Any(variant => variant.Key.Semantic == material.EngineSemantic &&
                        variant.Key.Pass == "opaque-forward" && variant.Key.VertexProfile == surface.VertexProfile &&
                        variant.Key.OutputProfile is "linear-hdr-v1" or "linear-hdr-directional-shadow-v1" or "linear-hdr-local-shadows-v1"))
                    throw new NotSupportedException($"BrowserCook.TexturedVariantMissing: '{path}' requires its exact opaque texture variant.");
                if (surface.Normal is not null && !textureSource.MaterialVariants.Any(variant =>
                    variant.Key == new EngineMaterialVariantKey(material.EngineSemantic, ShaderCompileTarget.WebGPUWgsl,
                        "depth-normal", surface.VertexProfile, "normal-rgba16f-v1")))
                    throw new NotSupportedException($"BrowserCook.TexturedNormalVariantMissing: '{path}' requires the mapped-normal replay variant.");
                return;
            }
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
            ShaderProgramArtifact? authored = null;
            foreach (XRShader shader in material.Shaders)
            {
                if (!shader.TryGetCookedArtifact(ShaderCompileTarget.WebGPUWgsl, resolver, out ShaderProgramArtifact? artifact))
                    throw new NotSupportedException($"BrowserCook.ShaderUnsupported: '{path}' material '{material.Name}', shader '{shader.Name ?? shader.Source?.FilePath}' has no cooked WebGPU companion. Cook the authored material's web target and configure BrowserShaderArtifactManifestPath before publishing.");
                if (artifact.Target != ShaderCompileTarget.WebGPUWgsl || artifact.DescriptorBytes.IsDefaultOrEmpty)
                    throw new NotSupportedException($"BrowserCook.ShaderArtifactUnsupported: '{path}' material '{material.Name}', pass '{artifact.Pass}', source '{artifact.SourcePath}' has no verified WGSL descriptor.");
                ShaderProgramArtifact verified = ShaderProgramArtifactReader.Read(artifact.DescriptorBytes.AsSpan(), artifact.Artifact.Bytes);
                if (verified.Identity != artifact.Identity || shader.CookedArtifactIdentity != artifact.Identity)
                    throw new InvalidDataException($"BrowserCook.ShaderIdentityMismatch: '{path}' material '{material.Name}', source '{artifact.SourcePath}'.");
                WebPipelineArtifactCatalog.ValidateEngineBindings(verified);
                if (authored is not null && authored.Identity != verified.Identity)
                    throw new NotSupportedException($"BrowserCook.ShaderProgramMismatch: '{path}' material '{material.Name}' retains stages with different whole-program companions.");
                authored = verified;
                artifacts.TryAdd(verified.Identity, verified);
            }
            string? authoredReason = null;
            if (material.EngineSemantic.Semantic == EngineMaterialSemantic.None)
            {
                if (authored?.VertexEntryPoint is null || authored.FragmentEntryPoint is null || authored.ComputeEntryPoint is not null)
                    throw new NotSupportedException($"BrowserCook.RasterProgramMissing: '{path}' material '{material.Name}' requires a complete cooked raster program.");
                return;
            }
            if (authored is null || !EngineAuthoredLitMaterialAdmission.TryAdmit(material, authored,
                out _, out StandardLitTextureSurfaceBinding? texture, out authoredReason))
                throw new NotSupportedException($"BrowserCook.AuthoredLitUnsupported: '{path}' material '{material.Name}': {authoredReason ?? "no complete cooked opaque PBR program"}.");
            if (geometry is not null && (!geometry.HasNormals ||
                texture is not null && (!texture.TryRead(out StandardLitTextureSurface authoredSurface, out _) ||
                    geometry.TexCoordCount == 0 || authoredSurface.Normal is not null && !geometry.HasTangents)))
                throw new NotSupportedException($"BrowserCook.AuthoredLitVertexUnsupported: '{path}' mesh '{meshName}' requires normals, plus UV0 and tangents when selected by the authored PBR recipe.");
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
