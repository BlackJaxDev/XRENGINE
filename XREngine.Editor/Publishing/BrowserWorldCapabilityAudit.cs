using XREngine.Components;
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
        HashSet<SceneNode> visited = new(ReferenceEqualityComparer.Instance);
        foreach (XRScene scene in world.Scenes)
            foreach (SceneNode root in scene.RootNodes)
                Visit(root, 0);
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
                            InspectMaterial(lod.Material, path, mesh.Name);
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
            if (material is null)
                throw new InvalidDataException($"BrowserCook.MaterialMissing: '{path}' mesh '{meshName}'.");
            if (material.EngineSemantic == EngineMaterialSemanticIdentity.StandardLitColorV1)
            {
                if (material.Shaders.Count != 0)
                    throw new NotSupportedException($"BrowserCook.SurfaceUnsupported: '{path}' material '{material.Name}' retains authored shader stages.");
                if (!StandardLitColorSurfaceBinding.TryCreate(material, out _, out string? reason))
                    throw new NotSupportedException($"BrowserCook.SurfaceUnsupported: '{path}' material '{material.Name}': {reason}.");
                if (resolver is not BrowserShaderArtifactSource source ||
                    !source.MaterialVariants.Any(variant =>
                        variant.Key.Semantic == material.EngineSemantic &&
                        variant.Key.Target == ShaderCompileTarget.WebGPUWgsl &&
                        variant.Key.Pass == "opaque-forward" &&
                        variant.Key.VertexProfile == "static-position-normal-v1" &&
                        variant.Key.OutputProfile is "linear-hdr-v1" or "linear-hdr-directional-shadow-v1"))
                    throw new NotSupportedException($"BrowserCook.VariantMissing: '{path}' material '{material.Name}' requires an exact StandardLitColorV1 WebGPU forward variant in the project shader manifest.");
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
