using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;
using YamlDotNet.RepresentationModel;

namespace XREngine.Scene.Importers;

internal static partial class UnitySceneImporter
{
    private static readonly HashSet<string> SupportedExternalModelExtensions =
    [
        ".3d",
        ".3ds",
        ".3mf",
        ".ac",
        ".acc",
        ".amj",
        ".ase",
        ".ask",
        ".b3d",
        ".bvh",
        ".csm",
        ".cob",
        ".dae",
        ".dxf",
        ".enff",
        ".fbx",
        ".gltf",
        ".glb",
        ".hmb",
        ".ifc",
        ".iqm",
        ".irr",
        ".irrmesh",
        ".lwo",
        ".lws",
        ".lxo",
        ".m3d",
        ".md2",
        ".md3",
        ".md5anim",
        ".md5camera",
        ".md5mesh",
        ".mdc",
        ".mdl",
        ".mesh",
        ".mesh.xml",
        ".mot",
        ".ms3d",
        ".ndo",
        ".nff",
        ".obj",
        ".off",
        ".ogex",
        ".ply",
        ".pmx",
        ".prj",
        ".q3o",
        ".q3s",
        ".raw",
        ".scn",
        ".sib",
        ".smd",
        ".stl",
        ".stp",
        ".step",
        ".ter",
        ".uc",
        ".usd",
        ".usda",
        ".usdc",
        ".usdz",
        ".vta",
        ".x",
        ".x3d",
        ".xgl",
        ".zgl",
    ];

    private static void AttachSupportedComponents(ParsedUnityFile parsed, ImportedHierarchy hierarchy, ImportState state)
    {
        foreach ((long gameObjectFileId, SceneNode node) in hierarchy.NodesByGameObjectId.OrderBy(static pair => pair.Key))
        {
            if (parsed.CamerasByGameObjectId.TryGetValue(gameObjectFileId, out ParsedCamera? camera))
                RegisterAttachedComponent(camera.FileId, AttachCameraComponent(node, camera), hierarchy);

            if (parsed.LightsByGameObjectId.TryGetValue(gameObjectFileId, out ParsedLight? light))
                RegisterAttachedComponent(light.FileId, AttachLightComponent(node, light), hierarchy);

            if (parsed.SkinnedMeshRenderersByGameObjectId.TryGetValue(gameObjectFileId, out ParsedSkinnedMeshRenderer? skinnedMeshRenderer))
            {
                RegisterAttachedComponent(
                    skinnedMeshRenderer.FileId,
                    AttachSkinnedMeshRendererComponent(node, hierarchy, skinnedMeshRenderer, state),
                    hierarchy);
                continue;
            }

            if (parsed.MeshRenderersByGameObjectId.TryGetValue(gameObjectFileId, out ParsedMeshRenderer? meshRenderer) &&
                parsed.MeshFiltersByGameObjectId.TryGetValue(gameObjectFileId, out ParsedMeshFilter? meshFilter))
            {
                RegisterAttachedComponent(
                    meshRenderer.FileId,
                    AttachMeshRendererComponent(node, hierarchy, meshFilter, meshRenderer, state),
                    hierarchy);
            }
        }
    }

    private static void ApplyPrefabRemovals(ParsedPrefabInstance prefabInstance, ImportedHierarchy hierarchy)
    {
        foreach (UnityReference removedGameObject in prefabInstance.RemovedGameObjects)
        {
            if (TryResolveTargetNode(hierarchy, removedGameObject.FileId, out SceneNode? node) && node is not null)
                RemoveNodeFromHierarchy(node, hierarchy);
        }

        foreach (UnityReference removedComponent in prefabInstance.RemovedComponents)
        {
            if (!hierarchy.ComponentsByFileId.TryGetValue(removedComponent.FileId, out XRComponent? component))
                continue;

            hierarchy.ComponentsByFileId.Remove(removedComponent.FileId);

            // Components must be detached from the owning SceneNode before destruction,
            // otherwise the node still reports them through TryGetComponent/GetComponents.
            component.SceneNode.DetachComponent(component);
            component.Destroy();
        }
    }

    private static void ApplyPrefabAdditions(
        ParsedPrefabInstance prefabInstance,
        ParsedUnityFile ownerFile,
        ImportedHierarchy ownerHierarchy,
        ImportedHierarchy prefabHierarchy,
        ImportState state)
    {
        foreach (AddedGameObjectDelta addedGameObject in prefabInstance.AddedGameObjects)
        {
            if (!TryResolveTargetNode(prefabHierarchy, addedGameObject.TargetCorrespondingSourceObject.FileId, out SceneNode? resolvedTargetNode) ||
                resolvedTargetNode is not SceneNode targetNode ||
                !TryResolveAddedSceneNode(ownerFile, ownerHierarchy, addedGameObject.AddedObject, out SceneNode? resolvedAddedNode, out long? addedTransformFileId) ||
                resolvedAddedNode is not SceneNode addedNode)
            {
                continue;
            }

            if (addedTransformFileId.HasValue)
                ownerHierarchy.ExcludedRootTransformIds.Add(addedTransformFileId.Value);

            ReinsertChild(targetNode, addedNode, addedGameObject.InsertIndex);
        }

        foreach (AddedComponentDelta addedComponent in prefabInstance.AddedComponents)
        {
            if (!TryResolveTargetNode(prefabHierarchy, addedComponent.TargetCorrespondingSourceObject.FileId, out SceneNode? resolvedTargetNode) ||
                resolvedTargetNode is not SceneNode targetNode ||
                !ownerFile.ComponentsByFileId.TryGetValue(addedComponent.AddedObject.FileId, out ParsedUnityComponent? componentDefinition))
            {
                continue;
            }

            XRComponent? attachedComponent = AttachSpecificComponent(targetNode, ownerHierarchy, componentDefinition, state);
            RegisterAttachedComponent(componentDefinition.FileId, attachedComponent, prefabHierarchy);
        }
    }

    private static void ApplyComponentModifications(XRComponent component, IEnumerable<PropertyModification> modifications)
    {
        switch (component)
        {
            case CameraComponent cameraComponent:
                ApplyCameraModifications(cameraComponent, modifications);
                break;
            case LightComponent lightComponent:
                ApplyLightModifications(lightComponent, modifications);
                break;
            case ModelComponent modelComponent:
                ApplyModelComponentModifications(modelComponent, modifications);
                break;
            default:
                foreach (PropertyModification modification in modifications)
                    if (modification.PropertyPath == "m_Enabled" && TryParseBool(modification.Value, out bool enabled))
                        component.IsActive = enabled;

                break;
        }
    }

