using Assimp;
using System.Numerics;
using XREngine.Animation;
using XREngine.Components.Animation;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Fbx;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using Matrix4x4 = System.Numerics.Matrix4x4;

namespace XREngine;

internal static class NativeFbxSceneImporter
{
    internal readonly record struct ImportResult(
        SceneNode RootNode,
        IReadOnlyList<XRMaterial> Materials,
        IReadOnlyList<XRMesh> Meshes);

    private sealed class MeshChunkBuilder(int materialSlot)
    {
        public int MaterialSlot { get; } = materialSlot;
        public List<Vertex> Vertices { get; } = [];
        public List<ushort> Indices { get; } = [];

        public bool CanAppendPolygon(int polygonVertexCount)
            => Vertices.Count + polygonVertexCount <= ushort.MaxValue + 1;

        public void AppendPolygon(IReadOnlyList<Vertex> polygonVertices)
        {
            if (polygonVertices.Count < 3)
                return;

            int baseVertex = Vertices.Count;
            Vertices.AddRange(polygonVertices);
            for (int triangleIndex = 1; triangleIndex < polygonVertices.Count - 1; triangleIndex++)
            {
                Indices.Add((ushort)baseVertex);
                Indices.Add((ushort)(baseVertex + triangleIndex));
                Indices.Add((ushort)(baseVertex + triangleIndex + 1));
            }
        }
    }

    private sealed class ImportedClipBuilder
    {
        private readonly AnimationMember _root = new("Root", EAnimationMemberType.Group);
        private readonly AnimationMember _sceneNode = new("SceneNode", EAnimationMemberType.Property);
        private readonly Dictionary<string, AnimationMember> _nodeCache = new(StringComparer.Ordinal);

        public ImportedClipBuilder()
            => _root.Children.Add(_sceneNode);

        public AnimationMember Root => _root;

        public bool HasAnimations => CountAnimations(_root) > 0;

        public void AddTransformScalarPropertyAnimation(string nodePath, string propertyName, PropAnimFloat anim)
        {
            AnimationMember node = GetSceneNodeByPath(nodePath);
            AnimationMember transform = GetOrAddChild(node, "Transform", EAnimationMemberType.Property);
            AnimationMember property = GetOrAddChild(transform, propertyName, EAnimationMemberType.Property);
            property.Animation = anim;
        }

        public void AddBlendShapeAnimation(string nodePath, string blendshapeName, PropAnimFloat anim)
        {
            AnimationMember node = GetSceneNodeByPath(nodePath);
            AnimationMember getComponent = GetOrAddMethod(node, "GetComponent", ["ModelComponent"], -1, true);
            AnimationMember method = GetOrAddMethod(getComponent, "SetBlendShapeWeightNormalized", [blendshapeName, 0.0f, StringComparison.InvariantCultureIgnoreCase], 1, false);
            method.Animation = anim;
        }

        private AnimationMember GetSceneNodeByPath(string nodePath)
        {
            if (_nodeCache.TryGetValue(nodePath, out AnimationMember? cached))
                return cached;

            AnimationMember current = _sceneNode;
            if (!string.IsNullOrWhiteSpace(nodePath))
            {
                foreach (string segment in nodePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    current = GetOrAddMethod(current, "FindDescendantByName", [segment, StringComparison.InvariantCultureIgnoreCase], -1, true);
            }

            _nodeCache[nodePath] = current;
            return current;
        }

        private static int CountAnimations(AnimationMember member)
        {
            int count = member.Animation is null ? 0 : 1;
            foreach (AnimationMember child in member.Children)
                count += CountAnimations(child);
            return count;
        }

        private static AnimationMember GetOrAddChild(AnimationMember parent, string memberName, EAnimationMemberType memberType)
        {
            foreach (AnimationMember child in parent.Children)
            {
                if (child.MemberName == memberName && child.MemberType == memberType)
                    return child;
            }

            AnimationMember created = new(memberName, memberType);
            parent.Children.Add(created);
            return created;
        }

        private static AnimationMember GetOrAddMethod(AnimationMember parent, string methodName, object?[] methodArgs, int animatedArgIndex, bool cacheReturnValue)
        {
            foreach (AnimationMember child in parent.Children)
            {
                if (child.MemberName != methodName || child.MemberType != EAnimationMemberType.Method)
                    continue;
                if (child.AnimatedMethodArgumentIndex != animatedArgIndex)
                    continue;
                if (child.MethodArguments.Length != methodArgs.Length)
                    continue;

                bool matches = true;
                for (int index = 0; index < methodArgs.Length; index++)
                {
                    if (!Equals(child.MethodArguments[index], methodArgs[index]))
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                    return child;
            }

            AnimationMember created = new(methodName, EAnimationMemberType.Method)
            {
                MethodArguments = methodArgs,
                AnimatedMethodArgumentIndex = animatedArgIndex,
                CacheReturnValue = cacheReturnValue,
            };
            parent.Children.Add(created);
            return created;
        }
    }

    public static ImportResult Import(
        ModelImporter importer,
        string sourceFilePath,
        ModelImportOptions? importOptions,
        float scaleConversion,
        bool zUp,
        int importLayer,
        CancellationToken cancellationToken,
        Action<float>? onProgress,
        Matrix4x4? rootTransformMatrix)
    {
        ArgumentNullException.ThrowIfNull(importer);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);

        using FbxStructuralDocument structural = FbxStructuralParser.ParseFile(sourceFilePath);
        FbxSceneSemanticsPolicy policy = new(
            importOptions?.FbxPivotPolicy ?? FbxSceneSemanticsPolicy.Default.PivotImportPolicy,
            FbxModelHierarchyPolicy.PreserveAuthoredNodes);

        FbxSemanticDocument semantic = FbxSemanticParser.Parse(structural, policy);
        FbxGeometryDocument geometry = FbxGeometryParser.Parse(structural, semantic);
        FbxDeformerDocument deformers = FbxDeformerParser.Parse(structural, semantic);
        FbxAnimationDocument animations = FbxAnimationParser.Parse(structural, semantic, deformers);

        SceneNode rootNode = new(Path.GetFileNameWithoutExtension(sourceFilePath)) { Layer = importLayer };
        ApplyLocalMatrix(rootNode, CreateImportRootMatrix(scaleConversion, zUp, rootTransformMatrix));

        Dictionary<long, SceneNode> nodesByObjectId = BuildSceneNodes(rootNode, semantic, importLayer, cancellationToken);
        List<XRMaterial> createdMaterials = [];
        List<XRMesh> createdMeshes = [];
        Dictionary<long, XRMaterial> materialCache = new();
        XRMaterial? defaultMaterial = null;

        List<FbxIntermediateMesh> meshes = semantic.IntermediateScene.Meshes.OrderBy(static mesh => mesh.ObjectIndex).ToList();
        for (int meshIndex = 0; meshIndex < meshes.Count; meshIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FbxIntermediateMesh mesh = meshes[meshIndex];
            if (!geometry.TryGetMeshGeometry(mesh.ObjectId, out FbxMeshGeometry meshGeometry))
                continue;

            foreach (long modelObjectId in mesh.ModelObjectIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!nodesByObjectId.TryGetValue(modelObjectId, out SceneNode? sceneNode))
                    continue;
                if (!semantic.TryGetObject(modelObjectId, out FbxSceneObject nodeObject))
                    continue;

                List<SubMesh> subMeshes = BuildSubMeshesForNode(
                    importer,
                    sourceFilePath,
                    semantic,
                    deformers,
                    mesh,
                    meshGeometry,
                    nodeObject,
                    nodesByObjectId,
                    createdMaterials,
                    createdMeshes,
                    materialCache,
                    ref defaultMaterial,
                    cancellationToken,
                    rootNode.Transform);

                if (subMeshes.Count == 0)
                    continue;

                AttachSubMeshesToNode(sceneNode, subMeshes, mesh.Name, importOptions, deformers.GetBlendShapeChannels(mesh.ObjectId));
            }

            onProgress?.Invoke((meshIndex + 1) / (float)meshes.Count);
        }

