using System.Numerics;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Importers.SourceToon;

namespace XREngine.Scene.Importers;

public static partial class SerializedMaterialImporter
{
    /// <summary>Maps source array playback and rejects controls without a matching operation.</summary>
    private static void BindSourceToonFlipbook(XRMaterial material, SerializedMaterialDocument document)
    {
        bool enabled = document.TryGetPositive("_EnableFlipbook");
        SetInt(material, "_SourceFlipbook", enabled ? 1 : 0);
        if (!enabled)
            return;

        // The source array owns this operation. The legacy atlas overlay must not run too.
        material.SetUberFeatureEnabled("flipbook", false);
        foreach ((string name, int maximum) in new (string, int)[] {
            ("_FlipbookTexArrayUV", 3), ("_FlipbookMaskUV", 3), ("_FlipbookMaskChannel", 3),
            ("_FlipbookAspectRatioMode", 2), ("_FlipbookSymmetryMode", 2),
            ("_FlipbookTiled", 2), ("_FlipbookAlphaControlsFinalAlpha", 2) })
        {
            float value = GetFloat(document, name);
            if (!float.IsFinite(value) || value != MathF.Truncate(value) || value < 0 || value > maximum)
                throw new SourceVisualImportException($"Active flipbook control '{name}' must be an integer from 0 to {maximum}.");
        }
        foreach (string name in new[] { "_EnableFlipbook1", "_FlipbookMirroredUVMode",
            "_FlipbookMaskGlobalMask", "_FlipbookColorThemeIndex" })
            if (GetFloat(document, name) != 0.0f)
                throw new SourceVisualImportException($"Active flipbook control '{name}' has no supported source mapping.");

        if (document.TryGetPositive("_EnableAudioLink"))
        {
            foreach (string name in new[] { "_FlipbookChronotensityEnabled", "_FlipbookALColorOverrideEnabled" })
                if (document.TryGetPositive(name))
                    throw new SourceVisualImportException($"Active flipbook control '{name}' requires AudioLink.");
            foreach (string name in new[] { "_AudioLinkFlipbookScale", "_AudioLinkFlipbookFrame",
                "_AudioLinkFlipbookAlpha", "_AudioLinkFlipbookEmission" })
                if (document.TryGetVector(name, out Vector4 audioValue) && audioValue != Vector4.Zero)
                    throw new SourceVisualImportException($"Active flipbook control '{name}' requires AudioLink.");
        }

        Vector4 scale = Vector("_FlipbookScale", Vector4.One);
        Vector4 position = Vector("_FlipbookPosition", new Vector4(0.5f));
        Vector4 sides = Vector("_FlipbookSideOffset", Vector4.Zero);
        if (MathF.Abs(scale.X + sides.Y + sides.X) < 0.0000001f ||
            MathF.Abs(scale.Y + sides.W + sides.Z) < 0.0000001f)
            throw new SourceVisualImportException("Active flipbook bounds have zero width or height.");
        SetVector4(material, "_SourceFlipbookLayout", new Vector4(scale.X, scale.Y, position.X, position.Y));
        SetVector4(material, "_SourceFlipbookSides", sides);
        SetVector4(material, "_SourceFlipbookOptions", new Vector4(
            GetFloat(document, "_FlipbookAlpha", 1), GetFloat(document, "_FlipbookColorReplaces"),
            GetFloat(document, "_FlipbookIntensityControlsAlpha"), GetFloat(document, "_FlipbookAspectRatioMode")));
        SetVector4(material, "_SourceFlipbookMotion", new Vector4(
            GetFloat(document, "_FlipbookRotation"), GetFloat(document, "_FlipbookRotationSpeed"),
            GetFloat(document, "_FlipbookSymmetryMode"), GetFloat(document, "_FlipbookTiled")));
        SetVector4(material, "_SourceFlipbookFlags", new Vector4(
            GetFloat(document, "_FlipbookStartAndEnd"), GetFloat(document, "_FlipbookHueSelectOrShift"),
            GetFloat(document, "_FlipbookMaskChannel"), 0));
        SetVector4(material, "_SourceFlipbookHue", new Vector4(
            GetFloat(document, "_FlipbookHueShiftEnabled"), GetFloat(document, "_FlipbookHueShift"),
            GetFloat(document, "_FlipbookHueShiftSpeed"), GetFloat(document, "_FlipbookHueShiftColorSpace")));
        foreach ((string name, float fallback) in new (string, float)[] {
            ("_FlipbookFPS", 30), ("_FlipbookFrameOffset", 0), ("_FlipbookManualFrameControl", 0),
            ("_FlipbookCurrentFrame", 0), ("_FlipbookStartFrame", 0), ("_FlipbookEndFrame", 0),
            ("_FlipbookCrossfadeEnabled", 0), ("_FlipbookEmissionStrength", 0) })
            SetFloat(material, name, GetFloat(document, name, fallback));
        foreach (string name in new[] { "_FlipbookTexArrayUV", "_FlipbookMaskUV", "_FlipbookBlendType", "_FlipbookAlphaControlsFinalAlpha" })
            SetInt(material, name, checked((int)GetFloat(document, name)));
        int blend = checked((int)GetFloat(document, "_FlipbookBlendType"));
        if (blend is not (0 or 1 or 2 or 5 or 6 or 7 or 8 or 9 or 20))
            throw new SourceVisualImportException($"Active flipbook blend mode {blend} is not supported.");
        SetVector4(material, "_FlipbookColor", Vector("_FlipbookColor", Vector4.One));
        Vector4 crossfade = Vector("_FlipbookCrossfadeRange", new Vector4(0.75f, 1, 0, 0));
        SetVector2(material, "_FlipbookCrossfadeRange", new Vector2(crossfade.X, crossfade.Y));
        SetVector4(material, "_FlipbookMask_ST", new Vector4(1, 1, 0, 0));
        SetVector4(material, "_FlipbookScaleOffset", new Vector4(1, 1, 0, 0));
        foreach (string name in new[] { "_FlipbookTexArrayPan", "_FlipbookMaskPan" })
        {
            Vector4 value = Vector(name, Vector4.Zero);
            SetVector2(material, name, new Vector2(value.X, value.Y));
        }
        ApplyTextureTransform(material, document, "_FlipbookMask", "_FlipbookMask");
        if (document.Textures.TryGetValue("_FlipbookTexArray", out SerializedTexturePropertyDocument? texture))
            SetVector4(material, "_FlipbookScaleOffset", new Vector4(texture.Scale.X, texture.Scale.Y, texture.Offset.X, texture.Offset.Y));

        Vector4 Vector(string name, Vector4 fallback)
            => document.TryGetVector(name, out Vector4 value) ? value : fallback;
    }
}