    private static void ApplyCameraModifications(CameraComponent component, IEnumerable<PropertyModification> modifications)
    {
        XRCameraParameters parameters = component.CameraParameters;
        float nearPlane = parameters.NearZ;
        float farPlane = parameters.FarZ;
        float fieldOfView = parameters.GetApproximateVerticalFov();
        bool orthographic = parameters is XROrthographicCameraParameters;
        float orthographicSize = parameters is XROrthographicCameraParameters orthoParams
            ? orthoParams.Height * 0.5f
            : 5.0f;

        foreach (PropertyModification modification in modifications)
        {
            switch (modification.PropertyPath)
            {
                case "m_Enabled":
                    if (TryParseBool(modification.Value, out bool enabled))
                        component.IsActive = enabled;
                    break;
                case "near clip plane":
                    if (TryParseFloat(modification.Value, out float nearValue))
                        nearPlane = nearValue;
                    break;
                case "far clip plane":
                    if (TryParseFloat(modification.Value, out float farValue))
                        farPlane = farValue;
                    break;
                case "field of view":
                    if (TryParseFloat(modification.Value, out float fieldOfViewValue))
                        fieldOfView = fieldOfViewValue;
                    break;
                case "orthographic":
                    if (TryParseBool(modification.Value, out bool orthographicValue))
                        orthographic = orthographicValue;
                    break;
                case "orthographic size":
                    if (TryParseFloat(modification.Value, out float orthographicSizeValue))
                        orthographicSize = orthographicSizeValue;
                    break;
            }
        }

        if (orthographic)
        {
            component.CameraParameters = new XROrthographicCameraParameters(
                orthographicSize * 2.0f,
                orthographicSize * 2.0f,
                nearPlane,
                farPlane)
            {
                InheritAspectRatio = true,
            };
        }
        else
        {
            component.SetPerspective(fieldOfView, nearPlane, farPlane, null);
        }
    }

    private static void ApplyLightModifications(LightComponent component, IEnumerable<PropertyModification> modifications)
    {
        Vector4 color = new(component.Color.R, component.Color.G, component.Color.B, 1.0f);
        float intensity = component is DirectionalLightComponent
            ? component.DiffuseIntensity
            : component switch
            {
                PointLightComponent pointLight => pointLight.Brightness,
                SpotLightComponent spotLight => spotLight.Brightness,
                _ => component.DiffuseIntensity,
            };

        float range = component switch
        {
            PointLightComponent pointLight => pointLight.Radius,
            SpotLightComponent spotLight => spotLight.Distance,
            _ => 10.0f,
        };

        float spotAngle = component is SpotLightComponent spotComponent
            ? spotComponent.OuterCutoffAngleDegrees * 2.0f
            : 30.0f;
        float innerSpotAngle = component is SpotLightComponent currentSpotComponent
            ? currentSpotComponent.InnerCutoffAngleDegrees * 2.0f
            : 20.0f;

        foreach (PropertyModification modification in modifications)
        {
            switch (modification.PropertyPath)
            {
                case "m_Enabled":
                    if (TryParseBool(modification.Value, out bool enabled))
                        component.IsActive = enabled;
                    break;
                case "m_Color.r":
                    if (TryParseFloat(modification.Value, out float red))
                        color.X = red;
                    break;
                case "m_Color.g":
                    if (TryParseFloat(modification.Value, out float green))
                        color.Y = green;
                    break;
                case "m_Color.b":
                    if (TryParseFloat(modification.Value, out float blue))
                        color.Z = blue;
                    break;
                case "m_Color.a":
                    if (TryParseFloat(modification.Value, out float alpha))
                        color.W = alpha;
                    break;
                case "m_Intensity":
                    if (TryParseFloat(modification.Value, out float parsedIntensity))
                        intensity = parsedIntensity;
                    break;
                case "m_Range":
                    if (TryParseFloat(modification.Value, out float parsedRange))
                        range = parsedRange;
                    break;
                case "m_SpotAngle":
                    if (TryParseFloat(modification.Value, out float parsedSpotAngle))
                        spotAngle = parsedSpotAngle;
                    break;
                case "m_InnerSpotAngle":
                    if (TryParseFloat(modification.Value, out float parsedInnerSpotAngle))
                        innerSpotAngle = parsedInnerSpotAngle;
                    break;
                case "m_Shadows.m_Type":
                    if (TryParseInt(modification.Value, out int shadowType))
                        component.CastsShadows = shadowType != 0;
                    break;
            }
        }

        component.Color = new ColorF3(color.X, color.Y, color.Z);

        switch (component)
        {
            case DirectionalLightComponent directionalLight:
                directionalLight.DiffuseIntensity = intensity;
                break;
            case PointLightComponent pointLight:
                pointLight.Brightness = intensity;
                pointLight.Radius = MathF.Max(range, 0.001f);
                pointLight.DiffuseIntensity = 1.0f;
                break;
            case SpotLightComponent spotLight:
                spotLight.Brightness = intensity;
                spotLight.Distance = MathF.Max(range, 0.001f);
                spotLight.OuterCutoffAngleDegrees = MathF.Max(spotAngle * 0.5f, 0.001f);
                spotLight.InnerCutoffAngleDegrees = Math.Clamp(innerSpotAngle * 0.5f, 0.0f, MathF.Max(spotAngle * 0.5f, 0.001f));
                spotLight.DiffuseIntensity = 1.0f;
                break;
            default:
                component.DiffuseIntensity = intensity;
                break;
        }
    }

    private static void ApplyModelComponentModifications(ModelComponent component, IEnumerable<PropertyModification> modifications)
    {
        foreach (PropertyModification modification in modifications)
        {
            switch (modification.PropertyPath)
            {
                case "m_Enabled":
                    if (TryParseBool(modification.Value, out bool enabled))
                        component.IsActive = enabled;
                    break;
                case "m_CastShadows":
                    if (TryParseBool(modification.Value, out bool castShadows))
                        component.MeshCastsShadows = castShadows;
                    break;
                case "m_ReceiveShadows":
                    if (TryParseBool(modification.Value, out bool receiveShadows))
                        SetReceivesShadows(component, receiveShadows);
                    break;
            }
        }
    }

