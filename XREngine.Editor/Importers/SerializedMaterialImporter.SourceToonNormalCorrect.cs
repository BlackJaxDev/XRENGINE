using System.Numerics;
using XREngine.Rendering;

namespace XREngine.Scene.Importers;

public static partial class SerializedMaterialImporter
{
    /// <summary>
    /// Binds the source normal correction to the Uber normal correction controls.
    /// </summary>
    /// <remarks>
    /// The source applies the correction amount only while its toggle is on. A material without the toggle
    /// has no correction.
    /// </remarks>
    private static void BindSourceToonNormalCorrect(XRMaterial material, SerializedMaterialDocument document)
    {
        bool enabled = document.TryGetPositive("_NormalCorrect");
        SetFloat(material, "_NormalCorrectStrength", enabled ? GetFloat(document, "_NormalCorrectAmount", 0.9f) : 0.0f);
        SetInt(material, "_NormalCorrectVertexColor", 0);
        Vector3 origin = document.TryGetVector("_NormalCorrectOrigin", out Vector4 authoredOrigin)
            ? new Vector3(authoredOrigin.X, authoredOrigin.Y, authoredOrigin.Z)
            : new Vector3(0.0f, 0.4f, -0.025f);
        SetVector3(material, "_NormalCorrectOrigin", origin);
    }
}
