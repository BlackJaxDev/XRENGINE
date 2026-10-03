using System.Numerics;
using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

/// <summary>
/// A context object, which controls low-level operations of Steam Audio. Typically, a context is specified once
/// during the execution of the client program, before calling any other API functions.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct IPLContext
{
    public IntPtr Handle;

    public static implicit operator IntPtr(IPLContext context) => context.Handle;
    public static implicit operator IPLContext(IntPtr handle) => new() { Handle = handle };

    public static bool operator ==(IPLContext left, IPLContext right) => left.Handle == right.Handle;
    public static bool operator !=(IPLContext left, IPLContext right) => left.Handle != right.Handle;

    public override readonly bool Equals(object? obj)
        => obj is IPLContext context && context == this;
    public override readonly int GetHashCode()
        => Handle.GetHashCode();
    public override readonly string ToString()
        => Handle.ToString();

    public readonly void Retain()
        => Phonon.iplContextRetain(this);

    public void Release()
        => Phonon.iplContextRelease(ref this);

    public readonly Vector3 CalculateRelativeDirection(Vector3 sourcePosition, Vector3 listenerPosition, Vector3 listenerAhead, Vector3 listenerUp)
        => Phonon.iplCalculateRelativeDirection(this, sourcePosition, listenerPosition, listenerAhead, listenerUp);

    public readonly IPLSerializedObject CreateSerializedObject(IPLSerializedObjectSettings settings)
    {
        IPLSerializedObject o = default;
        var error = Phonon.iplSerializedObjectCreate(this, ref settings, ref o);
        if (error != IPLerror.IPL_STATUS_SUCCESS)
            throw new Exception(error.ToString());
        return o;
    }

    public readonly IPLScene CreateScene(IPLSceneSettings settings)
    {
        IPLScene scene = default;
        var error = Phonon.iplSceneCreate(this, ref settings, ref scene);
        if (error != IPLerror.IPL_STATUS_SUCCESS)
            throw new Exception(error.ToString());
        return scene;
    }

    public readonly IPLAudioBuffer CreateAudioBuffer(int numChannels, int numSamples)
    {
        IPLAudioBuffer buffer = default;
        Phonon.iplAudioBufferAllocate(this, numChannels, numSamples, ref buffer);
        return buffer;
    }

    public readonly IPLProbeArray CreateProbeArray()
    {
        IPLProbeArray probeArray = default;
        Phonon.iplProbeArrayCreate(this, ref probeArray);
        return probeArray;
    }

    public readonly IPLProbeBatch CreateProbeBatch()
    {
        IPLProbeBatch probeBatch = default;
        Phonon.iplProbeBatchCreate(this, ref probeBatch);
        return probeBatch;
    }

    public readonly IPLSimulator CreateSimulator(IPLSimulationSettings settings)
    {
        IPLSimulator simulator = default;
        Phonon.iplSimulatorCreate(this, ref settings, ref simulator);
        return simulator;
    }

    public readonly IPLHRTF CreateHRTF(IPLAudioSettings settings, IPLHRTFSettings hrtfSettings)
    {
        IPLHRTF hrtf = default;
        Phonon.iplHRTFCreate(this, ref settings, ref hrtfSettings, ref hrtf);
        return hrtf;
    }

    public readonly IPLPanningEffect CreatePanningEffect(IPLAudioSettings settings, IPLPanningEffectSettings panningSettings)
    {
        IPLPanningEffect effect = default;
        Phonon.iplPanningEffectCreate(this, ref settings, ref panningSettings, ref effect);
        return effect;
    }

    public readonly IPLBinauralEffect CreateBinauralEffect(IPLAudioSettings settings, IPLBinauralEffectSettings binauralSettings)
    {
        IPLBinauralEffect effect = default;
        Phonon.iplBinauralEffectCreate(this, ref settings, ref binauralSettings, ref effect);
        return effect;
    }

    public readonly IPLVirtualSurroundEffect CreateVirtualSurroundEffect(IPLAudioSettings settings, IPLVirtualSurroundEffectSettings virtualSurroundSettings)
    {
        IPLVirtualSurroundEffect effect = default;
        Phonon.iplVirtualSurroundEffectCreate(this, ref settings, ref virtualSurroundSettings, ref effect);
        return effect;
    }

    public readonly IPLAmbisonicsEncodeEffect CreateAmbisonicsEncodeEffect(IPLAudioSettings settings, IPLAmbisonicsEncodeEffectSettings encodeSettings)
    {
        IPLAmbisonicsEncodeEffect effect = default;
        Phonon.iplAmbisonicsEncodeEffectCreate(this, ref settings, ref encodeSettings, ref effect);
        return effect;
    }

    public readonly IPLAmbisonicsPanningEffect CreateAmbisonicsPanningEffect(IPLAudioSettings settings, IPLAmbisonicsPanningEffectSettings panningSettings)
    {
        IPLAmbisonicsPanningEffect effect = default;
        Phonon.iplAmbisonicsPanningEffectCreate(this, ref settings, ref panningSettings, ref effect);
        return effect;
    }

    public readonly IPLAmbisonicsBinauralEffect CreateAmbisonicsBinauralEffect(IPLAudioSettings settings, IPLAmbisonicsBinauralEffectSettings binauralSettings)
    {
        IPLAmbisonicsBinauralEffect effect = default;
        Phonon.iplAmbisonicsBinauralEffectCreate(this, ref settings, ref binauralSettings, ref effect);
        return effect;
    }

    public readonly IPLAmbisonicsRotationEffect CreateAmbisonicsRotationEffect(IPLAudioSettings settings, IPLAmbisonicsRotationEffectSettings rotationSettings)
    {
        IPLAmbisonicsRotationEffect effect = default;
        Phonon.iplAmbisonicsRotationEffectCreate(this, ref settings, ref rotationSettings, ref effect);
        return effect;
    }

    public readonly IPLAmbisonicsDecodeEffect CreateAmbisonicsDecodeEffect(IPLAudioSettings settings, IPLAmbisonicsDecodeEffectSettings decodeSettings)
    {
        IPLAmbisonicsDecodeEffect effect = default;
        Phonon.iplAmbisonicsDecodeEffectCreate(this, ref settings, ref decodeSettings, ref effect);
        return effect;
    }

    public readonly IPLDirectEffect CreateDirectEffect(IPLAudioSettings settings, IPLDirectEffectSettings directSettings)
    {
        IPLDirectEffect effect = default;
        Phonon.iplDirectEffectCreate(this, ref settings, ref directSettings, ref effect);
        return effect;
    }

    public readonly IPLReflectionEffect CreateReflectionEffect(IPLAudioSettings settings, IPLReflectionEffectSettings reflectionSettings)
    {
        IPLReflectionEffect effect = default;
        Phonon.iplReflectionEffectCreate(this, ref settings, ref reflectionSettings, ref effect);
        return effect;
    }

    public readonly IPLReflectionMixer CreateReflectionMixer(IPLAudioSettings settings, IPLReflectionEffectSettings reflectionSettings)
    {
        IPLReflectionMixer mixer = default;
        Phonon.iplReflectionMixerCreate(this, ref settings, ref reflectionSettings, ref mixer);
        return mixer;
    }

    public readonly IPLPathEffect CreatePathEffect(IPLAudioSettings settings, IPLPathEffectSettings pathSettings)
    {
        IPLPathEffect effect = default;
        Phonon.iplPathEffectCreate(this, ref settings, ref pathSettings, ref effect);
        return effect;
    }

    public readonly IPLProbeBatch LoadProbeBatch(IPLSerializedObject serializedObject)
    {
        IPLProbeBatch probeBatch = default;
        Phonon.iplProbeBatchLoad(this, serializedObject, ref probeBatch);
        return probeBatch;
    }
}