    private static XRComponent? AttachSpecificComponent(
        SceneNode node,
        ImportedHierarchy hierarchy,
        ParsedUnityComponent componentDefinition,
        ImportState state)
        => componentDefinition switch
        {
            ParsedCamera camera => AttachCameraComponent(node, camera),
            ParsedLight light => AttachLightComponent(node, light),
            ParsedSkinnedMeshRenderer skinnedMeshRenderer => AttachSkinnedMeshRendererComponent(node, hierarchy, skinnedMeshRenderer, state),
            _ => null,
        };

    private static ModelComponent? AttachMeshRendererComponent(
        SceneNode node,
        ImportedHierarchy hierarchy,
        ParsedMeshFilter meshFilter,
        ParsedMeshRenderer meshRenderer,
        ImportState state)
    {
        Model? model = ResolveModelFromMeshReference(node, hierarchy, meshFilter.MeshReference, meshRenderer.Materials, null, state);
        if (model is null)
            return null;

        ModelComponent? component = GetOrAddComponent<ModelComponent>(node);
        if (component is null)
            return null;

        component.Model = model;
        component.IsActive = meshRenderer.Enabled;
        component.MeshCastsShadows = meshRenderer.CastShadows;
        SetReceivesShadows(component, meshRenderer.ReceiveShadows);
        return component;
    }

    private static ModelComponent? AttachSkinnedMeshRendererComponent(
        SceneNode node,
        ImportedHierarchy hierarchy,
        ParsedSkinnedMeshRenderer skinnedMeshRenderer,
        ImportState state)
    {
        Model? model = ResolveModelFromMeshReference(node, hierarchy, skinnedMeshRenderer.MeshReference, skinnedMeshRenderer.Materials, skinnedMeshRenderer, state);
        if (model is null)
            return null;

        ModelComponent? component = GetOrAddComponent<ModelComponent>(node);
        if (component is null)
            return null;

        component.Model = model;
        component.IsActive = skinnedMeshRenderer.Enabled;
        component.MeshCastsShadows = skinnedMeshRenderer.CastShadows;
        SetReceivesShadows(component, skinnedMeshRenderer.ReceiveShadows);
        return component;
    }

    private static CameraComponent? AttachCameraComponent(SceneNode node, ParsedCamera camera)
    {
        CameraComponent? component = GetOrAddComponent<CameraComponent>(node);
        if (component is null)
            return null;

        if (camera.Orthographic)
        {
            component.CameraParameters = new XROrthographicCameraParameters(
                camera.OrthographicSize * 2.0f,
                camera.OrthographicSize * 2.0f,
                camera.NearClipPlane,
                camera.FarClipPlane)
            {
                InheritAspectRatio = true,
            };
        }
        else
        {
            component.SetPerspective(camera.FieldOfView, camera.NearClipPlane, camera.FarClipPlane, null);
        }

        component.IsActive = camera.Enabled;
        return component;
    }

    private static LightComponent? AttachLightComponent(SceneNode node, ParsedLight light)
    {
        LightComponent? component = light.LightType switch
        {
            0 => GetOrAddComponent<SpotLightComponent>(node),
            1 => GetOrAddComponent<DirectionalLightComponent>(node),
            2 => GetOrAddComponent<PointLightComponent>(node),
            _ => null,
        };

        if (component is null)
        {
            Debug.LogWarning($"Unity light type '{light.LightType}' on '{node.Name}' is not supported yet.");
            return null;
        }

        component.Color = new ColorF3(light.Color.X, light.Color.Y, light.Color.Z);
        component.CastsShadows = light.CastsShadows;
        component.IsActive = light.Enabled;

        switch (component)
        {
            case DirectionalLightComponent directionalLight:
                directionalLight.DiffuseIntensity = light.Intensity;
                break;
            case PointLightComponent pointLight:
                pointLight.Radius = MathF.Max(light.Range, 0.001f);
                pointLight.Brightness = light.Intensity;
                pointLight.DiffuseIntensity = 1.0f;
                break;
            case SpotLightComponent spotLight:
                spotLight.Distance = MathF.Max(light.Range, 0.001f);
                spotLight.Brightness = light.Intensity;
                spotLight.DiffuseIntensity = 1.0f;
                spotLight.OuterCutoffAngleDegrees = MathF.Max(light.SpotAngle * 0.5f, 0.001f);
                spotLight.InnerCutoffAngleDegrees = Math.Clamp(light.InnerSpotAngle * 0.5f, 0.0f, MathF.Max(light.SpotAngle * 0.5f, 0.001f));
                break;
        }

        return component;
    }

    private static Model? ResolveModelFromMeshReference(
        SceneNode node,
        ImportedHierarchy hierarchy,
        UnityReference meshReference,
        IReadOnlyList<UnityReference> materialReferences,
        ParsedSkinnedMeshRenderer? skinnedMeshRenderer,
        ImportState state)
    {
        if (TryResolveBuiltInPrimitiveModel(meshReference, materialReferences, state, out Model? primitiveModel))
            return primitiveModel;

        string? assetPath = ResolveAssetPath(state, meshReference.Guid);
        if (string.IsNullOrWhiteSpace(assetPath) || !File.Exists(assetPath))
        {
            Debug.MeshesWarning($"Unity mesh reference '{meshReference.Guid ?? meshReference.FileId.ToString(CultureInfo.InvariantCulture)}' on '{node.Name}' could not be resolved.");
            return null;
        }

        if (string.Equals(Path.GetExtension(assetPath), ".asset", StringComparison.OrdinalIgnoreCase))
            return LoadSerializedUnityMeshModel(assetPath, node, hierarchy, materialReferences, skinnedMeshRenderer, state);

        if (SupportedExternalModelExtensions.Contains(Path.GetExtension(assetPath)))
            return LoadExternalModelForNode(assetPath, node, materialReferences, state);

        return null;
    }

    private static bool TryResolveBuiltInPrimitiveModel(
        UnityReference meshReference,
        IReadOnlyList<UnityReference> materialReferences,
        ImportState state,
        out Model? model)
    {
        model = null;
        if (!string.Equals(meshReference.Guid, "0000000000000000e000000000000000", StringComparison.OrdinalIgnoreCase))
            return false;

        XRMesh? mesh = meshReference.FileId switch
        {
            10202 => XRMesh.Shapes.SolidBox(new Vector3(-0.5f), new Vector3(0.5f)),
            10207 => XRMesh.Shapes.SolidSphere(Vector3.Zero, 0.5f, 32),
            _ => null,
        };

        if (mesh is null)
        {
            Debug.MeshesWarning($"Unity built-in mesh '{meshReference.FileId}' is not supported yet.");
            return false;
        }

        XRMaterial material = ResolveMaterialForSubMesh(materialReferences, 0, state, fallbackMaterial: null);
        model = new Model(new SubMesh(new SubMeshLOD(material, mesh, 0.0f)))
        {
            Name = $"UnityBuiltIn_{meshReference.FileId}",
        };
        return true;
    }

