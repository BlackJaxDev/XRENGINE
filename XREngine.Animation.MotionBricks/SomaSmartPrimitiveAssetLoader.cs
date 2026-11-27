using System.Numerics;
using System.Text.Json;

namespace XREngine.Animation.MotionBricks;

/// <summary>
/// Loads NVIDIA MotionBricks Smart Primitive JSON presets and their referenced 30 Hz SOMA
/// BVH mocap clips from a staged <c>Build/Dependencies/MotionBricks</c> directory.
/// </summary>
public static class SomaSmartPrimitiveAssetLoader
{
    private static readonly string[] StandingIdleClipRelativePaths =
    [
        Path.Combine("motions", "soma", "locomotion", "idle", "idle_loop_001__A268.bvh"),
        Path.Combine("motions", "soma", "locomotion", "idle", "warm_up_neck_001__A359.bvh"),
        Path.Combine("motions", "soma", "locomotion", "idle", "legs_relax_002__A407.bvh"),
    ];

    private static readonly string[] CrouchIdleClipRelativePaths =
    [
        Path.Combine("motions", "soma", "locomotion", "idle", "crouch_idle_001__A529.bvh"),
    ];

    private static readonly (string RelativePath, int AirStartFrame, int AirEndFrame, int LandStartFrame, int LandEndFrame)[] JumpClipSpecs =
    [
        (Path.Combine("motions", "soma", "jump", "jump_over_obstacle_0_5m_R_001__A415.bvh"), 90, 106, 107, 114),
        (Path.Combine("motions", "soma", "jump", "jump_over_obstacle_no_hand_0_75m_R_103__A416.bvh"), 54, 71, 72, 79),
    ];

    /// <summary>
    /// Attempts to load staged Smart Primitive target tokens and idle BVH clips under
    /// <paramref name="assetsRootDir"/>. Returns <see langword="false"/> when BVH files are not
    /// staged so callers fall back cleanly to synthetic templates.
    /// </summary>
    public static bool TryLoadPresetAssets(
        string? assetsRootDir,
        SomaRestSkeletonCalibration calibration,
        out SomaMocapLocomotionAssetSet? assetSet,
        out string diagnostic)
    {
        assetSet = null;
        if (string.IsNullOrWhiteSpace(assetsRootDir) || !Directory.Exists(assetsRootDir))
        {
            diagnostic = "MotionBricks asset directory was not found; using procedural pose templates.";
            return false;
        }

        string smartPrimitiveDir = Path.Combine(assetsRootDir, "smart_primitives", "soma");
        string linearPath = Path.Combine(smartPrimitiveDir, "locomotion_bs1_linear.smart_primitive.json");
        if (!File.Exists(linearPath))
        {
            diagnostic = "Smart Primitive JSON presets are not staged; using procedural pose templates.";
            return false;
        }

        try
        {
            Dictionary<string, SomaPoseTransform[]> linearTargets = LoadTargetsFromSmartPrimitive(linearPath, calibration);
            Dictionary<string, SomaPoseTransform[]> directionalTargets =
                LoadTargetsFromSmartPrimitive(Path.Combine(smartPrimitiveDir, "locomotion_bs2_directional.smart_primitive.json"), calibration);
            Dictionary<string, SomaPoseTransform[]> crouchTargets =
                LoadTargetsFromSmartPrimitive(Path.Combine(smartPrimitiveDir, "locomotion_bs2_directional_crouch.smart_primitive.json"), calibration);

            List<SomaMotionClip> standingIdles = LoadIdleClips(assetsRootDir, StandingIdleClipRelativePaths, calibration);
            List<SomaMotionClip> crouchIdles = LoadIdleClips(assetsRootDir, CrouchIdleClipRelativePaths, calibration);
            TryLoadJumpClips(assetsRootDir, calibration, out SomaMotionClip? jumpAirborneClip, out SomaMotionClip? jumpLandingClip);

            var loaded = new SomaMocapLocomotionAssetSet
            {
                IdleTarget = GetOrNull(linearTargets, "Idle"),
                WalkTarget = GetOrNull(linearTargets, "Walk"),
                JogTarget = GetOrNull(linearTargets, "Jog"),
                RunTarget = GetOrNull(linearTargets, "Run"),
                StrafeForwardTarget = GetOrNull(directionalTargets, "Forward"),
                StrafeBackwardTarget = GetOrNull(directionalTargets, "Backward"),
                StrafeLeftTarget = GetOrNull(directionalTargets, "Left"),
                StrafeRightTarget = GetOrNull(directionalTargets, "Right"),
                CrouchIdleTarget = GetOrNull(crouchTargets, "Idle"),
                CrouchForwardTarget = GetOrNull(crouchTargets, "Forward"),
                CrouchBackwardTarget = GetOrNull(crouchTargets, "Backward"),
                CrouchLeftTarget = GetOrNull(crouchTargets, "Left"),
                CrouchRightTarget = GetOrNull(crouchTargets, "Right"),
                StandingIdleClips = [.. standingIdles],
                CrouchIdleClips = [.. crouchIdles],
                JumpAirborneClip = jumpAirborneClip,
                JumpLandingClip = jumpLandingClip,
            };

            if (!loaded.HasLinearMocapTargets && standingIdles.Count == 0)
            {
                diagnostic = "No staged SOMA BVH mocap clips were found under motions/soma; run Tools/Dependencies/Get-MotionBricks.ps1.";
                return false;
            }

            assetSet = loaded;
            diagnostic =
                $"Loaded SOMA mocap targets (Linear={loaded.HasLinearMocapTargets}, StandingIdles={standingIdles.Count}, CrouchIdles={crouchIdles.Count}, JumpMocap={jumpAirborneClip is not null}).";
            return true;
        }
        catch (Exception ex)
        {
            diagnostic = $"Failed to load SOMA BVH mocap assets ({ex.Message}); falling back to procedural pose templates.";
            return false;
        }
    }

