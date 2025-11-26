using System;
using System.Reflection;
using Silk.NET.Input;
using Silk.NET.OpenGL.Extensions.ImGui;
using XREngine;

namespace XREngine.Rendering.UI;

internal static class ImGuiControllerUtilities
{
    private static readonly BindingFlags InstancePrivate = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;

    public static void DetachInputHandlers(ImGuiController? controller)
    {
        if (controller is null)
            return;

        try
        {
            var keyboard = GetKeyboard(controller);
            if (keyboard is null)
                return;

            var keyDown = CreateStaticDelegate<Action<IKeyboard, Key, int>>("OnKeyDown");
            var keyUp = CreateStaticDelegate<Action<IKeyboard, Key, int>>("OnKeyUp");
            var keyChar = CreateInstanceDelegate<Action<IKeyboard, char>>(controller, "OnKeyChar");

            if (keyDown is not null)
                keyboard.KeyDown -= keyDown;
            if (keyUp is not null)
                keyboard.KeyUp -= keyUp;
            if (keyChar is not null)
                keyboard.KeyChar -= keyChar;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Failed detaching ImGuiController input hooks: {ex.Message}");
        }
    }

    private static IKeyboard? GetKeyboard(ImGuiController controller)
    {
        var controllerType = typeof(ImGuiController);
        var field = controllerType.GetField("_keyboard", InstancePrivate);
        return field?.GetValue(controller) as IKeyboard;
    }

    private static TDelegate? CreateStaticDelegate<TDelegate>(string methodName) where TDelegate : Delegate
    {
        var controllerType = typeof(ImGuiController);
        var method = controllerType.GetMethod(methodName, StaticPrivate);
        return method is null ? null : (TDelegate)Delegate.CreateDelegate(typeof(TDelegate), method);
    }

    private static TDelegate? CreateInstanceDelegate<TDelegate>(ImGuiController controller, string methodName) where TDelegate : Delegate
    {
        var controllerType = typeof(ImGuiController);
        var method = controllerType.GetMethod(methodName, InstancePrivate);
        return method is null ? null : (TDelegate)Delegate.CreateDelegate(typeof(TDelegate), controller, method);
    }
}
