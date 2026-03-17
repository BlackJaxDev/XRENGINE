using System.Numerics;
using XREngine.Animation;
using XREngine.Components.Animation;
using XREngine.Components.Scene;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Runtime.AnimationIntegration;

public static class BootstrapAnimationWorldBuilder
{
    public static void AddIKTest(SceneNode rootNode, Action<SceneNode>? onTargetNodeCreated = null)
    {
        SceneNode ikTestRootNode = rootNode.NewChild();
        ikTestRootNode.Name = "IKTestRootNode";
        Transform tfmRoot = ikTestRootNode.GetTransformAs<Transform>(true)!;
        tfmRoot.Translation = new Vector3(0.0f, 0.0f, 0.0f);

        SceneNode ikTest1Node = ikTestRootNode.NewChild();
        ikTest1Node.Name = "IKTest1Node";
        Transform tfm1 = ikTest1Node.GetTransformAs<Transform>(true)!;
        tfm1.Translation = new Vector3(0.0f, 5.0f, 0.0f);

        SceneNode ikTest2Node = ikTest1Node.NewChild();
        ikTest2Node.Name = "IKTest2Node";
        Transform tfm2 = ikTest2Node.GetTransformAs<Transform>(true)!;
        tfm2.Translation = new Vector3(0.0f, 5.0f, 0.0f);

        var comp = ikTestRootNode.AddComponent<SingleTargetIKComponent>()!;
        comp.MaxIterations = 10;

        var targetNode = rootNode.NewChild();
        var targetTfm = targetNode.GetTransformAs<Transform>(true)!;
        targetTfm.Translation = new Vector3(2.0f, 5.0f, 0.0f);
        comp.TargetTransform = targetNode.Transform;

        onTargetNodeCreated?.Invoke(targetNode);
    }

    public static void AddSpline(SceneNode rootNode)
    {
        var spline = rootNode.AddComponent<Spline3DPreviewComponent>();
        PropAnimVector3 anim = new();
        Random random = new();
        float len = random.NextSingle() * 10.0f;
        int frameCount = random.Next(2, 10);
        for (int i = 0; i < frameCount; i++)
        {
            float t = i / (float)frameCount;
            Vector3 value = new(random.NextSingle() * 10.0f, random.NextSingle() * 10.0f, random.NextSingle() * 10.0f);
            Vector3 tangent = new(random.NextSingle() * 10.0f, random.NextSingle() * 10.0f, random.NextSingle() * 10.0f);
            anim.Keyframes.Add(new Vector3Keyframe(t, value, tangent, EVectorInterpType.Smooth));
        }

        anim.LengthInSeconds = len;
        spline!.Spline = anim;
    }
}