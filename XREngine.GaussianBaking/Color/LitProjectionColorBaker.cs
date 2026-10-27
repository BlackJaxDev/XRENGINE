using XREngine.Rendering.Models.Gaussian.Baking.Views;

namespace XREngine.Rendering.Models.Gaussian.Baking.Color;

/// <summary>
/// Bakes leaf colors from engine-lit renders. It captures the views in batches, and it adds each batch to the
/// colors before it asks for the next batch, so only one batch of images is in memory at a time.
/// </summary>
public sealed class LitProjectionColorBaker : ISplatColorBaker
{
    /// <summary>The number of views in one capture call.</summary>
    public const int BatchSize = 8;

    /// <inheritdoc />
    public string Mode => "lit-projection";

    /// <inheritdoc />
    public async Task<float> BakeAsync(SplatColorBakeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        LitProjectionColorAccumulator accumulator = new(context.Leaves, context.Rest, context.Width, context.Height);
        for (int start = 0; start < context.Views.Count; start += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int count = Math.Min(BatchSize, context.Views.Count - start);
            BakeCameraPose[] batch = new BakeCameraPose[count];
            for (int i = 0; i < count; i++)
                batch[i] = context.Views[start + i];

            float[][] images = await context.Capture(batch, cancellationToken).ConfigureAwait(false);
            if (images is null || images.Length != count)
                throw new GaussianBakeException(GaussianBakeFailureCodes.CaptureFailed, $"The capture returned {images?.Length ?? 0} images for {count} views.");

            accumulator.Accumulate(batch, images);
        }

        float scale = accumulator.Finish();
        LastSeenLeafCount = accumulator.SeenLeafCount;
        return scale;
    }

    /// <summary>Gets the number of leaves that a view saw in the last bake. Zero means that no leaf has a color.</summary>
    public int LastSeenLeafCount { get; private set; }
}