    private static Model? LoadExternalModelForNode(
        string assetPath,
        SceneNode targetNode,
        IReadOnlyList<UnityReference> materialReferences,
        ImportState state)
    {
        AssetManager? assets = Engine.Assets;
        if (assets is null)
            return null;

        XRPrefabSource? sourcePrefab = assets.Load<XRPrefabSource>(assetPath);
        SceneNode? sourceRoot = sourcePrefab?.RootNode;
        if (sourceRoot is null)
            return null;

        string targetNodeName = targetNode.Name ?? string.Empty;
        if (targetNodeName.Length == 0)
            return null;

        SceneNode? sourceNode = string.Equals(sourceRoot.Name, targetNodeName, StringComparison.Ordinal)
            ? sourceRoot
            : sourceRoot.FindDescendantByName(targetNodeName, StringComparison.Ordinal);
        if (sourceNode?.TryGetComponent<ModelComponent>(out ModelComponent? sourceModelComponent) != true ||
            sourceModelComponent is null ||
            sourceModelComponent.Model is null)
            return null;

        return CloneModelForTarget(sourceRoot, sourceNode, sourceModelComponent.Model, targetNode, materialReferences, state);
    }

    private static Model? LoadSerializedUnityMeshModel(
        string assetPath,
        SceneNode node,
        ImportedHierarchy hierarchy,
        IReadOnlyList<UnityReference> materialReferences,
        ParsedSkinnedMeshRenderer? skinnedMeshRenderer,
        ImportState state)
    {
        if (LoadUnityDocumentMapping(assetPath, "Mesh") is not YamlMappingNode meshMapping)
            return null;

        string meshName = GetScalarString(meshMapping, "m_Name") ?? Path.GetFileNameWithoutExtension(assetPath);
        List<Vertex> vertices = ParseSerializedUnityMeshVertices(meshMapping);
        List<UnitySerializedSubMesh> subMeshes = ParseSerializedUnitySubMeshes(meshMapping);
        byte[] indexBuffer = ParseHexBytes(GetScalarString(meshMapping, "m_IndexBuffer"));
        bool use32BitIndices = (GetScalarInt(meshMapping, "m_IndexFormat") ?? 0) != 0;

        if (vertices.Count == 0 || subMeshes.Count == 0 || indexBuffer.Length == 0)
            return null;

        var importedSubMeshes = new List<SubMesh>(subMeshes.Count);
        for (int subMeshIndex = 0; subMeshIndex < subMeshes.Count; subMeshIndex++)
        {
            UnitySerializedSubMesh unitySubMesh = subMeshes[subMeshIndex];
            List<ushort>? triangleIndices = ExtractTriangleIndices(indexBuffer, unitySubMesh.FirstByte, unitySubMesh.IndexCount, unitySubMesh.BaseVertex, use32BitIndices);
            if (triangleIndices is not { Count: >= 3 })
                continue;

            XRMesh xrMesh = new(vertices.Select(static vertex => vertex.HardCopy()), triangleIndices)
            {
                Name = $"{meshName}_{subMeshIndex}",
            };

            XRMaterial material = ResolveMaterialForSubMesh(materialReferences, subMeshIndex, state, fallbackMaterial: null);
            var lod = new SubMeshLOD(material, xrMesh, 0.0f);
            var subMesh = new SubMesh(lod)
            {
                Name = unitySubMesh.Name ?? $"{meshName}_{subMeshIndex}",
                RootTransform = node.Transform,
            };

            ApplySkinnedRendererBindings(subMesh, xrMesh, hierarchy, skinnedMeshRenderer);
            importedSubMeshes.Add(subMesh);
        }

        return importedSubMeshes.Count > 0
            ? new Model(importedSubMeshes) { Name = meshName }
            : null;
    }

    private static Model CloneModelForTarget(
        SceneNode sourceRoot,
        SceneNode sourceNode,
        Model sourceModel,
        SceneNode targetNode,
        IReadOnlyList<UnityReference> materialReferences,
        ImportState state)
    {
        SceneNode targetRoot = GetHierarchyRoot(targetNode);
        var clonedSubMeshes = new List<SubMesh>(sourceModel.Meshes.Count);

        for (int subMeshIndex = 0; subMeshIndex < sourceModel.Meshes.Count; subMeshIndex++)
        {
            SubMesh sourceSubMesh = sourceModel.Meshes[subMeshIndex];
            var clonedLods = new List<SubMeshLOD>(sourceSubMesh.LODs.Count);

            foreach (SubMeshLOD sourceLod in sourceSubMesh.LODs)
            {
                XRMesh? clonedMesh = sourceLod.Mesh?.Clone();
                if (clonedMesh is not null)
                    RemapMeshBonesToTarget(clonedMesh, sourceRoot, targetRoot);

                XRMaterial material = ResolveMaterialForSubMesh(materialReferences, subMeshIndex, state, sourceLod.Material);
                var clonedLod = new SubMeshLOD(material, clonedMesh, sourceLod.MaxVisibleDistance)
                {
                    GenerateAsync = sourceLod.GenerateAsync,
                    IsAutoGenerated = sourceLod.IsAutoGenerated,
                    GeneratedNormalizedError = sourceLod.GeneratedNormalizedError,
                    GeneratedObjectSpaceError = sourceLod.GeneratedObjectSpaceError,
                    GeneratedTargetIndexRatio = sourceLod.GeneratedTargetIndexRatio,
                };
                clonedLods.Add(clonedLod);
            }

            var clonedSubMesh = new SubMesh(clonedLods)
            {
                Name = sourceSubMesh.Name,
                Bounds = sourceSubMesh.Bounds,
                CullingBounds = sourceSubMesh.CullingBounds,
                RootTransform = MapSourceTransformToTarget(sourceSubMesh.RootTransform, sourceRoot, targetRoot) ?? targetNode.Transform,
                RootBone = MapSourceTransformToTarget(sourceSubMesh.RootBone, sourceRoot, targetRoot),
                MeshOptimizer = sourceSubMesh.MeshOptimizer,
            };

            clonedSubMeshes.Add(clonedSubMesh);
        }

        return new Model(clonedSubMeshes)
        {
            Name = sourceModel.Name,
        };
    }