/// <summary>
/// A serialized representation of an API object, like an \c IPLScene or \c IPLProbeBatch. Create an empty
/// serialized object if you want to serialize an existing object to a byte array, or create a serialized
/// object that wraps an existing byte array if you want to deserialize it.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct IPLSerializedObject { public IntPtr Handle; }

/// <summary>
/// Application-wide state for the Embree ray tracer. An Embree device must be created before using any of Steam
/// Audio's Embree ray tracing functionality. In terms of the Embree API, this object encapsulates an \c RTCDevice object.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct IPLEmbreeDevice { public IntPtr Handle; }

/// <summary>
/// Provides a list of OpenCL devices available on the user's system. Use this to enumerate the available
/// OpenCL devices, inspect their capabilities, and select the most suitable one for your application's needs.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct IPLOpenCLDeviceList { public IntPtr Handle; }

/// <summary>
/// Application-wide state for OpenCL. An OpenCL device must be created before using any of Steam Audio's
/// Radeon Rays or TrueAudio Next functionality. In terms of the OpenCL API, this object encapsulates a
/// \c cl_context object, along with up to 2 \c cl_command_queue objects.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct IPLOpenCLDevice { public IntPtr Handle; }

/// <summary>
/// Application-wide state for the Radeon Rays ray tracer. A Radeon Rays device must be created before using any of
/// Steam Audio's Radeon Rays ray tracing functionality. In terms of the Radeon Rays API, this object encapsulates
/// a \c RadeonRays::IntersectionApi object. */
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct IPLRadeonRaysDevice { public IntPtr Handle; }

