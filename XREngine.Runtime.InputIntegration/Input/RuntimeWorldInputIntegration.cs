using XREngine.Components;
using XREngine.Input.Devices;
using XREngine.Scene;

namespace XREngine.Input;

/// <summary>
/// Applies input/controller refresh policy when a pawn hierarchy is attached to
/// a runtime world outside the normal scene-load path (for example, an editor
/// tool scene).
/// </summary>
public static class RuntimeWorldInputIntegration
{
    /// <summary>
    /// Refreshes the camera and input registration for locally controlled pawns
    /// in <paramref name="root"/>.
    /// </summary>
    public static void RefreshControlledPawns(SceneNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        root.IterateHierarchy(static node =>
        {
            lock (node.Components)
            {
                foreach (XRComponent component in node.Components)
                {
                    if (component is not PawnComponent pawn)
                        continue;

                    IPawnController? controller = pawn.Controller;
                    if (controller is not { IsLocal: true }
                        || !ReferenceEquals(controller.ControlledPawnComponent, pawn))
                    {
                        continue;
                    }

                    controller.OnPawnCameraChanged();
                    if (controller.InputDevice is InputInterface input)
                        input.TryRegisterInput();
                }
            }
        });
    }
}