    private static void ApplySkinnedRendererBindings(
        SubMesh subMesh,
        XRMesh mesh,
        ImportedHierarchy hierarchy,
        ParsedSkinnedMeshRenderer? skinnedMeshRenderer)
    {
        if (skinnedMeshRenderer is null)
            return;

        if (skinnedMeshRenderer.RootBoneTransformFileId != 0 &&
            hierarchy.NodesByTransformId.TryGetValue(skinnedMeshRenderer.RootBoneTransformFileId, out SceneNode? rootBoneNode))
        {
            subMesh.RootBone = rootBoneNode.Transform;
        }

        if (!mesh.HasSkinning || skinnedMeshRenderer.BoneTransformFileIds.Count == 0 ||
            mesh.UtilizedBones.Length != skinnedMeshRenderer.BoneTransformFileIds.Count)
        {
            return;
        }

        var remap = new Dictionary<TransformBase, TransformBase>(ReferenceEqualityComparer.Instance);
        var reboundBones = new (TransformBase tfm, Matrix4x4 invBindWorldMtx)[mesh.UtilizedBones.Length];

        for (int index = 0; index < mesh.UtilizedBones.Length; index++)
        {
            (TransformBase sourceBone, Matrix4x4 inverseBind) = mesh.UtilizedBones[index];
            if (hierarchy.NodesByTransformId.TryGetValue(skinnedMeshRenderer.BoneTransformFileIds[index], out SceneNode? targetBoneNode))
            {
                remap[sourceBone] = targetBoneNode.Transform;
                reboundBones[index] = (targetBoneNode.Transform, inverseBind);
            }
            else
            {
                reboundBones[index] = (sourceBone, inverseBind);
            }
        }

        if (remap.Count == 0)
            return;

        mesh.UtilizedBones = reboundBones;
        RemapVertexWeights(mesh, remap);
        mesh.RebuildSkinningBuffersFromVertices();
    }

    private static void RemapMeshBonesToTarget(XRMesh mesh, SceneNode sourceRoot, SceneNode targetRoot)
    {
        if (!mesh.HasSkinning)
            return;

        var remap = new Dictionary<TransformBase, TransformBase>(ReferenceEqualityComparer.Instance);
        var reboundBones = new (TransformBase tfm, Matrix4x4 invBindWorldMtx)[mesh.UtilizedBones.Length];

        for (int index = 0; index < mesh.UtilizedBones.Length; index++)
        {
            (TransformBase sourceBone, Matrix4x4 inverseBind) = mesh.UtilizedBones[index];
            TransformBase? mappedBone = MapSourceTransformToTarget(sourceBone, sourceRoot, targetRoot);
            if (mappedBone is null)
            {
                reboundBones[index] = (sourceBone, inverseBind);
                continue;
            }

            remap[sourceBone] = mappedBone;
            reboundBones[index] = (mappedBone, inverseBind);
        }

        if (remap.Count == 0)
            return;

        mesh.UtilizedBones = reboundBones;
        RemapVertexWeights(mesh, remap);
        mesh.RebuildSkinningBuffersFromVertices();
    }

    private static void RemapVertexWeights(XRMesh mesh, IReadOnlyDictionary<TransformBase, TransformBase> remap)
    {
        if (mesh.Vertices is not { Length: > 0 })
            return;

        for (int vertexIndex = 0; vertexIndex < mesh.Vertices.Length; vertexIndex++)
        {
            Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>? weights = mesh.Vertices[vertexIndex].Weights;
            if (weights is null || weights.Count == 0)
                continue;

            var remappedWeights = new Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>(weights.Count, ReferenceEqualityComparer.Instance);
            foreach ((TransformBase sourceBone, (float weight, Matrix4x4 bindInvWorldMatrix) value) in weights)
            {
                TransformBase targetBone = remap.TryGetValue(sourceBone, out TransformBase? mappedBone)
                    ? mappedBone
                    : sourceBone;

                if (remappedWeights.TryGetValue(targetBone, out (float weight, Matrix4x4 bindInvWorldMatrix) existing))
                    remappedWeights[targetBone] = (existing.weight + value.weight, value.bindInvWorldMatrix);
                else
                    remappedWeights[targetBone] = value;
            }

            mesh.Vertices[vertexIndex].Weights = remappedWeights;
        }
    }

    private static TransformBase? MapSourceTransformToTarget(TransformBase? sourceTransform, SceneNode sourceRoot, SceneNode targetRoot)
    {
        if (sourceTransform?.SceneNode is not SceneNode sourceNode)
            return sourceTransform;

        if (ReferenceEquals(sourceNode, sourceRoot))
            return targetRoot.Transform;

        string sourceNodeName = sourceNode.Name ?? string.Empty;
        if (TryGetRelativeSceneNodePath(sourceRoot, sourceNode, out string? relativePath) && !string.IsNullOrWhiteSpace(relativePath))
            return targetRoot.FindDescendant(relativePath)?.Transform ??
                   (sourceNodeName.Length > 0 ? targetRoot.FindDescendantByName(sourceNodeName, StringComparison.Ordinal)?.Transform : null);

        return sourceNodeName.Length > 0
            ? targetRoot.FindDescendantByName(sourceNodeName, StringComparison.Ordinal)?.Transform
            : null;
    }

    private static bool TryGetRelativeSceneNodePath(SceneNode root, SceneNode node, out string? relativePath)
    {
        var segments = new Stack<string>();
        SceneNode? current = node;
        while (current is not null && !ReferenceEquals(current, root))
        {
            segments.Push(current.Name ?? string.Empty);
            current = current.Parent;
        }

        if (current is null)
        {
            relativePath = null;
            return false;
        }

        relativePath = string.Join('/', segments);
        return true;
    }

    private static SceneNode GetHierarchyRoot(SceneNode node)
    {
        SceneNode current = node;
        while (current.Parent is SceneNode parent)
            current = parent;

        return current;
    }

    private static XRMaterial ResolveMaterialForSubMesh(
        IReadOnlyList<UnityReference> materialReferences,
        int subMeshIndex,
        ImportState state,
        XRMaterial? fallbackMaterial)
    {
        if (materialReferences.Count > 0)
        {
            UnityReference reference = materialReferences[Math.Min(subMeshIndex, materialReferences.Count - 1)];
            XRMaterial? resolved = ResolveUnityMaterial(reference, state);
            if (resolved is not null)
                return resolved;
        }

        return fallbackMaterial ?? CreateFallbackUnityMaterial("UnityImporterDefaultMaterial", ColorF4.White);
    }

