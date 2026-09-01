#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Fbx;
using UnityEditor;
using UnityEditor.Formats.Fbx.Exporter;
using UnityEngine;

/// <summary>Regenerates the deterministic humanoid FBX fixtures used by Phase 10 conformance.</summary>
public static class Phase10FixtureGenerator
{
    private sealed class FixtureSpec
    {
        public string FileName;
        public bool ArbitraryNames;
        public bool IncludeOptionalBones;
        public bool ExportZUp;
        public float WidthScale;

        public FixtureSpec(
            string fileName,
            bool arbitraryNames,
            bool includeOptionalBones,
            bool exportZUp,
            float widthScale)
        {
            FileName = fileName;
            ArbitraryNames = arbitraryNames;
            IncludeOptionalBones = includeOptionalBones;
            ExportZUp = exportZUp;
            WidthScale = widthScale;
        }
    }

    private static readonly FixtureSpec[] Specs =
    {
        new FixtureSpec("conventional-standard.fbx", false, true, false, 1.0f),
        new FixtureSpec("arbitrary-axes.ascii.fbx", true, true, true, 1.0f),
        new FixtureSpec("lean-optional-absent.fbx", false, false, false, 0.72f),
    };

    [MenuItem("XRENGINE/Animation/Regenerate Phase 10 Humanoid Fixtures")]
    public static void Generate()
    {
        string outputDirectory = Path.Combine(
            Application.dataPath,
            "..",
            "XREngine.UnitTests",
            "TestData",
            "HumanoidConformance",
            "avatars");
        Directory.CreateDirectory(outputDirectory);

        foreach (FixtureSpec spec in Specs)
        {
            GameObject root = BuildFixture(spec);
            try
            {
                string path = Path.Combine(outputDirectory, spec.FileName);
                string exportedPath = ModelExporter.ExportObject(path, root);
                if (string.IsNullOrEmpty(exportedPath) || !File.Exists(exportedPath))
                    throw new InvalidOperationException("FBX export failed for " + spec.FileName);

                if (spec.ExportZUp)
                    ConvertToZUp(exportedPath);

                Debug.Log("[Phase10FixtureGenerator] Exported " + exportedPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
    }

    private static GameObject BuildFixture(FixtureSpec spec)
    {
        GameObject root = new GameObject(spec.ArbitraryNames ? "AxisLab" : Path.GetFileNameWithoutExtension(spec.FileName));
        Transform hips = AddBone(root.transform, Name(spec, "Hips", "pelvis_core"), new Vector3(0.0f, 1.0f, 0.0f));
        Transform spine = AddBone(hips, Name(spec, "Spine", "trunk_a"), new Vector3(0.0f, 0.24f, 0.0f));
        Transform chest = AddBone(spine, Name(spec, "Chest", "trunk_b"), new Vector3(0.0f, 0.24f, 0.0f));
        Transform torso = chest;
        if (spec.IncludeOptionalBones)
            torso = AddBone(chest, Name(spec, "UpperChest", "thorax_tip"), new Vector3(0.0f, 0.18f, 0.0f));
        Transform neck = AddBone(torso, Name(spec, "Neck", "cervical"), new Vector3(0.0f, 0.18f, 0.0f));
        AddBone(neck, Name(spec, "Head", "cranium"), new Vector3(0.0f, 0.20f, 0.0f));

        float shoulderOffset = 0.14f * spec.WidthScale;
        float upperArmLength = 0.28f * spec.WidthScale;
        float lowerArmLength = 0.27f * spec.WidthScale;
        AddArm(spec, torso, true, shoulderOffset, upperArmLength, lowerArmLength);
        AddArm(spec, torso, false, shoulderOffset, upperArmLength, lowerArmLength);
        AddLeg(spec, hips, true);
        AddLeg(spec, hips, false);

        AddSkinnedMarkerMesh(root, hips);
        return root;
    }

    private static void AddArm(
        FixtureSpec spec,
        Transform torso,
        bool left,
        float shoulderOffset,
        float upperArmLength,
        float lowerArmLength)
    {
        float direction = left ? -1.0f : 1.0f;
        string side = left ? "Left" : "Right";
        string arbitrarySide = left ? "port" : "starboard";
        Transform shoulder = AddBone(
            torso,
            Name(spec, side + "Shoulder", "clavicle_" + arbitrarySide),
            new Vector3(direction * shoulderOffset, 0.10f, 0.0f));
        Transform upperArm = AddBone(
            shoulder,
            Name(spec, side + "UpperArm", "limb_" + arbitrarySide + "_1"),
            new Vector3(direction * upperArmLength, 0.0f, 0.0f));
        Transform lowerArm = AddBone(
            upperArm,
            Name(spec, side + "LowerArm", "limb_" + arbitrarySide + "_2"),
            new Vector3(direction * lowerArmLength, 0.0f, 0.0f));
        AddBone(
            lowerArm,
            Name(spec, side + "Hand", "palm_" + arbitrarySide),
            new Vector3(direction * 0.18f * spec.WidthScale, 0.0f, 0.0f));
    }

    private static void AddLeg(FixtureSpec spec, Transform hips, bool left)
    {
        float direction = left ? -1.0f : 1.0f;
        string side = left ? "Left" : "Right";
        string arbitrarySide = left ? "port" : "starboard";
        Transform upperLeg = AddBone(
            hips,
            Name(spec, side + "UpperLeg", "stride_" + arbitrarySide + "_1"),
            new Vector3(direction * 0.09f * spec.WidthScale, -0.08f, 0.0f));
        Transform lowerLeg = AddBone(
            upperLeg,
            Name(spec, side + "LowerLeg", "stride_" + arbitrarySide + "_2"),
            new Vector3(0.0f, -0.46f, 0.0f));
        Transform foot = AddBone(
            lowerLeg,
            Name(spec, side + "Foot", "sole_" + arbitrarySide),
            new Vector3(0.0f, -0.43f, 0.04f));
        if (spec.IncludeOptionalBones)
            AddBone(foot, side + "Toes", new Vector3(0.0f, 0.0f, 0.16f));
    }

    private static Transform AddBone(Transform parent, string name, Vector3 localPosition)
    {
        GameObject bone = new GameObject(name);
        Transform transform = bone.transform;
        transform.SetParent(parent, false);
        transform.localPosition = localPosition;
        transform.localRotation = Quaternion.identity;
        transform.localScale = Vector3.one;
        return transform;
    }

    private static string Name(FixtureSpec spec, string conventional, string arbitrary)
        => spec.ArbitraryNames ? arbitrary : conventional;

    private static void AddSkinnedMarkerMesh(GameObject root, Transform hips)
    {
        Transform[] bones = hips.GetComponentsInChildren<Transform>(true);
        var vertices = new List<Vector3>(bones.Length * 4);
        var triangles = new List<int>(bones.Length * 12);
        var weights = new List<BoneWeight>(bones.Length * 4);
        const float radius = 0.018f;

        for (int boneIndex = 0; boneIndex < bones.Length; boneIndex++)
        {
            Vector3 center = root.transform.InverseTransformPoint(bones[boneIndex].position);
            int start = vertices.Count;
            vertices.Add(center + new Vector3(radius, radius, radius));
            vertices.Add(center + new Vector3(-radius, -radius, radius));
            vertices.Add(center + new Vector3(-radius, radius, -radius));
            vertices.Add(center + new Vector3(radius, -radius, -radius));
            triangles.AddRange(new[]
            {
                start, start + 2, start + 1,
                start, start + 1, start + 3,
                start, start + 3, start + 2,
                start + 1, start + 2, start + 3,
            });
            for (int vertex = 0; vertex < 4; vertex++)
                weights.Add(new BoneWeight { boneIndex0 = boneIndex, weight0 = 1.0f });
        }

        var mesh = new Mesh { name = "HumanoidFixtureMesh" };
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        mesh.boneWeights = weights.ToArray();
        Matrix4x4 rootMatrix = root.transform.localToWorldMatrix;
        var bindPoses = new Matrix4x4[bones.Length];
        for (int index = 0; index < bones.Length; index++)
            bindPoses[index] = bones[index].worldToLocalMatrix * rootMatrix;
        mesh.bindposes = bindPoses;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        GameObject rendererObject = new GameObject("BodyMesh");
        rendererObject.transform.SetParent(root.transform, false);
        SkinnedMeshRenderer renderer = rendererObject.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = mesh;
        renderer.rootBone = hips;
        renderer.bones = bones;
    }

    private static void ConvertToZUp(string inputPath)
    {
        string temporaryPath = inputPath + ".zup.tmp.fbx";
        using (FbxManager manager = FbxManager.Create())
        {
            FbxIOSettings settings = FbxIOSettings.Create(manager, Globals.IOSROOT);
            manager.SetIOSettings(settings);
            using (FbxScene scene = FbxScene.Create(manager, "Phase10ZUp"))
            using (FbxImporter importer = FbxImporter.Create(manager, "Phase10Importer"))
            {
                if (!importer.Initialize(inputPath, -1, settings) || !importer.Import(scene))
                    throw new InvalidOperationException("Could not re-open exported FBX " + inputPath);

                FbxAxisSystem.MayaZUp.DeepConvertScene(scene);
                using (FbxExporter exporter = FbxExporter.Create(manager, "Phase10Exporter"))
                {
                    if (!exporter.Initialize(temporaryPath, -1, settings) || !exporter.Export(scene))
                        throw new InvalidOperationException("Could not write Z-up FBX " + temporaryPath);
                }
            }
        }

        File.Delete(inputPath);
        File.Move(temporaryPath, inputPath);
    }
}
#endif
