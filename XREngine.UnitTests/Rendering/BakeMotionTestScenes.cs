using System.Numerics;
using XREngine.Animation;
using XREngine.Components.Animation;
using XREngine.Components.Scene.Mesh;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Builds small scenes with meshes and clips for the deformed surface and bake motion tests.
/// Each clip moves the node named <see cref="BoneName"/> along X, from 0 at the start to an end value at its length.
/// </summary>
internal static class BakeMotionTestScenes
{
    /// <summary>The name of the node that every test clip moves.</summary>
    public const string BoneName = "Hand";

    /// <summary>Gets the absolute path of the glTF model with one morph target.</summary>
    public static string MorphModelPath
        => Path.Combine(GltfImportTestUtilities.ResolveWorkspaceRoot(), "XREngine.UnitTests", "TestData", "Gltf", "morph-sparse-extras.gltf");

    /// <summary>Makes a root node with one child node named <see cref="BoneName"/>.</summary>
    public static SceneNode CreateRig(out SceneNode bone)
    {
        SceneNode root = new("Root", new Transform());
        bone = new SceneNode(root, BoneName, new Transform());
        return root;
    }

    /// <summary>Adds a model component with a unit cube to the node and returns the cube mesh.</summary>
    public static XRMesh AddStaticCube(SceneNode node)
    {
        XRMesh cube = XRMesh.Shapes.SolidBox(new Vector3(-0.5f), new Vector3(0.5f));
        ModelComponent model = node.AddComponent<ModelComponent>()!;
        model.Model = new Model(new SubMesh(cube, new XRMaterial()));
        return cube;
    }

    /// <summary>
    /// Makes a clip that moves <see cref="BoneName"/> from X = 0 to X = <paramref name="endTranslationX"/>.
    /// </summary>
    /// <param name="name">The clip name.</param>
    /// <param name="lengthSeconds">The clip length. Zero gives a clip with no length.</param>
    /// <param name="endTranslationX">The X translation at the end of the clip.</param>
    /// <param name="humanoid">When true, the clip says that it has humanoid muscle channels.</param>
    public static AnimationClip CreateClip(string name, float lengthSeconds, float endTranslationX = 10.0f, bool humanoid = false)
    {
        float trackLength = lengthSeconds > 0.0f ? lengthSeconds : 1.0f;
        PropAnimFloat track = new(trackLength, looped: true, useKeyframes: true);
        track.Keyframes.Add(
            new FloatKeyframe(0.0f, 0.0f, 0.0f, EVectorInterpType.Linear),
            new FloatKeyframe(trackLength, endTranslationX, 0.0f, EVectorInterpType.Linear));

        return new AnimationClip
        {
            Name = name,
            LengthInSeconds = lengthSeconds,
            Looped = true,
            SampleRate = 30,
            HasMuscleChannels = humanoid,
            RootMember = CreateTranslationRoot(track),
        };
    }

    /// <summary>Adds an animation clip component that holds <paramref name="clip"/>.</summary>
    public static AnimationClipComponent AddClip(SceneNode node, AnimationClip clip)
    {
        AnimationClipComponent component = node.AddComponent<AnimationClipComponent>()!;
        component.Animation = clip;
        return component;
    }

    /// <summary>Returns the first-LOD renderer of every renderable mesh under the root, in depth-first order.</summary>
    public static List<(RenderableComponent Component, XRMeshRenderer Renderer)> FindRenderers(SceneNode root)
    {
        List<(RenderableComponent Component, XRMeshRenderer Renderer)> renderers = [];
        root.IterateComponents<RenderableComponent>(component =>
        {
            foreach (RenderableMesh mesh in component.Meshes)
            {
                if (mesh.LODs.First?.Value.Renderer is XRMeshRenderer renderer)
                    renderers.Add((component, renderer));
            }
        }, true);
        return renderers;
    }

    /// <summary>Reads the world position of one surface vertex.</summary>
    public static Vector3 PositionOf(float[] positions, int vertex)
        => new(positions[vertex * 3], positions[vertex * 3 + 1], positions[vertex * 3 + 2]);

    private static AnimationMember CreateTranslationRoot(PropAnimFloat track)
    {
        AnimationMember root = new("Root", EAnimationMemberType.Group);
        AnimationMember sceneNode = new("SceneNode", EAnimationMemberType.Property);
        AnimationMember findBone = new("FindDescendantByName", EAnimationMemberType.Method)
        {
            MethodArguments = [BoneName, StringComparison.InvariantCultureIgnoreCase],
            AnimatedMethodArgumentIndex = -1,
            CacheReturnValue = true,
        };
        AnimationMember transform = new("Transform", EAnimationMemberType.Property);
        AnimationMember translationX = new("TranslationX", EAnimationMemberType.Property, track);

        root.Children.Add(sceneNode);
        sceneNode.Children.Add(findBone);
        findBone.Children.Add(transform);
        transform.Children.Add(translationX);
        return root;
    }
}
