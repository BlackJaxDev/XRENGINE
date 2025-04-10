using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

/** A directivity pattern that can be used to model changes in sound intensity as a function of the source's
    orientation. Can be used with both direct and indirect sound propagation.

    The default directivity model is a weighted dipole. This is a linear blend between an omnidirectional
    source (which emits sound with equal intensity in all directions), and a dipole oriented along the z-axis
    in the source's coordinate system (which focuses sound along the +z and -z axes). A callback function
    can be specified to implement any other arbitrary directivity pattern. */
[StructLayout(LayoutKind.Sequential)]
public struct IPLDirectivity
{
    /** How much of the dipole to blend into the directivity pattern. \c 0 = pure omnidirectional, \c 1 = pure
        dipole. \c 0.5f results in a cardioid directivity pattern. */
    public float dipoleWeight;

    /** How "sharp" the dipole is. Higher values result in sound being focused within a narrower range of
        directions. */
    public float dipolePower;

    /** If non \c NULL, this function will be called whenever Steam Audio needs to evaluate a directivity
        pattern. */
    public IntPtr callback;

    /** Pointer to arbitrary data that will be provided to the \c callback function whenever it is called.
        May be \c NULL. */
    public IntPtr userData;
}