    private static Dictionary<string, SomaPoseTransform[]> LoadTargetsFromSmartPrimitive(
        string jsonPath,
        SomaRestSkeletonCalibration calibration)
    {
        var results = new Dictionary<string, SomaPoseTransform[]>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(jsonPath))
            return results;

        string jsonDir = Path.GetDirectoryName(jsonPath) ?? string.Empty;
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        if (!doc.RootElement.TryGetProperty("root", out JsonElement root)
            || !root.TryGetProperty("children", out JsonElement children)
            || children.ValueKind != JsonValueKind.Array)
            return results;

        foreach (JsonElement child in children.EnumerateArray())
        {
            if (!child.TryGetProperty("name", out JsonElement nameEl))
                continue;
            string? targetName = nameEl.GetString();
            if (string.IsNullOrWhiteSpace(targetName))
                continue;

            if (!child.TryGetProperty("components", out JsonElement components)
                || components.ValueKind != JsonValueKind.Array)
                continue;

            foreach (JsonElement comp in components.EnumerateArray())
            {
                if (!comp.TryGetProperty("type", out JsonElement typeEl)
                    || !string.Equals(typeEl.GetString(), "target_pose", StringComparison.Ordinal))
                    continue;

                if (!comp.TryGetProperty("clip", out JsonElement clipEl))
                    continue;

                string? relClip = clipEl.GetString();
                if (string.IsNullOrWhiteSpace(relClip))
                    continue;

                string fullClipPath = Path.GetFullPath(Path.Combine(jsonDir, relClip));
                if (!File.Exists(fullClipPath))
                    continue;

                double sampleTime = comp.TryGetProperty("sample_time_seconds", out JsonElement timeEl)
                    ? timeEl.GetDouble()
                    : 0.0;

                results[targetName] = SomaBvhClipLoader.ExtractFourPoseToken(fullClipPath, sampleTime, calibration);
                break;
            }
        }

