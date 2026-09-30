using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

/// <summary>
/// Specifies requirements that an OpenCL device must meet in order to be considered when listing
/// OpenCL devices.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct IPLOpenCLDeviceSettings
{
    /// <summary>
    /// The type of device. Set to IPL_OPENCLDEVICETYPE_ANY to consider all available devices.
    /// </summary>
    public IPLOpenCLDeviceType type;

    /// <summary>
    /// The number of GPU compute units (CUs) that should be reserved for use by Steam Audio. If set to a
    /// non-zero value, then a GPU will be included in the device list only if it can reserve at least
    /// this many CUs. Set to 0 to indicate that Steam Audio can use the entire GPU, in which case all
    /// available GPUs will be considered.
    /// Ignored if type is IPL_OPENCLDEVICETYPE_CPU.
    /// </summary>
    public int numCUsToReserve;

    /// <summary>
    /// The fraction of reserved CUs that should be used for impulse response (IR) update. IR update
    /// includes: a) ray tracing using Radeon Rays to simulate sound propagation, and/or b) pre-transformation
    /// of IRs for convolution using TrueAudio Next. Steam Audio will only list GPU devices that are able
    /// to subdivide the reserved CUs as per this value. The value must be between 0 and 1.
    /// For example, if numCUsToReserve is 8, and fractionCUsForIRUpdate is 0.5f, then 4 CUs
    /// will be used for IR update and 4 CUs will be used for convolution. Below are typical scenarios:
    /// - Using only TrueAudio Next. Set fractionCUsForIRUpdate to 0.5f. This ensures that reserved
    ///   CUs are available for IR update as well as convolution.
    /// - Using TrueAudio Next and Radeon Rays for real-time simulation and rendering. Choosing
    ///   fractionCUsForIRUpdate may require some experimentation to utilize reserved CUs optimally. You
    ///   can start by setting fractionCUsForIRUpdate to 0.5f. However, if IR calculation has high
    ///   latency with these settings, increase fractionCUsForIRUpdate to use more CUs for ray tracing.
    /// - Using only Radeon Rays. Set fractionCUsForIRUpdate to 1, to make sure all the reserved CUs
    ///   are used for ray tracing. If using Steam Audio for preprocessing (e.g. baking reverb), then
    ///   consider setting numCUsToReserve to 0 to use the entire GPU for accelerated ray tracing.
    /// Ignored if type is IPL_OPENCLDEVICETYPE_CPU or numCUsToReserve is 0.
    /// </summary>
    public float fractionCUsForIRUpdate;

    /// <summary>
    /// If IPL_TRUE, then the GPU device must support TrueAudio Next. It is not necessary to set this
    /// to IPL_TRUE if numCUsToReserve or fractionCUsForIRUpdate are set to non-zero values.
    /// </summary>
    public IPLbool requiresTAN;
}