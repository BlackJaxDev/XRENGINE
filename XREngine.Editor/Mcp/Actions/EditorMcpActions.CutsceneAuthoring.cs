using System.ComponentModel;
using System.Numerics;
using XREngine.Animation;
using XREngine.Components.Scene.Mesh;
using XREngine.Cutscenes;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Editor.Cutscenes;
using XREngine.Rendering.Info;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor.Mcp;

public sealed partial class EditorMcpActions
{
    /// <summary>Creates an empty cutscene asset and assigns it to a director.</summary>
    [XRMcp(Name = "cutscene_create_asset", Permission = McpPermissionLevel.Mutate, PermissionReason = "Adds a director component and replaces its cutscene asset.")]
    [McpThreadAffinity(McpThreadAffinity.Update)]
    [Description("Create an empty cutscene asset with a duration and assign it to the CutsceneDirectorComponent of a node. The tool adds the director when the node has none. The asset replaces the current asset of the director and is not saved until cutscene_save_asset.")]
    public static Task<McpToolResponse> CutsceneCreateAssetAsync(
        McpToolContext context,
        [McpName("node_id"), Description("ID of the node that holds or gets the CutsceneDirectorComponent.")] string nodeId,
        [McpName("duration_seconds"), Description("The length of the cutscene in seconds.")] double durationSeconds,
        [McpName("name"), Description("Optional name of the asset.")] string? name = null)
    {
        if (!TryGetNodeById(context.World, nodeId, out SceneNode? node, out string? error) || node is null)
            return Task.FromResult(new McpToolResponse(error ?? $"The node '{nodeId}' was not found.", isError: true));
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0.0)
            return Task.FromResult(new McpToolResponse("duration_seconds must be a finite number above zero.", isError: true));

