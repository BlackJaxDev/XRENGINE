using OpenVR.NET.Manifest;
using XREngine;
using ActionType = OpenVR.NET.Manifest.ActionType;

namespace MonkeyBallVR;

internal static class MonkeyBallVrManifest
{
    private const string AppKey = "com.blackjax.monkeyballvr";
    private const string KnucklesBindingFileName = "bindings_knuckles.json";

    public static VrManifest CreateApplicationManifest()
        => new()
        {
            AppKey = AppKey,
            WindowsPath = Environment.ProcessPath,
            WindowsArguments = string.Empty,
            IsDashboardOverlay = false,
            LocalizedNames = new Dictionary<string, NameDescription>
            {
                ["en_us"] = ("MonkeyBall VR", "Tilt the course and guide the ball into the goal."),
            },
        };

    public static ActionManifest<MonkeyBallActionSet, MonkeyBallAction> CreateActionManifest()
    {
        string? bindingPath = TryWriteKnucklesBinding();
        return new ActionManifest<MonkeyBallActionSet, MonkeyBallAction>
        {
            ActionSets =
            [
                new ActionSet<MonkeyBallActionSet, MonkeyBallAction>
                {
                    Name = MonkeyBallActionSet.Global,
                    Type = ActionSetType.LeftRight,
                    LocalizedNames = new Dictionary<string, string> { ["en_us"] = "Gameplay" },
                }
            ],
            Actions =
            [
                CreateAction(MonkeyBallAction.Tilt, ActionType.Vector2, "Tilt course", Requirement.Mandatory),
                CreateAction(MonkeyBallAction.Reset, ActionType.Boolean, "Reset ball", Requirement.Suggested),
                CreateAction(MonkeyBallAction.Pause, ActionType.Boolean, "Pause", Requirement.Suggested),
            ],
            DefaultBindings = bindingPath is null
                ? []
                : [new DefaultBinding { ControllerType = "knuckles", Path = bindingPath }],
        };
    }

    private static OpenVR.NET.Manifest.Action<MonkeyBallActionSet, MonkeyBallAction> CreateAction(
        MonkeyBallAction name,
        ActionType type,
        string localizedName,
        Requirement requirement)
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
