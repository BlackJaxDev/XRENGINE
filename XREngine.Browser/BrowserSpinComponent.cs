using System.Numerics;
using XREngine.Components;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Browser;

/// <summary>Moves a browser scene node from sampled canvas input on real scene ticks.</summary>
public sealed class BrowserSpinComponent : XRComponent
{
    private BrowserCanvasRenderTarget? _target;
    public BrowserCanvasRenderTarget? Target
    {
        get => _target;
        set => SetField(ref _target, value);
    }

    protected override void OnBeginPlay()
    {
        base.OnBeginPlay();
        RegisterTick(ETickGroup.Normal, ETickOrder.Logic, UpdateScene);
    }

    private void UpdateScene()
    {
        RuntimeSceneHost host = WorldAs<RuntimeSceneHost>()
            ?? throw new InvalidOperationException("Component has no scene host.");
        BrowserCanvasRenderTarget target = Target
            ?? throw new InvalidOperationException("Component has no canvas target.");
        Transform transform = (Transform)SceneNode.Transform;
        float delta = host.DeltaSeconds;
        RuntimeInputState input = target.Input;

        transform.Rotation = Quaternion.Normalize(
            transform.Rotation * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, delta * 0.8f));
        if (input.IsPrimaryPointerDown)
        {
            transform.Translation = new Vector3(
                (input.PointerX - 0.5f) * 2.0f,
                (0.5f - input.PointerY) * 2.0f,
                -2.5f);
        }
        else
        {
            Vector3 position = transform.Translation;
            transform.Translation = new Vector3(
                Math.Clamp(position.X + input.DirectionX * delta, -2.0f, 2.0f),
                Math.Clamp(position.Y + input.DirectionY * delta, -2.0f, 2.0f),
                -2.5f);
        }
    }

    protected override void OnEndPlay()
    {
        UnregisterTick(ETickGroup.Normal, ETickOrder.Logic, UpdateScene);
        base.OnEndPlay();
    }
}
