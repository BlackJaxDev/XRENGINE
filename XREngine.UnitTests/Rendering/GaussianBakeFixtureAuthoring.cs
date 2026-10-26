using NUnit.Framework;
using Shouldly;
using System.Numerics;
using XREngine.Components.Scene.Mesh;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Gaussian.Baking;
using XREngine.Rendering.Models.Gaussian.Baking.Input;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Writes the cooked archives that the gaussian bake command uses as fixtures, in
/// <c>Build/GaussianBakeFixtures/</c>. The tests are explicit because they import models and
/// write into the workspace; run each one alone. Git ignores the output folder.
/// The fixture meshes use the default lit engine materials, so that the bake can render them.
/// </summary>
[TestFixture]
public sealed class GaussianBakeFixtureAuthoring
{
    public const string AvatarAssetPath = "Fixtures/Avatar.asset";
    public const string ItemAssetPath = "Fixtures/Item.asset";
    public const string WorldAssetPath = "Fixtures/World.asset";

    /// <summary>Space between the prop and the avatar in the world fixture, in meters.</summary>
    private const float AvatarClearance = 1.0f;

    [Test]
    [Explicit("Writes Build/GaussianBakeFixtures/avatar.pak.")]
    public void WriteAvatarFixturePak()
    {
        string avatarPak = FixturePath("avatar.pak");
        XRPrefabSource avatar = ImportAvatar();
        GaussianBakeTestAssets.CountClipComponents(avatar.RootNode.ShouldNotBeNull(), requireAnimation: true).ShouldBeGreaterThan(0);

        GaussianBakeTestAssets.WritePak(avatarPak, (AvatarAssetPath, GaussianBakeTestAssets.Cook(avatar)));

        CookedBakeInput avatarInput = CookedBakeInputLoader.Load(avatarPak, AvatarAssetPath, GaussianBakeKind.Avatar);
        SceneNode avatarRoot = avatarInput.PrefabRoot.ShouldNotBeNull();
        AssertRenderableMesh(avatarRoot);
        GaussianBakeTestAssets.CountClipComponents(avatarRoot, requireAnimation: true).ShouldBeGreaterThan(0);
        WriteSummary("avatar", avatarPak, avatarInput, avatarRoot);
    }

    [Test]
    [Explicit("Writes Build/GaussianBakeFixtures/item.pak. The Corvette import takes several minutes.")]
    public void WriteItemFixturePak()
    {
        string itemPak = FixturePath("item.pak");
        XRPrefabSource item = ImportItem();
        GaussianBakeTestAssets.CountClipComponents(item.RootNode.ShouldNotBeNull(), requireAnimation: false).ShouldBe(0);

        GaussianBakeTestAssets.WritePak(itemPak, (ItemAssetPath, GaussianBakeTestAssets.Cook(item)));

        CookedBakeInput itemInput = CookedBakeInputLoader.Load(itemPak, ItemAssetPath, GaussianBakeKind.Item);
        SceneNode itemRoot = itemInput.PrefabRoot.ShouldNotBeNull();
        AssertRenderableMesh(itemRoot);
        GaussianBakeTestAssets.CountClipComponents(itemRoot, requireAnimation: false).ShouldBe(0);
        WriteSummary("item", itemPak, itemInput, itemRoot);
    }

    [Test]
    [Explicit("Writes Build/GaussianBakeFixtures/world.pak. The Corvette import takes several minutes.")]
    public void WriteWorldFixturePak()
    {
        string worldPak = FixturePath("world.pak");
        GaussianBakeTestAssets.WritePak(worldPak, (WorldAssetPath, GaussianBakeTestAssets.Cook(CreateWorld(ImportAvatar(), ImportItem()))));

        CookedBakeInput worldInput = CookedBakeInputLoader.Load(worldPak, WorldAssetPath, GaussianBakeKind.World);
        XRWorld world = worldInput.World.ShouldNotBeNull();
        XRScene scene = world.Scenes.ShouldHaveSingleItem();
        scene.RootNodes.Count.ShouldBe(2);
        foreach (SceneNode root in scene.RootNodes)
            AssertRenderableMesh(root);
        scene.RootNodes.Sum(static root => GaussianBakeTestAssets.CountClipComponents(root, requireAnimation: true)).ShouldBeGreaterThan(0);

        foreach (SceneNode root in scene.RootNodes)
            WriteSummary($"world/{root.Name}", worldPak, worldInput, root);
    }

    private static string FixturePath(string fileName)
    {
        string fixtureRoot = Path.Combine(GltfImportTestUtilities.ResolveWorkspaceRoot(), "Build", "GaussianBakeFixtures");
        Directory.CreateDirectory(fixtureRoot);
        return Path.Combine(fixtureRoot, fileName);
    }

