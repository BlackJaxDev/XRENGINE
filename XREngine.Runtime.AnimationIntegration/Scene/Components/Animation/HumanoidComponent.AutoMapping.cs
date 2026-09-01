using System.Numerics;
using XREngine.Scene;

namespace XREngine.Components.Animation;

public partial class HumanoidComponent
{
    private readonly HumanoidAvatarRoleMappingEvidence?[] _autoMappingEvidence =
        new HumanoidAvatarRoleMappingEvidence?[CompiledHumanoidAvatarDefinition.RoleCount];

    private readonly record struct AutoMapSelection(
        HumanoidAvatarAutoMapCandidate? Candidate,
        float Score,
        float Margin,
        float Topology,
        float Geometry,
        float Axis,
        float Alias);

    /// <summary>
    /// Assigns a role supplied by trustworthy model metadata. Imported semantic
    /// data outranks inference but remains editable through the same avatar
    /// definition used by runtime playback.
    /// </summary>
    public void SetImportedAvatarBoneMapping(EHumanoidAvatarBoneRole role, SceneNode? node)
    {
        BoneDef bone = GetBoneDefinition(role);
        bone.Node = node;
        if (node is not null)
            RefreshBoneBindPose(bone);

        _autoMappingEvidence[(int)role] = node is null
            ? null
            : new HumanoidAvatarRoleMappingEvidence
            {
                Source = EHumanoidAvatarMappingSource.ImportedSemanticMetadata,
                Confidence = 1.0f,
                ImportedMetadataScore = 1.0f,
                TopologyScore = 1.0f,
                GeometryScore = 1.0f,
                AxisScore = 1.0f,
                SymmetryScore = 1.0f,
                AliasScore = 0.0f,
                Summary = "Imported semantic humanoid mapping.",
            };
        RefreshAvatarDefinition();
    }

    private void AutoMapAvatarSkeleton()
    {
        Array.Clear(_autoMappingEvidence);
        var assigned = new HashSet<SceneNode>(ReferenceEqualityComparer.Instance);
        PreserveAuthoritativeAvatarMappings(assigned);

        GetAutoMappingBasis(out Vector3 bodyRight, out Vector3 bodyUp, out Vector3 bodyForward);
        List<HumanoidAvatarAutoMapCandidate> candidates = GatherAutoMapCandidates(bodyUp);
        if (candidates.Count == 0)
            return;

        var byNode = new Dictionary<SceneNode, HumanoidAvatarAutoMapCandidate>(
            candidates.Count,
            ReferenceEqualityComparer.Instance);
        for (int i = 0; i < candidates.Count; i++)
            byNode[candidates[i].Node] = candidates[i];

        float minimumHeight = float.PositiveInfinity;
        float maximumHeight = float.NegativeInfinity;
        for (int i = 0; i < candidates.Count; i++)
        {
            float height = Vector3.Dot(candidates[i].Position, bodyUp);
            minimumHeight = MathF.Min(minimumHeight, height);
            maximumHeight = MathF.Max(maximumHeight, height);
        }
        float skeletonHeight = MathF.Max(maximumHeight - minimumHeight, 1e-4f);

        SceneNode? hips = Hips.Node;
        if (hips is null)
        {
            AutoMapSelection hipsSelection = SelectHipsCandidate(
                candidates,
                byNode,
                bodyUp,
                minimumHeight,
                skeletonHeight,
                assigned);
            hips = hipsSelection.Candidate?.Node;
            AssignInferredRole(
                EHumanoidAvatarBoneRole.Hips,
                hips,
                hipsSelection,
                symmetry: 0.75f,
                "Central branching joint with two descending limb branches and one ascending torso branch.",
                assigned);
        }

        if (hips is null || !byNode.TryGetValue(hips, out HumanoidAvatarAutoMapCandidate? hipsCandidate))
            return;

        List<SceneNode> torsoPath = BuildTorsoPath(
            hips,
            byNode,
            bodyUp,
            maximumNodes: 16);
        MapTorso(torsoPath, byNode, bodyRight, bodyUp, skeletonHeight, assigned);
        MapLegs(hipsCandidate, candidates, byNode, bodyRight, bodyUp, bodyForward, skeletonHeight, assigned);
        MapArms(torsoPath, byNode, bodyRight, bodyUp, bodyForward, skeletonHeight, assigned);
        MapHeadDetails(byNode, bodyRight, bodyUp, bodyForward, skeletonHeight, assigned);
        MapFingerChains(Left.Wrist.Node, isLeft: true, byNode, bodyForward, assigned);
        MapFingerChains(Right.Wrist.Node, isLeft: false, byNode, bodyForward, assigned);
    }

    private void PreserveAuthoritativeAvatarMappings(HashSet<SceneNode> assigned)
    {
        HumanoidAvatarDefinitionMetadata definition = AvatarDefinition;
        EHumanoidAvatarBoneRole[] roles = Enum.GetValues<EHumanoidAvatarBoneRole>();
        for (int i = 0; i < roles.Length; i++)
        {
            EHumanoidAvatarBoneRole role = roles[i];
            BoneDef bone = GetBoneDefinition(role);
            SceneNode? node = bone.Node;
            HumanoidAvatarBoneBinding? binding = FindBinding(definition.Bones, role);
            bool importedComponentBinding = definition.Status == EHumanoidAvatarDefinitionStatus.Uninitialized
                && node is not null;
            bool authoritative = node is not null
                && (importedComponentBinding
                    || binding is not null
                    && (binding.Locked
                        || binding.MappingSource == EHumanoidAvatarMappingSource.ImportedSemanticMetadata));

            if (!authoritative)
            {
                bone.Node = null;
                continue;
            }

            if (!assigned.Add(node!))
            {
                bone.Node = null;
                continue;
            }

            bool locked = binding?.Locked == true;
            EHumanoidAvatarMappingSource source = locked
                ? EHumanoidAvatarMappingSource.EditorCorrection
                : EHumanoidAvatarMappingSource.ImportedSemanticMetadata;
            _autoMappingEvidence[(int)role] = new HumanoidAvatarRoleMappingEvidence
            {
                Source = source,
                Confidence = 1.0f,
                ImportedMetadataScore = source == EHumanoidAvatarMappingSource.ImportedSemanticMetadata ? 1.0f : 0.0f,
                TopologyScore = 1.0f,
                GeometryScore = 1.0f,
                AxisScore = 1.0f,
                SymmetryScore = 1.0f,
                AliasScore = 0.0f,
                Summary = locked ? "Locked editor correction." : "Imported semantic humanoid mapping.",
            };
        }
    }

    private void GetAutoMappingBasis(out Vector3 right, out Vector3 up, out Vector3 forward)
    {
        HumanoidAvatarBodyAxes axes = AvatarDefinition.BodyAxes;
        if (AvatarDefinition.Status == EHumanoidAvatarDefinitionStatus.Valid
            && axes.IsFiniteOrthonormal())
        {
            right = Vector3.Normalize(axes.Right);
            up = Vector3.Normalize(axes.Up);
            forward = Vector3.Normalize(axes.Forward);
            return;
        }

        Matrix4x4 rootBind = GetHumanoidBindWorldPose(SceneNode);
        // XRE's canonical humanoid convention is +X toward the avatar's left,
        // so anatomical right is the transformed -X axis. Keeping this method
        // anatomically named prevents bilateral roles from being swapped before
        // semantic metadata or editor corrections are available.
        right = NormalizeOrFallback(Vector3.TransformNormal(-Vector3.UnitX, rootBind), -Vector3.UnitX);
        up = NormalizeOrFallback(Vector3.TransformNormal(Vector3.UnitY, rootBind), Vector3.UnitY);
        forward = NormalizeOrFallback(Vector3.Cross(up, right), Vector3.UnitZ);
        right = NormalizeOrFallback(Vector3.Cross(forward, up), right);
    }

    private List<HumanoidAvatarAutoMapCandidate> GatherAutoMapCandidates(Vector3 bodyUp)
    {
        List<HumanoidAvatarAutoMapCandidate> candidates = [];
        GatherAutoMapCandidatesRecursive(SceneNode, depth: 0, bodyUp, candidates);
        return candidates;
    }

