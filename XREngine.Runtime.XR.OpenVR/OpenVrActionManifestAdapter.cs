using System.Text.Json;
using System.Text.Json.Serialization;
using OpenVR.NET.Manifest;
using XREngine.Input;

namespace XREngine;

internal sealed class OpenVrActionManifestAdapter : IActionManifest
{
    private readonly OpenVrActionSetAdapter[] _sets;
    private readonly OpenVrActionAdapter[] _actions;
    private readonly IRuntimeOpenVrActionManifest _source;

    public OpenVrActionManifestAdapter(IRuntimeOpenVrActionManifest source)
    {
        _source = source;
        _sets = source.EnumerateActionSets().Select(static set => new OpenVrActionSetAdapter(set)).ToArray();
        _actions = source.EnumerateActions().Select(static action => new OpenVrActionAdapter(action)).ToArray();
    }

    public IEnumerable<IActionSet> ActionSets => _sets;

    public IEnumerable<IAction> ActionsForSet(IActionSet set)
    {
        Enum category = set.Name;
        foreach (OpenVrActionAdapter action in _actions)
        {
            if (action.Descriptor.Category.Equals(category))
                yield return action;
        }
    }

    public string ToJson()
    {
        object manifest = new
        {
            default_bindings = _source.DefaultBindings?.Select(static binding => new
            {
                controller_type = binding.ControllerType,
                binding_url = binding.Path,
            }).ToArray(),
            supports_dominant_hand_setting = _source.SupportsDominantHandSetting,
            actions = !_source.HasActions ? null : _actions.Select(static action => new
            {
                name = action.Path,
                requirement = action.Descriptor.Requirement == RuntimeOpenVrActionRequirement.Suggested
                    ? null : action.Descriptor.Requirement.ToString().ToLowerInvariant(),
                type = GetTypeName(action.Descriptor.Type),
                skeleton = action.Descriptor.Type == RuntimeOpenVrActionType.LeftHandSkeleton
                    ? "/skeleton/hand/left"
                    : action.Descriptor.Type == RuntimeOpenVrActionType.RightHandSkeleton
                        ? "/skeleton/hand/right" : null,
            }).ToArray(),
            action_sets = !_source.HasActionSets ? null : _sets.Select(static set => new
            {
                name = set.Path,
                usage = set.Descriptor.Type.ToString().ToLowerInvariant(),
            }).ToArray(),
            localization = _sets.SelectMany(static set =>
                    (set.Descriptor.LocalizedNames ?? []).Select(pair =>
                        (tag: pair.Key, key: set.Path, value: pair.Value)))
                .Concat(_actions.SelectMany(static action =>
                    (action.Descriptor.LocalizedNames ?? []).Select(pair =>
                        (tag: pair.Key, key: action.Path, value: pair.Value))))
                .GroupBy(static item => item.tag)
                .Select(static group => group.Append(
                    (tag: group.Key, key: "language_tag", value: group.Key))
                    .ToDictionary(static item => item.key, static item => item.value))
                .ToArray(),
            version = _source.Version,
            minimum_required_version = _source.RequiredVersion,
        };
        return JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
            IncludeFields = true,
        });
    }

    private static string GetTypeName(RuntimeOpenVrActionType type)
        => type switch
        {
            RuntimeOpenVrActionType.Boolean => "boolean",
            RuntimeOpenVrActionType.Scalar => "vector1",
            RuntimeOpenVrActionType.Vector2 => "vector2",
            RuntimeOpenVrActionType.Vector3 => "vector3",
            RuntimeOpenVrActionType.Vibration => "vibration",
            RuntimeOpenVrActionType.Pose => "pose",
            RuntimeOpenVrActionType.LeftHandSkeleton or RuntimeOpenVrActionType.RightHandSkeleton => "skeleton",
            _ => throw new InvalidOperationException($"Unsupported OpenVR action type {type}."),
        };
}