    private static XRMaterial? ResolveUnityMaterial(UnityReference materialReference, ImportState state)
    {
        if (materialReference.FileId == 0 && string.IsNullOrWhiteSpace(materialReference.Guid))
            return null;

        string? assetPath = ResolveAssetPath(state, materialReference.Guid);
        if (string.IsNullOrWhiteSpace(assetPath) || !File.Exists(assetPath))
            return null;

        if (string.Equals(Path.GetExtension(assetPath), ".mat", StringComparison.OrdinalIgnoreCase))
            return LoadUnityMaterial(assetPath, state);

        AssetManager? assets = Engine.Assets;
        return assets?.Load<XRMaterial>(assetPath);
    }

    private static XRMaterial? LoadUnityMaterial(string materialPath, ImportState state)
    {
        try
        {
            UnityMaterialImportResult result = UnityMaterialImporter.ImportWithReport(materialPath, state.ProjectRoot);
            foreach (string warning in result.Warnings)
                Debug.LogWarning(warning);

            return result.Material;
        }
        catch (Exception ex)
        {
            string materialName = Path.GetFileNameWithoutExtension(materialPath);
            Debug.LogWarning($"Unity material '{materialName}' could not be imported; using a placeholder material instead. {ex.Message}");
            return CreateFallbackUnityMaterial(materialName, ColorF4.White);
        }
    }

    private static XRMaterial CreateFallbackUnityMaterial(string materialName, ColorF4 baseColor)
        => new()
        {
            Name = materialName,
            RenderPass = (int)EDefaultRenderPass.OpaqueDeferred,
        };

    private static ColorF4 ResolveMaterialColor(YamlMappingNode materialMapping)
    {
        if (GetNode(materialMapping, "m_SavedProperties") is not YamlMappingNode savedProperties ||
            GetNode(savedProperties, "m_Colors") is not YamlSequenceNode colorsNode)
        {
            return ColorF4.White;
        }

        foreach (string propertyName in new[] { "_BaseColor", "_Color" })
        {
            foreach (YamlNode item in colorsNode.Children)
            {
                if (item is not YamlMappingNode entryMapping || GetNode(entryMapping, propertyName) is not YamlMappingNode colorNode)
                    continue;

                Vector4 color = GetVector4(entryMapping, propertyName, Vector4.One);
                return new ColorF4(color.X, color.Y, color.Z, color.W);
            }
        }

        return ColorF4.White;
    }

    private static XRTexture2D? ResolveMaterialTexture(YamlMappingNode materialMapping, ImportState state)
    {
        if (GetNode(materialMapping, "m_SavedProperties") is not YamlMappingNode savedProperties ||
            GetNode(savedProperties, "m_TexEnvs") is not YamlSequenceNode texEnvsNode)
        {
            return null;
        }

        foreach (string propertyName in new[] { "_BaseMap", "_MainTex" })
        {
            foreach (YamlNode item in texEnvsNode.Children)
            {
                if (item is not YamlMappingNode entryMapping || GetNode(entryMapping, propertyName) is not YamlMappingNode texEnv)
                    continue;

                UnityReference textureReference = ParseReference(GetNode(texEnv, "m_Texture"));
                if (textureReference.FileId == 0 && string.IsNullOrWhiteSpace(textureReference.Guid))
                    continue;

                string? texturePath = ResolveAssetPath(state, textureReference.Guid);
                if (string.IsNullOrWhiteSpace(texturePath) || !File.Exists(texturePath))
                    continue;

                AssetManager? assets = Engine.Assets;
                XRTexture2D? texture = assets?.Load<XRTexture2D>(texturePath);
                if (texture is not null)
                    return texture;
            }
        }

        return null;
    }

    private static YamlMappingNode? LoadUnityDocumentMapping(string assetPath, string documentType)
    {
        var yaml = new YamlStream();
        using var reader = new StreamReader(assetPath);
        yaml.Load(reader);

        foreach (YamlDocument document in yaml.Documents)
        {
            if (document.RootNode is not YamlMappingNode rootNode || rootNode.Children.Count == 0)
                continue;

            foreach ((YamlNode keyNode, YamlNode valueNode) in rootNode.Children)
            {
                string? key = (keyNode as YamlScalarNode)?.Value;
                if (!string.Equals(key, documentType, StringComparison.Ordinal) || valueNode is not YamlMappingNode mappingNode)
                    continue;

                return mappingNode;
            }
        }

        return null;
    }

    private static List<Vertex> ParseSerializedUnityMeshVertices(YamlMappingNode meshMapping)
    {
        if (GetNode(meshMapping, "m_VertexData") is not YamlMappingNode vertexDataMapping ||
            GetNode(vertexDataMapping, "m_Channels") is not YamlSequenceNode channelsNode)
        {
            return [];
        }

        int vertexCount = GetScalarInt(vertexDataMapping, "m_VertexCount") ?? 0;
        if (vertexCount <= 0)
            return [];

        byte[] rawVertexData = ParseHexBytes(GetScalarString(vertexDataMapping, "_typelessdata"));
        if (rawVertexData.Length == 0)
            return [];

        UnityVertexChannel[] channels = channelsNode.Children
            .OfType<YamlMappingNode>()
            .Select(ParseUnityVertexChannel)
            .ToArray();

        Dictionary<int, int> streamStrides = ComputeStreamStrides(channels);
        Dictionary<int, int> streamOffsets = ComputeStreamOffsets(streamStrides, vertexCount);
        var vertices = new List<Vertex>(vertexCount);

        for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
        {
            var vertex = new Vertex();

            for (int channelIndex = 0; channelIndex < channels.Length; channelIndex++)
            {
                UnityVertexChannel channel = channels[channelIndex];
                if (channel.Dimension <= 0 || !streamOffsets.TryGetValue(channel.Stream, out int streamOffset) || !streamStrides.TryGetValue(channel.Stream, out int streamStride))
                    continue;

                int dataOffset = streamOffset + (vertexIndex * streamStride) + channel.Offset;
                if (dataOffset < 0 || dataOffset >= rawVertexData.Length)
                    continue;

                switch (channelIndex)
                {
                    case 0:
                        Vector3 position = ReadVector3(rawVertexData, dataOffset, channel);
                        vertex.Position = ConvertPosition(position);
                        break;
                    case 1:
                        Vector3 normal = ReadVector3(rawVertexData, dataOffset, channel);
                        vertex.Normal = Vector3.Normalize(ConvertDirection(normal));
                        break;
                    case 2:
                        Vector4 tangent = ReadVector4(rawVertexData, dataOffset, channel);
                        vertex.Tangent = Vector3.Normalize(ConvertDirection(new Vector3(tangent.X, tangent.Y, tangent.Z)));
                        vertex.BitangentSign = tangent.W;
                        break;
                    case 3:
                        Vector4 color = ReadVector4(rawVertexData, dataOffset, channel);
                        vertex.ColorSets = [color];
                        break;
                    case 4:
                        Vector2 uv0 = ReadVector2(rawVertexData, dataOffset, channel);
                        vertex.TextureCoordinateSets = [uv0];
                        break;
                }
            }

            vertices.Add(vertex);
        }

        return vertices;
    }