        AttachAnimationClips(rootNode, semantic, animations, nodesByObjectId);

        return new ImportResult(rootNode, createdMaterials, createdMeshes);
    }

    private static Dictionary<long, SceneNode> BuildSceneNodes(SceneNode rootNode, FbxSemanticDocument semantic, int importLayer, CancellationToken cancellationToken)
    {
        Dictionary<long, SceneNode> nodesByObjectId = new(semantic.IntermediateScene.Nodes.Count);
        foreach (FbxIntermediateNode node in semantic.IntermediateScene.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SceneNode parent = node.ParentNodeIndex is int parentIndex
                ? nodesByObjectId[semantic.IntermediateScene.Nodes[parentIndex].ObjectId]
                : rootNode;

            SceneNode sceneNode = new(parent, node.Name) { Layer = importLayer };
            ApplyLocalMatrix(sceneNode, node.LocalTransform);
            nodesByObjectId[node.ObjectId] = sceneNode;
        }

        return nodesByObjectId;
    }

    private static void AttachSubMeshesToNode(
        SceneNode sceneNode,
        IReadOnlyList<SubMesh> subMeshes,
        string fallbackName,
        ModelImportOptions? importOptions,
        IReadOnlyList<FbxBlendShapeChannelBinding> blendShapeChannels)
    {
        bool splitSubmeshesIntoSeparateModelComponents = importOptions?.SplitSubmeshesIntoSeparateModelComponents ?? false;
        if (splitSubmeshesIntoSeparateModelComponents)
        {
            for (int index = 0; index < subMeshes.Count; index++)
            {
                SubMesh subMesh = subMeshes[index];
                ModelComponent component = sceneNode.AddComponent<ModelComponent>()!;
                component.Name = string.IsNullOrWhiteSpace(subMesh.Name)
                    ? $"{fallbackName} SubMesh {index}"
                    : subMesh.Name;
                component.Model = new Model(subMesh);
                component.Model.Meshes.ThreadSafe = true;
                ApplyDefaultBlendShapeWeights(component, blendShapeChannels);
            }

            return;
        }

        ModelComponent sharedComponent = sceneNode.AddComponent<ModelComponent>()!;
        sharedComponent.Name = string.IsNullOrWhiteSpace(sceneNode.Name) ? fallbackName : sceneNode.Name;
        sharedComponent.Model = new Model(subMeshes);
        sharedComponent.Model.Meshes.ThreadSafe = true;
        ApplyDefaultBlendShapeWeights(sharedComponent, blendShapeChannels);
    }