    /// <summary>
    /// Imports the avatar. The avatar mesh is one plane, so its materials draw both faces;
    /// otherwise the capture views behind the plane would show nothing.
    /// </summary>
    private static XRPrefabSource ImportAvatar()
        => GaussianBakeTestAssets.ImportPrefabWithEngineMaterials(GaussianBakeTestAssets.SkinnedAvatarModelPath, doubleSided: true);

    private static XRPrefabSource ImportItem()
        => GaussianBakeTestAssets.ImportPrefabWithEngineMaterials(GaussianBakeTestAssets.CorvetteModelPath, doubleSided: false);

    /// <summary>
    /// Puts the prop at the origin and the avatar beside the prop, on the +X side of its bounds,
    /// so that the two do not overlap.
    /// </summary>
    private static XRWorld CreateWorld(XRPrefabSource avatar, XRPrefabSource item)
    {
        SceneNode prop = item.Instantiate();
        prop.Name = "Prop";
        (Vector3 propMin, Vector3 propMax) = ComputeWorldBounds(prop);
        Vector3 avatarOffset = new(
            propMax.X + AvatarClearance,
            propMin.Y,
            (propMin.Z + propMax.Z) * 0.5f);

        SceneNode avatarAnchor = new("Avatar", new Transform(avatarOffset));
        avatar.Instantiate(world: null, parent: avatarAnchor);
        return new XRWorld("GaussianBakeFixtureWorld", new XRScene("GaussianBakeFixtureScene", prop, avatarAnchor));
    }

    /// <summary>
    /// Checks that the first sub-mesh has readable positions and an engine material whose shaders have
    /// GLSL source text, so that the bake can render the mesh.
    /// </summary>
    private static void AssertRenderableMesh(SceneNode root)
    {
        SubMesh subMesh = GaussianBakeTestAssets.FirstSubMesh(root).ShouldNotBeNull();
        SubMeshLOD lod = subMesh.LODs.Min.ShouldNotBeNull();
        XRMesh mesh = lod.Mesh.ShouldNotBeNull();
        mesh.VertexCount.ShouldBeGreaterThan(0);
        float.IsFinite(mesh.GetPosition(0).X).ShouldBeTrue();

        XRMaterial material = lod.Material.ShouldNotBeNull();
        material.Shaders.Count.ShouldBeGreaterThan(0);
        foreach (XRShader shader in material.Shaders)
            (shader.Source?.Text ?? string.Empty).ShouldContain("#version");
    }

    private static (Vector3 Min, Vector3 Max) ComputeWorldBounds(SceneNode root)
    {
        root.Transform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true);
        Vector3 min = new(float.PositiveInfinity);
        Vector3 max = new(float.NegativeInfinity);
        Accumulate(root, ref min, ref max);
        return float.IsFinite(min.X) ? (min, max) : (Vector3.Zero, Vector3.Zero);

        static void Accumulate(SceneNode node, ref Vector3 min, ref Vector3 max)
        {
            Matrix4x4 world = node.Transform.WorldMatrix;
            foreach (ModelComponent component in node.GetComponents<ModelComponent>())
            {
                if (component.Model is null)
                    continue;

                foreach (SubMesh subMesh in component.Model.Meshes)
                {
                    if (subMesh.LODs.Min?.Mesh is not XRMesh mesh)
                        continue;

                    Vector3 localMin = mesh.Bounds.Min;
                    Vector3 localMax = mesh.Bounds.Max;
                    for (int corner = 0; corner < 8; corner++)
                    {
                        Vector3 point = new(
                            (corner & 1) == 0 ? localMin.X : localMax.X,
                            (corner & 2) == 0 ? localMin.Y : localMax.Y,
                            (corner & 4) == 0 ? localMin.Z : localMax.Z);
                        Vector3 transformed = Vector3.Transform(point, world);
                        min = Vector3.Min(min, transformed);
                        max = Vector3.Max(max, transformed);
                    }
                }
            }

            foreach (TransformBase child in node.Transform.Children)
            {
                if (child.SceneNode is SceneNode childNode)
                    Accumulate(childNode, ref min, ref max);
            }
        }
    }

    private static void WriteSummary(string label, string pakPath, CookedBakeInput input, SceneNode root)
    {
        (Vector3 min, Vector3 max) = ComputeWorldBounds(root);
        TestContext.Out.WriteLine(
            $"{label}: pak '{pakPath}' ({new FileInfo(pakPath).Length} bytes), asset '{input.AssetPath}', " +
            $"sha256 {input.InputSha256}, clips {GaussianBakeTestAssets.CountClipComponents(root, requireAnimation: true)}, bounds {min} .. {max}");
    }
}