    private static List<UnitySerializedSubMesh> ParseSerializedUnitySubMeshes(YamlMappingNode meshMapping)
    {
        if (GetNode(meshMapping, "m_SubMeshes") is not YamlSequenceNode subMeshesNode)
            return [];

        var subMeshes = new List<UnitySerializedSubMesh>(subMeshesNode.Children.Count);
        int index = 0;
        foreach (YamlNode child in subMeshesNode.Children)
        {
            if (child is not YamlMappingNode subMeshMapping)
                continue;

            subMeshes.Add(new UnitySerializedSubMesh(
                GetScalarInt(subMeshMapping, "firstByte") ?? 0,
                GetScalarInt(subMeshMapping, "indexCount") ?? 0,
                GetScalarInt(subMeshMapping, "baseVertex") ?? 0,
                $"SubMesh_{index}"));
            index++;
        }

        return subMeshes;
    }

    private static List<ushort>? ExtractTriangleIndices(
        byte[] indexBuffer,
        int firstByte,
        int indexCount,
        int baseVertex,
        bool use32BitIndices)
    {
        if (indexCount < 3)
            return null;

        int bytesPerIndex = use32BitIndices ? sizeof(uint) : sizeof(ushort);
        int byteCount = indexCount * bytesPerIndex;
        if (firstByte < 0 || firstByte + byteCount > indexBuffer.Length)
            return null;

        var indices = new List<ushort>(indexCount);
        for (int index = 0; index < indexCount; index++)
        {
            int byteIndex = firstByte + (index * bytesPerIndex);
            uint rawIndex = use32BitIndices
                ? BinaryPrimitives.ReadUInt32LittleEndian(indexBuffer.AsSpan(byteIndex, sizeof(uint)))
                : BinaryPrimitives.ReadUInt16LittleEndian(indexBuffer.AsSpan(byteIndex, sizeof(ushort)));
            int adjustedIndex = checked((int)rawIndex + baseVertex);
            if (adjustedIndex < 0 || adjustedIndex > ushort.MaxValue)
            {
                Debug.MeshesWarning($"Unity sub-mesh index '{adjustedIndex}' exceeds the XRMesh ushort index limit.");
                return null;
            }

            indices.Add((ushort)adjustedIndex);
        }

        return indices;
    }