    private static List<SubMesh> BuildSubMeshesForNode(
        ModelImporter importer,
        string sourceFilePath,
        FbxSemanticDocument semantic,
        FbxDeformerDocument deformers,
        FbxIntermediateMesh mesh,
        FbxMeshGeometry meshGeometry,
        FbxSceneObject nodeObject,
        IReadOnlyDictionary<long, SceneNode> nodesByObjectId,
        List<XRMaterial> createdMaterials,
        List<XRMesh> createdMeshes,
        Dictionary<long, XRMaterial> materialCache,
        ref XRMaterial? defaultMaterial,
        CancellationToken cancellationToken,
        TransformBase rootTransform)
    {
        List<FbxIntermediateMaterial> nodeMaterials = semantic.IntermediateScene.Materials
            .Where(material => material.ModelObjectIds.Contains(nodeObject.Id))
            .OrderBy(static material => material.ObjectIndex)
            .ToList();

        Dictionary<int, Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>>? skinWeightsByControlPoint =
            deformers.TryGetSkinBinding(mesh.ObjectId, out FbxSkinBinding skinBinding)
                ? BuildSkinWeightsByControlPoint(skinBinding, nodesByObjectId)
                : null;
        IReadOnlyList<FbxBlendShapeChannelBinding> blendShapeChannels = deformers.GetBlendShapeChannels(mesh.ObjectId);

        Dictionary<int, List<MeshChunkBuilder>> buildersByMaterialSlot = new();
        FbxIntermediateNode intermediateNode = semantic.IntermediateScene.Nodes.First(node => node.ObjectId == nodeObject.Id);

        int polygonVertexStart = 0;
        int polygonIndex = 0;
        while (polygonVertexStart < meshGeometry.PolygonVertexIndices.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<Vertex> polygonVertices = [];
            int polygonVertexIndex = polygonVertexStart;
            while (polygonVertexIndex < meshGeometry.PolygonVertexIndices.Count)
            {
                int encodedControlPointIndex = meshGeometry.PolygonVertexIndices[polygonVertexIndex];
                bool endOfPolygon = encodedControlPointIndex < 0;
                int controlPointIndex = endOfPolygon ? ~encodedControlPointIndex : encodedControlPointIndex;
                polygonVertices.Add(CreateVertex(
                    meshGeometry,
                    intermediateNode.GeometryTransform,
                    controlPointIndex,
                    polygonVertexIndex,
                    polygonIndex,
                    skinWeightsByControlPoint,
                    blendShapeChannels));
                polygonVertexIndex++;
                if (endOfPolygon)
                    break;
            }

            int materialSlot = ResolveMaterialSlot(meshGeometry.Materials, polygonIndex, polygonVertexStart, nodeMaterials.Count);
            if (!buildersByMaterialSlot.TryGetValue(materialSlot, out List<MeshChunkBuilder>? chunks))
            {
                chunks = [];
                buildersByMaterialSlot[materialSlot] = chunks;
            }

            MeshChunkBuilder chunk = chunks.Count > 0 && chunks[^1].CanAppendPolygon(polygonVertices.Count)
                ? chunks[^1]
                : CreateChunk(chunks, materialSlot);
            chunk.AppendPolygon(polygonVertices);

            polygonVertexStart = polygonVertexIndex;
            polygonIndex++;
        }

        List<SubMesh> subMeshes = [];
        foreach ((int materialSlot, List<MeshChunkBuilder> chunks) in buildersByMaterialSlot.OrderBy(static pair => pair.Key))
        {
            XRMaterial material = ResolveMaterial(
                importer,
                sourceFilePath,
                semantic,
                nodeMaterials,
                materialSlot,
                materialCache,
                createdMaterials,
                ref defaultMaterial);

            for (int chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
            {
                MeshChunkBuilder chunk = chunks[chunkIndex];
                if (chunk.Vertices.Count == 0 || chunk.Indices.Count == 0)
                    continue;

                XRMesh xrMesh = new(chunk.Vertices, chunk.Indices);
                if (skinWeightsByControlPoint is not null && skinWeightsByControlPoint.Count > 0)
                    xrMesh.RebuildSkinningBuffersFromVertices();
                if (blendShapeChannels.Count > 0)
                {
                    xrMesh.BlendshapeNames = [.. blendShapeChannels.Select(static channel => channel.Name).Distinct(StringComparer.Ordinal)];
                    xrMesh.RebuildBlendshapeBuffersFromVertices();
                }
                createdMeshes.Add(xrMesh);

                SubMesh subMesh = new(new SubMeshLOD(material, xrMesh, 0.0f)
                {
                    GenerateAsync = importer.ImportOptions?.GenerateMeshRenderersAsync ?? false,
                })
                {
                    Name = chunks.Count == 1 ? mesh.Name : $"{mesh.Name} {chunkIndex}",
                    RootTransform = rootTransform,
                };

                subMeshes.Add(subMesh);
            }
        }

        return subMeshes;

        static MeshChunkBuilder CreateChunk(List<MeshChunkBuilder> chunks, int materialSlot)
        {
            MeshChunkBuilder chunk = new(materialSlot);
            chunks.Add(chunk);
            return chunk;
        }
    }

    private static Vertex CreateVertex(
        FbxMeshGeometry meshGeometry,
        Matrix4x4 geometryTransform,
        int controlPointIndex,
        int polygonVertexIndex,
        int polygonIndex,
        IReadOnlyDictionary<int, Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>>? skinWeightsByControlPoint,
        IReadOnlyList<FbxBlendShapeChannelBinding> blendShapeChannels)
    {
        Vector3 position = controlPointIndex >= 0 && controlPointIndex < meshGeometry.ControlPoints.Count
            ? Vector3.Transform(meshGeometry.ControlPoints[controlPointIndex], geometryTransform)
            : Vector3.Zero;

        Vertex vertex = new(position);
        Vector3 baseNormal = Vector3.Zero;
        bool hasNormal = false;
        Vector3 baseTangent = Vector3.Zero;
        bool hasTangent = false;

        if (TryResolveLayerValue(meshGeometry.Normals.FirstOrDefault(), controlPointIndex, polygonVertexIndex, polygonIndex, out Vector3 normal)
            && normal.LengthSquared() > 0.0f)
        {
            baseNormal = Vector3.Normalize(Vector3.TransformNormal(normal, geometryTransform));
            vertex.Normal = baseNormal;
            hasNormal = true;
        }

        if (TryResolveLayerValue(meshGeometry.Tangents.FirstOrDefault(), controlPointIndex, polygonVertexIndex, polygonIndex, out Vector3 tangent)
            && tangent.LengthSquared() > 0.0f)
        {
            baseTangent = Vector3.Normalize(Vector3.TransformNormal(tangent, geometryTransform));
            vertex.Tangent = baseTangent;
            hasTangent = true;
        }

        if (meshGeometry.TextureCoordinates.Count > 0)
        {
            List<Vector2> textureCoordinateSets = new(meshGeometry.TextureCoordinates.Count);
            foreach (FbxLayerElement<Vector2> layer in meshGeometry.TextureCoordinates)
                textureCoordinateSets.Add(ResolveLayerValue(layer, controlPointIndex, polygonVertexIndex, polygonIndex, Vector2.Zero));
            vertex.TextureCoordinateSets = textureCoordinateSets;
        }

        if (meshGeometry.Colors.Count > 0)
        {
            List<Vector4> colorSets = new(meshGeometry.Colors.Count);
            foreach (FbxLayerElement<Vector4> layer in meshGeometry.Colors)
                colorSets.Add(ResolveLayerValue(layer, controlPointIndex, polygonVertexIndex, polygonIndex, Vector4.One));
            vertex.ColorSets = colorSets;
        }

        if (skinWeightsByControlPoint is not null
            && controlPointIndex >= 0
            && skinWeightsByControlPoint.TryGetValue(controlPointIndex, out Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>? weights)
            && weights.Count > 0)
        {
            vertex.Weights = new Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>(weights.Count);
            foreach ((TransformBase bone, (float weight, Matrix4x4 bindInvWorldMatrix) data) in weights)
                vertex.Weights[bone] = data;
        }

        if (blendShapeChannels.Count > 0 && controlPointIndex >= 0)
        {
            foreach (FbxBlendShapeChannelBinding channel in blendShapeChannels)
            {
                bool hasPositionDelta = channel.PositionDeltasByControlPoint.TryGetValue(controlPointIndex, out Vector3 positionDelta);
                bool hasNormalDelta = channel.NormalDeltasByControlPoint.TryGetValue(controlPointIndex, out Vector3 normalDelta);
                if (!hasPositionDelta && !hasNormalDelta)
                    continue;

                Vector3 absolutePosition = vertex.Position + (hasPositionDelta ? Vector3.TransformNormal(positionDelta, geometryTransform) : Vector3.Zero);
                Vector3? absoluteNormal = hasNormal
                    ? Vector3.Normalize(baseNormal + (hasNormalDelta ? Vector3.TransformNormal(normalDelta, geometryTransform) : Vector3.Zero))
                    : null;

                vertex.Blendshapes ??= [];
                vertex.Blendshapes.Add((
                    channel.Name,
                    new VertexData
                    {
                        Position = absolutePosition,
                        Normal = absoluteNormal,
                        Tangent = hasTangent ? baseTangent : null,
                    }));
            }
        }

        return vertex;
    }

    private static Dictionary<int, Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>> BuildSkinWeightsByControlPoint(
        FbxSkinBinding skinBinding,
        IReadOnlyDictionary<long, SceneNode> nodesByObjectId)
    {
        Dictionary<int, Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>> weightsByControlPoint = new();
        foreach (FbxClusterBinding cluster in skinBinding.Clusters)
        {
            if (!nodesByObjectId.TryGetValue(cluster.BoneModelObjectId, out SceneNode? boneNode))
                continue;

            TransformBase boneTransform = boneNode.Transform;
            foreach ((int controlPointIndex, float weight) in cluster.ControlPointWeights)
            {
                if (controlPointIndex < 0 || weight <= 0.0f)
                    continue;

                if (!weightsByControlPoint.TryGetValue(controlPointIndex, out Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>? controlPointWeights))
                {
                    controlPointWeights = new Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>();
                    weightsByControlPoint[controlPointIndex] = controlPointWeights;
                }

                if (controlPointWeights.TryGetValue(boneTransform, out (float weight, Matrix4x4 bindInvWorldMatrix) existing))
                    controlPointWeights[boneTransform] = (existing.weight + weight, existing.bindInvWorldMatrix);
                else
                    controlPointWeights[boneTransform] = (weight, cluster.InverseBindMatrix);
            }
        }

        foreach (Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)> controlPointWeights in weightsByControlPoint.Values)
        {
            float totalWeight = controlPointWeights.Values.Sum(static entry => entry.weight);
            if (totalWeight <= 0.0f)
                continue;

            TransformBase[] bones = [.. controlPointWeights.Keys];
            foreach (TransformBase bone in bones)
            {
                (float weight, Matrix4x4 bindInvWorldMatrix) data = controlPointWeights[bone];
                controlPointWeights[bone] = (data.weight / totalWeight, data.bindInvWorldMatrix);
            }
        }

        return weightsByControlPoint;
    }

