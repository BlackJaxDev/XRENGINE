using System.Numerics;
using XREngine.Components;
using XREngine.Scene.Transforms;

namespace XREngine.Browser;

/// <summary>Exercises component registration and transform propagation using engine lifecycle callbacks.</summary>
public sealed class SceneBootComponent : XRComponent
{
    public int Begins { get; private set; }
    public int Updates { get; private set; }
    public int Ends { get; private set; }

    protected override void OnBeginPlay()
    {
        base.OnBeginPlay();
        Begins++;
        RegisterTick(ETickGroup.Normal, ETickOrder.Logic, UpdateScene);
    }

    private void UpdateScene()
    {
        RuntimeSceneHost host = WorldAs<RuntimeSceneHost>()
            ?? throw new InvalidOperationException("Component has no scene host.");
        var transform = (Transform)SceneNode.Transform;
        transform.Translation += new Vector3(0.5f * host.DeltaSeconds, 0, 0);
        Updates++;
    }

    protected override void OnEndPlay()
    {
        UnregisterTick(ETickGroup.Normal, ETickOrder.Logic, UpdateScene);
        Ends++;
        base.OnEndPlay();
    }
}