    private HumanoidAvatarAutoMapCandidate GatherAutoMapCandidatesRecursive(
        SceneNode node,
        int depth,
        Vector3 bodyUp,
        List<HumanoidAvatarAutoMapCandidate> candidates)
    {
        Matrix4x4 localBind = _humanoidBindLocalPoses.TryGetValue(node, out Matrix4x4 storedLocal)
            ? storedLocal
            : GetCurrentLocalMatrix(node);
        Matrix4x4 worldBind = GetHumanoidBindWorldPose(node);
        Vector3 position = worldBind.Translation;
        var candidate = new HumanoidAvatarAutoMapCandidate
        {
            Node = node,
            LocalBindTransform = localBind,
            WorldBindTransform = worldBind,
            Position = position,
            Depth = depth,
            TraversalIndex = candidates.Count,
            SubtreeNodeCount = 1,
            DescendantLeafCount = 0,
            SubtreeMinimumY = Vector3.Dot(position, bodyUp),
            SubtreeMaximumY = Vector3.Dot(position, bodyUp),
        };
        candidates.Add(candidate);

        int childCount = 0;
        float childAxisScore = 0.0f;
        foreach (var childTransform in node.Transform.Children)
        {
            SceneNode? child = childTransform.SceneNode;
            if (child is null)
                continue;
            HumanoidAvatarAutoMapCandidate childCandidate = GatherAutoMapCandidatesRecursive(
                child,
                depth + 1,
                bodyUp,
                candidates);
            childCount++;
            childAxisScore += CalculateJointAxisAlignment(worldBind, position, childCandidate.Position);
            candidate.SubtreeNodeCount += childCandidate.SubtreeNodeCount;
            candidate.DescendantLeafCount += childCandidate.DescendantLeafCount;
            candidate.SubtreeMinimumY = MathF.Min(candidate.SubtreeMinimumY, childCandidate.SubtreeMinimumY);
            candidate.SubtreeMaximumY = MathF.Max(candidate.SubtreeMaximumY, childCandidate.SubtreeMaximumY);
        }
        if (childCount == 0)
            candidate.DescendantLeafCount = 1;
        candidate.JointAxisScore = childCount > 0
            ? childAxisScore / childCount
            : CalculateDominantAxisAlignment(localBind.Translation);
        return candidate;
    }

    private static float CalculateJointAxisAlignment(
        Matrix4x4 jointWorldBind,
        Vector3 jointPosition,
        Vector3 childPosition)
    {
        Vector3 direction = childPosition - jointPosition;
        if (!Matrix4x4.Invert(jointWorldBind, out Matrix4x4 inverseBind))
            return 0.0f;
        return CalculateDominantAxisAlignment(Vector3.TransformNormal(direction, inverseBind));
    }

    private static float CalculateDominantAxisAlignment(Vector3 direction)
    {
        float lengthSquared = direction.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared <= 1e-10f)
            return 0.0f;

