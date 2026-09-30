using System.Runtime.InteropServices;

namespace XREngine.Audio.Steam;

public static class Phonon
{
    public const uint STEAMAUDIO_VERSION_MAJOR = 4;
    public const uint STEAMAUDIO_VERSION_MINOR = 6;
    public const uint STEAMAUDIO_VERSION_PATCH = 0;
    public const uint STEAMAUDIO_VERSION = (STEAMAUDIO_VERSION_MAJOR << 16) | (STEAMAUDIO_VERSION_MINOR << 8) | STEAMAUDIO_VERSION_PATCH;

    private const string DllName = "phonon";

    #region Context
    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLerror iplContextCreate(ref IPLContextSettings settings, ref IPLContext context);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLContext iplContextRetain(IPLContext context);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplContextRelease(ref IPLContext context);
    #endregion

    #region Geometry
    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLVector3 iplCalculateRelativeDirection(IPLContext context, IPLVector3 sourcePosition, IPLVector3 listenerPosition, IPLVector3 listenerAhead, IPLVector3 listenerUp);
    #endregion

    #region Serialization
    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLerror iplSerializedObjectCreate(IPLContext context, ref IPLSerializedObjectSettings settings, ref IPLSerializedObject serializedObject);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLSerializedObject iplSerializedObjectRetain(IPLSerializedObject serializedObject);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplSerializedObjectRelease(ref IPLSerializedObject serializedObject);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IntPtr iplSerializedObjectGetSize(IPLSerializedObject serializedObject);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IntPtr iplSerializedObjectGetData(IPLSerializedObject serializedObject);
    #endregion

    #region Scene
    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern IPLerror iplSceneCreate(IPLContext context, ref IPLSceneSettings settings, ref IPLScene scene);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern IPLScene iplSceneRetain(IPLScene scene);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern void iplSceneRelease(ref IPLScene scene);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern IPLerror iplSceneLoad(IPLContext context, ref IPLSceneSettings settings, IPLSerializedObject serializedObject, IPLProgressCallback progressCallback, IntPtr progressCallbackUserData, ref IPLScene scene);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern void iplSceneSave(IPLScene scene, IPLSerializedObject serializedObject);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern void iplSceneSaveOBJ(IPLScene scene, IntPtr fileBaseName);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern void iplSceneCommit(IPLScene scene);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern IPLerror iplStaticMeshCreate(IPLScene scene, ref IPLStaticMeshSettings settings, ref IPLStaticMesh staticMesh);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern IPLStaticMesh iplStaticMeshRetain(IPLStaticMesh staticMesh);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern void iplStaticMeshRelease(ref IPLStaticMesh staticMesh);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern void iplStaticMeshAdd(IPLStaticMesh staticMesh, IPLScene scene);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern void iplStaticMeshRemove(IPLStaticMesh staticMesh, IPLScene scene);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern IPLerror iplInstancedMeshCreate(IPLScene scene, ref IPLInstancedMeshSettings settings, ref IPLInstancedMesh instancedMesh);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern IPLInstancedMesh iplInstancedMeshRetain(IPLInstancedMesh instancedMesh);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern void iplInstancedMeshRelease(ref IPLInstancedMesh instancedMesh);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern void iplInstancedMeshAdd(IPLInstancedMesh instancedMesh, IPLScene scene);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern void iplInstancedMeshRemove(IPLInstancedMesh instancedMesh, IPLScene scene);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern void iplInstancedMeshUpdateTransform(IPLInstancedMesh instancedMesh, IPLScene scene, IPLMatrix4x4 transform);
    #endregion

    #region Audio Buffers
    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern IPLerror iplAudioBufferAllocate(IPLContext context, int numChannels, int numSamples, ref IPLAudioBuffer audioBuffer);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern void iplAudioBufferFree(IPLContext context, ref IPLAudioBuffer audioBuffer);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern void iplAudioBufferInterleave(IPLContext context, ref IPLAudioBuffer src, IntPtr dst);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern void iplAudioBufferDeinterleave(IPLContext context, IntPtr src, ref IPLAudioBuffer dst);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern void iplAudioBufferMix(IPLContext context, ref IPLAudioBuffer input, ref IPLAudioBuffer mix);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern void iplAudioBufferDownmix(IPLContext context, ref IPLAudioBuffer input, ref IPLAudioBuffer output);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern void iplAudioBufferConvertAmbisonics(IPLContext context, IPLAmbisonicsType inType, IPLAmbisonicsType outType, ref IPLAudioBuffer input, ref IPLAudioBuffer output);
    #endregion