        return results;
    }

    private static List<SomaMotionClip> LoadIdleClips(
        string assetsRootDir,
        string[] relativePaths,
        SomaRestSkeletonCalibration calibration)
    {
        var clips = new List<SomaMotionClip>(relativePaths.Length);
        for (int i = 0; i < relativePaths.Length; i++)
        {
            string fullPath = Path.Combine(assetsRootDir, relativePaths[i]);
            if (File.Exists(fullPath))
                clips.Add(SomaBvhClipLoader.LoadClip(fullPath, calibration));
        }

        return clips;
    }

    private static void TryLoadJumpClips(
        string assetsRootDir,
        SomaRestSkeletonCalibration calibration,
        out SomaMotionClip? airborneClip,
        out SomaMotionClip? landingClip)
    {
        airborneClip = null;
        landingClip = null;

        for (int i = 0; i < JumpClipSpecs.Length; i++)
        {
            var spec = JumpClipSpecs[i];
            string fullPath = Path.Combine(assetsRootDir, spec.RelativePath);
            if (!File.Exists(fullPath))
                continue;

            SomaMotionClip fullClip = SomaBvhClipLoader.LoadClip(fullPath, calibration);
            if (fullClip.FrameCount <= spec.LandEndFrame)
                continue;

            airborneClip = ExtractCapsuleRelativeJumpSegment(
                fullClip,
                spec.AirStartFrame,
                spec.AirEndFrame,
                stripFootLiftY: true,
                nameSuffix: "_airborne");
            landingClip = ExtractCapsuleRelativeJumpSegment(
                fullClip,
                spec.LandStartFrame,
                spec.LandEndFrame,
                stripFootLiftY: false,
                nameSuffix: "_landing");
            return;
        }
    }

    /// <summary>
    /// Extracts a contiguous 30 Hz sub-clip from a SOMA jump BVH with root XZ zeroed and
    /// heading aligned to canonical <c>+Z</c> on every frame so the physics capsule owns world
    /// altitude and horizontal carry velocity while mocap drives full-body jump and landing form.
    /// </summary>
    private static SomaMotionClip ExtractCapsuleRelativeJumpSegment(
        SomaMotionClip source,
        int startFrame,
        int endFrame,
        bool stripFootLiftY,
        string nameSuffix)
    {
        const int jointCount = SomaModelContract.JointCount;
        int clampedStart = Math.Clamp(startFrame, 0, source.FrameCount - 1);
        int clampedEnd = Math.Clamp(endFrame, clampedStart, source.FrameCount - 1);
        int frameCount = clampedEnd - clampedStart + 1;

        var poses = new SomaPoseTransform[frameCount * jointCount];
        Span<SomaPoseTransform> worldScratch = stackalloc SomaPoseTransform[jointCount];

        for (int f = 0; f < frameCount; f++)
        {
            ReadOnlySpan<SomaPoseTransform> srcFrame = source.Poses.AsSpan((clampedStart + f) * jointCount, jointCount);
            Span<SomaPoseTransform> dstFrame = poses.AsSpan(f * jointCount, jointCount);
            srcFrame.CopyTo(dstFrame);

            SomaTensorCodec.ComputeWorldPose(srcFrame, SomaPoseTransform.Identity, worldScratch);
            float heading = SomaTensorCodec.ComputeHeadingFromHipPositions(
                worldScratch[SomaModelContract.RightLegJointIndex].Position,
                worldScratch[SomaModelContract.LeftLegJointIndex].Position);
            Quaternion invYaw = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -heading);

            float hipsY = srcFrame[0].Position.Y;
            if (stripFootLiftY)
            {
                float minFootY = MathF.Min(
                    worldScratch[SomaModelContract.LeftFootJointIndex].Position.Y,
                    worldScratch[SomaModelContract.RightFootJointIndex].Position.Y);
                // Remove the clip's world jump altitude above standing ankle height (~0.08 m)
                // so Jolt's capsule controls world height without double-lifting the avatar.
                float worldLift = MathF.Max(0.0f, minFootY - 0.08f);
                hipsY = Math.Clamp(hipsY - worldLift, 0.52f, 1.06f);
            }
            else
            {
                hipsY = Math.Clamp(hipsY, 0.55f, 1.05f);
            }

            Quaternion alignedRootRot = SomaPoseTransform.NormalizeOrIdentity(invYaw * srcFrame[0].Rotation);
            dstFrame[0] = new SomaPoseTransform(new Vector3(0.0f, hipsY, 0.0f), alignedRootRot);
        }

        return new SomaMotionClip(source.Name + nameSuffix, poses, source.SampleRateHz);
    }

    private static SomaPoseTransform[]? GetOrNull(Dictionary<string, SomaPoseTransform[]> map, string key)
        => map.TryGetValue(key, out SomaPoseTransform[]? value) ? value : null;
}
