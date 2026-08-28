using XREngine.Rendering;
using XREngine.Scene;

namespace XREngine.Runtime.Bootstrap;

/// <summary>Bootstrap-owned normalization of authored startup aggregates into stable runtime values.</summary>
public static class RuntimeStartupPolicy
{
    public static RuntimeStartupPlan Normalize(GameStartupSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        RuntimeStartupValues values = new(
            settings.TargetUpdatesPerSecond,
            settings.FixedFramesPerSecond,
            settings.TargetFramesPerSecond,
            settings.UnfocusedTargetFramesPerSecond,
            settings.LogOutputToFile,
            settings.RunWithoutWindows,
            settings.GPURenderDispatch);

        RuntimeWindowStartupPlan[] windows = new RuntimeWindowStartupPlan[settings.StartupWindows.Count];
        for (int i = 0; i < windows.Length; i++)
        {
            GameWindowStartupSettings source = settings.StartupWindows[i];
            windows[i] = new RuntimeWindowStartupPlan(
                new WindowStartupValues(
                    source.WindowTitle,
                    source.X,
                    source.Y,
                    source.Width,
                    source.Height,
                    source.WindowState,
                    source.LocalPlayers,
                    source.VSync,
                    source.TransparentFramebuffer,
                    source.OutputHDR,
                    source.UseNativeTitleBar,
                    MapResizeMode(source.InteractiveResizeStrategy)),
                source.TargetWorld);
        }

        return new RuntimeStartupPlan(values, windows);
    }

    public static void ValidateProfile(RuntimeApplicationProfile profile, RuntimeStartupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.AllowsWindows && plan.Windows.Count != 0)
            throw new InvalidOperationException($"Application profile '{profile.Name}' forbids startup windows.");
        if (!profile.AllowsWindows && !plan.Values.RunWithoutWindows)
            throw new InvalidOperationException($"Application profile '{profile.Name}' requires presentationless startup values.");
    }

    private static RuntimeWindowResizeMode MapResizeMode(EInteractiveWindowResizeStrategy strategy)
        => strategy switch
        {
            EInteractiveWindowResizeStrategy.SdlBackend => RuntimeWindowResizeMode.NativeBackend,
            EInteractiveWindowResizeStrategy.EngineBorderlessResize => RuntimeWindowResizeMode.EngineBorderless,
            _ => RuntimeWindowResizeMode.Default,
        };
}

/// <summary>Normalized startup values plus Bootstrap's world association for each window.</summary>
public sealed record RuntimeStartupPlan(
    RuntimeStartupValues Values,
    IReadOnlyList<RuntimeWindowStartupPlan> Windows);

/// <summary>Stable window values paired with the Core world selected by application policy.</summary>
public sealed record RuntimeWindowStartupPlan(WindowStartupValues Values, XRWorld? TargetWorld);