        CutsceneDirectorComponent director = node.GetComponent<CutsceneDirectorComponent>()
            ?? node.AddComponent<CutsceneDirectorComponent>()
            ?? throw new InvalidOperationException("The director component was not added.");
        CutsceneAsset asset = new()
        {
            Name = string.IsNullOrWhiteSpace(name) ? node.Name ?? "Cutscene" : name,
            Duration = CutsceneTime.FromSeconds(durationSeconds),
        };
        director.Asset = asset;
        return Task.FromResult(new McpToolResponse("Created the cutscene asset.", new { assetId = asset.ID, name = asset.Name, duration = durationSeconds }));
    }

    /// <summary>Adds one track with its clips to the cutscene asset of a director.</summary>
    [XRMcp(Name = "cutscene_add_track", Permission = McpPermissionLevel.Mutate, PermissionReason = "Changes the cutscene asset of a director.")]
    [McpThreadAffinity(McpThreadAffinity.Update)]
    [Description(
        "Add a track to the cutscene asset of a director. track_type is transform, camera_cut, camera_property, property, animation_clip, or simulation_control. " +
        "target_node_id is the node that the track acts on. Each clip has start and duration in seconds, and optional clip_offset, speed, and extrapolation (None, Hold). " +
        "Curves have times in clip seconds, values (1 number per key for float curves, 3 for position and scale, 4 for rotation as x,y,z,w), and interpolation (Linear or Step). " +
        "A transform clip uses position, rotation, and scale. A camera_cut clip uses camera_node_id (default: target_node_id). A camera_property clip uses field_of_view. " +
        "A property clip uses float_curve, vector3_curve, or quaternion_curve with component_type and member_path on the track. An animation_clip clip uses animation_clip_path. " +
        "A simulation_control track has no clips and uses simulation_policy, warm_up_seconds, and seed.")]
    public static Task<McpToolResponse> CutsceneAddTrackAsync(
        McpToolContext context,
        [McpName("director_node_id"), Description("ID of the node that holds the CutsceneDirectorComponent.")] string directorNodeId,
        [McpName("track_type"), Description("transform, camera_cut, camera_property, property, animation_clip, or simulation_control.")] string trackType,
        [McpName("target_node_id"), Description("ID of the node that the track acts on.")] string targetNodeId,
        [McpName("clips"), Description("The clips of the track.")] McpCutsceneClipInput[]? clips = null,
        [McpName("name"), Description("Optional track name.")] string? name = null,
        [McpName("component_type"), Description("Property tracks: the component type name, for example UITextComponent.")] string? componentType = null,
        [McpName("member_path"), Description("Property tracks: the member path on the component, for example Opacity.")] string? memberPath = null,
        [McpName("when_finished"), Description("Optional finish behavior: KeepState or RestoreState.")] ECutsceneFinishBehavior? whenFinished = null,
        [McpName("simulation_policy"), Description("Simulation tracks: SettleFromReset or WarmUp.")] ECutsceneSimulationPolicy? simulationPolicy = null,
        [McpName("warm_up_seconds"), Description("Simulation tracks: the warm-up duration in seconds.")] double? warmUpSeconds = null,
        [McpName("seed"), Description("Simulation tracks: the random seed of particle emitters.")] int? seed = null)
    {
        if (!TryGetNodeById(context.World, directorNodeId, out SceneNode? directorNode, out string? error) || directorNode is null)
            return Task.FromResult(new McpToolResponse(error ?? $"The node '{directorNodeId}' was not found.", isError: true));
        if (directorNode.GetComponent<CutsceneDirectorComponent>() is not { Asset: { } asset })
            return Task.FromResult(new McpToolResponse("The node has no director with a cutscene asset. Call cutscene_create_asset first.", isError: true));
        if (!TryGetNodeById(context.World, targetNodeId, out SceneNode? target, out error) || target is null)
            return Task.FromResult(new McpToolResponse(error ?? $"The node '{targetNodeId}' was not found.", isError: true));

        try
        {
            int slot = CutsceneAssetEditing.GetOrAddBinding(asset, target);
            CutsceneTrack track = CreateTrack(context, trackType, slot, target, clips ?? [], componentType, memberPath, simulationPolicy, warmUpSeconds, seed, asset);
            track.Name = string.IsNullOrWhiteSpace(name) ? $"{target.Name} {trackType}" : name;
            if (whenFinished is { } finish)
                track.WhenFinished = finish;

            asset.Tracks.Add(track);
            return Task.FromResult(new McpToolResponse("Added the track.", new
            {
                trackIndex = asset.Tracks.Count - 1,
                bindingSlot = slot,
                clipCount = track.Clips.Count,
            }));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        {
            return Task.FromResult(new McpToolResponse(ex.Message, isError: true));
        }
    }

    /// <summary>Saves the cutscene asset of a director.</summary>
    [XRMcp(Name = "cutscene_save_asset", Permission = McpPermissionLevel.Mutate, PermissionReason = "Writes a cutscene asset file.")]
    [McpThreadAffinity(McpThreadAffinity.Update)]
    [Description("Save the cutscene asset of a director. A new asset goes to asset_folder (default Cutscenes) of the game assets. An asset that has a file is saved to that file.")]
    public static Task<McpToolResponse> CutsceneSaveAssetAsync(
        McpToolContext context,
        [McpName("director_node_id"), Description("ID of the node that holds the CutsceneDirectorComponent.")] string directorNodeId,
        [McpName("asset_folder"), Description("Folder of a new asset, relative to the game assets folder.")] string? assetFolder = null)
    {
        if (!TryGetNodeById(context.World, directorNodeId, out SceneNode? directorNode, out string? error) || directorNode is null)
            return Task.FromResult(new McpToolResponse(error ?? $"The node '{directorNodeId}' was not found.", isError: true));
        if (directorNode.GetComponent<CutsceneDirectorComponent>() is not { Asset: { } asset })
            return Task.FromResult(new McpToolResponse("The node has no director with a cutscene asset.", isError: true));

        if (string.IsNullOrEmpty(asset.FilePath))
            Engine.Assets.SaveGameAssetTo(asset, string.IsNullOrWhiteSpace(assetFolder) ? "Cutscenes" : assetFolder);
        else
            Engine.Assets.Save(asset);

        return Task.FromResult(new McpToolResponse("Saved the cutscene asset.", new { assetId = asset.ID, filePath = asset.FilePath }));
    }

    /// <summary>Returns the world bounds of the meshes under a node.</summary>
    [XRMcp(Name = "get_node_bounds")]
    [McpThreadAffinity(McpThreadAffinity.Update)]
    [Description(
        "Return the world-space bounding box of a node and its descendants. With source 'meshes' (the default), each static mesh adds its vertex bounds at the transform of its node, " +
        "and skinned meshes are skipped. With source 'transforms', each descendant transform adds its world position, which measures a skinned avatar by its skeleton. " +
        "Scripts use it to place cameras relative to an object.")]
    public static Task<McpToolResponse> GetNodeBoundsAsync(
        McpToolContext context,
        [McpName("node_id"), Description("ID of the root node.")] string nodeId,
        [McpName("include_inactive"), Description("True to include inactive components and nodes.")] bool includeInactive = false,
        [McpName("source"), Description("'meshes' (default) or 'transforms'.")] string source = "meshes")
    {
        if (!TryGetNodeById(context.World, nodeId, out SceneNode? node, out string? error) || node is null)
            return Task.FromResult(new McpToolResponse(error ?? $"The node '{nodeId}' was not found.", isError: true));

        node.Transform.RecalculateMatrixHierarchyImmediate(forceWorldRecalc: true, setRenderMatrixNow: true);
        Vector3 min = new(float.PositiveInfinity);
        Vector3 max = new(float.NegativeInfinity);
        int meshCount = 0;
        int skippedSkinned = 0;
        if (string.Equals(source, "transforms", StringComparison.OrdinalIgnoreCase))
        {
            int transformCount = AddTransformPositions(node.Transform, includeInactive, ref min, ref max);
            return Task.FromResult(BoundsResponse(min, max, transformCount, transformCount == 0));
        }
        node.IterateComponents<RenderableComponent>(component =>
        {
            if (!includeInactive && (!component.IsActive || component.SceneNode?.IsActiveInHierarchy == false))
                return;

            // A static mesh adds its vertex bounds at the transform of its node. A skinned mesh is skipped: its vertex bounds
            // are in bind space, and its render culling volumes exist only after it renders, so an edit-mode scene gives no
            // reliable value. The skeleton measures a skinned avatar instead (source 'transforms').
            Matrix4x4 toWorld = component.Transform.WorldMatrix;
            foreach (RenderableMesh mesh in component.Meshes)
            {
                if (mesh.IsSkinned)
                {
                    skippedSkinned++;
                    continue;
                }

                if (mesh.LODs.First?.Value.Renderer?.Mesh is not { } sourceMesh ||
                    sourceMesh.VertexCount <= 0 ||
                    sourceMesh.Bounds is not { IsValid: true } local)
                {
                    continue;
                }

                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 point = new(
                        (corner & 1) == 0 ? local.Min.X : local.Max.X,
                        (corner & 2) == 0 ? local.Min.Y : local.Max.Y,
                        (corner & 4) == 0 ? local.Min.Z : local.Max.Z);
                    Vector3 world = Vector3.Transform(point, toWorld);
                    min = Vector3.Min(min, world);
                    max = Vector3.Max(max, world);
                }

                meshCount++;
            }
        }, iterateChildHierarchy: true);

        if (meshCount == 0 && skippedSkinned > 0)
            return Task.FromResult(new McpToolResponse($"The node has only skinned meshes ({skippedSkinned}). Use source 'transforms' to measure the skeleton.", isError: true));

        return Task.FromResult(BoundsResponse(min, max, meshCount, meshCount == 0));
    }

    private static McpToolResponse BoundsResponse(Vector3 min, Vector3 max, int count, bool empty)
    {
        if (empty)
            return new McpToolResponse("The node has nothing that gives bounds.", isError: true);

        return new McpToolResponse("Computed the node bounds.", new
        {
            min = new[] { min.X, min.Y, min.Z },
            max = new[] { max.X, max.Y, max.Z },
            center = new[] { (min.X + max.X) * 0.5f, (min.Y + max.Y) * 0.5f, (min.Z + max.Z) * 0.5f },
            size = new[] { max.X - min.X, max.Y - min.Y, max.Z - min.Z },
            count,
        });
    }

    private static int AddTransformPositions(TransformBase transform, bool includeInactive, ref Vector3 min, ref Vector3 max)
    {
        if (!includeInactive && transform.SceneNode is { IsActiveSelf: false })
            return 0;

        Vector3 position = transform.WorldMatrix.Translation;
        min = Vector3.Min(min, position);
        max = Vector3.Max(max, position);
        int count = 1;
        foreach (TransformBase child in transform.Children)
            count += AddTransformPositions(child, includeInactive, ref min, ref max);

        return count;
    }

    private static CutsceneTrack CreateTrack(
        McpToolContext context,
        string trackType,
        int slot,
        SceneNode target,
        McpCutsceneClipInput[] clips,
        string? componentType,
        string? memberPath,
        ECutsceneSimulationPolicy? simulationPolicy,
        double? warmUpSeconds,
        int? seed,
        CutsceneAsset asset)
    {
        switch (trackType.Trim().ToLowerInvariant())
        {
            case "transform":
            {
                TransformTrack track = new() { BindingSlot = slot };
                foreach (McpCutsceneClipInput input in clips)
                {
                    TransformTrackClip clip = new();
                    ApplyClipTiming(clip, input);
                    if (input.Position is { } position)
                        clip.Position = ToVector3Curve(position, "position");
                    if (input.Rotation is { } rotation)
                        clip.Rotation = ToQuaternionCurve(rotation, "rotation");
                    if (input.Scale is { } scale)
                        clip.Scale = ToVector3Curve(scale, "scale", Vector3.One);
                    track.Clips.Add(clip);
                }
                return track;
            }
            case "camera_cut":
            {
                CameraCutTrack track = new() { BindingSlot = slot };
                foreach (McpCutsceneClipInput input in clips)
                {
                    int cameraSlot = slot;
                    if (!string.IsNullOrWhiteSpace(input.CameraNodeId))
                    {
                        if (!TryGetNodeById(context.World, input.CameraNodeId, out SceneNode? cameraNode, out string? error) || cameraNode is null)
                            throw new ArgumentException(error ?? $"The camera node '{input.CameraNodeId}' was not found.");
                        cameraSlot = CutsceneAssetEditing.GetOrAddBinding(asset, cameraNode);
                    }

                    CameraCutClip clip = new() { CameraBindingSlot = cameraSlot };
                    ApplyClipTiming(clip, input);
                    track.Clips.Add(clip);
                }
                return track;
            }
            case "camera_property":
            {
                CameraPropertyTrack track = new() { BindingSlot = slot };
                foreach (McpCutsceneClipInput input in clips)
                {
                    CameraPropertyClip clip = new();
                    ApplyClipTiming(clip, input);
                    if (input.FieldOfView is { } fov)
                        clip.FieldOfView = ToFloatCurve(fov, "field_of_view");
                    track.Clips.Add(clip);
                }
                return track;
            }
            case "property":
            {
                if (string.IsNullOrWhiteSpace(componentType) || string.IsNullOrWhiteSpace(memberPath))
                    throw new ArgumentException("A property track needs component_type and member_path.");

                PropertyTrack track = new() { BindingSlot = slot, ComponentType = componentType, MemberPath = memberPath };
                foreach (McpCutsceneClipInput input in clips)
                {
                    PropertyTrackClip clip = new();
                    ApplyClipTiming(clip, input);
                    if (input.FloatCurve is { } floatCurve)
                        clip.FloatCurve = ToFloatCurve(floatCurve, "float_curve");
                    if (input.Vector3Curve is { } vectorCurve)
                        clip.Vector3Curve = ToVector3Curve(vectorCurve, "vector3_curve");
                    if (input.QuaternionCurve is { } quaternionCurve)
                        clip.QuaternionCurve = ToQuaternionCurve(quaternionCurve, "quaternion_curve");
                    track.Clips.Add(clip);
                }
                return track;
            }
            case "animation_clip":
            {
                AnimationClipTrack track = new() { BindingSlot = slot };
                foreach (McpCutsceneClipInput input in clips)
                {
                    if (string.IsNullOrWhiteSpace(input.AnimationClipPath))
                        throw new ArgumentException("An animation clip needs animation_clip_path.");

                    string fullPath = Path.IsPathRooted(input.AnimationClipPath)
                        ? input.AnimationClipPath
                        : Path.Combine(Engine.Assets.GameAssetsPath, input.AnimationClipPath);
                    AnimationClip animation = Engine.Assets.Load<AnimationClip>(fullPath)
                        ?? throw new ArgumentException($"The animation clip '{input.AnimationClipPath}' did not load.");

                    AnimationClipTrackClip clip = new() { Clip = animation };
                    ApplyClipTiming(clip, input);
                    track.Clips.Add(clip);
                }
                return track;
            }
            case "simulation_control":
            {
                SimulationControlTrack track = new() { BindingSlot = slot };
                if (simulationPolicy is { } policy)
                    track.Policy = policy;
                if (warmUpSeconds is { } warmUp)
                    track.WarmUpSeconds = warmUp;
                if (seed is { } value)
                    track.Seed = value;
                return track;
            }
            default:
                throw new ArgumentException($"Unknown track_type '{trackType}'.");
        }
    }

    private static void ApplyClipTiming(CutsceneClip clip, McpCutsceneClipInput input)
    {
        if (!double.IsFinite(input.Start) || input.Start < 0.0 || !double.IsFinite(input.Duration) || input.Duration <= 0.0)
            throw new ArgumentException("Each clip needs a start of 0 or more and a duration above zero, in seconds.");

        clip.Start = CutsceneTime.FromSeconds(input.Start);
        clip.Duration = CutsceneTime.FromSeconds(input.Duration);
        clip.ClipOffset = CutsceneTime.FromSeconds(input.ClipOffset);
        if (input.Speed is { } speed)
            clip.Speed = speed;
        if (input.Extrapolation is { } extrapolation)
            clip.Extrapolation = extrapolation;
    }

    private static CutsceneTime[] ToTimes(McpCutsceneCurveInput curve, string name, int stride)
    {
        double[] times = curve.Times ?? [];
        float[] values = curve.Values ?? [];
        if (times.Length == 0)
            throw new ArgumentException($"The curve '{name}' has no keys.");
        if (values.Length != times.Length * stride)
            throw new ArgumentException($"The curve '{name}' has {times.Length} keys, so it needs {times.Length * stride} values, not {values.Length}.");

        CutsceneTime[] result = new CutsceneTime[times.Length];
        for (int i = 0; i < times.Length; i++)
        {
            if (!double.IsFinite(times[i]) || (i > 0 && times[i] < times[i - 1]))
                throw new ArgumentException($"The curve '{name}' needs finite key times in increasing order.");
            result[i] = CutsceneTime.FromSeconds(times[i]);
        }

        return result;
    }

    private static CutsceneFloatCurve ToFloatCurve(McpCutsceneCurveInput curve, string name)
        => new()
        {
            Interpolation = curve.Interpolation ?? ECutsceneCurveInterpolation.Linear,
            Times = ToTimes(curve, name, 1),
            Values = [.. curve.Values!],
        };

    private static CutsceneVector3Curve ToVector3Curve(McpCutsceneCurveInput curve, string name, Vector3? defaultValue = null)
    {
        CutsceneTime[] times = ToTimes(curve, name, 3);
        float[] raw = curve.Values!;
        Vector3[] values = new Vector3[times.Length];
        for (int i = 0; i < values.Length; i++)
            values[i] = new Vector3(raw[i * 3], raw[(i * 3) + 1], raw[(i * 3) + 2]);

        CutsceneVector3Curve result = new()
        {
            Interpolation = curve.Interpolation ?? ECutsceneCurveInterpolation.Linear,
            Times = times,
            Values = values,
        };
        if (defaultValue is { } value)
            result.DefaultValue = value;
        return result;
    }

    private static CutsceneQuaternionCurve ToQuaternionCurve(McpCutsceneCurveInput curve, string name)
    {
        CutsceneTime[] times = ToTimes(curve, name, 4);
        float[] raw = curve.Values!;
        Quaternion[] values = new Quaternion[times.Length];
        for (int i = 0; i < values.Length; i++)
            values[i] = Quaternion.Normalize(new Quaternion(raw[i * 4], raw[(i * 4) + 1], raw[(i * 4) + 2], raw[(i * 4) + 3]));

        return new CutsceneQuaternionCurve
        {
            Interpolation = curve.Interpolation ?? ECutsceneCurveInterpolation.Linear,
            Times = times,
            Values = values,
        };
    }
}