    private static void ApplyDefaultBlendShapeWeights(ModelComponent component, IReadOnlyList<FbxBlendShapeChannelBinding> blendShapeChannels)
    {
        foreach (FbxBlendShapeChannelBinding channel in blendShapeChannels)
        {
            if (channel.FullWeight <= 0.0f)
                continue;

            float normalizedWeight = channel.DefaultDeformPercent / channel.FullWeight;
            if (!float.IsFinite(normalizedWeight))
                continue;

            component.SetBlendShapeWeightNormalized(channel.Name, Math.Clamp(normalizedWeight, 0.0f, 1.0f), StringComparison.InvariantCultureIgnoreCase);
        }
    }

    private static void AttachAnimationClips(
        SceneNode rootNode,
        FbxSemanticDocument semantic,
        FbxAnimationDocument animations,
        IReadOnlyDictionary<long, SceneNode> nodesByObjectId)
    {
        if (animations.Stacks.Count == 0)
            return;

        Dictionary<long, FbxIntermediateMesh> meshesByObjectId = semantic.IntermediateScene.Meshes.ToDictionary(static mesh => mesh.ObjectId);
        foreach (FbxAnimationStackBinding stack in animations.Stacks)
        {
            ImportedClipBuilder builder = new();

            foreach (FbxNodeAnimationBinding nodeAnimation in stack.NodeAnimations)
            {
                if (!nodesByObjectId.TryGetValue(nodeAnimation.ModelObjectId, out SceneNode? sceneNode))
                    continue;
                if (!semantic.TryGetObject(nodeAnimation.ModelObjectId, out FbxSceneObject nodeObject))
                    continue;

                string nodePath = BuildNodePath(rootNode, sceneNode);
                Vector3 defaultTranslation = GetVectorProperty(nodeObject, "Lcl Translation") ?? Vector3.Zero;
                Vector3 defaultRotationDegrees = GetVectorProperty(nodeObject, "Lcl Rotation") ?? Vector3.Zero;
                Vector3 defaultScale = GetVectorProperty(nodeObject, "Lcl Scaling") ?? Vector3.One;

                AddScalarClip(builder, nodePath, "TranslationX", nodeAnimation.TranslationX, defaultTranslation.X);
                AddScalarClip(builder, nodePath, "TranslationY", nodeAnimation.TranslationY, defaultTranslation.Y);
                AddScalarClip(builder, nodePath, "TranslationZ", nodeAnimation.TranslationZ, defaultTranslation.Z);
                AddScalarClip(builder, nodePath, "ScaleX", nodeAnimation.ScaleX, defaultScale.X);
                AddScalarClip(builder, nodePath, "ScaleY", nodeAnimation.ScaleY, defaultScale.Y);
                AddScalarClip(builder, nodePath, "ScaleZ", nodeAnimation.ScaleZ, defaultScale.Z);

                AddQuaternionClips(builder, nodePath, nodeAnimation, defaultRotationDegrees);
            }

            foreach (FbxBlendShapeAnimationBinding blendShapeAnimation in stack.BlendShapeAnimations)
            {
                if (!meshesByObjectId.TryGetValue(blendShapeAnimation.GeometryObjectId, out FbxIntermediateMesh? mesh))
                    continue;

                foreach (long modelObjectId in mesh.ModelObjectIds)
                {
                    if (!nodesByObjectId.TryGetValue(modelObjectId, out SceneNode? modelNode))
                        continue;

                    string nodePath = BuildNodePath(rootNode, modelNode);
                    float defaultWeight = blendShapeAnimation.FullWeight > 0.0f
                        ? blendShapeAnimation.DefaultDeformPercent / blendShapeAnimation.FullWeight
                        : 0.0f;
                    PropAnimFloat weightAnimation = CreateFloatAnimation(blendShapeAnimation.WeightCurve, defaultWeight, NormalizeBlendShapeWeight(blendShapeAnimation.FullWeight));
                    builder.AddBlendShapeAnimation(nodePath, blendShapeAnimation.BlendShapeName, weightAnimation);
                }
            }

            if (!builder.HasAnimations)
                continue;

            SortedSet<long> stackKeyTimes = [.. stack.EnumerateKeyTimes()];
            AnimationClip clip = new(builder.Root)
            {
                Name = string.IsNullOrWhiteSpace(stack.Name) ? $"AnimationStack_{stack.StackObjectId}" : stack.Name,
                ClipKind = EAnimationClipKind.GenericTransform,
                Looped = false,
                LengthInSeconds = stackKeyTimes.Count > 0 ? FbxAnimationTime.ToSeconds(stackKeyTimes.Max) : 0.0f,
                SampleRate = DetermineSampleRate(stackKeyTimes),
                TotalAnimCount = CountAnimatedMembers(builder.Root),
            };

            AnimationClipComponent component = rootNode.AddComponent<AnimationClipComponent>()!;
            component.Name = clip.Name;
            component.Animation = clip;
        }
    }