/// <summary>
/// Application-wide state for the TrueAudio Next convolution engine. A TrueAudio Next device must be created
/// before using any of Steam Audio's TrueAudio Next convolution functionality. In terms of the TrueAudio Next API,
/// this object encapsulates an \c amf::TANContext and amf::TANConvolution object. */
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct IPLTrueAudioNextDevice { public IntPtr Handle; }

/// <summary>
/// A 3D scene, which can contain geometry objects that can interact with acoustic rays. The scene object itself
/// doesn't contain any geometry, but is a container for \c IPLStaticMesh and \c IPLInstancedMesh objects, which
/// do contain geometry.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct IPLScene { public IntPtr Handle; }

/// <summary>
/// A triangle mesh that doesn't move or deform in any way. The unchanging portions of a scene should typically
/// be collected into a single static mesh object. In addition to the geometry, a static mesh also contains
/// acoustic material information for each triangle.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct IPLStaticMesh { public IntPtr Handle; }

/// <summary>
/// A triangle mesh that can be moved (translated), rotated, or scaled, but cannot deform. Portions of a scene
/// that undergo rigid-body motion can be represented as instanced meshes. An instanced mesh is essentially a
/// the sub-scene into the scene with the transform applied. For example, the sub-scene may be a prefab door,
/// and the transform can be used to place it in a doorway and animate it as it opens or closes.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct IPLInstancedMesh { public IntPtr Handle; }

/// <summary>
/// A Head-Related Transfer Function (HRTF). HRTFs describe how sound from different directions is perceived by a
/// each of a listener's ears, and are a crucial component of spatial audio. Steam Audio includes a built-in HRTF,
/// while also allowing developers and users to import their own custom HRTFs.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct IPLHRTF { public IntPtr Handle; }

/// <summary>
/// Pans a single-channel point source to a multi-channel speaker layout based on the 3D position of the source relative to the listener.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct IPLPanningEffect { public IntPtr Handle; }

/// <summary>
/// Spatializes a point source using an HRTF, based on the 3D position of the source relative to the listener. The
/// source audio can be 1- or 2-channel; in either case all input channels are spatialized from the same position.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct IPLBinauralEffect { public IntPtr Handle; }

/// <summary>
/// Spatializes multi-channel speaker-based audio (e.g., stereo, quadraphonic, 5.1, or 7.1) using HRTF-based binaural
/// rendering. The audio signal for each speaker is spatialized from a point in space corresponding to the speaker's
/// location. This allows users to experience a surround sound mix over regular stereo headphones.
/// 
/// Virtual surround is also a fast way to get approximate binaural rendering. All sources can be panned to some
/// surround format (say, 7.1). After the sources are mixed, the mix can be rendered using virtual surround. This can
/// reduce CPU usage, at the cost of spatialization accuracy.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct IPLVirtualSurroundEffect { public IntPtr Handle; }

