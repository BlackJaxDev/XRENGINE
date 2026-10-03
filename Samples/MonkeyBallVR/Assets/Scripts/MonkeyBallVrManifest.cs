using XREngine;
using XREngine.Input;

namespace MonkeyBallVR;

internal static class MonkeyBallVrManifest
{
    private const string AppKey = "com.blackjax.monkeyballvr";
    private const string KnucklesBindingFileName = "bindings_knuckles.json";

    public static RuntimeOpenVrApplicationManifest CreateApplicationManifest()
        => new()
        {
            AppKey = AppKey,
            WindowsPath = Environment.ProcessPath,
            WindowsArguments = string.Empty,
            IsDashboardOverlay = false,
            LocalizedNames = new Dictionary<string, RuntimeOpenVrApplicationNameDescription>
            {
                ["en_us"] = new("MonkeyBall VR", "Tilt the course and guide the ball into the goal."),
            },
        };

    public static RuntimeOpenVrActionManifest<MonkeyBallActionSet, MonkeyBallAction> CreateActionManifest()
    {
        string? bindingPath = TryWriteKnucklesBinding();
        return new RuntimeOpenVrActionManifest<MonkeyBallActionSet, MonkeyBallAction>
        {
            ActionSets =
            [
                new RuntimeOpenVrActionSet<MonkeyBallActionSet>
                {
                    Name = MonkeyBallActionSet.Global,
                    Type = RuntimeOpenVrActionSetType.LeftRight,
                    LocalizedNames = new Dictionary<string, string> { ["en_us"] = "Gameplay" },
                }
            ],
            Actions =
            [
                CreateAction(MonkeyBallAction.Tilt, RuntimeOpenVrActionType.Vector2, "Tilt course", RuntimeOpenVrActionRequirement.Mandatory),
                CreateAction(MonkeyBallAction.Reset, RuntimeOpenVrActionType.Boolean, "Reset ball", RuntimeOpenVrActionRequirement.Suggested),
                CreateAction(MonkeyBallAction.Pause, RuntimeOpenVrActionType.Boolean, "Pause", RuntimeOpenVrActionRequirement.Suggested),
            ],
            DefaultBindings = bindingPath is null
                ? []
                : [new RuntimeOpenVrDefaultBinding { ControllerType = "knuckles", Path = bindingPath }],
        };
    }

    private static RuntimeOpenVrAction<MonkeyBallActionSet, MonkeyBallAction> CreateAction(
        MonkeyBallAction name,
        RuntimeOpenVrActionType type,
        string localizedName,
        RuntimeOpenVrActionRequirement requirement)
        => new()
        {
            Name = name,
            Category = MonkeyBallActionSet.Global,
            Type = type,
            Requirement = requirement,
            LocalizedNames = new Dictionary<string, string> { ["en_us"] = localizedName },
        };

    private static string? TryWriteKnucklesBinding()
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MonkeyBallVR",
                "SteamVR");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, KnucklesBindingFileName);
            File.WriteAllText(path, KnucklesBindingJson);
            return path;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Unable to materialize the optional SteamVR Index binding: {ex.Message}");
            return null;
        }
    }

    private const string KnucklesBindingJson = """
        {
          "action_manifest_version": 0,
          "bindings": {
            "/actions/Global": {
              "sources": [
                {
                  "path": "/user/hand/left/input/thumbstick",
                  "mode": "joystick",
                  "inputs": { "position": { "output": "/actions/Global/in/Tilt" } }
                },
                {
                  "path": "/user/hand/right/input/a",
                  "mode": "button",
                  "inputs": { "click": { "output": "/actions/Global/in/Reset" } }
                },
                {
                  "path": "/user/hand/left/input/b",
                  "mode": "button",
                  "inputs": { "click": { "output": "/actions/Global/in/Pause" } }
                }
              ]
            }
          },
          "category": "steamvr_input",
          "controller_type": "knuckles",
          "description": "Default MonkeyBall VR bindings for Valve Index controllers",
          "name": "MonkeyBall VR",
          "options": {},
          "simulated_actions": []
        }
        """;
}