    private static void AddScalarClip(
        ImportedClipBuilder builder,
        string nodePath,
        string propertyName,
        FbxScalarCurve? curve,
        float defaultValue)
    {
        if (curve is null)
            return;

        PropAnimFloat anim = CreateFloatAnimation(curve, defaultValue, 1.0f);
        builder.AddTransformScalarPropertyAnimation(nodePath, propertyName, anim);
    }

    private static void AddQuaternionClips(ImportedClipBuilder builder, string nodePath, FbxNodeAnimationBinding nodeAnimation, Vector3 defaultRotationDegrees)
    {
        FbxScalarCurve?[] rotationCurves = [nodeAnimation.RotationX, nodeAnimation.RotationY, nodeAnimation.RotationZ];
        if (rotationCurves.All(static curve => curve is null))
            return;

        SortedSet<long> keyTimes = [];
        foreach (FbxScalarCurve? curve in rotationCurves)
        {
            if (curve is null)
                continue;
            foreach (long keyTime in curve.KeyTimes)
                keyTimes.Add(keyTime);
        }
        keyTimes.Add(0L);

        PropAnimFloat quaternionX = new(keyTimes.Count > 0 ? FbxAnimationTime.ToSeconds(keyTimes.Max) : 0.0f, false, true);
        PropAnimFloat quaternionY = new(keyTimes.Count > 0 ? FbxAnimationTime.ToSeconds(keyTimes.Max) : 0.0f, false, true);
        PropAnimFloat quaternionZ = new(keyTimes.Count > 0 ? FbxAnimationTime.ToSeconds(keyTimes.Max) : 0.0f, false, true);
        PropAnimFloat quaternionW = new(keyTimes.Count > 0 ? FbxAnimationTime.ToSeconds(keyTimes.Max) : 0.0f, false, true);

        foreach (long keyTime in keyTimes)
        {
            Vector3 sampledEulerDegrees = new(
                EvaluateCurveOrDefault(nodeAnimation.RotationX, keyTime, defaultRotationDegrees.X),
                EvaluateCurveOrDefault(nodeAnimation.RotationY, keyTime, defaultRotationDegrees.Y),
                EvaluateCurveOrDefault(nodeAnimation.RotationZ, keyTime, defaultRotationDegrees.Z));
            Quaternion rotation = CreateQuaternionFromEulerDegrees(sampledEulerDegrees);
            float second = FbxAnimationTime.ToSeconds(keyTime);
            quaternionX.Keyframes.Add(CreateLinearKeyframe(second, rotation.X));
            quaternionY.Keyframes.Add(CreateLinearKeyframe(second, rotation.Y));
            quaternionZ.Keyframes.Add(CreateLinearKeyframe(second, rotation.Z));
            quaternionW.Keyframes.Add(CreateLinearKeyframe(second, rotation.W));
        }

        builder.AddTransformScalarPropertyAnimation(nodePath, "QuaternionX", quaternionX);
        builder.AddTransformScalarPropertyAnimation(nodePath, "QuaternionY", quaternionY);
        builder.AddTransformScalarPropertyAnimation(nodePath, "QuaternionZ", quaternionZ);
        builder.AddTransformScalarPropertyAnimation(nodePath, "QuaternionW", quaternionW);
    }

