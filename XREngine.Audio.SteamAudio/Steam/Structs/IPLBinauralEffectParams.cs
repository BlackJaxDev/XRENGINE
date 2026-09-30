using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

/** Parameters for applying a binaural effect to an audio buffer. */
[StructLayout(LayoutKind.Sequential)]
public struct IPLBinauralEffectParams
{
    /** Unit vector pointing from the listener towards the source. */
    public IPLVector3 direction;

    /** The interpolation technique to use. */
    public IPLHRTFInterpolation interpolation;

    /** Amount to blend input audio with spatialized audio. When set to 0, output audio is not spatialized at all
        and is close to input audio. If set to 1, output audio is fully spatialized. */
    public float spatialBlend;

    /** The HRTF to use. */
    public IPLHRTF hrtf;

    /** Base address of an array into which to write the left- and right-ear peak delays for the HRTF used
        to spatialize the input audio. Memory for this array must be allocated and managed by the caller.
        Can be NULL, in which case peak delays will not be written. */
    public IntPtr peakDelays;
}