    #region HRTF
    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern IPLerror iplHRTFCreate(IPLContext context, ref IPLAudioSettings audioSettings, ref IPLHRTFSettings hrtfSettings, ref IPLHRTF hrtf);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern IPLHRTF iplHRTFRetain(IPLHRTF hrtf);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern void iplHRTFRelease(ref IPLHRTF hrtf);
    #endregion

    #region Embree
    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLerror iplEmbreeDeviceCreate(IPLContext context, IntPtr settings, ref IPLEmbreeDevice device);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLEmbreeDevice iplEmbreeDeviceRetain(IPLEmbreeDevice device);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplEmbreeDeviceRelease(ref IPLEmbreeDevice device);
    #endregion

    #region OpenCL
    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLerror iplOpenCLDeviceListCreate(IPLContext context, ref IPLOpenCLDeviceSettings settings, ref IPLOpenCLDeviceList deviceList);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLOpenCLDeviceList iplOpenCLDeviceListRetain(IPLOpenCLDeviceList deviceList);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplOpenCLDeviceListRelease(ref IPLOpenCLDeviceList deviceList);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int iplOpenCLDeviceListGetNumDevices(IPLOpenCLDeviceList deviceList);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplOpenCLDeviceListGetDeviceDesc(IPLOpenCLDeviceList deviceList, int index, ref IPLOpenCLDeviceDesc deviceDesc);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLerror iplOpenCLDeviceCreate(IPLContext context, IPLOpenCLDeviceList deviceList, int index, ref IPLOpenCLDevice device);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLerror iplOpenCLDeviceCreateFromExisting(IPLContext context, IntPtr convolutionQueue, IntPtr irUpdateQueue, ref IPLOpenCLDevice device);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLOpenCLDevice iplOpenCLDeviceRetain(IPLOpenCLDevice device);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplOpenCLDeviceRelease(ref IPLOpenCLDevice device);
    #endregion

    #region RadeonRays
    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLerror iplRadeonRaysDeviceCreate(IPLOpenCLDevice openCLDevice, IntPtr settings, ref IPLRadeonRaysDevice rrDevice);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLRadeonRaysDevice iplRadeonRaysDeviceRetain(IPLRadeonRaysDevice device);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplRadeonRaysDeviceRelease(ref IPLRadeonRaysDevice device);
    #endregion

    #region TAN
    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLerror iplTrueAudioNextDeviceCreate(IPLOpenCLDevice openCLDevice, ref IPLTrueAudioNextDeviceSettings settings, ref IPLTrueAudioNextDevice tanDevice);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLTrueAudioNextDevice iplTrueAudioNextDeviceRetain(IPLTrueAudioNextDevice device);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplTrueAudioNextDeviceRelease(ref IPLTrueAudioNextDevice device);
    #endregion

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern IPLerror iplStaticMeshLoad(IPLScene scene, IPLSerializedObject serializedObject, IPLProgressCallback progressCallback, IntPtr progressCallbackUserData, ref IPLStaticMesh staticMesh);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern void iplStaticMeshSave(IPLStaticMesh staticMesh, IPLSerializedObject serializedObject);