    private static byte[] ParseHexBytes(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return [];

        string normalized = hex.Trim();
        if ((normalized.Length & 1) != 0)
            return [];

        var bytes = new byte[normalized.Length / 2];
        for (int index = 0; index < bytes.Length; index++)
        {
            if (!byte.TryParse(normalized.Substring(index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[index]))
                return [];
        }

        return bytes;
    }

    private static UnityVertexChannel ParseUnityVertexChannel(YamlMappingNode channelMapping)
        => new(
            GetScalarInt(channelMapping, "stream") ?? 0,
            GetScalarInt(channelMapping, "offset") ?? 0,
            GetScalarInt(channelMapping, "format") ?? 0,
            GetScalarInt(channelMapping, "dimension") ?? 0);

    private static Dictionary<int, int> ComputeStreamStrides(IEnumerable<UnityVertexChannel> channels)
    {
        var streamStrides = new Dictionary<int, int>();
        foreach (UnityVertexChannel channel in channels)
        {
            if (channel.Dimension <= 0)
                continue;

            int elementSize = GetVertexFormatElementSize(channel.Format);
            int stride = channel.Offset + (elementSize * channel.Dimension);
            if (!streamStrides.TryGetValue(channel.Stream, out int currentStride) || stride > currentStride)
                streamStrides[channel.Stream] = stride;
        }

        return streamStrides;
    }

    private static Dictionary<int, int> ComputeStreamOffsets(IReadOnlyDictionary<int, int> streamStrides, int vertexCount)
    {
        var offsets = new Dictionary<int, int>();
        int runningOffset = 0;
        foreach ((int streamIndex, int stride) in streamStrides.OrderBy(static pair => pair.Key))
        {
            offsets[streamIndex] = runningOffset;
            runningOffset += stride * vertexCount;
        }

        return offsets;
    }

    private static int GetVertexFormatElementSize(int format)
        => format switch
        {
            0 => sizeof(float),
            1 => sizeof(ushort),
            2 => sizeof(byte),
            3 => sizeof(sbyte),
            4 => sizeof(ushort),
            5 => sizeof(short),
            _ => sizeof(float),
        };

    private static Vector2 ReadVector2(byte[] rawVertexData, int offset, UnityVertexChannel channel)
        => new(
            ReadVertexChannelComponent(rawVertexData, offset, channel, 0),
            ReadVertexChannelComponent(rawVertexData, offset, channel, 1));

    private static Vector3 ReadVector3(byte[] rawVertexData, int offset, UnityVertexChannel channel)
        => new(
            ReadVertexChannelComponent(rawVertexData, offset, channel, 0),
            ReadVertexChannelComponent(rawVertexData, offset, channel, 1),
            ReadVertexChannelComponent(rawVertexData, offset, channel, 2));

    private static Vector4 ReadVector4(byte[] rawVertexData, int offset, UnityVertexChannel channel)
        => new(
            ReadVertexChannelComponent(rawVertexData, offset, channel, 0),
            ReadVertexChannelComponent(rawVertexData, offset, channel, 1),
            ReadVertexChannelComponent(rawVertexData, offset, channel, 2),
            ReadVertexChannelComponent(rawVertexData, offset, channel, 3));

    private static float ReadVertexChannelComponent(byte[] rawVertexData, int offset, UnityVertexChannel channel, int componentIndex)
    {
        if (componentIndex >= channel.Dimension)
            return 0.0f;

        int componentOffset = offset + (componentIndex * GetVertexFormatElementSize(channel.Format));
        if (componentOffset < 0 || componentOffset >= rawVertexData.Length)
            return 0.0f;

        return channel.Format switch
        {
            0 when componentOffset + sizeof(float) <= rawVertexData.Length =>
                BitConverter.ToSingle(rawVertexData, componentOffset),
            1 when componentOffset + sizeof(ushort) <= rawVertexData.Length =>
                (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(rawVertexData.AsSpan(componentOffset, sizeof(ushort)))),
            2 => rawVertexData[componentOffset] / 255.0f,
            3 => Math.Clamp((sbyte)rawVertexData[componentOffset] / 127.0f, -1.0f, 1.0f),
            4 when componentOffset + sizeof(ushort) <= rawVertexData.Length =>
                BinaryPrimitives.ReadUInt16LittleEndian(rawVertexData.AsSpan(componentOffset, sizeof(ushort))) / 65535.0f,
            5 when componentOffset + sizeof(short) <= rawVertexData.Length =>
                Math.Clamp(BinaryPrimitives.ReadInt16LittleEndian(rawVertexData.AsSpan(componentOffset, sizeof(short))) / 32767.0f, -1.0f, 1.0f),
            _ => 0.0f,
        };
    }

    private static Vector3 ConvertDirection(Vector3 unityDirection)
        => new(unityDirection.X, unityDirection.Y, -unityDirection.Z);

    private static void RegisterAttachedComponent(long fileId, XRComponent? component, ImportedHierarchy hierarchy)
    {
        if (component is not null)
            hierarchy.ComponentsByFileId[fileId] = component;
    }

    private static void RemoveNodeFromHierarchy(SceneNode node, ImportedHierarchy hierarchy)
    {
        TransformBase? parentTransform = node.Transform.Parent;
        if (parentTransform is not null)
        {
            parentTransform.Children.Remove(node.Transform);
            node.Transform.Parent = null;
        }

        if (node.Parent is not null)
            node.Parent = null;

        hierarchy.RootEntries.RemoveAll(entry => IsNodeSelfOrDescendant(entry.Node, node));

        long[] transformIds = hierarchy.NodesByTransformId
            .Where(pair => IsNodeSelfOrDescendant(pair.Value, node))
            .Select(static pair => pair.Key)
            .ToArray();
        foreach (long transformId in transformIds)
        {
            hierarchy.NodesByTransformId.Remove(transformId);
            hierarchy.TransformSortOrders.Remove(transformId);
            hierarchy.ExcludedRootTransformIds.Add(transformId);
        }

        long[] gameObjectIds = hierarchy.NodesByGameObjectId
            .Where(pair => IsNodeSelfOrDescendant(pair.Value, node))
            .Select(static pair => pair.Key)
            .ToArray();
        foreach (long gameObjectId in gameObjectIds)
            hierarchy.NodesByGameObjectId.Remove(gameObjectId);

        long[] componentIds = hierarchy.ComponentsByFileId
            .Where(pair => IsNodeSelfOrDescendant(pair.Value.SceneNode, node))
            .Select(static pair => pair.Key)
            .ToArray();
        foreach (long componentId in componentIds)
            hierarchy.ComponentsByFileId.Remove(componentId);
    }

    private static bool IsNodeSelfOrDescendant(SceneNode candidate, SceneNode ancestor)
    {
        for (SceneNode? current = candidate; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current, ancestor))
                return true;
        }

        return false;
    }

    private static void ReinsertChild(SceneNode parent, SceneNode child, int? insertIndex)
    {
        List<TransformBase> reorderedChildren = [.. parent.Transform.Children.Where(existing => !ReferenceEquals(existing, child.Transform))];

        int targetIndex = insertIndex.HasValue
            ? Math.Clamp(insertIndex.Value, 0, reorderedChildren.Count)
            : reorderedChildren.Count;

        reorderedChildren.Insert(targetIndex, child.Transform);
        SetOrderedChildren(parent, reorderedChildren);
    }

    private static void SetOrderedChildren(SceneNode parent, IReadOnlyList<TransformBase> orderedChildren)
    {
        foreach (TransformBase existingChild in parent.Transform.Children.ToArray())
        {
            while (parent.Transform.Children.Remove(existingChild)) { }
        }

        foreach (TransformBase orderedChild in orderedChildren)
        {
            if (orderedChild.SceneNode is SceneNode orderedNode)
                orderedNode.Parent = parent;
            else
                orderedChild.Parent = parent.Transform;
        }
    }

    private static bool TryResolveTargetNode(ImportedHierarchy hierarchy, long targetFileId, out SceneNode? node)
    {
        if (hierarchy.NodesByGameObjectId.TryGetValue(targetFileId, out node))
            return true;

        return hierarchy.NodesByTransformId.TryGetValue(targetFileId, out node);
    }

    private static bool TryResolveAddedSceneNode(
        ParsedUnityFile ownerFile,
        ImportedHierarchy ownerHierarchy,
        UnityReference addedObject,
        out SceneNode? node,
        out long? transformFileId)
    {
        if (ownerHierarchy.NodesByTransformId.TryGetValue(addedObject.FileId, out node))
        {
            transformFileId = addedObject.FileId;
            return true;
        }

        if (ownerHierarchy.NodesByGameObjectId.TryGetValue(addedObject.FileId, out node))
        {
            transformFileId = ownerFile.TransformIdsByGameObjectId.GetValueOrDefault(addedObject.FileId);
            return true;
        }

        transformFileId = null;
        return false;
    }

    private static void SetReceivesShadows(ModelComponent component, bool receivesShadows)
    {
        foreach (RenderableMesh mesh in component.Meshes)
            mesh.RenderInfo.ReceivesShadows = receivesShadows;
    }

    private static T? GetOrAddComponent<T>(SceneNode node) where T : XRComponent
        => node.TryGetComponent<T>(out T? existing) && existing is not null
            ? existing
            : node.AddComponent<T>();

    private sealed record UnityVertexChannel(int Stream, int Offset, int Format, int Dimension);

    private sealed record UnitySerializedSubMesh(int FirstByte, int IndexCount, int BaseVertex, string? Name);
}