        Vector3 normalized = direction / MathF.Sqrt(lengthSquared);
        float dominant = MathF.Max(
            MathF.Abs(normalized.X),
            MathF.Max(MathF.Abs(normalized.Y), MathF.Abs(normalized.Z)));
        const float diagonalAlignment = 0.5773502692f;
        return Math.Clamp(
            (dominant - diagonalAlignment) / (1.0f - diagonalAlignment),
            0.0f,
            1.0f);
    }

    private static AutoMapSelection SelectHipsCandidate(
        List<HumanoidAvatarAutoMapCandidate> candidates,
        Dictionary<SceneNode, HumanoidAvatarAutoMapCandidate> byNode,
        Vector3 bodyUp,
        float minimumHeight,
        float skeletonHeight,
        HashSet<SceneNode> assigned)
    {
        HumanoidAvatarAutoMapCandidate? best = null;
        float bestScore = float.NegativeInfinity;
        float secondScore = float.NegativeInfinity;
        float bestTopology = 0.0f;
        float bestGeometry = 0.0f;
        float bestAxis = 0.0f;
        float bestAlias = 0.0f;
        bool bestIsSemanticAnchor = false;
        for (int i = 0; i < candidates.Count; i++)
        {
            HumanoidAvatarAutoMapCandidate candidate = candidates[i];
            if (assigned.Contains(candidate.Node) || candidate.Node.Parent is null)
                continue;

            float height = Vector3.Dot(candidate.Position, bodyUp);
            int descendingBranches = 0;
            int ascendingBranches = 0;
            foreach (var childTransform in candidate.Node.Transform.Children)
            {
                SceneNode? child = childTransform.SceneNode;
                if (child is null || !byNode.TryGetValue(child, out HumanoidAvatarAutoMapCandidate? childCandidate))
                    continue;
                if (childCandidate.SubtreeMinimumY < height - skeletonHeight * 0.18f)
                    descendingBranches++;
                if (childCandidate.SubtreeMaximumY > height + skeletonHeight * 0.22f)
                    ascendingBranches++;
            }

            float topology = Math.Clamp(descendingBranches / 2.0f, 0.0f, 1.0f) * 0.7f
                + Math.Clamp(ascendingBranches, 0, 1) * 0.3f;
            float normalizedHeight = (height - minimumHeight) / skeletonHeight;
            float geometry = Math.Clamp(1.0f - MathF.Abs(normalizedHeight - 0.48f) / 0.38f, 0.0f, 1.0f);
            float axis = candidate.JointAxisScore;
            float semanticAlias = AliasScore(candidate.Node.Name, "hips", "pelvis");
            float alias = MathF.Max(
                semanticAlias,
                AliasScore(candidate.Node.Name, "root") * 0.2f);
            bool isSemanticAnchor = semanticAlias >= 0.9f
                && descendingBranches >= 2
                && ascendingBranches >= 1;
            float score = topology * 0.54f + geometry * 0.30f + axis * 0.08f + alias * 0.08f;
            bool replaceBest = isSemanticAnchor != bestIsSemanticAnchor
                ? isSemanticAnchor
                : score > bestScore
                    || score == bestScore && candidate.TraversalIndex < best!.TraversalIndex;
            if (replaceBest)
            {
                secondScore = bestScore;
                best = candidate;
                bestScore = score;
                bestTopology = topology;
                bestGeometry = geometry;
                bestAxis = axis;
                bestAlias = alias;
                bestIsSemanticAnchor = isSemanticAnchor;
            }
            else if (isSemanticAnchor == bestIsSemanticAnchor && score > secondScore)
                secondScore = score;
        }

        float margin = float.IsFinite(secondScore) ? Math.Clamp(bestScore - secondScore, 0.0f, 1.0f) : 1.0f;
        return new AutoMapSelection(best, bestScore, margin, bestTopology, bestGeometry, bestAxis, bestAlias);
    }

    private void MapTorso(
        List<SceneNode> path,
        Dictionary<SceneNode, HumanoidAvatarAutoMapCandidate> byNode,
        Vector3 bodyRight,
        Vector3 bodyUp,
        float skeletonHeight,
        HashSet<SceneNode> assigned)
    {
        if (path.Count == 0)
            return;

        int headIndex = SelectHeadIndex(path, byNode, bodyUp);
        SceneNode head = path[headIndex];
        SceneNode spine = path[0];
        AssignTopologyRole(EHumanoidAvatarBoneRole.Spine, spine, 0.92f, "First stable joint on the ascending torso chain.", assigned);
        AssignTopologyRole(EHumanoidAvatarBoneRole.Head, head, 0.88f, "Top branching joint on the ascending torso chain.", assigned);

        int neckIndex = FindTorsoAliasIndex(path, 1, headIndex, "neck");
        if (neckIndex < 0 && headIndex > 0)
            neckIndex = headIndex - 1;

        int shoulderLevel = FindShoulderLevel(path, headIndex, byNode, bodyRight, skeletonHeight);
        int chestIndex = FindTorsoAliasIndex(path, 1, neckIndex, "chest", "upperchest");
        int upperChestIndex = chestIndex >= 0
            ? FindTorsoAliasIndex(path, chestIndex + 1, neckIndex, "upperchest")
            : -1;
        if (chestIndex < 0 && shoulderLevel > 0 && shoulderLevel < neckIndex)
        {
            // The lateral-arm branch is the upper torso joint when another
            // distinct joint exists below it; otherwise it is the Chest itself.
            if (shoulderLevel > 1)
            {
                chestIndex = shoulderLevel - 1;
                upperChestIndex = shoulderLevel;
            }
            else
            {
                chestIndex = shoulderLevel;
            }
        }

        if (chestIndex > 0 && chestIndex < neckIndex)
            AssignTopologyRole(EHumanoidAvatarBoneRole.Chest, path[chestIndex], 0.88f, "Torso joint below the optional upper-chest/shoulder level.", assigned);
        if (upperChestIndex > chestIndex && upperChestIndex < neckIndex)
            AssignTopologyRole(EHumanoidAvatarBoneRole.UpperChest, path[upperChestIndex], 0.86f, "Optional upper torso joint supporting the shoulder level.", assigned);

        if (neckIndex > 0 && neckIndex < headIndex)
        {
            SceneNode neck = path[neckIndex];
            float neckAlias = AliasScore(neck.Name, "neck");
            float neckLength = Vector3.Distance(byNode[neck].Position, byNode[head].Position);
            if (neckAlias > 0.0f || neckLength < skeletonHeight * 0.18f)
                AssignTopologyRole(EHumanoidAvatarBoneRole.Neck, neck, 0.76f + neckAlias * 0.12f, "Short terminal torso joint immediately below the head.", assigned);
        }
    }

    private static int FindTorsoAliasIndex(
        List<SceneNode> path,
        int startIndex,
        int endExclusive,
        string alias,
        string? excludedAlias = null)
    {
        int boundedEnd = Math.Min(endExclusive, path.Count);
        for (int i = Math.Max(0, startIndex); i < boundedEnd; i++)
        {
            if (excludedAlias is not null && AliasScore(path[i].Name, excludedAlias) >= 0.9f)
                continue;
            if (AliasScore(path[i].Name, alias) >= 0.9f)
                return i;
        }
        return -1;
    }

    private static int SelectHeadIndex(
        List<SceneNode> path,
        Dictionary<SceneNode, HumanoidAvatarAutoMapCandidate> byNode,
        Vector3 bodyUp)
    {
        int semanticIndex = -1;
        float bestSemanticAlias = 0.0f;
        for (int i = 0; i < path.Count; i++)
        {
            float alias = AliasScore(path[i].Name, "head");
            if (alias >= 0.9f && (alias > bestSemanticAlias || alias == bestSemanticAlias && i > semanticIndex))
            {
                semanticIndex = i;
                bestSemanticAlias = alias;
            }
        }
        if (semanticIndex >= 0)
            return semanticIndex;

        int bestIndex = path.Count - 1;
        float minimum = Vector3.Dot(byNode[path[0]].Position, bodyUp);
        float maximum = Vector3.Dot(byNode[path[^1]].Position, bodyUp);
        float range = MathF.Max(maximum - minimum, 1e-4f);
        float bestScore = float.NegativeInfinity;
        for (int i = 0; i < path.Count; i++)
        {
            HumanoidAvatarAutoMapCandidate candidate = byNode[path[i]];
            float height = (Vector3.Dot(candidate.Position, bodyUp) - minimum) / range;
            float branching = Math.Clamp(candidate.Node.Transform.Children.Count / 3.0f, 0.0f, 1.0f);
            float alias = AliasScore(candidate.Node.Name, "head");
            float score = height * 0.62f + branching * 0.23f + alias * 0.15f;
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }
        }
        return bestIndex;
    }

    private static int FindShoulderLevel(
        List<SceneNode> path,
        int headIndex,
        Dictionary<SceneNode, HumanoidAvatarAutoMapCandidate> byNode,
        Vector3 bodyRight,
        float skeletonHeight)
    {
        var torsoNodes = new HashSet<SceneNode>(path, ReferenceEqualityComparer.Instance);
        for (int i = headIndex - 1; i > 0; i--)
            if (AliasScore(path[i].Name, "upperchest") < 0.9f
                && AliasScore(path[i].Name, "chest") >= 0.9f)
                return i;

        int bestIndex = -1;
        float bestReach = 0.0f;
        for (int i = 0; i < headIndex; i++)
        {
            SceneNode torso = path[i];
            float originSide = Vector3.Dot(byNode[torso].Position, bodyRight);
            foreach (var childTransform in torso.Transform.Children)
            {
                SceneNode? child = childTransform.SceneNode;
                if (child is null || torsoNodes.Contains(child) || !byNode.TryGetValue(child, out HumanoidAvatarAutoMapCandidate? candidate))
                    continue;
                float lateralReach = MathF.Max(
                    MathF.Abs(Vector3.Dot(candidate.Position, bodyRight) - originSide),
                    EstimateMaximumLateralReach(child, byNode, bodyRight, originSide));
                if (lateralReach > bestReach && lateralReach > skeletonHeight * 0.12f)
                {
                    bestReach = lateralReach;
                    bestIndex = i;
                }
            }
        }
        return bestIndex;
    }

    private void MapLegs(
        HumanoidAvatarAutoMapCandidate hips,
        List<HumanoidAvatarAutoMapCandidate> candidates,
        Dictionary<SceneNode, HumanoidAvatarAutoMapCandidate> byNode,
        Vector3 bodyRight,
        Vector3 bodyUp,
        Vector3 bodyForward,
        float skeletonHeight,
        HashSet<SceneNode> assigned)
    {
        AutoMapSelection left = SelectUpperLegCandidate(hips, candidates, bodyRight, bodyUp, skeletonHeight, sideSign: -1.0f, assigned);
        AutoMapSelection right = SelectUpperLegCandidate(hips, candidates, bodyRight, bodyUp, skeletonHeight, sideSign: 1.0f, assigned);
        float pairSymmetry = CalculatePairSymmetry(left.Candidate, right.Candidate, hips.Position);
        MapLegChain(left, isLeft: true, byNode, bodyUp, bodyForward, pairSymmetry, assigned);
        MapLegChain(right, isLeft: false, byNode, bodyUp, bodyForward, pairSymmetry, assigned);
    }

    private static AutoMapSelection SelectUpperLegCandidate(
        HumanoidAvatarAutoMapCandidate hips,
        List<HumanoidAvatarAutoMapCandidate> candidates,
        Vector3 bodyRight,
        Vector3 bodyUp,
        float skeletonHeight,
        float sideSign,
        HashSet<SceneNode> assigned)
    {
        HumanoidAvatarAutoMapCandidate? best = null;
        float bestScore = float.NegativeInfinity;
        float second = float.NegativeInfinity;
        float bestTopology = 0.0f;
        float bestGeometry = 0.0f;
        float bestAxis = 0.0f;
        float bestAlias = 0.0f;
        bool bestIsSemanticAnchor = false;
        float hipsHeight = Vector3.Dot(hips.Position, bodyUp);
        bool isLeft = sideSign < 0.0f;
        for (int i = 0; i < candidates.Count; i++)
        {
            HumanoidAvatarAutoMapCandidate candidate = candidates[i];
            if (assigned.Contains(candidate.Node)
                || !IsStrictDescendant(hips.Node, candidate.Node)
                || candidate.Depth - hips.Depth is < 1 or > 4)
                continue;

            float side = Vector3.Dot(candidate.Position - hips.Position, bodyRight) * sideSign;
            float height = Vector3.Dot(candidate.Position, bodyUp);
            float descent = (height - candidate.SubtreeMinimumY) / skeletonHeight;
            float proximalHeight = Math.Clamp(1.0f - MathF.Abs(height - hipsHeight) / (skeletonHeight * 0.35f), 0.0f, 1.0f);
            float sideScore = Math.Clamp(side / (skeletonHeight * 0.08f), 0.0f, 1.0f);
            float topology = Math.Clamp(descent / 0.32f, 0.0f, 1.0f);
            float geometry = proximalHeight * 0.55f + sideScore * 0.45f;
            float axis = candidate.JointAxisScore;
            float alias = AliasScore(candidate.Node.Name, "upleg", "upperleg", "thigh", "leg");
            float semanticAlias = isLeft
                ? AliasScore(candidate.Node.Name, "leftupleg", "leftupperleg", "leftthigh", "leftleg")
                : AliasScore(candidate.Node.Name, "rightupleg", "rightupperleg", "rightthigh", "rightleg");
            bool isSemanticAnchor = semanticAlias >= 0.9f
                && HasDescendantAlias(candidate.Node, maximumDepth: 4, "knee", "lowerleg", "calf", "shin")
                && HasDescendantAlias(candidate.Node, maximumDepth: 5, "foot", "ankle");
            float score = topology * 0.48f + geometry * 0.36f + axis * 0.08f + alias * 0.08f;
            bool replaceBest = isSemanticAnchor != bestIsSemanticAnchor
                ? isSemanticAnchor
                : score > bestScore
                    || score == bestScore && candidate.TraversalIndex < best!.TraversalIndex;
            if (replaceBest)
            {
                second = bestScore;
                best = candidate;
                bestScore = score;
                bestTopology = topology;
                bestGeometry = geometry;
                bestAxis = axis;
                bestAlias = alias;
                bestIsSemanticAnchor = isSemanticAnchor;
            }
            else if (isSemanticAnchor == bestIsSemanticAnchor && score > second)
                second = score;
        }
        float margin = float.IsFinite(second) ? Math.Clamp(bestScore - second, 0.0f, 1.0f) : 1.0f;
        return new AutoMapSelection(best, bestScore, margin, bestTopology, bestGeometry, bestAxis, bestAlias);
    }

    private void MapLegChain(
        AutoMapSelection selection,
        bool isLeft,
        Dictionary<SceneNode, HumanoidAvatarAutoMapCandidate> byNode,
        Vector3 bodyUp,
        Vector3 bodyForward,
        float symmetry,
        HashSet<SceneNode> assigned)
    {
        SceneNode? upper = selection.Candidate?.Node;
        if (upper is null)
            return;
        EHumanoidAvatarBoneRole upperRole = isLeft ? EHumanoidAvatarBoneRole.LeftUpperLeg : EHumanoidAvatarBoneRole.RightUpperLeg;
        EHumanoidAvatarBoneRole lowerRole = isLeft ? EHumanoidAvatarBoneRole.LeftLowerLeg : EHumanoidAvatarBoneRole.RightLowerLeg;
        EHumanoidAvatarBoneRole footRole = isLeft ? EHumanoidAvatarBoneRole.LeftFoot : EHumanoidAvatarBoneRole.RightFoot;
        EHumanoidAvatarBoneRole toesRole = isLeft ? EHumanoidAvatarBoneRole.LeftToes : EHumanoidAvatarBoneRole.RightToes;
        AssignInferredRole(upperRole, upper, selection, symmetry, "Proximal descending limb root below the hips.", assigned);

        List<SceneNode> path = BuildExtremeDescendantPath(upper, byNode, bodyUp, positiveDirection: false, maximumNodes: 12);
        if (path.Count == 0)
            return;
        int footIndex = SelectFootIndex(path, byNode, bodyUp);
        int lowerIndex = SelectLowerLegIndex(path, footIndex, byNode, bodyUp);
        if (lowerIndex >= 0)
            AssignTopologyRole(lowerRole, path[lowerIndex], 0.84f * symmetry + 0.12f, "Mid-chain knee joint selected from cumulative leg geometry.", assigned);
        if (footIndex >= 0)
            AssignTopologyRole(footRole, path[footIndex], 0.86f * symmetry + 0.1f, "Lowest stable joint at the end of the descending leg chain.", assigned);

        SceneNode? foot = footIndex >= 0 ? path[footIndex] : null;
        SceneNode? toes = foot is null ? null : SelectToesCandidate(foot, byNode, bodyForward, assigned);
        if (toes is not null)
            AssignTopologyRole(toesRole, toes, 0.75f, "Optional terminal joint extending from the foot.", assigned);
    }

    private void MapArms(
        List<SceneNode> torsoPath,
        Dictionary<SceneNode, HumanoidAvatarAutoMapCandidate> byNode,
        Vector3 bodyRight,
        Vector3 bodyUp,
        Vector3 bodyForward,
        float skeletonHeight,
        HashSet<SceneNode> assigned)
    {
        var torsoSet = new HashSet<SceneNode>(torsoPath, ReferenceEqualityComparer.Instance);
        AutoMapSelection left = SelectArmBranch(torsoPath, torsoSet, byNode, bodyRight, skeletonHeight, sideSign: -1.0f, assigned);
        AutoMapSelection right = SelectArmBranch(torsoPath, torsoSet, byNode, bodyRight, skeletonHeight, sideSign: 1.0f, assigned);
        Vector3 origin = Hips.WorldBindPose.Translation;
        float symmetry = CalculatePairSymmetry(left.Candidate, right.Candidate, origin);
        MapArmChain(left, isLeft: true, byNode, bodyRight, bodyUp, bodyForward, symmetry, assigned);
        MapArmChain(right, isLeft: false, byNode, bodyRight, bodyUp, bodyForward, symmetry, assigned);
    }

    private static AutoMapSelection SelectArmBranch(
        List<SceneNode> torsoPath,
        HashSet<SceneNode> torsoSet,
        Dictionary<SceneNode, HumanoidAvatarAutoMapCandidate> byNode,
        Vector3 bodyRight,
        float skeletonHeight,
        float sideSign,
        HashSet<SceneNode> assigned)
    {
        HumanoidAvatarAutoMapCandidate? best = null;
        float bestScore = float.NegativeInfinity;
        float second = float.NegativeInfinity;
        float bestGeometry = 0.0f;
        float bestAxis = 0.0f;
        float bestAlias = 0.0f;
        bool bestIsSemanticAnchor = false;
        bool isLeft = sideSign < 0.0f;
        for (int i = 0; i < torsoPath.Count; i++)
        {
            SceneNode torso = torsoPath[i];
            float originSide = Vector3.Dot(byNode[torso].Position, bodyRight);
            foreach (var childTransform in torso.Transform.Children)
            {
                SceneNode? child = childTransform.SceneNode;
                if (child is null || torsoSet.Contains(child) || assigned.Contains(child) || !byNode.TryGetValue(child, out HumanoidAvatarAutoMapCandidate? candidate))
                    continue;
                float reach = EstimateMaximumSignedLateralReach(child, byNode, bodyRight, originSide, sideSign);
                float geometry = Math.Clamp(reach / (skeletonHeight * 0.28f), 0.0f, 1.0f);
                float topology = Math.Clamp(candidate.SubtreeNodeCount / 4.0f, 0.0f, 1.0f);
                float axis = candidate.JointAxisScore;
                float alias = AliasScore(child.Name, "shoulder", "clavicle", "upperarm", "arm");
                float semanticAlias = isLeft
                    ? AliasScore(child.Name, "leftshoulder", "leftclavicle", "leftupperarm", "leftarm")
                    : AliasScore(child.Name, "rightshoulder", "rightclavicle", "rightupperarm", "rightarm");
                bool isSemanticAnchor = semanticAlias >= 0.9f
                    && HasDescendantAlias(child, maximumDepth: 4, "elbow", "lowerarm", "forearm")
                    && HasDescendantAlias(child, maximumDepth: 5, "hand", "wrist", "palm");
                float score = geometry * 0.54f + topology * 0.30f + axis * 0.08f + alias * 0.08f;
                bool replaceBest = isSemanticAnchor != bestIsSemanticAnchor
                    ? isSemanticAnchor
                    : score > bestScore
                        || score == bestScore && candidate.TraversalIndex < best!.TraversalIndex;
                if (replaceBest)
                {
                    second = bestScore;
                    best = candidate;
                    bestScore = score;
                    bestGeometry = geometry;
                    bestAxis = axis;
                    bestAlias = alias;
                    bestIsSemanticAnchor = isSemanticAnchor;
                }
                else if (isSemanticAnchor == bestIsSemanticAnchor && score > second)
                    second = score;
            }
        }
        float margin = float.IsFinite(second) ? Math.Clamp(bestScore - second, 0.0f, 1.0f) : 1.0f;
        return new AutoMapSelection(
            best,
            bestScore,
            margin,
            best?.SubtreeNodeCount >= 3 ? 1.0f : 0.4f,
            bestGeometry,
            bestAxis,
            bestAlias);
    }

    private void MapArmChain(
        AutoMapSelection selection,
        bool isLeft,
        Dictionary<SceneNode, HumanoidAvatarAutoMapCandidate> byNode,
        Vector3 bodyRight,
        Vector3 bodyUp,
        Vector3 bodyForward,
        float symmetry,
        HashSet<SceneNode> assigned)
    {
        SceneNode? branchRoot = selection.Candidate?.Node;
        if (branchRoot is null)
            return;
        Vector3 lateral = isLeft ? -bodyRight : bodyRight;
        List<SceneNode> path = BuildExtremeDescendantPath(branchRoot, byNode, lateral, positiveDirection: true, maximumNodes: 16);
        path.Insert(0, branchRoot);
        RemoveRepeatedPathNodes(path);
        if (path.Count < 3)
            return;

        int handIndex = SelectHandIndex(path, byNode);
        if (handIndex < 2)
            return;
        int lowerIndex = handIndex - 1;
        int upperIndex = SelectUpperArmIndex(path, lowerIndex, byNode);
        int shoulderIndex = upperIndex > 0 ? upperIndex - 1 : -1;

        EHumanoidAvatarBoneRole shoulderRole = isLeft ? EHumanoidAvatarBoneRole.LeftShoulder : EHumanoidAvatarBoneRole.RightShoulder;
        EHumanoidAvatarBoneRole upperRole = isLeft ? EHumanoidAvatarBoneRole.LeftUpperArm : EHumanoidAvatarBoneRole.RightUpperArm;
        EHumanoidAvatarBoneRole lowerRole = isLeft ? EHumanoidAvatarBoneRole.LeftLowerArm : EHumanoidAvatarBoneRole.RightLowerArm;
        EHumanoidAvatarBoneRole handRole = isLeft ? EHumanoidAvatarBoneRole.LeftHand : EHumanoidAvatarBoneRole.RightHand;

        if (shoulderIndex >= 0)
        {
            float shoulderSegment = Vector3.Distance(byNode[path[shoulderIndex]].Position, byNode[path[upperIndex]].Position);
            float upperSegment = Vector3.Distance(byNode[path[upperIndex]].Position, byNode[path[lowerIndex]].Position);
            if (AliasScore(path[shoulderIndex].Name, "shoulder", "clavicle") > 0.0f || shoulderSegment < upperSegment * 0.72f)
                AssignTopologyRole(shoulderRole, path[shoulderIndex], 0.76f, "Optional short clavicle/shoulder segment before the upper arm.", assigned);
        }

        AssignInferredRole(upperRole, path[upperIndex], selection, symmetry, "Proximal lateral limb joint selected from torso branch reach.", assigned);
        AssignTopologyRole(lowerRole, path[lowerIndex], 0.86f * symmetry + 0.1f, "Intermediate elbow joint on the lateral arm chain.", assigned);
        AssignTopologyRole(handRole, path[handIndex], 0.88f * symmetry + 0.08f, "Terminal arm joint or branching palm root.", assigned);
    }

    private void MapHeadDetails(
        Dictionary<SceneNode, HumanoidAvatarAutoMapCandidate> byNode,
        Vector3 bodyRight,
        Vector3 bodyUp,
        Vector3 bodyForward,
        float skeletonHeight,
        HashSet<SceneNode> assigned)
    {
        SceneNode? head = Head.Node;
        if (head is null)
            return;

        List<HumanoidAvatarAutoMapCandidate> descendants = [];
        foreach (HumanoidAvatarAutoMapCandidate candidate in byNode.Values)
            if (IsStrictDescendant(head, candidate.Node))
                descendants.Add(candidate);
        descendants.Sort(static (a, b) => a.TraversalIndex.CompareTo(b.TraversalIndex));

        SceneNode? jaw = SelectBestAlias(descendants, assigned, "jaw", "chin", "mouth");
        if (jaw is not null)
            AssignAliasRole(EHumanoidAvatarBoneRole.Jaw, jaw, 0.72f, "Optional head descendant with jaw/chin semantic evidence.", assigned);

        SceneNode? leftEye = SelectEyeCandidate(descendants, assigned, bodyRight, bodyUp, bodyForward, skeletonHeight, sideSign: -1.0f);
        SceneNode? rightEye = SelectEyeCandidate(descendants, assigned, bodyRight, bodyUp, bodyForward, skeletonHeight, sideSign: 1.0f);
        if (leftEye is not null && rightEye is not null)
        {
            AssignAliasRole(EHumanoidAvatarBoneRole.LeftEye, leftEye, 0.74f, "Bilateral eye candidate under the head.", assigned);
            AssignAliasRole(EHumanoidAvatarBoneRole.RightEye, rightEye, 0.74f, "Bilateral eye candidate under the head.", assigned);
        }
    }

    private void MapFingerChains(
        SceneNode? hand,
        bool isLeft,
        Dictionary<SceneNode, HumanoidAvatarAutoMapCandidate> byNode,
        Vector3 bodyForward,
        HashSet<SceneNode> assigned)
    {
        if (hand is null)
            return;

        List<List<SceneNode>> chains = [];
        foreach (var childTransform in hand.Transform.Children)
        {
            SceneNode? child = childTransform.SceneNode;
            if (child is null || assigned.Contains(child))
                continue;
            List<SceneNode> chain = BuildLongestSingleBranchChain(child, byNode, maximumNodes: 6);
            if (chain.Count >= 3)
                chains.Add(chain);
        }
        if (chains.Count == 0)
            return;

        chains.Sort((left, right) =>
        {
            float leftForward = Vector3.Dot(byNode[left[0]].Position, bodyForward);
            float rightForward = Vector3.Dot(byNode[right[0]].Position, bodyForward);
            int compare = leftForward.CompareTo(rightForward);
            return compare != 0 ? compare : byNode[left[0]].TraversalIndex.CompareTo(byNode[right[0]].TraversalIndex);
        });

        bool[] used = new bool[chains.Count];
        MapNamedFinger(chains, used, isLeft, "thumb", 0, assigned);
        MapNamedFinger(chains, used, isLeft, "index", 1, assigned);
        MapNamedFinger(chains, used, isLeft, "middle", 2, assigned);
        MapNamedFinger(chains, used, isLeft, "ring", 3, assigned);
        MapNamedFinger(chains, used, isLeft, "little", 4, assigned, "pinky", "pinkie");

        int semanticIndex = 0;
        for (int i = 0; i < chains.Count && semanticIndex < 5; i++)
        {
            if (used[i])
                continue;
            while (semanticIndex < 5 && IsFingerSemanticMapped(isLeft, semanticIndex))
                semanticIndex++;
            if (semanticIndex >= 5)
                break;
            MapFingerRoleChain(chains[i], isLeft, semanticIndex, confidence: 0.58f, "Topology-only finger order; review and lock if the source has arbitrary finger ordering.", assigned);
            semanticIndex++;
        }
    }

    private void MapNamedFinger(
        List<List<SceneNode>> chains,
        bool[] used,
        bool isLeft,
        string primaryAlias,
        int semanticIndex,
        HashSet<SceneNode> assigned,
        params string[] additionalAliases)
    {
        for (int i = 0; i < chains.Count; i++)
        {
            if (used[i])
                continue;
            string? name = chains[i][0].Name;
            float score = AliasScore(name, primaryAlias);
            for (int j = 0; j < additionalAliases.Length; j++)
                score = MathF.Max(score, AliasScore(name, additionalAliases[j]));
            if (score <= 0.0f)
                continue;
            used[i] = true;
            MapFingerRoleChain(chains[i], isLeft, semanticIndex, confidence: 0.82f, $"Finger chain matched semantic alias '{primaryAlias}' and three-segment topology.", assigned);
            return;
        }
    }

    private void MapFingerRoleChain(
        List<SceneNode> chain,
        bool isLeft,
        int semanticIndex,
        float confidence,
        string summary,
        HashSet<SceneNode> assigned)
    {
        int baseRole = isLeft
            ? (int)EHumanoidAvatarBoneRole.LeftThumbProximal
            : (int)EHumanoidAvatarBoneRole.RightThumbProximal;
        int start = Math.Max(0, chain.Count - 3);
        for (int segment = 0; segment < 3; segment++)
        {
            EHumanoidAvatarBoneRole role = (EHumanoidAvatarBoneRole)(baseRole + semanticIndex * 3 + segment);
            AssignTopologyRole(role, chain[start + segment], confidence, summary, assigned);
        }
    }

    private bool IsFingerSemanticMapped(bool isLeft, int semanticIndex)
    {
        int baseRole = isLeft
            ? (int)EHumanoidAvatarBoneRole.LeftThumbProximal
            : (int)EHumanoidAvatarBoneRole.RightThumbProximal;
        return GetBoneDefinition((EHumanoidAvatarBoneRole)(baseRole + semanticIndex * 3)).Node is not null;
    }

    private void AssignTopologyRole(
        EHumanoidAvatarBoneRole role,
        SceneNode? node,
        float confidence,
        string summary,
        HashSet<SceneNode> assigned)
    {
        float alias = AliasScore(node?.Name, GetRoleAlias(role));
        float axis = node is null ? 0.0f : CalculateNodeJointAxisScore(node);
        var selection = new AutoMapSelection(
            node is null ? null : new HumanoidAvatarAutoMapCandidate
            {
                Node = node,
                LocalBindTransform = GetCurrentLocalMatrix(node),
                WorldBindTransform = GetHumanoidBindWorldPose(node),
                Position = GetHumanoidBindWorldPose(node).Translation,
            },
            confidence,
            0.25f,
            Math.Clamp(confidence, 0.0f, 1.0f),
            Math.Clamp(confidence, 0.0f, 1.0f),
            axis,
            alias);
        AssignInferredRole(role, node, selection, symmetry: confidence, summary, assigned);
    }

    private void AssignAliasRole(
        EHumanoidAvatarBoneRole role,
        SceneNode node,
        float confidence,
        string summary,
        HashSet<SceneNode> assigned)
    {
        var selection = new AutoMapSelection(
            null,
            confidence,
            0.2f,
            0.55f,
            0.65f,
            CalculateNodeJointAxisScore(node),
            1.0f);
        AssignInferredRole(role, node, selection, symmetry: 0.65f, summary, assigned);
    }

    private void AssignInferredRole(
        EHumanoidAvatarBoneRole role,
        SceneNode? node,
        AutoMapSelection selection,
        float symmetry,
        string summary,
        HashSet<SceneNode> assigned)
    {
        if (node is null || GetBoneDefinition(role).Node is not null)
            return;
        if (!assigned.Add(node))
            return;

        float confidence = Math.Clamp(
            0.36f
            + selection.Topology * 0.22f
            + selection.Geometry * 0.15f
            + selection.Axis * 0.08f
            + symmetry * 0.11f
            + selection.Alias * 0.03f
            + Math.Clamp(selection.Margin * 2.0f, 0.0f, 1.0f) * 0.05f,
            0.0f,
            1.0f);
        if (selection.Score > 0.0f)
            confidence = MathF.Max(confidence, Math.Clamp(selection.Score, 0.0f, 0.96f));

        BoneDef bone = GetBoneDefinition(role);
        bone.Node = node;
        _autoMappingEvidence[(int)role] = new HumanoidAvatarRoleMappingEvidence
        {
            Source = EHumanoidAvatarMappingSource.Automatic,
            Confidence = confidence,
            ImportedMetadataScore = 0.0f,
            TopologyScore = Math.Clamp(selection.Topology, 0.0f, 1.0f),
            GeometryScore = Math.Clamp(selection.Geometry, 0.0f, 1.0f),
            AxisScore = Math.Clamp(selection.Axis, 0.0f, 1.0f),
            SymmetryScore = Math.Clamp(symmetry, 0.0f, 1.0f),
            AliasScore = Math.Clamp(selection.Alias, 0.0f, 1.0f),
            Summary = summary,
        };
    }

    private float CalculateNodeJointAxisScore(SceneNode node)
    {
        Matrix4x4 worldBind = GetHumanoidBindWorldPose(node);
        Vector3 position = worldBind.Translation;
        float score = 0.0f;
        int childCount = 0;
        foreach (var childTransform in node.Transform.Children)
        {
            SceneNode? child = childTransform.SceneNode;
            if (child is null)
                continue;
            score += CalculateJointAxisAlignment(
                worldBind,
                position,
                GetHumanoidBindWorldPose(child).Translation);
            childCount++;
        }
        return childCount > 0
            ? score / childCount
            : CalculateDominantAxisAlignment(GetCurrentLocalMatrix(node).Translation);
    }

    /// <summary>
    /// Prefers a semantically named torso chain only when its ancestry also
    /// proves that it belongs to the selected hips. This prevents large hair,
    /// tail, and clothing-bone subtrees from outranking an otherwise ordinary
    /// humanoid spine while retaining the geometry-only fallback for rigs with
    /// opaque bone names.
    /// </summary>
    private static List<SceneNode> BuildTorsoPath(
        SceneNode hips,
        Dictionary<SceneNode, HumanoidAvatarAutoMapCandidate> byNode,
        Vector3 bodyUp,
        int maximumNodes)
    {
        List<SceneNode> semanticPath = FindSemanticallyAnchoredTorsoPath(hips, byNode, maximumNodes);
        return semanticPath.Count > 0
            ? semanticPath
            : BuildExtremeDescendantPath(
                hips,
                byNode,
                bodyUp,
                positiveDirection: true,
                maximumNodes);
    }

    private static List<SceneNode> FindSemanticallyAnchoredTorsoPath(
        SceneNode hips,
        Dictionary<SceneNode, HumanoidAvatarAutoMapCandidate> byNode,
        int maximumNodes)
    {
        List<SceneNode>? bestPath = null;
        float bestScore = float.NegativeInfinity;
        int bestTraversalIndex = int.MaxValue;
        foreach (HumanoidAvatarAutoMapCandidate candidate in byNode.Values)
        {
            float headAlias = AliasScore(candidate.Node.Name, "head");
            if (headAlias < 0.9f || !IsStrictDescendant(hips, candidate.Node))
                continue;

            var reversePath = new List<SceneNode>(maximumNodes);
            SceneNode? current = candidate.Node;
            while (current is not null
                && !ReferenceEquals(current, hips)
                && reversePath.Count < maximumNodes)
            {
                if (!byNode.ContainsKey(current))
                    break;
                reversePath.Add(current);
                current = current.Parent;
            }
            if (!ReferenceEquals(current, hips) || reversePath.Count == 0)
                continue;

            reversePath.Reverse();
            float spineAlias = 0.0f;
            float chestAlias = 0.0f;
            float neckAlias = 0.0f;
            for (int i = 0; i < reversePath.Count; i++)
            {
                SceneNode node = reversePath[i];
                spineAlias = MathF.Max(spineAlias, AliasScore(node.Name, "spine"));
                chestAlias = MathF.Max(chestAlias, AliasScore(node.Name, "chest", "upperchest"));
                neckAlias = MathF.Max(neckAlias, AliasScore(node.Name, "neck"));
            }
            if (MathF.Max(spineAlias, chestAlias) < 0.9f)
                continue;

            float pathLengthScore = 1.0f - Math.Clamp(MathF.Abs(reversePath.Count - 4) / 8.0f, 0.0f, 1.0f);
            float score = headAlias * 0.35f
                + spineAlias * 0.25f
                + chestAlias * 0.20f
                + neckAlias * 0.10f
                + pathLengthScore * 0.10f;
            if (score > bestScore
                || score == bestScore && candidate.TraversalIndex < bestTraversalIndex)
            {
                bestPath = reversePath;
                bestScore = score;
                bestTraversalIndex = candidate.TraversalIndex;
            }
        }
        return bestPath ?? [];
    }

    private static bool HasDescendantAlias(
        SceneNode root,
        int maximumDepth,
        params string[] aliases)
    {
        var stack = new Stack<(SceneNode Node, int Depth)>();
        foreach (var childTransform in root.Transform.Children)
            if (childTransform.SceneNode is SceneNode child)
                stack.Push((child, 1));

        while (stack.Count > 0)
        {
            (SceneNode node, int depth) = stack.Pop();
            if (AliasScore(node.Name, aliases) >= 0.9f)
                return true;
            if (depth >= maximumDepth)
                continue;
            foreach (var childTransform in node.Transform.Children)
                if (childTransform.SceneNode is SceneNode child)
                    stack.Push((child, depth + 1));
        }
        return false;
    }

    private static List<SceneNode> BuildExtremeDescendantPath(
        SceneNode root,
        Dictionary<SceneNode, HumanoidAvatarAutoMapCandidate> byNode,
        Vector3 direction,
        bool positiveDirection,
        int maximumNodes)
    {
        List<SceneNode> path = [];
        SceneNode current = root;
        float rootProjection = Vector3.Dot(byNode[root].Position, direction);
        for (int depth = 0; depth < maximumNodes; depth++)
        {
            SceneNode? best = null;
            float bestProjection = positiveDirection ? float.NegativeInfinity : float.PositiveInfinity;
            foreach (var childTransform in current.Transform.Children)
            {
                SceneNode? child = childTransform.SceneNode;
                if (child is null || !byNode.TryGetValue(child, out HumanoidAvatarAutoMapCandidate? candidate))
                    continue;
                float projection = positiveDirection ? candidate.SubtreeMaximumY : candidate.SubtreeMinimumY;
                if (!IsApproximatelyUpDirection(direction))
                    projection = EstimateExtremeProjection(child, byNode, direction, positiveDirection);
                bool better = positiveDirection ? projection > bestProjection : projection < bestProjection;
                if (better)
                {
                    bestProjection = projection;
                    best = child;
                }
            }
            if (best is null)
                break;
            float bestNodeProjection = Vector3.Dot(byNode[best].Position, direction);
            if (path.Count > 0 && MathF.Abs(bestNodeProjection - rootProjection) <= 1e-7f && best.Transform.Children.Count == 0)
                break;
            path.Add(best);
            current = best;
        }
        return path;
    }

    private static List<SceneNode> BuildLongestSingleBranchChain(
        SceneNode root,
        Dictionary<SceneNode, HumanoidAvatarAutoMapCandidate> byNode,
        int maximumNodes)
    {
        List<SceneNode> path = [root];
        SceneNode current = root;
        for (int i = 1; i < maximumNodes; i++)
        {
            SceneNode? best = null;
            int bestSubtree = -1;
            foreach (var childTransform in current.Transform.Children)
            {
                SceneNode? child = childTransform.SceneNode;
                if (child is null || !byNode.TryGetValue(child, out HumanoidAvatarAutoMapCandidate? candidate))
                    continue;
                if (candidate.SubtreeNodeCount > bestSubtree)
                {
                    bestSubtree = candidate.SubtreeNodeCount;
                    best = child;
                }
            }
            if (best is null)
                break;
            path.Add(best);
            current = best;
        }
        return path;
    }

    private static int SelectFootIndex(
        List<SceneNode> path,
        Dictionary<SceneNode, HumanoidAvatarAutoMapCandidate> byNode,
        Vector3 bodyUp)
    {
        int semanticIndex = -1;
        float bestSemanticAlias = 0.0f;
        for (int i = 0; i < path.Count; i++)
        {
            float alias = AliasScore(path[i].Name, "foot", "ankle");
            if (alias >= 0.9f && alias > bestSemanticAlias)
            {
                semanticIndex = i;
                bestSemanticAlias = alias;
            }
        }
        if (semanticIndex >= 0)
            return semanticIndex;

        int bestIndex = path.Count - 1;
        float bestScore = float.NegativeInfinity;
        float minimumHeight = float.PositiveInfinity;
        for (int i = 0; i < path.Count; i++)
            minimumHeight = MathF.Min(minimumHeight, Vector3.Dot(byNode[path[i]].Position, bodyUp));
        for (int i = 0; i < path.Count; i++)
        {
            float height = Vector3.Dot(byNode[path[i]].Position, bodyUp);
            float low = 1.0f / (1.0f + MathF.Abs(height - minimumHeight) * 20.0f);
            float alias = AliasScore(path[i].Name, "foot", "ankle");
            float score = low * 0.82f + alias * 0.18f;
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }
        }
        return bestIndex;
    }

    private static int SelectLowerLegIndex(
        List<SceneNode> path,
        int footIndex,
        Dictionary<SceneNode, HumanoidAvatarAutoMapCandidate> byNode,
        Vector3 bodyUp)
    {
        if (footIndex <= 0)
            return -1;
        int semanticIndex = -1;
        float bestSemanticAlias = 0.0f;
        for (int i = 0; i < footIndex; i++)
        {
            float alias = AliasScore(path[i].Name, "knee", "lowerleg", "calf", "shin");
            if (alias >= 0.9f && alias > bestSemanticAlias)
            {
                semanticIndex = i;
                bestSemanticAlias = alias;
            }
        }
        if (semanticIndex >= 0)
            return semanticIndex;

        float startHeight = Vector3.Dot(byNode[path[0]].Position, bodyUp);
        float footHeight = Vector3.Dot(byNode[path[footIndex]].Position, bodyUp);
        float range = MathF.Max(MathF.Abs(startHeight - footHeight), 1e-5f);
        int bestIndex = 0;
        float bestScore = float.NegativeInfinity;
        for (int i = 0; i < footIndex; i++)
        {
            float height = Vector3.Dot(byNode[path[i]].Position, bodyUp);
            float progress = MathF.Abs(startHeight - height) / range;
            float midpoint = 1.0f - Math.Clamp(MathF.Abs(progress - 0.5f) / 0.5f, 0.0f, 1.0f);
            float alias = AliasScore(path[i].Name, "knee", "lowerleg", "calf", "shin");
            float score = midpoint * 0.82f + alias * 0.18f;
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }
        }
        return bestIndex;
    }

    private static SceneNode? SelectToesCandidate(
        SceneNode foot,
        Dictionary<SceneNode, HumanoidAvatarAutoMapCandidate> byNode,
        Vector3 bodyForward,
        HashSet<SceneNode> assigned)
    {
        SceneNode? best = null;
        float bestScore = float.NegativeInfinity;
        Vector3 origin = byNode[foot].Position;
        foreach (var childTransform in foot.Transform.Children)
        {
            SceneNode? child = childTransform.SceneNode;
            if (child is null || assigned.Contains(child) || !byNode.TryGetValue(child, out HumanoidAvatarAutoMapCandidate? candidate))
                continue;
            float extension = MathF.Abs(Vector3.Dot(candidate.Position - origin, bodyForward));
            float alias = AliasScore(child.Name, "toe", "toes", "ball");
            float score = extension + alias;
            if (score > bestScore)
            {
                bestScore = score;
                best = child;
            }
        }
        return best;
    }

    private static int SelectHandIndex(
        List<SceneNode> path,
        Dictionary<SceneNode, HumanoidAvatarAutoMapCandidate> byNode)
    {
        int bestAliasIndex = -1;
        float bestAlias = 0.0f;
        for (int i = 2; i < path.Count; i++)
        {
            float alias = AliasScore(path[i].Name, "hand", "wrist", "palm");
            if (alias > bestAlias)
            {
                bestAlias = alias;
                bestAliasIndex = i;
            }
        }
        if (bestAliasIndex >= 0)
            return bestAliasIndex;

        // A palm/wrist is a fan-out point for multiple multi-joint digit
        // chains. Raw child count is insufficient because elbows commonly
        // carry one wrist plus one or more twist/helper leaves.
        for (int i = 2; i < path.Count; i++)
            if (CountLongChildChains(path[i], byNode, minimumNodes: 3) >= 2)
                return i;

        // Rigs without fingers normally terminate at the hand. The path is
        // already the most laterally extended descendant chain, so its final
        // node is the deterministic topology-only fallback.
        return path.Count - 1;
    }

    private static int CountLongChildChains(
        SceneNode node,
        Dictionary<SceneNode, HumanoidAvatarAutoMapCandidate> byNode,
        int minimumNodes)
    {
        int count = 0;
        foreach (var childTransform in node.Transform.Children)
        {
            SceneNode? child = childTransform.SceneNode;
            if (child is null || !byNode.ContainsKey(child))
                continue;
            if (BuildLongestSingleBranchChain(child, byNode, minimumNodes).Count >= minimumNodes)
                count++;
        }
        return count;
    }

    private static int SelectUpperArmIndex(
        List<SceneNode> path,
        int lowerIndex,
        Dictionary<SceneNode, HumanoidAvatarAutoMapCandidate> byNode)
    {
        if (lowerIndex <= 1)
            return 0;

        int bestAliasIndex = -1;
        float bestAlias = 0.0f;
        for (int i = 0; i < lowerIndex; i++)
        {
            float alias = AliasScore(path[i].Name, "upperarm", "arm");
            if (alias > bestAlias)
            {
                bestAlias = alias;
                bestAliasIndex = i;
            }
        }
        if (bestAliasIndex >= 0)
            return bestAliasIndex;

        int index = lowerIndex - 1;
        if (index > 0)
        {
            float candidateSegment = Vector3.Distance(
                byNode[path[index]].Position,
                byNode[path[lowerIndex]].Position);
            float precedingSegment = Vector3.Distance(
                byNode[path[index - 1]].Position,
                byNode[path[index]].Position);
            if (candidateSegment < precedingSegment * 0.22f)
                index--;
        }
        return index;
    }

    private static SceneNode? SelectBestAlias(
        List<HumanoidAvatarAutoMapCandidate> candidates,
        HashSet<SceneNode> assigned,
        params string[] aliases)
    {
        SceneNode? best = null;
        float bestScore = 0.0f;
        for (int i = 0; i < candidates.Count; i++)
        {
            HumanoidAvatarAutoMapCandidate candidate = candidates[i];
            if (assigned.Contains(candidate.Node))
                continue;
            float score = AliasScore(candidate.Node.Name, aliases);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate.Node;
            }
        }
        return best;
    }

    private static SceneNode? SelectEyeCandidate(
        List<HumanoidAvatarAutoMapCandidate> descendants,
        HashSet<SceneNode> assigned,
        Vector3 bodyRight,
        Vector3 bodyUp,
        Vector3 bodyForward,
        float skeletonHeight,
        float sideSign)
    {
        SceneNode? best = null;
        float bestScore = float.NegativeInfinity;
        for (int i = 0; i < descendants.Count; i++)
        {
            HumanoidAvatarAutoMapCandidate candidate = descendants[i];
            if (assigned.Contains(candidate.Node))
                continue;
            float side = Vector3.Dot(candidate.Position, bodyRight) * sideSign;
            float height = Vector3.Dot(candidate.Position, bodyUp);
            float forward = Vector3.Dot(candidate.Position, bodyForward);
            float alias = AliasScore(candidate.Node.Name, "eye", "eyeball");
            float geometry = Math.Clamp(side / (skeletonHeight * 0.02f), 0.0f, 1.0f)
                + Math.Clamp(MathF.Abs(height) + MathF.Abs(forward), 0.0f, 1.0f) * 0.01f;
            float score = alias * 0.75f + geometry * 0.25f;
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate.Node;
            }
        }
        return bestScore >= 0.35f ? best : null;
    }

    private static float CalculatePairSymmetry(
        HumanoidAvatarAutoMapCandidate? left,
        HumanoidAvatarAutoMapCandidate? right,
        Vector3 origin)
    {
        if (left is null || right is null)
            return 0.0f;
        float leftLength = Vector3.Distance(origin, left.Position);
        float rightLength = Vector3.Distance(origin, right.Position);
        float maximum = MathF.Max(leftLength, rightLength);
        return maximum <= 1e-6f
            ? 0.0f
            : Math.Clamp(1.0f - MathF.Abs(leftLength - rightLength) / maximum, 0.0f, 1.0f);
    }

    private static float EstimateMaximumLateralReach(
        SceneNode root,
        Dictionary<SceneNode, HumanoidAvatarAutoMapCandidate> byNode,
        Vector3 bodyRight,
        float originSide)
        => MathF.Max(
            EstimateMaximumSignedLateralReach(root, byNode, bodyRight, originSide, -1.0f),
            EstimateMaximumSignedLateralReach(root, byNode, bodyRight, originSide, 1.0f));

    private static float EstimateMaximumSignedLateralReach(
        SceneNode root,
        Dictionary<SceneNode, HumanoidAvatarAutoMapCandidate> byNode,
        Vector3 bodyRight,
        float originSide,
        float sideSign)
    {
        float maximum = 0.0f;
        var stack = new Stack<SceneNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            SceneNode node = stack.Pop();
            if (byNode.TryGetValue(node, out HumanoidAvatarAutoMapCandidate? candidate))
            {
                float reach = (Vector3.Dot(candidate.Position, bodyRight) - originSide) * sideSign;
                maximum = MathF.Max(maximum, reach);
            }
            foreach (var childTransform in node.Transform.Children)
                if (childTransform.SceneNode is SceneNode child)
                    stack.Push(child);
        }
        return maximum;
    }

    private static float EstimateExtremeProjection(
        SceneNode root,
        Dictionary<SceneNode, HumanoidAvatarAutoMapCandidate> byNode,
        Vector3 direction,
        bool maximum)
    {
        float extreme = maximum ? float.NegativeInfinity : float.PositiveInfinity;
        var stack = new Stack<SceneNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            SceneNode node = stack.Pop();
            if (byNode.TryGetValue(node, out HumanoidAvatarAutoMapCandidate? candidate))
            {
                float projection = Vector3.Dot(candidate.Position, direction);
                extreme = maximum ? MathF.Max(extreme, projection) : MathF.Min(extreme, projection);
            }
            foreach (var childTransform in node.Transform.Children)
                if (childTransform.SceneNode is SceneNode child)
                    stack.Push(child);
        }
        return extreme;
    }

    private static bool IsApproximatelyUpDirection(Vector3 direction)
        => MathF.Abs(Vector3.Dot(Vector3.Normalize(direction), Vector3.UnitY)) > 0.98f;

    private static bool IsStrictDescendant(SceneNode ancestor, SceneNode node)
        => !ReferenceEquals(ancestor, node) && IsDescendantOrSelf(ancestor, node);

    private static void RemoveRepeatedPathNodes(List<SceneNode> path)
    {
        for (int i = path.Count - 1; i > 0; i--)
            if (ReferenceEquals(path[i], path[i - 1]))
                path.RemoveAt(i);
    }

    private static float AliasScore(string? name, params string[] aliases)
    {
        if (string.IsNullOrWhiteSpace(name))
            return 0.0f;
        string normalized = NormalizeAlias(name);
        float best = 0.0f;
        for (int i = 0; i < aliases.Length; i++)
        {
            string alias = NormalizeAlias(aliases[i]);
            if (normalized.Equals(alias, StringComparison.Ordinal))
                return 1.0f;
            if (normalized.EndsWith(alias, StringComparison.Ordinal))
                best = MathF.Max(best, 0.9f);
            else if (normalized.Contains(alias, StringComparison.Ordinal))
                best = MathF.Max(best, 0.72f);
        }
        return best;
    }

    private static string NormalizeAlias(string value)
    {
        Span<char> buffer = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
        int count = 0;
        for (int i = 0; i < value.Length; i++)
            if (char.IsLetterOrDigit(value[i]))
                buffer[count++] = char.ToLowerInvariant(value[i]);
        return new string(buffer[..count]);
    }

    private static string GetRoleAlias(EHumanoidAvatarBoneRole role)
        => role.ToString();
}