    private static PropAnimFloat CreateFloatAnimation(FbxScalarCurve curve, float defaultValue, float valueScale)
    {
        SortedSet<long> keyTimes = [.. curve.KeyTimes];
        keyTimes.Add(0L);

        PropAnimFloat anim = new(keyTimes.Count > 0 ? FbxAnimationTime.ToSeconds(keyTimes.Max) : 0.0f, false, true);
        foreach (long keyTime in keyTimes)
        {
            float value = EvaluateCurveOrDefault(curve, keyTime, defaultValue) * valueScale;
            anim.Keyframes.Add(CreateLinearKeyframe(FbxAnimationTime.ToSeconds(keyTime), value));
        }

        return anim;
    }

    private static FloatKeyframe CreateLinearKeyframe(float second, float value)
        => new()
        {
            SyncInOutValues = true,
            SyncInOutTangentDirections = true,
            SyncInOutTangentMagnitudes = true,
            Second = second,
            InValue = value,
            OutValue = value,
            InterpolationTypeIn = EVectorInterpType.Linear,
            InterpolationTypeOut = EVectorInterpType.Linear,
        };

    private static float EvaluateCurveOrDefault(FbxScalarCurve? curve, long keyTime, float defaultValue)
    {
        if (curve is null || !curve.HasKeys)
            return defaultValue;
        if (curve.KeyTimes[0] > keyTime)
            return defaultValue;
        return curve.Evaluate(keyTime);
    }

