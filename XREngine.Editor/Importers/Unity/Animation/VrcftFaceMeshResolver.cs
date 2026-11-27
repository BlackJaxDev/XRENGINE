using XREngine.Animation;
using XREngine.Animation.Importers;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Scene.Transforms;

namespace XREngine.Scene.Importers;

/// <summary>
/// Resolves an avatar's target face mesh for VRCFT template path remapping and audits
/// blendshape coverage between compiled clips and the resolved <see cref="ModelComponent"/>.
/// </summary>
public static class VrcftFaceMeshResolver
{
    private const string BlendshapeAttributePrefix = "blendShape.";

    /// <summary>
    /// Representative Unified Expressions, ARKit, and SRanipal blendshape names used to
    /// identify the primary face mesh when neither an override nor <c>VisemeRenderer</c> exists.
    /// </summary>
    private static readonly HashSet<string> FaceTrackingBlendshapeSignatures = new(StringComparer.OrdinalIgnoreCase)
    {
        // Unified Expressions
        "EyeClosedLeft",
        "EyeClosedRight",
        "EyeSquintLeft",
        "EyeSquintRight",
        "EyeWideLeft",
        "EyeWideRight",
        "BrowInnerUpLeft",
        "BrowInnerUpRight",
        "BrowPinchLeft",
        "BrowPinchRight",
        "BrowLowererLeft",
        "BrowLowererRight",
        "BrowOuterUpLeft",
        "BrowOuterUpRight",
        "CheekPuffLeft",
        "CheekPuffRight",
        "CheekSquintLeft",
        "CheekSquintRight",
        "JawOpen",
        "JawLeft",
        "JawRight",
        "JawForward",
        "MouthClosed",
        "MouthCornerPullLeft",
        "MouthCornerPullRight",
        "MouthFrownLeft",
        "MouthFrownRight",
        "LipFunnelUpperLeft",
        "LipFunnelUpperRight",
        "LipPuckerUpperLeft",
        "LipPuckerUpperRight",
        "TongueOut",
        "TongueUp",
        "TongueDown",
        // ARKit
        "eyeBlinkLeft",
        "eyeBlinkRight",
        "eyeLookUpLeft",
        "eyeLookUpRight",
        "browInnerUp",
        "cheekPuff",
        "mouthSmileLeft",
        "mouthSmileRight",
        "mouthFunnel",
        "mouthPucker",
        // SRanipal
        "Eye_Left_Blink",
        "Eye_Right_Blink",
        "Jaw_Open",
        "Mouth_Smile_Left",
        "Mouth_Smile_Right",
        "Cheek_Puff_Left",
        "Cheek_Puff_Right",
        "Tongue_LongStep1",
        "Tongue_LongStep2",
    };