    #region Panning Effect
    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLerror iplPanningEffectCreate(IPLContext context, ref IPLAudioSettings audioSettings, ref IPLPanningEffectSettings effectSettings, ref IPLPanningEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLPanningEffect iplPanningEffectRetain(IPLPanningEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplPanningEffectRelease(ref IPLPanningEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplPanningEffectReset(IPLPanningEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLAudioEffectState iplPanningEffectApply(IPLPanningEffect effect, ref IPLPanningEffectParams parameters, ref IPLAudioBuffer input, ref IPLAudioBuffer output);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int iplPanningEffectGetTailSize(IPLPanningEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLAudioEffectState iplPanningEffectGetTail(IPLPanningEffect effect, ref IPLAudioBuffer output);
    #endregion

    #region Binaural Effect
    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLerror iplBinauralEffectCreate(IPLContext context, ref IPLAudioSettings audioSettings, ref IPLBinauralEffectSettings effectSettings, ref IPLBinauralEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLBinauralEffect iplBinauralEffectRetain(IPLBinauralEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplBinauralEffectRelease(ref IPLBinauralEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplBinauralEffectReset(IPLBinauralEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLAudioEffectState iplBinauralEffectApply(IPLBinauralEffect effect, ref IPLBinauralEffectParams parameters, ref IPLAudioBuffer input, ref IPLAudioBuffer output);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int iplBinauralEffectGetTailSize(IPLBinauralEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLAudioEffectState iplBinauralEffectGetTail(IPLBinauralEffect effect, ref IPLAudioBuffer output);
    #endregion

    #region Virtual Surround Effect
    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLerror iplVirtualSurroundEffectCreate(IPLContext context, ref IPLAudioSettings audioSettings, ref IPLVirtualSurroundEffectSettings effectSettings, ref IPLVirtualSurroundEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLVirtualSurroundEffect iplVirtualSurroundEffectRetain(IPLVirtualSurroundEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplVirtualSurroundEffectRelease(ref IPLVirtualSurroundEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplVirtualSurroundEffectReset(IPLVirtualSurroundEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLAudioEffectState iplVirtualSurroundEffectApply(IPLVirtualSurroundEffect effect, ref IPLVirtualSurroundEffectParams parameters, ref IPLAudioBuffer input, ref IPLAudioBuffer output);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int iplVirtualSurroundEffectGetTailSize(IPLVirtualSurroundEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLAudioEffectState iplVirtualSurroundEffectGetTail(IPLVirtualSurroundEffect effect, ref IPLAudioBuffer output);
    #endregion

    #region Ambisonics Encode Effect
    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLerror iplAmbisonicsEncodeEffectCreate(IPLContext context, ref IPLAudioSettings audioSettings, ref IPLAmbisonicsEncodeEffectSettings effectSettings, ref IPLAmbisonicsEncodeEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLAmbisonicsEncodeEffect iplAmbisonicsEncodeEffectRetain(IPLAmbisonicsEncodeEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplAmbisonicsEncodeEffectRelease(ref IPLAmbisonicsEncodeEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplAmbisonicsEncodeEffectReset(IPLAmbisonicsEncodeEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLAudioEffectState iplAmbisonicsEncodeEffectApply(IPLAmbisonicsEncodeEffect effect, ref IPLAmbisonicsEncodeEffectParams parameters, ref IPLAudioBuffer input, ref IPLAudioBuffer output);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int iplAmbisonicsEncodeEffectGetTailSize(IPLAmbisonicsEncodeEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLAudioEffectState iplAmbisonicsEncodeEffectGetTail(IPLAmbisonicsEncodeEffect effect, ref IPLAudioBuffer output);
    #endregion

    #region Ambisonics Panning Effect
    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLerror iplAmbisonicsPanningEffectCreate(IPLContext context, ref IPLAudioSettings audioSettings, ref IPLAmbisonicsPanningEffectSettings effectSettings, ref IPLAmbisonicsPanningEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLAmbisonicsPanningEffect iplAmbisonicsPanningEffectRetain(IPLAmbisonicsPanningEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplAmbisonicsPanningEffectRelease(ref IPLAmbisonicsPanningEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplAmbisonicsPanningEffectReset(IPLAmbisonicsPanningEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLAudioEffectState iplAmbisonicsPanningEffectApply(IPLAmbisonicsPanningEffect effect, ref IPLAmbisonicsPanningEffectParams parameters, ref IPLAudioBuffer input, ref IPLAudioBuffer output);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int iplAmbisonicsPanningEffectGetTailSize(IPLAmbisonicsPanningEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLAudioEffectState iplAmbisonicsPanningEffectGetTail(IPLAmbisonicsPanningEffect effect, ref IPLAudioBuffer output);
    #endregion

    #region Ambisonics Binaural Effect
    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLerror iplAmbisonicsBinauralEffectCreate(IPLContext context, ref IPLAudioSettings audioSettings, ref IPLAmbisonicsBinauralEffectSettings effectSettings, ref IPLAmbisonicsBinauralEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLAmbisonicsBinauralEffect iplAmbisonicsBinauralEffectRetain(IPLAmbisonicsBinauralEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplAmbisonicsBinauralEffectRelease(ref IPLAmbisonicsBinauralEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplAmbisonicsBinauralEffectReset(IPLAmbisonicsBinauralEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLAudioEffectState iplAmbisonicsBinauralEffectApply(IPLAmbisonicsBinauralEffect effect, ref IPLAmbisonicsBinauralEffectParams parameters, ref IPLAudioBuffer input, ref IPLAudioBuffer output);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int iplAmbisonicsBinauralEffectGetTailSize(IPLAmbisonicsBinauralEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLAudioEffectState iplAmbisonicsBinauralEffectGetTail(IPLAmbisonicsBinauralEffect effect, ref IPLAudioBuffer output);
    #endregion

    #region Ambisonics Rotation Effect
    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLerror iplAmbisonicsRotationEffectCreate(IPLContext context, ref IPLAudioSettings audioSettings, ref IPLAmbisonicsRotationEffectSettings effectSettings, ref IPLAmbisonicsRotationEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLAmbisonicsRotationEffect iplAmbisonicsRotationEffectRetain(IPLAmbisonicsRotationEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplAmbisonicsRotationEffectRelease(ref IPLAmbisonicsRotationEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplAmbisonicsRotationEffectReset(IPLAmbisonicsRotationEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLAudioEffectState iplAmbisonicsRotationEffectApply(IPLAmbisonicsRotationEffect effect, ref IPLAmbisonicsRotationEffectParams parameters, ref IPLAudioBuffer input, ref IPLAudioBuffer output);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int iplAmbisonicsRotationEffectGetTailSize(IPLAmbisonicsRotationEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLAudioEffectState iplAmbisonicsRotationEffectGetTail(IPLAmbisonicsRotationEffect effect, ref IPLAudioBuffer output);
    #endregion

    #region Ambisonics Decode Effect
    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLerror iplAmbisonicsDecodeEffectCreate(IPLContext context, ref IPLAudioSettings audioSettings, ref IPLAmbisonicsDecodeEffectSettings effectSettings, ref IPLAmbisonicsDecodeEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLAmbisonicsDecodeEffect iplAmbisonicsDecodeEffectRetain(IPLAmbisonicsDecodeEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplAmbisonicsDecodeEffectRelease(ref IPLAmbisonicsDecodeEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplAmbisonicsDecodeEffectReset(IPLAmbisonicsDecodeEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLAudioEffectState iplAmbisonicsDecodeEffectApply(IPLAmbisonicsDecodeEffect effect, ref IPLAmbisonicsDecodeEffectParams parameters, ref IPLAudioBuffer input, ref IPLAudioBuffer output);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int iplAmbisonicsDecodeEffectGetTailSize(IPLAmbisonicsDecodeEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLAudioEffectState iplAmbisonicsDecodeEffectGetTail(IPLAmbisonicsDecodeEffect effect, ref IPLAudioBuffer output);
    #endregion

    #region Direct Effect
    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLerror iplDirectEffectCreate(IPLContext context, ref IPLAudioSettings audioSettings, ref IPLDirectEffectSettings effectSettings, ref IPLDirectEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLDirectEffect iplDirectEffectRetain(IPLDirectEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplDirectEffectRelease(ref IPLDirectEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplDirectEffectReset(IPLDirectEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLAudioEffectState iplDirectEffectApply(IPLDirectEffect effect, ref IPLDirectEffectParams parameters, ref IPLAudioBuffer input, ref IPLAudioBuffer output);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int iplDirectEffectGetTailSize(IPLDirectEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLAudioEffectState iplDirectEffectGetTail(IPLDirectEffect effect, ref IPLAudioBuffer output);
    #endregion

    #region Reflection Effect
    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLerror iplReflectionEffectCreate(IPLContext context, ref IPLAudioSettings audioSettings, ref IPLReflectionEffectSettings effectSettings, ref IPLReflectionEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLReflectionEffect iplReflectionEffectRetain(IPLReflectionEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplReflectionEffectRelease(ref IPLReflectionEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplReflectionEffectReset(IPLReflectionEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLAudioEffectState iplReflectionEffectApply(IPLReflectionEffect effect, ref IPLReflectionEffectParams parameters, ref IPLAudioBuffer input, ref IPLAudioBuffer output, IPLReflectionMixer mixer);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int iplReflectionEffectGetTailSize(IPLReflectionEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLAudioEffectState iplReflectionEffectGetTail(IPLReflectionEffect effect, ref IPLAudioBuffer output, IPLReflectionMixer mixer);
    #endregion

    #region Reflection Mixer
    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLerror iplReflectionMixerCreate(IPLContext context, ref IPLAudioSettings audioSettings, ref IPLReflectionEffectSettings effectSettings, ref IPLReflectionMixer mixer);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLReflectionMixer iplReflectionMixerRetain(IPLReflectionMixer mixer);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplReflectionMixerRelease(ref IPLReflectionMixer mixer);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplReflectionMixerReset(IPLReflectionMixer mixer);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLAudioEffectState iplReflectionMixerApply(IPLReflectionMixer mixer, ref IPLReflectionEffectParams parameters, ref IPLAudioBuffer output);
    #endregion

    #region Path Effect
    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLerror iplPathEffectCreate(IPLContext context, ref IPLAudioSettings audioSettings, ref IPLPathEffectSettings effectSettings, ref IPLPathEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLPathEffect iplPathEffectRetain(IPLPathEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplPathEffectRelease(ref IPLPathEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplPathEffectReset(IPLPathEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLAudioEffectState iplPathEffectApply(IPLPathEffect effect, ref IPLPathEffectParams parameters, ref IPLAudioBuffer input, ref IPLAudioBuffer output);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int iplPathEffectGetTailSize(IPLPathEffect effect);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLAudioEffectState iplPathEffectGetTail(IPLPathEffect effect, ref IPLAudioBuffer output);
    #endregion

    #region Probes
    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLerror iplProbeArrayCreate(IPLContext context, ref IPLProbeArray probeArray);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLProbeArray iplProbeArrayRetain(IPLProbeArray probeArray);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplProbeArrayRelease(ref IPLProbeArray probeArray);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplProbeArrayGenerateProbes(IPLProbeArray probeArray, IPLScene scene, ref IPLProbeGenerationParams parameters);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int iplProbeArrayGetNumProbes(IPLProbeArray probeArray);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLSphere iplProbeArrayGetProbe(IPLProbeArray probeArray, int index);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLerror iplProbeBatchCreate(IPLContext context, ref IPLProbeBatch probeBatch);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLProbeBatch iplProbeBatchRetain(IPLProbeBatch probeBatch);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplProbeBatchRelease(ref IPLProbeBatch probeBatch);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLerror iplProbeBatchLoad(IPLContext context, IPLSerializedObject serializedObject, ref IPLProbeBatch probeBatch);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplProbeBatchSave(IPLProbeBatch probeBatch, IPLSerializedObject serializedObject);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int iplProbeBatchGetNumProbes(IPLProbeBatch probeBatch);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplProbeBatchAddProbe(IPLProbeBatch probeBatch, IPLSphere probe);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplProbeBatchAddProbeArray(IPLProbeBatch probeBatch, IPLProbeArray probeArray);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplProbeBatchRemoveProbe(IPLProbeBatch probeBatch, int index);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplProbeBatchCommit(IPLProbeBatch probeBatch);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplProbeBatchRemoveData(IPLProbeBatch probeBatch, ref IPLBakedDataIdentifier identifier);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IntPtr iplProbeBatchGetDataSize(IPLProbeBatch probeBatch, ref IPLBakedDataIdentifier identifier);
    #endregion

    #region Baking
    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplReflectionsBakerBake(IPLContext context, ref IPLReflectionsBakeParams parameters, IPLProgressCallback progressCallback, IntPtr userData);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplReflectionsBakerCancelBake(IPLContext context);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplPathBakerBake(IPLContext context, ref IPLPathBakeParams parameters, IPLProgressCallback progressCallback, IntPtr userData);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplPathBakerCancelBake(IPLContext context);
    #endregion

    #region Simulation
    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLerror iplSimulatorCreate(IPLContext context, ref IPLSimulationSettings settings, ref IPLSimulator simulator);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern IPLSimulator iplSimulatorRetain(IPLSimulator simulator);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplSimulatorRelease(ref IPLSimulator simulator);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplSimulatorSetScene(IPLSimulator simulator, IPLScene scene);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplSimulatorAddProbeBatch(IPLSimulator simulator, IPLProbeBatch probeBatch);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplSimulatorRemoveProbeBatch(IPLSimulator simulator, IPLProbeBatch probeBatch);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplSimulatorSetSharedInputs(IPLSimulator simulator, IPLSimulationFlags flags, ref IPLSimulationSharedInputs sharedInputs);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplSimulatorCommit(IPLSimulator simulator);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplSimulatorRunDirect(IPLSimulator simulator);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplSimulatorRunReflections(IPLSimulator simulator);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplSimulatorRunPathing(IPLSimulator simulator);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplSourceAdd(IPLSource source, IPLSimulator simulator);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplSourceRemove(IPLSource source, IPLSimulator simulator);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplSourceSetInputs(IPLSource source, IPLSimulationFlags flags, ref IPLSimulationInputs inputs);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplSourceGetOutputs(IPLSource source, IPLSimulationFlags flags, ref IPLSimulationOutputs outputs);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern IPLerror iplSourceCreate(IPLSimulator simulator, ref IPLSourceSettings settings, ref IPLSource source);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern IPLSource iplSourceRetain(IPLSource source);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern void iplSourceRelease(ref IPLSource source);
    #endregion

    #region Advanced Simulation
    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern float iplDistanceAttenuationCalculate(IPLContext context, IPLVector3 source, IPLVector3 listener, ref IPLDistanceAttenuationModel model);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void iplAirAbsorptionCalculate(IPLContext context, IPLVector3 source, IPLVector3 listener, ref IPLAirAbsorptionModel model, float[] airAbsorption);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    internal static extern float iplDirectivityCalculate(IPLContext context, IPLCoordinateSpace3 source, IPLVector3 listener, ref IPLDirectivity model);
    #endregion
}