/// <summary>
/// Encodes a point source into Ambisonics. Given a point source with some direction relative to the listener, this
/// effect generates an Ambisonic audio buffer that approximates a point source in the given direction. This allows
/// multiple point sources and ambiences to mixed to a single Ambisonics buffer before being spatialized.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct IPLAmbisonicsEncodeEffect { public IntPtr Handle; }

/// <summary>
/// Renders Ambisonic audio by panning it to a standard speaker layout. This involves calculating signals to emit
/// from each speaker so as to approximate the Ambisonic sound field.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct IPLAmbisonicsPanningEffect { public IntPtr Handle; }

/// <summary>
/// Renders Ambisonic audio using HRTF-based binaural rendering. This results in more immersive spatialization of the
/// Ambisonic audio as compared to using an Ambisonics panning effect, at the cost of slightly increased CPU usage.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct IPLAmbisonicsBinauralEffect { public IntPtr Handle; }

/// <summary>
/// Applies a rotation to an Ambisonics audio buffer. The input buffer is assumed to describe a sound field in
/// "world space". The output buffer is then the same sound field, but expressed relative to the listener's orientation.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct IPLAmbisonicsRotationEffect { public IntPtr Handle; }

/// <summary>
/// Applies a rotation to an Ambisonics audio buffer, then decodes it using panning or binaural rendering. This is
/// essentially an Ambisonics rotate effect followed by either an Ambisonics panning effect or an Ambisonics binaural effect.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct IPLAmbisonicsDecodeEffect { public IntPtr Handle; }

/// <summary>
/// Filters and attenuates an audio signal based on various properties of the direct path between a point source and the listener.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct IPLDirectEffect { public IntPtr Handle; }

/// <summary>
/// A multi-channel impulse response for use with a reflection effect. Steam Audio creates and manages objects of
/// this type internally, your application only needs to pass handles to these objects to the appropriate Steam Audio API functions.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct IPLReflectionEffectIR { public IntPtr Handle; }

/// <summary>
/// Applies the result of physics-based reflections simulation to an audio buffer. The result is encoded in 
/// Ambisonics, and can be decoded using an Ambisonics decode effect.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct IPLReflectionEffect { public IntPtr Handle; }

/// <summary>
/// Mixes the outputs of multiple reflection effects, and generates a single sound field containing all the 
/// reflected sound reaching the listener. Using this is optional. Depending on the reflection effect algorithm used, 
/// a reflection mixer may provide a reduction in CPU usage.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct IPLReflectionMixer { public IntPtr Handle; }

/// <summary>
/// Applies the result of simulating sound paths from the source to the listener. Multiple paths that sound can take
/// as it propagates from the source to the listener are combined into an Ambisonic sound field.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct IPLPathEffect { public IntPtr Handle; }

/// <summary>
/// An array of sound probes. Each probe has a position and a radius of influence.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct IPLProbeArray { public IntPtr Handle; }

/// <summary>
/// A batch of sound probes, along with associated data. The associated data may include reverb, reflections from a
/// static source position, pathing, and more. This data is loaded and unloaded as a unit, either from disk or over the network.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct IPLProbeBatch { public IntPtr Handle; }

/// <summary>
/// A sound source, for the purposes of simulation. This object is used to specify various parameters for direct
/// and indirect sound propagation simulation, and to retrieve the simulation results.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct IPLSource { public IntPtr Handle; }

/// <summary>
/// Manages direct and indirect sound propagation simulation for multiple sources. Your application will typically
/// create one simulator object and use it to run simulations with different source and listener parameters between
/// consecutive simulation runs. The simulator can also be reused across scene changes.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct IPLSimulator 
{
    public IntPtr Handle;

    public readonly IPLSource CreateSource(IPLSourceSettings sourceSettings)
    {
        IPLSource source = default;
        Phonon.iplSourceCreate(this, ref sourceSettings, ref source);
        return source;
    }
}