using System.Numerics;

namespace XREngine.Fbx;

public static class FbxDeformerParser
{
    public static FbxDeformerDocument Parse(FbxStructuralDocument structural, FbxSemanticDocument semantic)
    {
        ArgumentNullException.ThrowIfNull(structural);
        ArgumentNullException.ThrowIfNull(semantic);

        return FbxTrace.TraceOperation(
            "DeformerParser",
            $"Parsing deformers for skins={semantic.IntermediateScene.Skins.Count:N0}, clusters={semantic.IntermediateScene.Clusters.Count:N0}, blendShapes={semantic.IntermediateScene.BlendShapes.Count:N0}.",
            document => $"Parsed deformers: skinnedGeometries={document.SkinsByGeometryObjectId.Count:N0}, totalClusters={document.SkinsByGeometryObjectId.Values.Sum(static skin => skin.Clusters.Count):N0}, geometryBlendShapeSets={document.BlendShapeChannelsByGeometryObjectId.Count:N0}, blendShapeChannels={document.BlendShapeChannelsByGeometryObjectId.Values.Sum(static channels => channels.Length):N0}",
            () =>
            {
                FbxStructuralValueReader reader = new(structural);

                Dictionary<long, FbxSkinBinding> skinsByGeometryObjectId = new();
                foreach (FbxIntermediateSkin skin in semantic.IntermediateScene.Skins)
                {
                    if (!semantic.ObjectIndexById.TryGetValue(skin.ObjectId, out int skinObjectIndex))
                        continue;

                    long[] geometryObjectIds = [.. skin.ConnectedObjectIds.Where(objectId => semantic.TryGetObject(objectId, out FbxSceneObject sceneObject) && sceneObject.Category == FbxObjectCategory.Geometry)];
                    HashSet<long> clusterObjectIdsSet = [];
                    foreach (int connectionIndex in semantic.GetInboundConnectionIndices(skinObjectIndex))
                    {
                        FbxConnection connection = semantic.Connections[connectionIndex];
                        if (connection.Source.Id is not long sourceObjectId)
                            continue;
                        if (!semantic.TryGetObject(sourceObjectId, out FbxSceneObject sceneObject))
                            continue;
                        if (sceneObject.Category == FbxObjectCategory.Deformer && sceneObject.Subclass.Equals("Cluster", StringComparison.OrdinalIgnoreCase))
                            clusterObjectIdsSet.Add(sourceObjectId);
                    }

                    long[] clusterObjectIds = [.. clusterObjectIdsSet];
                    if (geometryObjectIds.Length == 0 || clusterObjectIds.Length == 0)
                        continue;

                    List<FbxClusterBinding> clusters = [];
                    foreach (long clusterObjectId in clusterObjectIds)
                    {
                        FbxClusterBinding? clusterBinding = ParseClusterBinding(reader, semantic, clusterObjectId);
                        if (clusterBinding is not null)
                            clusters.Add(clusterBinding);
                    }

                    if (clusters.Count == 0)
                        continue;

                    if (FbxTrace.IsEnabled(FbxLogVerbosity.Verbose))
                    {
                        FbxTrace.Verbose(
                            "DeformerParser",
                            $"Skin '{skin.Name}' (objectId={skin.ObjectId}) binds {clusters.Count:N0} clusters to {geometryObjectIds.Length:N0} geometry object(s).");
                    }

                    foreach (long geometryObjectId in geometryObjectIds)
                    {
                        if (skinsByGeometryObjectId.TryGetValue(geometryObjectId, out FbxSkinBinding? existingBinding))
                        {
                            skinsByGeometryObjectId[geometryObjectId] = existingBinding with
                            {
                                Clusters = existingBinding.Clusters.Concat(clusters).ToArray(),
                            };
                        }
                        else
                        {
                            skinsByGeometryObjectId[geometryObjectId] = new FbxSkinBinding(geometryObjectId, skin.ObjectId, skin.Name, clusters.ToArray());
                        }
                    }
                }

                Dictionary<long, List<FbxBlendShapeChannelBinding>> blendShapeChannelsByGeometryObjectId = new();
                Dictionary<long, FbxBlendShapeChannelBinding> blendShapeChannelsByObjectId = new();
                foreach (FbxIntermediateBlendShape blendShape in semantic.IntermediateScene.BlendShapes)
                {
                    if (!blendShape.Subclass.Equals("BlendShapeChannel", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!semantic.TryGetObject(blendShape.ObjectId, out FbxSceneObject channelObject))
                        continue;

                    long? shapeGeometryObjectId = FindConnectedObjectId(
                        blendShape.ConnectedObjectIds,
                        semantic,
                        static sceneObject => sceneObject.Category == FbxObjectCategory.Geometry && sceneObject.Subclass.Equals("Shape", StringComparison.OrdinalIgnoreCase));
                    if (!shapeGeometryObjectId.HasValue)
                        continue;

                    long? blendShapeObjectId = FindConnectedObjectId(
                        blendShape.ConnectedObjectIds,
                        semantic,
                        static sceneObject => sceneObject.Category == FbxObjectCategory.Deformer && sceneObject.Subclass.Equals("BlendShape", StringComparison.OrdinalIgnoreCase));
                    if (!blendShapeObjectId.HasValue || !semantic.TryGetObject(blendShapeObjectId.Value, out FbxSceneObject blendShapeObject))
                        continue;

                    FbxIntermediateBlendShape? blendShapeOwner = semantic.IntermediateScene.BlendShapes.FirstOrDefault(entry => entry.ObjectId == blendShapeObjectId.Value);
                    long? geometryObjectId = blendShapeOwner is not null
                        ? FindConnectedObjectId(
                            blendShapeOwner.ConnectedObjectIds,
                            semantic,
                            static sceneObject => sceneObject.Category == FbxObjectCategory.Geometry && sceneObject.Subclass.Equals("Mesh", StringComparison.OrdinalIgnoreCase))
                        : null;
                    if (!geometryObjectId.HasValue)
                        geometryObjectId = FindConnectedObjectIdToObject(semantic, blendShapeObject.Id, static sceneObject => sceneObject.Category == FbxObjectCategory.Geometry && sceneObject.Subclass.Equals("Mesh", StringComparison.OrdinalIgnoreCase));
                    if (!geometryObjectId.HasValue)
                        continue;

                    FbxBlendShapeChannelBinding? channelBinding = ParseBlendShapeChannelBinding(reader, semantic, geometryObjectId.Value, channelObject, shapeGeometryObjectId.Value);
                    if (channelBinding is null)
                        continue;

                    if (FbxTrace.IsEnabled(FbxLogVerbosity.Verbose))
                    {
                        FbxTrace.Verbose(
                            "DeformerParser",
                            $"Blend shape channel '{channelBinding.Name}' (channelObjectId={channelBinding.ChannelObjectId}) targets geometryObjectId={channelBinding.GeometryObjectId} with {channelBinding.PositionDeltasByControlPoint.Count:N0} position delta(s) and {channelBinding.NormalDeltasByControlPoint.Count:N0} normal delta(s).");
                    }

                    blendShapeChannelsByObjectId[channelBinding.ChannelObjectId] = channelBinding;
                    if (!blendShapeChannelsByGeometryObjectId.TryGetValue(channelBinding.GeometryObjectId, out List<FbxBlendShapeChannelBinding>? channels))
                    {
                        channels = [];
                        blendShapeChannelsByGeometryObjectId[channelBinding.GeometryObjectId] = channels;
                    }

                    channels.Add(channelBinding);
                }

                Dictionary<long, FbxBlendShapeChannelBinding[]> frozenBlendShapeChannels = new(blendShapeChannelsByGeometryObjectId.Count);
                foreach ((long geometryObjectId, List<FbxBlendShapeChannelBinding> bindings) in blendShapeChannelsByGeometryObjectId)
                    frozenBlendShapeChannels[geometryObjectId] = [.. bindings.OrderBy(static binding => binding.ChannelObjectId)];

                return new FbxDeformerDocument(skinsByGeometryObjectId, frozenBlendShapeChannels, blendShapeChannelsByObjectId);
            });
    }

    private static FbxClusterBinding? ParseClusterBinding(FbxStructuralValueReader reader, FbxSemanticDocument semantic, long clusterObjectId)
    {
        if (!semantic.TryGetObject(clusterObjectId, out FbxSceneObject clusterObject))
            return null;

        long boneModelObjectId = semantic.IntermediateScene.Clusters
            .First(entry => entry.ObjectId == clusterObjectId)
            .ConnectedObjectIds
            .FirstOrDefault(objectId => semantic.TryGetObject(objectId, out FbxSceneObject sceneObject) && sceneObject.Category == FbxObjectCategory.Model);
        if (boneModelObjectId == 0)
            return null;

        int[] indices = reader.TryReadInt32ArrayChild(clusterObject.NodeIndex, "Indexes") ?? [];
        double[] weights = reader.TryReadDoubleArrayChild(clusterObject.NodeIndex, "Weights") ?? [];
        if (indices.Length == 0 || weights.Length == 0)
            return null;

        Matrix4x4 transformMatrix = reader.TryReadMatrix4x4Child(clusterObject.NodeIndex, "Transform") ?? Matrix4x4.Identity;
        Matrix4x4 transformLinkMatrix = reader.TryReadMatrix4x4Child(clusterObject.NodeIndex, "TransformLink") ?? Matrix4x4.Identity;
        Matrix4x4 inverseBindMatrix = Matrix4x4.Invert(transformLinkMatrix, out Matrix4x4 inverseLink)
            ? inverseLink * transformMatrix
            : Matrix4x4.Identity;

        Dictionary<int, float> controlPointWeights = new(indices.Length);
        int pairCount = Math.Min(indices.Length, weights.Length);
        for (int index = 0; index < pairCount; index++)
        {
            int controlPointIndex = indices[index];
            float weight = (float)weights[index];
            if (controlPointIndex < 0 || weight <= 0.0f)
                continue;

            if (controlPointWeights.TryGetValue(controlPointIndex, out float existingWeight))
                controlPointWeights[controlPointIndex] = existingWeight + weight;
            else
                controlPointWeights[controlPointIndex] = weight;
        }

        if (controlPointWeights.Count == 0)
            return null;

        return new FbxClusterBinding(
            clusterObjectId,
            boneModelObjectId,
            clusterObject.DisplayName,
            transformMatrix,
            transformLinkMatrix,
            inverseBindMatrix,
            controlPointWeights);
    }

    private static FbxBlendShapeChannelBinding? ParseBlendShapeChannelBinding(
        FbxStructuralValueReader reader,
        FbxSemanticDocument semantic,
        long geometryObjectId,
        FbxSceneObject channelObject,
        long shapeGeometryObjectId)
    {
        if (!semantic.TryGetObject(shapeGeometryObjectId, out FbxSceneObject shapeGeometryObject))
            return null;

        int[] indices = reader.TryReadInt32ArrayChild(shapeGeometryObject.NodeIndex, "Indexes") ?? [];
        Vector3[] positionDeltas = reader.ReadVector3ArrayChild(shapeGeometryObject.NodeIndex, "Vertices");
        Vector3[] normalDeltas = reader.ReadVector3ArrayChild(shapeGeometryObject.NodeIndex, "Normals");
        if (positionDeltas.Length == 0)
            return null;

        if (indices.Length == 0)
        {
            indices = new int[positionDeltas.Length];
            for (int index = 0; index < indices.Length; index++)
                indices[index] = index;
        }

        Dictionary<int, Vector3> positionDeltasByControlPoint = new(indices.Length);
        Dictionary<int, Vector3> normalDeltasByControlPoint = new(indices.Length);
        int positionCount = Math.Min(indices.Length, positionDeltas.Length);
        for (int index = 0; index < positionCount; index++)
        {
            int controlPointIndex = indices[index];
            if (controlPointIndex < 0)
                continue;
            positionDeltasByControlPoint[controlPointIndex] = positionDeltas[index];
        }

        int normalCount = Math.Min(indices.Length, normalDeltas.Length);
        for (int index = 0; index < normalCount; index++)
        {
            int controlPointIndex = indices[index];
            if (controlPointIndex < 0)
                continue;
            normalDeltasByControlPoint[controlPointIndex] = normalDeltas[index];
        }

        if (positionDeltasByControlPoint.Count == 0)
            return null;

        double[] fullWeights = reader.TryReadDoubleArrayChild(channelObject.NodeIndex, "FullWeights") ?? [];
        float fullWeight = (float)fullWeights.Where(static value => value > 0.0d).DefaultIfEmpty(100.0d).Max();
        if (fullWeight <= 0.0f)
            fullWeight = 100.0f;

        float defaultDeformPercent = (float)(reader.ReadLeafDouble(channelObject.NodeIndex, "DeformPercent") ?? 0.0d);
        return new FbxBlendShapeChannelBinding(
            channelObject.Id,
            geometryObjectId,
            channelObject.DisplayName,
            defaultDeformPercent,
            fullWeight,
            positionDeltasByControlPoint,
            normalDeltasByControlPoint);
    }

    private static long? FindConnectedObjectId(
        IEnumerable<long> objectIds,
        FbxSemanticDocument semantic,
        Func<FbxSceneObject, bool> predicate)
    {
        foreach (long objectId in objectIds)
        {
            if (semantic.TryGetObject(objectId, out FbxSceneObject sceneObject) && predicate(sceneObject))
                return objectId;
        }

        return null;
    }

    private static long? FindConnectedObjectIdToObject(FbxSemanticDocument semantic, long objectId, Func<FbxSceneObject, bool> predicate)
    {
        if (!semantic.ObjectIndexById.TryGetValue(objectId, out int objectIndex))
            return null;

        foreach (int outboundConnectionIndex in semantic.GetOutboundConnectionIndices(objectIndex))
        {
            FbxConnection connection = semantic.Connections[outboundConnectionIndex];
            if (connection.Destination.Id is not long destinationObjectId)
                continue;
            if (semantic.TryGetObject(destinationObjectId, out FbxSceneObject sceneObject) && predicate(sceneObject))
                return destinationObjectId;
        }

        return null;
    }
}