    private static Quaternion CreateQuaternionFromEulerDegrees(Vector3 eulerDegrees)
    {
        Vector3 radians = new(
            float.DegreesToRadians(eulerDegrees.X),
            float.DegreesToRadians(eulerDegrees.Y),
            float.DegreesToRadians(eulerDegrees.Z));
        return Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(radians.Y, radians.X, radians.Z));
    }

    private static string BuildNodePath(SceneNode rootNode, SceneNode targetNode)
    {
        if (ReferenceEquals(rootNode, targetNode))
            return string.Empty;

        Stack<string> segments = new();
        SceneNode? current = targetNode;
        while (current is not null && !ReferenceEquals(current, rootNode))
        {
            if (!string.IsNullOrWhiteSpace(current.Name))
                segments.Push(current.Name);
            current = current.Transform.Parent?.SceneNode;
        }

        return string.Join('/', segments);
    }

    private static int DetermineSampleRate(IEnumerable<long> keyTimes)
    {
        long previous = -1;
        long minDelta = long.MaxValue;
        foreach (long keyTime in keyTimes.OrderBy(static value => value))
        {
            if (previous >= 0)
            {
                long delta = keyTime - previous;
                if (delta > 0 && delta < minDelta)
                    minDelta = delta;
            }

            previous = keyTime;
        }

        if (minDelta == long.MaxValue)
            return 30;

        return Math.Clamp((int)Math.Round(FbxAnimationTime.TicksPerSecond / (double)minDelta), 1, 240);
    }

    private static int CountAnimatedMembers(AnimationMember member)
    {
        int count = member.Animation is null ? 0 : 1;
        foreach (AnimationMember child in member.Children)
            count += CountAnimatedMembers(child);
        return count;
    }

    private static float NormalizeBlendShapeWeight(float fullWeight)
        => fullWeight > 0.0f ? 1.0f / fullWeight : 1.0f;

    private static int ResolveMaterialSlot(FbxLayerElement<int>? layer, int polygonIndex, int polygonVertexStart, int materialCount)
    {
        if (materialCount <= 0 || layer is null)
            return 0;

        int resolved = ResolveLayerValue(layer, 0, polygonVertexStart, polygonIndex, 0);
        if (resolved < 0)
            return 0;
        if (resolved >= materialCount)
            return materialCount - 1;
        return resolved;
    }

    private static XRMaterial ResolveMaterial(
        ModelImporter importer,
        string sourceFilePath,
        FbxSemanticDocument semantic,
        IReadOnlyList<FbxIntermediateMaterial> nodeMaterials,
        int materialSlot,
        Dictionary<long, XRMaterial> materialCache,
        List<XRMaterial> createdMaterials,
        ref XRMaterial? defaultMaterial)
    {
        if (materialSlot >= 0 && materialSlot < nodeMaterials.Count)
        {
            FbxIntermediateMaterial intermediateMaterial = nodeMaterials[materialSlot];
            if (materialCache.TryGetValue(intermediateMaterial.ObjectId, out XRMaterial? cachedMaterial))
                return cachedMaterial;

            semantic.TryGetObject(intermediateMaterial.ObjectId, out FbxSceneObject sceneMaterial);
            string materialName = sceneMaterial?.DisplayName ?? intermediateMaterial.Name;
            bool transparentBlendHint = ShouldUseTransparentBlend(sceneMaterial);

            List<TextureSlot> textureSlots = BuildTextureSlots(semantic, intermediateMaterial.ObjectId, transparentBlendHint);
            XRMaterial material = importer.MaterialFactory(sourceFilePath, materialName, textureSlots, 0, ShadingMode.Phong, []);
            ApplyMaterialPropertyOverrides(material, sceneMaterial, textureSlots.Count == 0);

            materialCache[intermediateMaterial.ObjectId] = material;
            createdMaterials.Add(material);
            return material;
        }

        defaultMaterial ??= importer.MaterialFactory(sourceFilePath, "DefaultMaterial", [], 0, ShadingMode.Phong, []);
        if (!createdMaterials.Contains(defaultMaterial))
            createdMaterials.Add(defaultMaterial);
        return defaultMaterial;
    }

    private static List<TextureSlot> BuildTextureSlots(FbxSemanticDocument semantic, long materialObjectId, bool transparentBlendHint)
    {
        List<TextureSlot> textureSlots = [];
        foreach (FbxConnection connection in semantic.Connections)
        {
            if (connection.Destination.Id != materialObjectId || connection.Source.Id is not long textureObjectId)
                continue;
            if (!semantic.TryGetObject(textureObjectId, out FbxSceneObject textureObject) || textureObject.Category != FbxObjectCategory.Texture)
                continue;

            string? filePath = ResolveTextureFilePath(semantic, textureObject);
            if (string.IsNullOrWhiteSpace(filePath))
                continue;

            TextureType textureType = MapTextureType(connection.PropertyName, textureObject, filePath);
            int flags = transparentBlendHint && textureType is TextureType.Diffuse or TextureType.BaseColor ? 0x2 : 0;
            textureSlots.Add(new TextureSlot(filePath, textureType, 0, default, 0, 1.0f, default, default, default, flags));
        }

        return textureSlots;
    }

    private static string? ResolveTextureFilePath(FbxSemanticDocument semantic, FbxSceneObject textureObject)
    {
        string? path = GetInlineAttribute(textureObject, "RelativeFilename")
            ?? GetInlineAttribute(textureObject, "FileName")
            ?? GetInlineAttribute(textureObject, "Filename")
            ?? GetInlineAttribute(textureObject, "Path");
        if (!string.IsNullOrWhiteSpace(path))
            return path;

        if (!semantic.ObjectIndexById.TryGetValue(textureObject.Id, out int textureObjectIndex))
            return null;

        foreach (int inboundConnectionIndex in semantic.GetInboundConnectionIndices(textureObjectIndex))
        {
            FbxConnection connection = semantic.Connections[inboundConnectionIndex];
            if (connection.Source.Id is not long videoObjectId)
                continue;
            if (!semantic.TryGetObject(videoObjectId, out FbxSceneObject videoObject) || videoObject.Category != FbxObjectCategory.Video)
                continue;

            path = GetInlineAttribute(videoObject, "RelativeFilename")
                ?? GetInlineAttribute(videoObject, "FileName")
                ?? GetInlineAttribute(videoObject, "Filename")
                ?? GetInlineAttribute(videoObject, "Path");
            if (!string.IsNullOrWhiteSpace(path))
                return path;
        }

        return null;
    }

    private static TextureType MapTextureType(string? propertyName, FbxSceneObject textureObject, string filePath)
    {
        string basis = propertyName ?? textureObject.DisplayName;
        basis = basis + " " + Path.GetFileNameWithoutExtension(filePath);

        return basis.ToLowerInvariant() switch
        {
            var text when text.Contains("normal") => TextureType.NormalCamera,
            var text when text.Contains("bump") || text.Contains("height") => TextureType.Height,
            var text when text.Contains("specular") || text.Contains("shininess") => TextureType.Specular,
            var text when text.Contains("opacity") || text.Contains("transparent") || text.Contains("alpha") => TextureType.Opacity,
            var text when text.Contains("emiss") => TextureType.Emissive,
            var text when text.Contains("metal") => TextureType.Metalness,
            var text when text.Contains("rough") => TextureType.Roughness,
            var text when text.Contains("basecolor") || text.Contains("diffuse") => TextureType.BaseColor,
            _ => TextureType.Diffuse,
        };
    }

    private static void ApplyMaterialPropertyOverrides(XRMaterial material, FbxSceneObject? materialObject, bool noTextures)
    {
        if (materialObject is null)
            return;

        Vector3 diffuseColor = GetVectorProperty(materialObject, "DiffuseColor")
            ?? GetVectorProperty(materialObject, "BaseColor")
            ?? new Vector3((float)ColorF4.Magenta.R, (float)ColorF4.Magenta.G, (float)ColorF4.Magenta.B);
        float opacity = GetFloatProperty(materialObject, "Opacity") ?? 1.0f;
        float roughness = GetFloatProperty(materialObject, "Roughness") ?? 1.0f;
        float metallic = GetFloatProperty(materialObject, "Metalness") ?? 0.0f;
        Vector3 emissive = GetVectorProperty(materialObject, "EmissiveColor") ?? Vector3.Zero;

        if (noTextures)
            material.SetVector3("BaseColor", diffuseColor);
        material.SetFloat("Opacity", opacity);
        material.SetFloat("Roughness", roughness);
        material.SetFloat("Metallic", metallic);
        material.SetFloat("Emission", emissive.LengthSquared() > 0.0f ? 1.0f : 0.0f);
    }

    private static bool ShouldUseTransparentBlend(FbxSceneObject? materialObject)
    {
        if (materialObject is null)
            return false;

        float opacity = GetFloatProperty(materialObject, "Opacity") ?? 1.0f;
        float transparencyFactor = GetFloatProperty(materialObject, "TransparencyFactor") ?? 0.0f;
        Vector3 transparentColor = GetVectorProperty(materialObject, "TransparentColor") ?? Vector3.Zero;
        return opacity < 0.999f || transparencyFactor > 0.001f || transparentColor.LengthSquared() > 0.0f;
    }

    private static string? GetInlineAttribute(FbxSceneObject sceneObject, string key)
        => sceneObject.InlineAttributes.TryGetValue(key, out FbxSemanticValue value) ? value.AsString() : null;

    private static float? GetFloatProperty(FbxSceneObject sceneObject, string name)
    {
        if (!sceneObject.Properties.TryGetValue(name, out FbxPropertyEntry? property))
            return null;

        float value = (float)property.GetDoubleOrDefault(0, double.NaN);
        return float.IsNaN(value) ? null : value;
    }

    private static Vector3? GetVectorProperty(FbxSceneObject sceneObject, string name)
    {
        if (!sceneObject.Properties.TryGetValue(name, out FbxPropertyEntry? property))
            return null;

        Vector3 value = property.GetVector3OrDefault(new Vector3(float.NaN));
        return float.IsNaN(value.X) || float.IsNaN(value.Y) || float.IsNaN(value.Z)
            ? null
            : value;
    }

    private static bool TryResolveLayerValue<T>(FbxLayerElement<T>? layer, int controlPointIndex, int polygonVertexIndex, int polygonIndex, out T value)
    {
        if (layer is null)
        {
            value = default!;
            return false;
        }

        value = ResolveLayerValue(layer, controlPointIndex, polygonVertexIndex, polygonIndex, default!);
        return true;
    }

    private static T ResolveLayerValue<T>(FbxLayerElement<T> layer, int controlPointIndex, int polygonVertexIndex, int polygonIndex, T fallback)
    {
        int lookupIndex = layer.MappingType switch
        {
            FbxLayerElementMappingType.ByControlPoint => controlPointIndex,
            FbxLayerElementMappingType.ByPolygonVertex => polygonVertexIndex,
            FbxLayerElementMappingType.ByPolygon => polygonIndex,
            FbxLayerElementMappingType.AllSame => 0,
            _ => 0,
        };

        int directIndex = layer.ReferenceType switch
        {
            FbxLayerElementReferenceType.Direct => lookupIndex,
            FbxLayerElementReferenceType.Index or FbxLayerElementReferenceType.IndexToDirect
                => lookupIndex >= 0 && lookupIndex < layer.Indices.Count ? layer.Indices[lookupIndex] : lookupIndex,
            _ => lookupIndex,
        };

        return directIndex >= 0 && directIndex < layer.DirectValues.Count
            ? layer.DirectValues[directIndex]
            : fallback;
    }

    private static Matrix4x4 CreateImportRootMatrix(float scaleConversion, bool zUp, Matrix4x4? rootTransformMatrix)
    {
        Matrix4x4 importMatrix = Matrix4x4.Identity;
        if (scaleConversion != 1.0f)
            importMatrix *= Matrix4x4.CreateScale(scaleConversion);
        if (zUp)
            importMatrix *= Matrix4x4.CreateRotationX(float.DegreesToRadians(90.0f));
        if (rootTransformMatrix.HasValue)
            importMatrix *= rootTransformMatrix.Value;
        return importMatrix;
    }

    private static void ApplyLocalMatrix(SceneNode sceneNode, Matrix4x4 localMatrix)
    {
        Transform transform = sceneNode.GetTransformAs<Transform>(true)!;
        transform.DeriveLocalMatrix(localMatrix);
        transform.RecalculateMatrices(true, false);
        transform.SaveBindState();
    }
}