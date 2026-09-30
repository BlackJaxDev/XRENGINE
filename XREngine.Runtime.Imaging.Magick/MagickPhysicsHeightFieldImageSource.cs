using ImageMagick;
using XREngine.Scene.Physics;

namespace XREngine.Runtime.Imaging.Magick;

/// <summary>Decodes grayscale height samples for authored physics terrain.</summary>
public sealed class MagickPhysicsHeightFieldImageSource : IPhysicsHeightFieldImageSource
{
    public PhysicsHeightFieldImage Load(string imagePath)
    {
        using MagickImage image = new(imagePath);
        using IPixelCollection<float> pixels = image.GetPixels()
            ?? throw new InvalidDataException($"ImageMagick returned no pixels for '{imagePath}'.");
        ushort[] samples = pixels.ToShortArray("I")
            ?? throw new InvalidDataException($"ImageMagick returned no height samples for '{imagePath}'.");
        return new PhysicsHeightFieldImage(image.Width, image.Height, samples);
    }
}
