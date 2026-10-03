using XREngine.Scene.Physics;
using XREngine.Imaging;

namespace XREngine.Runtime.Imaging.Magick;

/// <summary>Installs ImageMagick source-image decoders for authoring.</summary>
public static class MagickImagingBackend
{
    public static void Register()
    {
        RuntimeImageCodecs.Current = new MagickRuntimeImageCodec();
        PhysicsHeightFieldImageSource.Install(new MagickPhysicsHeightFieldImageSource());
    }
}
