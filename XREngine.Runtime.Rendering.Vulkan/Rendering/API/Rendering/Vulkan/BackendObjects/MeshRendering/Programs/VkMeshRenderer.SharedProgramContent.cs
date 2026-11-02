using System.Text;

namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
    /// <summary>
    /// Builds the content identity of a generated program from the exact source text of
    /// every stage and the generation axes. Runs only when a renderer has no cached entry
    /// for the program. A stage without inline source falls back to the per-material
    /// identity, so it shares nothing that its text cannot prove equal.
    /// </summary>
    private static string BuildSharedProgramContentIdentity(
        IReadOnlyList<XRShader> shaders,
        string generatedProgramAxes,
        string fallbackIdentity)
    {
        StringBuilder builder = new(256);
        builder.Append("axes=").Append(generatedProgramAxes);
        for (int index = 0; index < shaders.Count; index++)
        {
            XRShader shader = shaders[index];
            if (shader.Source?.Text is not { Length: > 0 } text)
                return "material:" + fallbackIdentity;

            builder.Append(";stage=").Append((int)shader.Type)
                .Append(':').Append(text.Length)
                .Append(':').Append(text);
        }

        return builder.ToString();
    }
}