    /// <summary>
    /// Resolves the avatar face mesh in priority order: explicit override,
    /// <see cref="AvatarPresentationComponent.VisemeRenderer"/>, a descendant named <c>Body</c>,
    /// and the <see cref="ModelComponent"/> with the best face-tracking blendshape match.
    /// </summary>
    public static VrcftFaceMeshResolution Resolve(SceneNode avatarRoot, string? faceMeshPathOverride = null)
    {
        ArgumentNullException.ThrowIfNull(avatarRoot);

        // Priority 1: explicit caller override (for example, editor selection or VRCFury rewrite).
        if (faceMeshPathOverride is not null)
        {
            string normalizedOverride = NormalizeRelativePath(faceMeshPathOverride);
            SceneNode? overrideNode = FindNodeByRelativePath(avatarRoot, normalizedOverride);
            ModelComponent? overrideModel = overrideNode?.GetComponent<ModelComponent>();
            HashSet<string> overrideNames = CollectModelBlendshapeNames(overrideModel);
            return new VrcftFaceMeshResolution
            {
                RelativeNodePath = normalizedOverride,
                TargetNode = overrideNode,
                ModelComponent = overrideModel,
                Source = EVrcftFaceMeshResolutionSource.ExplicitOverride,
                MatchedSignatureCount = CountSignatureMatches(overrideNames),
                BlendshapeNames = overrideNames,
            };
        }

        // Priority 2: AvatarPresentationComponent.VisemeRenderer when it lies inside the avatar hierarchy.
        AvatarPresentationComponent? presentation = FindAvatarPresentation(avatarRoot);
        if (presentation?.VisemeRenderer is ModelComponent visemeRenderer && visemeRenderer.SceneNode is SceneNode visemeNode)
        {
            if (TryGetRelativePath(avatarRoot, visemeNode, out string visemePath))
            {
                HashSet<string> visemeNames = CollectModelBlendshapeNames(visemeRenderer);
                return new VrcftFaceMeshResolution
                {
                    RelativeNodePath = visemePath,
                    TargetNode = visemeNode,
                    ModelComponent = visemeRenderer,
                    Source = EVrcftFaceMeshResolutionSource.AvatarPresentationVisemeRenderer,
                    MatchedSignatureCount = CountSignatureMatches(visemeNames),
                    BlendshapeNames = visemeNames,
                };
            }
        }

        // Collect all candidate ModelComponents in the avatar hierarchy once.
        var candidates = new List<ModelCandidate>();
        CollectModelCandidates(avatarRoot, string.Empty, candidates);

        // Priority 3: highest face-tracking blendshape match count across all candidates.
        // Prefer a node named "Body" on ties, or as fallback when no candidate matches known signatures.
        ModelCandidate? bestBody = null;
        ModelCandidate? bestBlendshapeMatch = null;
        for (int i = 0; i < candidates.Count; i++)
        {
            ModelCandidate candidate = candidates[i];
            if (string.Equals(candidate.Node.Name, "Body", StringComparison.OrdinalIgnoreCase))
            {
                if (bestBody is null
                    || candidate.MatchedSignatureCount > bestBody.MatchedSignatureCount
                    || (candidate.MatchedSignatureCount == bestBody.MatchedSignatureCount
                        && candidate.BlendshapeNames.Count > bestBody.BlendshapeNames.Count))
                {
                    bestBody = candidate;
                }
            }

            if (candidate.BlendshapeNames.Count == 0)
                continue;

            if (bestBlendshapeMatch is null
                || candidate.MatchedSignatureCount > bestBlendshapeMatch.MatchedSignatureCount
                || (candidate.MatchedSignatureCount == bestBlendshapeMatch.MatchedSignatureCount
                    && candidate.BlendshapeNames.Count > bestBlendshapeMatch.BlendshapeNames.Count))
            {
                bestBlendshapeMatch = candidate;
            }
        }

        if (bestBody is not null
            && (bestBlendshapeMatch is null
                || bestBody.MatchedSignatureCount >= bestBlendshapeMatch.MatchedSignatureCount))
        {
            return new VrcftFaceMeshResolution
            {
                RelativeNodePath = bestBody.RelativePath,
                TargetNode = bestBody.Node,
                ModelComponent = bestBody.Model,
                Source = EVrcftFaceMeshResolutionSource.NamedBodyFallback,
                MatchedSignatureCount = bestBody.MatchedSignatureCount,
                BlendshapeNames = bestBody.BlendshapeNames,
            };
        }

        if (bestBlendshapeMatch is not null)
        {
            return new VrcftFaceMeshResolution
            {
                RelativeNodePath = bestBlendshapeMatch.RelativePath,
                TargetNode = bestBlendshapeMatch.Node,
                ModelComponent = bestBlendshapeMatch.Model,
                Source = EVrcftFaceMeshResolutionSource.BlendshapeMatchFallback,
                MatchedSignatureCount = bestBlendshapeMatch.MatchedSignatureCount,
                BlendshapeNames = bestBlendshapeMatch.BlendshapeNames,
            };
        }

        return new VrcftFaceMeshResolution
        {
            RelativeNodePath = null,
            TargetNode = null,
            ModelComponent = null,
            Source = EVrcftFaceMeshResolutionSource.None,
            MatchedSignatureCount = 0,
            BlendshapeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>
    /// Compares all blendshape names driven by compiled layers starting at
    /// <paramref name="startLayerIndex"/> against the blendshapes present on the resolved face mesh.
    /// </summary>
    public static (int MatchedCount, List<string> UnmatchedBlendshapes) AuditBlendshapeCoverage(
        AnimStateMachine stateMachine,
        int startLayerIndex,
        IReadOnlySet<string> availableBlendshapes)
    {
        ArgumentNullException.ThrowIfNull(stateMachine);
        ArgumentNullException.ThrowIfNull(availableBlendshapes);

        var referencedBlendshapes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visitedMotions = new HashSet<MotionBase>(ReferenceEqualityComparer.Instance);

        int clampedStart = Math.Clamp(startLayerIndex, 0, stateMachine.Layers.Count);
        for (int layerIndex = clampedStart; layerIndex < stateMachine.Layers.Count; layerIndex++)
        {
            AnimLayer layer = stateMachine.Layers[layerIndex];
            for (int stateIndex = 0; stateIndex < layer.States.Count; stateIndex++)
                CollectMotionBlendshapes(layer.States[stateIndex].Motion, visitedMotions, referencedBlendshapes);
        }

        int matchedCount = 0;
        var unmatched = new List<string>();
        foreach (string blendshapeName in referencedBlendshapes)
        {
            if (availableBlendshapes.Contains(blendshapeName))
                matchedCount++;
            else
                unmatched.Add(blendshapeName);
        }

        unmatched.Sort(StringComparer.OrdinalIgnoreCase);
        return (matchedCount, unmatched);
    }

    /// <summary>
    /// Collects all distinct blendshape names exposed across every mesh LOD of a <see cref="ModelComponent"/>.
    /// </summary>
    public static HashSet<string> CollectModelBlendshapeNames(ModelComponent? model)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (model?.Model is null)
            return names;

        foreach (var renderableMesh in model.Model.Meshes)
        {
            foreach (var lod in renderableMesh.LODs)
            {
                string[]? blendshapes = lod.Mesh?.BlendshapeNames;
                if (blendshapes is null)
                    continue;
                for (int i = 0; i < blendshapes.Length; i++)
                {
                    if (!string.IsNullOrWhiteSpace(blendshapes[i]))
                        names.Add(blendshapes[i]);
                }
            }
        }

        return names;
    }

    internal static string NormalizeRelativePath(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return string.Empty;

        string normalized = rawPath.Replace('\\', '/').Trim().Trim('/');
        return normalized;
    }

    private static AvatarPresentationComponent? FindAvatarPresentation(SceneNode root)
    {
        if (root.GetComponent<AvatarPresentationComponent>() is AvatarPresentationComponent direct)
            return direct;

        var stack = new Stack<SceneNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            SceneNode current = stack.Pop();
            foreach (TransformBase child in current.Transform.Children)
            {
                if (child.SceneNode is not SceneNode childNode)
                    continue;
                if (childNode.GetComponent<AvatarPresentationComponent>() is AvatarPresentationComponent found)
                    return found;
                stack.Push(childNode);
            }
        }

        return null;
    }

