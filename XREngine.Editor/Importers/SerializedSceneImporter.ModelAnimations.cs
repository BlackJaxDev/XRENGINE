using XREngine.Animation;
using XREngine.Components.Animation;
using XREngine.Components.Scene.Mesh;
using XREngine.Fbx;
using XREngine.Scene.Prefabs;

namespace XREngine.Scene.Importers;

internal static partial class SerializedSceneImporter
{
    /// <summary>
    /// Publishes generic source clips only after every stack converts successfully.
    /// Unsupported animation retains the visual model and reports a behavior error.
    /// </summary>
    private static void ImportSourceModelAnimations(string path, SerializedModelImporterDocument metadata,
        SceneNode root, ImportState state)
    {
        if (!metadata.ImportAnimation || !string.Equals(Path.GetExtension(path), ".fbx", StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            FbxSemanticDocument source = state.Context.FbxSourceDocuments.GetSemantic(path);
            if (source.IntermediateScene.AnimationStacks.Count == 0)
                return;
            FbxAnimationDocument animations;
            (source, animations) = state.Context.FbxSourceDocuments.GetAnimationSnapshot(path,
                requireLosslessLinearAnimation: true);
            var converter = new UnityFbxAnimationConverter(source, root, metadata.PreserveHierarchy,
                state.Context.CancellationToken);
            var clips = new List<AnimationClip>(animations.Stacks.Count);
            foreach (FbxAnimationStackBinding stack in animations.Stacks)
                clips.Add(converter.Convert(stack));
            foreach (SceneNode target in converter.Targets)
                state.ModelAnimationTargets.Add((path, root, target, target.Parent));
            foreach (var morph in converter.MorphTargets)
                state.ModelAnimationMorphTargets.Add((path, root, morph.Target, morph.Channel));
            foreach (AnimationClip clip in clips)
            {
                AnimationClipComponent component = root.AddComponent<AnimationClipComponent>()
                    ?? throw new InvalidDataException("Could not attach the converted model animation.");
                component.Animation = clip;
                state.Context.AddDiagnostic("UNITYMODEL0010", SourceImportDiagnosticSeverity.Info,
                    SourceImportDiagnosticCategory.AvatarComponent,
                    $"Converted source clip '{clip.Name}': duration={clip.LengthInSeconds:R}, tracks={clip.TotalAnimCount}, " +
                    "binding=SerializedTransformIdentity, sampling=120HzWithAdaptiveSubdivision, kind=GenericTransform. " +
                    "Unity humanoid retargeting and controller behavior are separate conversions.", path);
            }
        }
        catch (InvalidDataException error)
        {
            state.Context.AddDiagnostic("UNITYMODEL0009", SourceImportDiagnosticSeverity.Error,
                SourceImportDiagnosticCategory.AvatarComponent,
                $"Source FBX animation conversion is unsupported: {error.Message} The visual model remains available.", path);
        }
    }

    private static void ValidateComposedModelAnimations(ImportState state)
    {
        var rejectedOwners = new HashSet<SceneNode>();
        foreach (var binding in state.ModelAnimationTargets)
        {
            if (rejectedOwners.Contains(binding.Owner))
                continue;
            bool rootTarget = ReferenceEquals(binding.Target, binding.Owner);
            if ((rootTarget || ReferenceEquals(binding.Target.Parent, binding.Parent)) &&
                ReferenceEquals(binding.Owner.Transform.FindSelfOrDescendantBySerializedReferenceId(
                    binding.Target.Transform.EffectiveSerializedReferenceId), binding.Target.Transform))
                continue;
            rejectedOwners.Add(binding.Owner);
            while (binding.Owner.GetComponent<AnimationClipComponent>() is not null)
                binding.Owner.RemoveComponent<AnimationClipComponent>();
            state.Context.AddDiagnostic("UNITYMODEL0009", SourceImportDiagnosticSeverity.Error,
                SourceImportDiagnosticCategory.AvatarComponent,
                $"Prefab composition removed or reparented animation target '{binding.Target.Name}'. " +
                "Source clips require a new hierarchy conversion before playback.", binding.Path);
        }
        foreach (var binding in state.ModelAnimationMorphTargets)
        {
            if (rejectedOwners.Contains(binding.Owner))
                continue;
            if (binding.Target.GetComponent<ModelComponent>()?.Model is { } model &&
                model.Meshes.Any(subMesh => subMesh.LODs.Any(lod =>
                    lod.Mesh?.BlendshapeNames.Contains(binding.Channel, StringComparer.Ordinal) == true)))
                continue;
            rejectedOwners.Add(binding.Owner);
            while (binding.Owner.GetComponent<AnimationClipComponent>() is not null)
                binding.Owner.RemoveComponent<AnimationClipComponent>();
            state.Context.AddDiagnostic("UNITYMODEL0009", SourceImportDiagnosticSeverity.Error,
                SourceImportDiagnosticCategory.AvatarComponent,
                $"Prefab composition removed animation channel '{binding.Channel}' from '{binding.Target.Name}'.", binding.Path);
        }
    }
}
