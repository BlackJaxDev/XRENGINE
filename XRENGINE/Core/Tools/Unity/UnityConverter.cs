using Unity;
using XREngine.Animation;

namespace XREngine.Core.Tools.Unity
{
    public static class UnityConverter
    {
        /// <summary>
        /// Converts a Unity TangentMode to an EVectorInterpType.
        /// </summary>
        private static EVectorInterpType ToInterpType(TangentMode mode)
            => mode switch
            {
                TangentMode.Constant => EVectorInterpType.Step,
                TangentMode.Linear => EVectorInterpType.Linear,
                TangentMode.Free or TangentMode.Auto or TangentMode.ClampedAuto => EVectorInterpType.Smooth,
                _ => EVectorInterpType.Smooth,
            };

        public static AnimationClip ConvertFloatAnimation(UnityAnimationClip animClip)
        {
            var settings = animClip.AnimationClipSettings;
            float lengthInSeconds = (settings?.StopTime ?? 0) - (settings?.StartTime ?? 0);
            int fps = animClip.SampleRate;
            var anims = new List<(string? path, string? attrib, BasePropAnim anim)>();
            animClip.FloatCurves?.ForEach(curve =>
            {
                var anim = new PropAnimFloat
                {
                    LengthInSeconds = lengthInSeconds,
                    Looped = (settings?.LoopTime ?? 0) != 0,
                    BakedFramesPerSecond = fps
                };
                var path = curve.Path;
                var attrib = curve.Attribute;
                var kfs = curve.Curve?.Curve?.Select(kf =>
                {
                    var leftTangentMode = TangentModeHelper.GetLeftTangentMode(kf.CombinedTangentMode);
                    var rightTangentMode = TangentModeHelper.GetRightTangentMode(kf.CombinedTangentMode);
                    return new FloatKeyframe
                    {
                        Second = kf.Time,
                        InValue = kf.Value,
                        OutValue = kf.Value,
                        InTangent = kf.InSlope,
                        OutTangent = kf.OutSlope,
                        InterpolationTypeIn = ToInterpType(leftTangentMode),
                        InterpolationTypeOut = ToInterpType(rightTangentMode),
                    };
                });
                if (kfs is not null)
                    anim.Keyframes.Add(kfs);
                anims.Add((path, attrib, anim));
            });
            var tree = new AnimationClip();
            anims.ForEach(anim =>
            {
                if (anim.attrib is not null)
                {
                    string? path = anim.path;
                    string? correctedPath = null;
                    switch (anim.attrib)
                    {
                        case string s when s.StartsWith("blendShape."):
                            if (path is not null)
                                path += $".{s[11..]}";
                            else
                                path = $"{s[11..]}";
                            correctedPath = "";
                            break;
                        case string s when s.StartsWith("material."):
                            if (path is not null)
                                path += $".{s[9..]}";
                            else
                                path = $"{s[9..]}";
                            correctedPath = "material";
                            break;
                        case "RootT.x":
                            correctedPath = "position.x";
                            break;
                        default:
                            correctedPath = anim.attrib;
                            break;
                    }
                    if (correctedPath != null)
                    {
                        //var member = new AnimationMember(path)
                        //{
                        //    Animation = anim.anim,
                        //    MemberType = memberType,
                        //};
                        //tree.RootMember.Children.Add(member);
                    }
                }
                else
                {
                    Debug.LogWarning("Animation path or attribute is null: " + anim);
                }
            });
            return tree;
        }
    }
}