    private static SceneNode? FindNodeByRelativePath(SceneNode root, string normalizedPath)
    {
        if (normalizedPath.Length == 0)
            return root;

        SceneNode? current = root;
        string[] segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < segments.Length && current is not null; i++)
        {
            SceneNode? next = null;
            foreach (TransformBase child in current.Transform.Children)
            {
                if (child.SceneNode is SceneNode childNode
                    && string.Equals(childNode.Name, segments[i], StringComparison.OrdinalIgnoreCase))
                {
                    next = childNode;
                    break;
                }
            }
            current = next;
        }

        return current;
    }

    private static bool TryGetRelativePath(SceneNode root, SceneNode target, out string relativePath)
    {
        if (ReferenceEquals(root, target))
        {
            relativePath = string.Empty;
            return true;
        }

        var segments = new Stack<string>();
        SceneNode? current = target;
        while (current is not null && !ReferenceEquals(current, root))
        {
            segments.Push(current.Name ?? string.Empty);
            current = current.Parent;
        }

        if (!ReferenceEquals(current, root))
        {
            relativePath = string.Empty;
            return false;
        }

        relativePath = string.Join('/', segments);
        return true;
    }

    private static void CollectModelCandidates(SceneNode node, string relativePath, List<ModelCandidate> candidates)
    {
        if (node.GetComponent<ModelComponent>() is ModelComponent model)
        {
            HashSet<string> blendshapes = CollectModelBlendshapeNames(model);
            candidates.Add(new ModelCandidate(
                node,
                model,
                relativePath,
                blendshapes,
                CountSignatureMatches(blendshapes)));
        }

        foreach (TransformBase child in node.Transform.Children)
        {
            if (child.SceneNode is not SceneNode childNode)
                continue;
            string childName = childNode.Name ?? string.Empty;
            string childPath = relativePath.Length == 0 ? childName : $"{relativePath}/{childName}";
            CollectModelCandidates(childNode, childPath, candidates);
        }
    }

    private static int CountSignatureMatches(HashSet<string> blendshapes)
    {
        int matches = 0;
        foreach (string name in blendshapes)
        {
            if (FaceTrackingBlendshapeSignatures.Contains(name))
                matches++;
        }
        return matches;
    }

    private static void CollectMotionBlendshapes(
        MotionBase? motion,
        HashSet<MotionBase> visited,
        HashSet<string> blendshapes)
    {
        if (motion is null || !visited.Add(motion))
            return;

        switch (motion)
        {
            case AnimationClip clip:
                if (clip.RootMember is not null)
                    CollectMemberBlendshapes(clip.RootMember, blendshapes);
                ImportedAnimationBindingDescriptor[] bindings = clip.ImportedGenericBindings;
                for (int i = 0; i < bindings.Length; i++)
                {
                    string attribute = bindings[i].Attribute;
                    if (attribute.StartsWith(BlendshapeAttributePrefix, StringComparison.Ordinal))
                    {
                        string name = attribute[BlendshapeAttributePrefix.Length..];
                        if (!string.IsNullOrWhiteSpace(name))
                            blendshapes.Add(name);
                    }
                }
                return;

            case BlendTree1D tree1D:
                for (int i = 0; i < tree1D.Children.Count; i++)
                    CollectMotionBlendshapes(tree1D.Children[i].Motion, visited, blendshapes);
                return;

            case BlendTree2D tree2D:
                for (int i = 0; i < tree2D.Children.Count; i++)
                    CollectMotionBlendshapes(tree2D.Children[i].Motion, visited, blendshapes);
                return;

            case BlendTreeDirect treeDirect:
                for (int i = 0; i < treeDirect.Children.Count; i++)
                    CollectMotionBlendshapes(treeDirect.Children[i].Motion, visited, blendshapes);
                return;
        }
    }

    private static void CollectMemberBlendshapes(AnimationMember member, HashSet<string> blendshapes)
    {
        if (member.MemberType == EAnimationMemberType.Method
            && string.Equals(member.MemberName, "SetBlendShapeWeightNormalized", StringComparison.Ordinal)
            && member.MethodArguments is { Length: >= 1 }
            && member.MethodArguments[0] is string blendshapeName
            && !string.IsNullOrWhiteSpace(blendshapeName))
        {
            blendshapes.Add(blendshapeName);
        }

        for (int i = 0; i < member.Children.Count; i++)
            CollectMemberBlendshapes(member.Children[i], blendshapes);
    }

    private sealed record ModelCandidate(
        SceneNode Node,
        ModelComponent Model,
        string RelativePath,
        HashSet<string> BlendshapeNames,
        int MatchedSignatureCount);
}
