using System.Globalization;
using System.Text;

namespace XREngine.Rendering.Vulkan;

internal sealed record VulkanTransformFeedbackCompilePlan(IReadOnlyList<VulkanTransformFeedbackBufferCapture> Buffers)
{
    public bool HasCaptures => Buffers.Count > 0;
    public string Identity { get; } = BuildIdentity(Buffers);

    public static VulkanTransformFeedbackCompilePlan Empty { get; } =
        new(Array.Empty<VulkanTransformFeedbackBufferCapture>());

    private static string BuildIdentity(IReadOnlyList<VulkanTransformFeedbackBufferCapture> buffers)
    {
        if (buffers.Count == 0)
            return "TransformFeedback=<none>";

        StringBuilder builder = new("TransformFeedback=");
        for (int i = 0; i < buffers.Count; i++)
        {
            if (i > 0)
                builder.Append(';');

            VulkanTransformFeedbackBufferCapture buffer = buffers[i];
            builder
                .Append(buffer.Binding.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(buffer.Type)
                .Append(':')
                .AppendJoin('|', buffer.Names);
        }

        return builder.ToString();
    }
}
