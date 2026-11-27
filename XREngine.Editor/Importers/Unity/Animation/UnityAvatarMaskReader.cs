using XREngine.Components.Animation;
using YamlDotNet.RepresentationModel;

namespace XREngine.Scene.Importers;

/// <summary>
/// Reads Unity <c>.mask</c> (<c>AvatarMask</c>, classID 319) assets into native <see cref="EHumanoidBodyMask"/> flags.
/// </summary>
internal static class UnityAvatarMaskReader
{
    /// <summary>
    /// Loads a Unity <c>.mask</c> YAML file and maps its humanoid body-part bit array to <see cref="EHumanoidBodyMask"/>.
    /// </summary>
    public static EHumanoidBodyMask Read(string maskPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(maskPath);

        if (!File.Exists(maskPath))
            return EHumanoidBodyMask.All;

        string yamlText = File.ReadAllText(maskPath);
        YamlMappingNode? maskMapping = SerializedAssetYamlReader.LoadDocumentMapping(yamlText, "AvatarMask");
        if (maskMapping is null)
            return EHumanoidBodyMask.All;

        string? maskHex = SerializedAssetYamlReader.GetScalarString(maskMapping, "m_Mask");
        if (string.IsNullOrWhiteSpace(maskHex) || maskHex.Length < 8 || maskHex.Length % 8 != 0)
            return EHumanoidBodyMask.All;

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(maskHex);
        }
        catch (FormatException)
        {
            return EHumanoidBodyMask.All;
        }

        int partCount = bytes.Length / sizeof(uint);
        EHumanoidBodyMask result = EHumanoidBodyMask.None;

        bool IsPartEnabled(int index)
            => index < partCount && BitConverter.ToUInt32(bytes, index * sizeof(uint)) != 0u;

        // Unity AvatarMaskBodyPart order:
        // 0 Root, 1 Body, 2 Head (includes Eyes/Jaw), 3 LeftLeg, 4 RightLeg,
        // 5 LeftArm, 6 RightArm, 7 LeftFingers, 8 RightFingers,
        // 9 LeftFootIK, 10 RightFootIK, 11 LeftHandIK, 12 RightHandIK.
        if (IsPartEnabled(0))
            result |= EHumanoidBodyMask.Root;
        if (IsPartEnabled(1))
            result |= EHumanoidBodyMask.Body;
        if (IsPartEnabled(2))
            result |= EHumanoidBodyMask.FaceOnly;
        if (IsPartEnabled(3) || IsPartEnabled(9))
            result |= EHumanoidBodyMask.LeftLeg;
        if (IsPartEnabled(4) || IsPartEnabled(10))
            result |= EHumanoidBodyMask.RightLeg;
        if (IsPartEnabled(5) || IsPartEnabled(11))
            result |= EHumanoidBodyMask.LeftArm;
        if (IsPartEnabled(6) || IsPartEnabled(12))
            result |= EHumanoidBodyMask.RightArm;
        if (IsPartEnabled(7))
            result |= EHumanoidBodyMask.LeftFingers;
        if (IsPartEnabled(8))
            result |= EHumanoidBodyMask.RightFingers;

        // Allow non-humanoid channels (blendshapes and animated parameters) when the mask has no
        // explicit transform exclusions or enables at least one transform entry.
        if (SerializedAssetYamlReader.GetNode(maskMapping, "m_Elements") is not YamlSequenceNode elements
            || elements.Children.Count == 0
            || HasAnyEnabledTransformElement(elements))
        {
            result |= EHumanoidBodyMask.NonHumanoid;
        }

        return result;
    }

    private static bool HasAnyEnabledTransformElement(YamlSequenceNode elements)
    {
        foreach (YamlNode child in elements.Children)
        {
            if (child is YamlMappingNode element
                && (SerializedAssetYamlReader.GetScalarFloat(element, "m_Weight") ?? 0.0f) > 0.5f)
            {
                return true;
            }
        }

        return false;
    }
}
