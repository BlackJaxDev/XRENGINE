using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Components.VR;
using XREngine.Data.Components.Scene;
using XREngine.Rendering;
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
                if (component is not ModelComponent { Model: { } model })
                    continue;
                foreach (var mesh in model.Meshes)
                    foreach (var lod in mesh.LODs)
                    {
                        XRMaterial material = lod.Material
                            ?? throw new InvalidDataException($"BrowserCook.MaterialMissing: '{path}' mesh '{mesh.Name}'.");
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
            foreach (var transform in node.Transform.Children)
                if (transform.SceneNode is SceneNode child)
                    Visit(child, depth + 1);
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
