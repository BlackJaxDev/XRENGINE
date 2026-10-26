using System.Runtime.CompilerServices;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Rendering.Models.Gaussian.Baking;
using XREngine.Rendering.Models.Gaussian.Baking.Input;
using XREngine.Scene;

namespace XREngine.Editor.GaussianBake;

/// <summary>
/// Builds the world that a gaussian preview bake renders, and finds the bounds of the content in it.
/// </summary>
public static class BakeWorldFactory
{
    /// <summary>The name of the bake world of an avatar or an item.</summary>
    public const string PrefabWorldName = "GaussianBakeWorld";

    /// <summary>The name of the scene that holds the stage and the prefab instance.</summary>
    public const string PrefabSceneName = "GaussianBakeScene";

    /// <summary>The name of the scene that the stage camera rig goes into when the input is a world.</summary>
    public const string CameraRigSceneName = "GaussianBakeCameraRig";

    /// <summary>
    /// Keeps the rest-pose content bounds of each input, so that the command and the render services
    /// do not compute them two times. An input that is no longer used releases its entry.
    /// </summary>
    private static readonly ConditionalWeakTable<CookedBakeInput, StrongBox<AABB>> ContentBoundsByInput = new();

    /// <summary>
    /// Returns the world to render. An avatar or an item gives a new world with the stage and the prefab instance,
    /// lit by the stage. A world gives the input world with its own lighting, plus the stage camera rig if the stage has one.
    /// </summary>
    /// <param name="input">The loaded bake input.</param>
    /// <param name="stage">The root node of the preview stage. The world takes ownership of the node.</param>
    public static XRWorld Create(CookedBakeInput input, SceneNode stage)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(stage);

        return input.Kind switch
        {
            GaussianBakeKind.Avatar or GaussianBakeKind.Item => CreatePrefabWorld(input, stage),
            GaussianBakeKind.World => CreateWorldWithCameraRig(input, stage),
            _ => throw new ArgumentOutOfRangeException(nameof(input), input.Kind, "The bake kind is not defined."),
        };
    }

    /// <summary>
    /// Joins the world bounds of every renderable mesh in the content. The stage is not part of the content.
    /// The method updates the transforms of the content first, so it can run before the content enters a world.
    /// The first successful result for an input is kept, and later calls for that input return it again.
    /// </summary>
    /// <param name="input">The loaded bake input.</param>
    /// <param name="bounds">The joined bounds when the method returns true.</param>
    /// <returns>True when at least one mesh gives valid bounds.</returns>
    public static bool TryComputeContentBounds(CookedBakeInput input, out AABB bounds)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (ContentBoundsByInput.TryGetValue(input, out StrongBox<AABB>? known))
        {
            bounds = known.Value;
            return true;
        }

        if (!ComputeContentBounds(input, out bounds))
            return false;

        ContentBoundsByInput.AddOrUpdate(input, new StrongBox<AABB>(bounds));
        return true;
    }

    private static bool ComputeContentBounds(CookedBakeInput input, out AABB bounds)
    {
        bool found = false;
        AABB total = default;
        foreach (SceneNode root in EnumerateContentRoots(input))
        {
            root.Transform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: true);
            root.IterateComponents<RenderableComponent>(component =>
            {
                foreach (RenderableMesh mesh in component.Meshes)
                {
                    if (!mesh.TryGetWorldBounds(out AABB meshBounds) || !meshBounds.IsValid)
                        continue;

                    total = found ? AABB.Union(total, meshBounds) : meshBounds;
                    found = true;
                }
            }, iterateChildHierarchy: true);
        }

        bounds = total;
        return found;
    }

    /// <summary>Returns the root nodes of the content: the prefab instance, or every root node of every world scene.</summary>
    public static IEnumerable<SceneNode> EnumerateContentRoots(CookedBakeInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.PrefabRoot is SceneNode prefabRoot)
        {
            yield return prefabRoot;
            yield break;
        }

        if (input.World is not XRWorld world)
            yield break;

        foreach (XRScene scene in world.Scenes)
        {
            // The stage camera rig is not content.
            if (scene.Name == CameraRigSceneName)
                continue;

            foreach (SceneNode root in scene.RootNodes)
                yield return root;
        }
    }

    private static XRWorld CreatePrefabWorld(CookedBakeInput input, SceneNode stage)
    {
        SceneNode prefabRoot = input.PrefabRoot
            ?? throw new GaussianBakeException(GaussianBakeFailureCodes.ContentEmpty, $"The asset '{input.AssetPath}' has no prefab root node.");

        XRScene scene = new(PrefabSceneName, stage, prefabRoot);
        XRWorld world = new(PrefabWorldName, scene);
        world.Settings.AmbientLightColor = new ColorF3(1.0f, 1.0f, 1.0f);
        world.Settings.AmbientLightIntensity = GaussianPreviewStage.AmbientIntensity;
        return world;
    }

    private static XRWorld CreateWorldWithCameraRig(CookedBakeInput input, SceneNode stage)
    {
        XRWorld world = input.World
            ?? throw new GaussianBakeException(GaussianBakeFailureCodes.ContentEmpty, $"The asset '{input.AssetPath}' has no world.");

        // A world keeps its own lights and environment. Only the camera rig of the stage goes in.
        SceneNode? cameraRig = stage.FindDescendant(transform => transform.SceneNode?.GetComponent<CameraComponent>() is not null);
        if (cameraRig is null)
            return world;

        if (!ReferenceEquals(cameraRig, stage))
            cameraRig.Parent = null;
        world.Scenes.Add(new XRScene(CameraRigSceneName, cameraRig));
        return world;
    }
}
