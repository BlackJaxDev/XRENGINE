# Native subsystem type identity audit

The [source inventory snapshot](../../../../Build/_AgentValidation/20260929-160000-native-subsystem-split/reports/native-subsystem-identity.json) records Roslyn syntax-level public declarations, original project and path, and a lexical persisted-name search. This audit pairs those declarations with current leaf source filenames. A namespace plus type name is the CLR identity shown below; `+` denotes a nested type. Moving a declaration between assemblies keeps that name but changes its assembly qualifier. The repository's asset resolver uses full type names, so this source comparison supports compatibility but does not replace deserialization or runtime validation.

The snapshot has limits: it includes files with a lexical subsystem match, does not evaluate conditional compilation or generated declarations, and labels text occurrences as candidates. The lists below preserve every baseline public identity found in a source file moved into a leaf by filename (119 distinct leaf/name pairs). Declarations already in their leaf when the snapshot was captured are listed separately (179 pairs). New contracts are additions rather than moved identities. A source file's matching name alone is not proof that every declaration is compiled on every target.

## Public identities moved after the snapshot

### XREngine.Audio.OpenAL (22)

- `XREngine.Audio.EffectContext`
- `XREngine.Audio.Effects.AudioEffect`
- `XREngine.Audio.Effects.AutowahEffect`
- `XREngine.Audio.Effects.ChorusEffect`
- `XREngine.Audio.Effects.ChorusEffect+EChorusWaveform`
- `XREngine.Audio.Effects.CompressorEffect`
- `XREngine.Audio.Effects.DistortionEffect`
- `XREngine.Audio.Effects.EAXReverbEffect`
- `XREngine.Audio.Effects.EchoEffect`
- `XREngine.Audio.Effects.EqualizerEffect`
- `XREngine.Audio.Effects.FlangerEffect`
- `XREngine.Audio.Effects.FlangerEffect+EFlangerWaveform`
- `XREngine.Audio.Effects.FrequencyShifterEffect`
- `XREngine.Audio.Effects.FrequencyShifterEffect+EDirection`
- `XREngine.Audio.Effects.PitchShifterEffect`
- `XREngine.Audio.Effects.ReverbEffect`
- `XREngine.Audio.Effects.RingModulatorEffect`
- `XREngine.Audio.Effects.RingModulatorEffect+ERingModulatorWaveform`
- `XREngine.Audio.Effects.VocalMorpherEffect`
- `XREngine.Audio.Effects.VocalMorpherEffect+EVocalMorpherWaveform`
- `XREngine.Audio.OpenALEfxProcessor`
- `XREngine.Audio.OpenALTransport`

### XREngine.Runtime.Diagnostics.Desktop (4)

- `XREngine.Data.CudaInterop`
- `XREngine.Data.CudaInterop+CudaEvent`
- `XREngine.Data.CudaInterop+CudaStream`
- `XREngine.Data.CudaInterop+DeviceBuffer`

### XREngine.Runtime.Net.Sockets (9)

- `XREngine.Components.FaceMotion3DCaptureComponent`
- `XREngine.Components.NetworkDiscoveryComponent`
- `XREngine.Components.TcpClientComponent`
- `XREngine.Components.TcpServerClient`
- `XREngine.Components.TcpServerComponent`
- `XREngine.Components.UdpDatagram`
- `XREngine.Components.UdpSocketComponent`
- `XREngine.Networking.RealtimeTlsGateway`
- `XREngine.Networking.UdpSocketOptions`

### XREngine.Runtime.Physics.PhysX (66)

- `XREngine.Components.PhysxHeightFieldComponent`
- `XREngine.Scene.Physics.Physx.ControllerManager`
- `XREngine.Scene.Physics.Physx.ControllerManager+DelFilterControllerCollision`
- `XREngine.Scene.Physics.Physx.ControllerManager+DelGetBehaviorFlagsController`
- `XREngine.Scene.Physics.Physx.ControllerManager+DelGetBehaviorFlagsObstacle`
- `XREngine.Scene.Physics.Physx.ControllerManager+DelGetBehaviorFlagsShape`
- `XREngine.Scene.Physics.Physx.ControllerManager+DelOnControllerHit`
- `XREngine.Scene.Physics.Physx.ControllerManager+DelOnObstacleHit`
- `XREngine.Scene.Physics.Physx.ControllerManager+DelOnShapeHit`
- `XREngine.Scene.Physics.Physx.ControllerManager+DelPostFilterCallback`
- `XREngine.Scene.Physics.Physx.ControllerManager+DelPreFilterCallback`
- `XREngine.Scene.Physics.Physx.ControllerManager+PxControllerFilterCallbackVTable`
- `XREngine.Scene.Physics.Physx.ControllerManager+PxQueryFilterCallbackVTable`
- `XREngine.Scene.Physics.Physx.Joints.PhysxJoint`
- `XREngine.Scene.Physics.Physx.Joints.PhysxJoint_Contact`
- `XREngine.Scene.Physics.Physx.Joints.PhysxJoint_D6`
- `XREngine.Scene.Physics.Physx.Joints.PhysxJoint_Distance`
- `XREngine.Scene.Physics.Physx.Joints.PhysxJoint_Fixed`
- `XREngine.Scene.Physics.Physx.Joints.PhysxJoint_Gear`
- `XREngine.Scene.Physics.Physx.Joints.PhysxJoint_Prismatic`
- `XREngine.Scene.Physics.Physx.Joints.PhysxJoint_RackAndPinion`
- `XREngine.Scene.Physics.Physx.Joints.PhysxJoint_Revolute`
- `XREngine.Scene.Physics.Physx.Joints.PhysxJoint_Spherical`
- `XREngine.Scene.Physics.Physx.Obstacle`
- `XREngine.Scene.Physics.Physx.ObstacleContext`
- `XREngine.Scene.Physics.Physx.PhysxActor`
- `XREngine.Scene.Physics.Physx.PhysxBase`
- `XREngine.Scene.Physics.Physx.PhysxBatchQuery`
- `XREngine.Scene.Physics.Physx.PhysxBoxController`
- `XREngine.Scene.Physics.Physx.PhysxCapsuleController`
- `XREngine.Scene.Physics.Physx.PhysxController`
- `XREngine.Scene.Physics.Physx.PhysxController+ControllerHitDelegate`
- `XREngine.Scene.Physics.Physx.PhysxController+DelGetBehaviorFlagsController2`
- `XREngine.Scene.Physics.Physx.PhysxController+DelGetBehaviorFlagsObstacle2`
- `XREngine.Scene.Physics.Physx.PhysxController+DelGetBehaviorFlagsShape2`
- `XREngine.Scene.Physics.Physx.PhysxController+ObstacleHitDelegate`
- `XREngine.Scene.Physics.Physx.PhysxController+PxControllerBehaviorCallbackVTable`
- `XREngine.Scene.Physics.Physx.PhysxController+PxUserControllerHitReportVTable`
- `XREngine.Scene.Physics.Physx.PhysxController+ShapeHitDelegate`
- `XREngine.Scene.Physics.Physx.PhysxControllerActorProxy`
- `XREngine.Scene.Physics.Physx.PhysxConversions`
- `XREngine.Scene.Physics.Physx.PhysxConvexHullCooker`
- `XREngine.Scene.Physics.Physx.PhysxConvexMesh`
- `XREngine.Scene.Physics.Physx.PhysxConvexMeshGeometryExtension`
- `XREngine.Scene.Physics.Physx.PhysxDynamicRigidBody`
- `XREngine.Scene.Physics.Physx.PhysxHeightField`
- `XREngine.Scene.Physics.Physx.PhysxHeightFieldGeometryExtension`
- `XREngine.Scene.Physics.Physx.PhysxMaterial`
- `XREngine.Scene.Physics.Physx.PhysxParticleSystemGeometryExtension`
- `XREngine.Scene.Physics.Physx.PhysxPhysicsBackendModule`
- `XREngine.Scene.Physics.Physx.PhysxPlane`
- `XREngine.Scene.Physics.Physx.PhysxRefCounted`
- `XREngine.Scene.Physics.Physx.PhysxRigidActor`
- `XREngine.Scene.Physics.Physx.PhysxRigidBody`
- `XREngine.Scene.Physics.Physx.PhysxScene`
- `XREngine.Scene.Physics.Physx.PhysxScene+DelPostFilter`
- `XREngine.Scene.Physics.Physx.PhysxScene+DelPreFilter`
- `XREngine.Scene.Physics.Physx.PhysxScene+FilterShader`
- `XREngine.Scene.Physics.Physx.PhysxScene+Native`
- `XREngine.Scene.Physics.Physx.PhysxScene+PhysxQueryFilter`
- `XREngine.Scene.Physics.Physx.PhysxShape`
- `XREngine.Scene.Physics.Physx.PhysxStaticRigidBody`
- `XREngine.Scene.Physics.Physx.PhysxTetrahedronMesh`
- `XREngine.Scene.Physics.Physx.PhysxTetrahedronMeshGeometryExtension`
- `XREngine.Scene.Physics.Physx.PhysxTriangleMesh`
- `XREngine.Scene.Physics.Physx.PhysxTriangleMeshGeometryExtension`

### XREngine.Runtime.Platform.Desktop (2)

- `XREngine.Rendering.RuntimeWindowPumpHost`
- `XREngine.Rendering.RuntimeWindowPumpHostMode`

### XREngine.Runtime.Rendering.ImGui (2)

- `XREngine.Components.DearImGuiComponent`
- `XREngine.Rendering.UI.ImGuiTextFieldHelper`

### XREngine.Runtime.UI.Rive (1)

- `XREngine.Rendering.UI.RiveUIComponent`

### XREngine.Runtime.UI.Skia (1)

- `XREngine.Rendering.UI.UISvgComponent`

### XREngine.Runtime.UI.Ultralight (1)

- `XREngine.Rendering.UI.UltralightWebRendererBackend`

### XREngine.Runtime.XR.OpenVR (1)

- `XREngine.Core.OpenVRExtensions`

### XREngine.Runtime.XR.OpenXR (10)

- `XREngine.Rendering.API.Rendering.OpenXR.OpenXRAPI`
- `XREngine.Rendering.API.Rendering.OpenXR.OpenXRAPI+DelRenderToFBO`
- `XREngine.Rendering.API.Rendering.OpenXR.OpenXRAPI+ERenderer`
- `XREngine.Rendering.API.Rendering.OpenXR.OpenXRAPI+OpenXrActionSyncPolicy`
- `XREngine.Rendering.API.Rendering.OpenXR.OpenXRAPI+OpenXrCollectVisiblePosePolicy`
- `XREngine.Rendering.API.Rendering.OpenXR.OpenXRAPI+OpenXrPoseTiming`
- `XREngine.Rendering.API.Rendering.OpenXR.OpenXRAPI+OpenXrRenderPacingMode`
- `XREngine.Rendering.API.Rendering.OpenXR.OpenXRAPI+OpenXrRuntimeLossReason`
- `XREngine.Rendering.API.Rendering.OpenXR.OpenXRAPI+OpenXrRuntimeState`
- `XREngine.Rendering.API.Rendering.OpenXR.OpenXRAPI+OpenXrTrackingLossPolicy`

## Public identities already in a leaf at the snapshot

These were in the baseline's leaf project and remain part of the identity surface; they are listed so the audit covers subsystems that were already partly split when the snapshot was taken.

### XREngine.Audio.NAudio (6)

- `XREngine.Audio.NAudioBackend`
- `XREngine.Audio.NAudioCaptureStream`
- `XREngine.Audio.NAudioCodecServices`
- `XREngine.Audio.NAudioFileDecoder`
- `XREngine.Audio.NAudioTransport`
- `XREngine.Audio.XRAudioUtil`

### XREngine.Audio.OpenAL (2)

- `XREngine.Audio.AudioInputDevice`
- `XREngine.Audio.AudioInputDeviceFloat`

### XREngine.Audio.SteamAudio (131)

- `XREngine.Audio.Steam.IPLAirAbsorptionCallback`
- `XREngine.Audio.Steam.IPLAirAbsorptionModel`
- `XREngine.Audio.Steam.IPLAirAbsorptionModelType`
- `XREngine.Audio.Steam.IPLAllocateFunction`
- `XREngine.Audio.Steam.IPLAmbisonicsBinauralEffect`
- `XREngine.Audio.Steam.IPLAmbisonicsBinauralEffectParams`
- `XREngine.Audio.Steam.IPLAmbisonicsBinauralEffectSettings`
- `XREngine.Audio.Steam.IPLAmbisonicsDecodeEffect`
- `XREngine.Audio.Steam.IPLAmbisonicsDecodeEffectParams`
- `XREngine.Audio.Steam.IPLAmbisonicsDecodeEffectSettings`
- `XREngine.Audio.Steam.IPLAmbisonicsEncodeEffect`
- `XREngine.Audio.Steam.IPLAmbisonicsEncodeEffectParams`
- `XREngine.Audio.Steam.IPLAmbisonicsEncodeEffectSettings`
- `XREngine.Audio.Steam.IPLAmbisonicsPanningEffect`
- `XREngine.Audio.Steam.IPLAmbisonicsPanningEffectParams`
- `XREngine.Audio.Steam.IPLAmbisonicsPanningEffectSettings`
- `XREngine.Audio.Steam.IPLAmbisonicsRotationEffect`
- `XREngine.Audio.Steam.IPLAmbisonicsRotationEffectParams`
- `XREngine.Audio.Steam.IPLAmbisonicsRotationEffectSettings`
- `XREngine.Audio.Steam.IPLAmbisonicsType`
- `XREngine.Audio.Steam.IPLAnyHitCallback`
- `XREngine.Audio.Steam.IPLAudioBuffer`
- `XREngine.Audio.Steam.IPLAudioEffectState`
- `XREngine.Audio.Steam.IPLAudioSettings`
- `XREngine.Audio.Steam.IPLBakedDataIdentifier`
- `XREngine.Audio.Steam.IPLBakedDataType`
- `XREngine.Audio.Steam.IPLBakedDataVariation`
- `XREngine.Audio.Steam.IPLBatchedAnyHitCallback`
- `XREngine.Audio.Steam.IPLBatchedClosestHitCallback`
- `XREngine.Audio.Steam.IPLBinauralEffect`
- `XREngine.Audio.Steam.IPLBinauralEffectParams`
- `XREngine.Audio.Steam.IPLBinauralEffectSettings`
- `XREngine.Audio.Steam.IPLBox`
- `XREngine.Audio.Steam.IPLClosestHitCallback`
- `XREngine.Audio.Steam.IPLContext`
- `XREngine.Audio.Steam.IPLContextFlags`
- `XREngine.Audio.Steam.IPLContextSettings`
- `XREngine.Audio.Steam.IPLCoordinateSpace3`
- `XREngine.Audio.Steam.IPLDirectEffect`
- `XREngine.Audio.Steam.IPLDirectEffectFlags`
- `XREngine.Audio.Steam.IPLDirectEffectParams`
- `XREngine.Audio.Steam.IPLDirectEffectSettings`
- `XREngine.Audio.Steam.IPLDirectivity`
- `XREngine.Audio.Steam.IPLDirectivityCallback`
- `XREngine.Audio.Steam.IPLDirectSimulationFlags`
- `XREngine.Audio.Steam.IPLDistanceAttenuationCallback`
- `XREngine.Audio.Steam.IPLDistanceAttenuationModel`
- `XREngine.Audio.Steam.IPLDistanceAttenuationModelType`
- `XREngine.Audio.Steam.IPLEmbreeDevice`
- `XREngine.Audio.Steam.IPLFreeFunction`
- `XREngine.Audio.Steam.IPLHit`
- `XREngine.Audio.Steam.IPLHRTF`
- `XREngine.Audio.Steam.IPLHRTFInterpolation`
- `XREngine.Audio.Steam.IPLHRTFNormType`
- `XREngine.Audio.Steam.IPLHRTFSettings`
- `XREngine.Audio.Steam.IPLHRTFType`
- `XREngine.Audio.Steam.IPLInstancedMesh`
- `XREngine.Audio.Steam.IPLInstancedMeshSettings`
- `XREngine.Audio.Steam.IPLLogFunction`
- `XREngine.Audio.Steam.IPLLogLevel`
- `XREngine.Audio.Steam.IPLMaterial`
- `XREngine.Audio.Steam.IPLMatrix4x4`
- `XREngine.Audio.Steam.IPLOcclusionType`
- `XREngine.Audio.Steam.IPLOpenCLDevice`
- `XREngine.Audio.Steam.IPLOpenCLDeviceDesc`
- `XREngine.Audio.Steam.IPLOpenCLDeviceList`
- `XREngine.Audio.Steam.IPLOpenCLDeviceSettings`
- `XREngine.Audio.Steam.IPLOpenCLDeviceType`
- `XREngine.Audio.Steam.IPLPanningEffect`
- `XREngine.Audio.Steam.IPLPanningEffectParams`
- `XREngine.Audio.Steam.IPLPanningEffectSettings`
- `XREngine.Audio.Steam.IPLPathBakeParams`
- `XREngine.Audio.Steam.IPLPathEffect`
- `XREngine.Audio.Steam.IPLPathEffectParams`
- `XREngine.Audio.Steam.IPLPathEffectSettings`
- `XREngine.Audio.Steam.IPLPathingVisualizationCallback`
- `XREngine.Audio.Steam.IPLProbeArray`
- `XREngine.Audio.Steam.IPLProbeBatch`
- `XREngine.Audio.Steam.IPLProbeGenerationParams`
- `XREngine.Audio.Steam.IPLProbeGenerationType`
- `XREngine.Audio.Steam.IPLProgressCallback`
- `XREngine.Audio.Steam.IPLRadeonRaysDevice`
- `XREngine.Audio.Steam.IPLRay`
- `XREngine.Audio.Steam.IPLReflectionEffect`
- `XREngine.Audio.Steam.IPLReflectionEffectIR`
- `XREngine.Audio.Steam.IPLReflectionEffectParams`
- `XREngine.Audio.Steam.IPLReflectionEffectSettings`
- `XREngine.Audio.Steam.IPLReflectionEffectType`
- `XREngine.Audio.Steam.IPLReflectionMixer`
- `XREngine.Audio.Steam.IPLReflectionsBakeFlags`
- `XREngine.Audio.Steam.IPLReflectionsBakeParams`
- `XREngine.Audio.Steam.IPLScene`
- `XREngine.Audio.Steam.IPLSceneSettings`
- `XREngine.Audio.Steam.IPLSceneType`
- `XREngine.Audio.Steam.IPLSerializedObject`
- `XREngine.Audio.Steam.IPLSerializedObjectSettings`
- `XREngine.Audio.Steam.IPLSIMDLevel`
- `XREngine.Audio.Steam.IPLSimulationFlags`
- `XREngine.Audio.Steam.IPLSimulationInputs`
- `XREngine.Audio.Steam.IPLSimulationOutputs`
- `XREngine.Audio.Steam.IPLSimulationSettings`
- `XREngine.Audio.Steam.IPLSimulationSharedInputs`
- `XREngine.Audio.Steam.IPLSimulator`
- `XREngine.Audio.Steam.IPLSource`
- `XREngine.Audio.Steam.IPLSourceSettings`
- `XREngine.Audio.Steam.IPLSpeakerLayout`
- `XREngine.Audio.Steam.IPLSpeakerLayoutType`
- `XREngine.Audio.Steam.IPLSphere`
- `XREngine.Audio.Steam.IPLStaticMesh`
- `XREngine.Audio.Steam.IPLStaticMeshSettings`
- `XREngine.Audio.Steam.IPLTransmissionType`
- `XREngine.Audio.Steam.IPLTriangle`
- `XREngine.Audio.Steam.IPLTrueAudioNextDevice`
- `XREngine.Audio.Steam.IPLTrueAudioNextDeviceSettings`
- `XREngine.Audio.Steam.IPLVector3`
- `XREngine.Audio.Steam.IPLVirtualSurroundEffect`
- `XREngine.Audio.Steam.IPLVirtualSurroundEffectParams`
- `XREngine.Audio.Steam.IPLVirtualSurroundEffectSettings`
- `XREngine.Audio.Steam.PathBakeSettings`
- `XREngine.Audio.Steam.Phonon`
- `XREngine.Audio.Steam.ReflectionsBakeSettings`
- `XREngine.Audio.Steam.SteamAudioBaker`
- `XREngine.Audio.Steam.SteamAudioMaterial`
- `XREngine.Audio.Steam.SteamAudioProbeBatch`
- `XREngine.Audio.Steam.SteamAudioProcessor`
- `XREngine.Audio.Steam.SteamAudioScene`
- `XREngine.Audio.SteamAudioBackend`
- `XREngine.Components.SteamAudioGeometryComponent`
- `XREngine.Components.SteamAudioProbeComponent`
- `XREngine.Components.SteamAudioProbeComponent+EBakeStatus`
- `XREngine.Components.SteamAudioProbeComponent+EProbeGenerationMode`

### XREngine.Input.Silk (3)

- `XREngine.Input.Devices.Glfw.GlfwGamepad`
- `XREngine.Input.Devices.Glfw.GlfwKeyboard`
- `XREngine.Input.Devices.Glfw.GlfwMouse`

### XREngine.Input.XInput (4)

- `XREngine.Input.Devices.DirectX.DXGamepad`
- `XREngine.Input.Devices.DirectX.DXGamepadAwaiter`
- `XREngine.Input.Devices.DirectX.DXGamepadConfiguration`
- `XREngine.Input.XInputBackend`

### XREngine.Runtime.Physics.Jitter (3)

- `XREngine.Scene.Physics.Jitter2.JitterExtensions`
- `XREngine.Scene.Physics.Jitter2.JitterPhysicsBackendModule`
- `XREngine.Scene.Physics.Jitter2.JitterScene`

### XREngine.Runtime.Physics.Jolt (10)

- `XREngine.Scene.LayerMaskJoltExtensions`
- `XREngine.Scene.Physics.Jolt.JoltActor`
- `XREngine.Scene.Physics.Jolt.JoltCharacterVirtualController`
- `XREngine.Scene.Physics.Jolt.JoltDebugRenderSnapshot`
- `XREngine.Scene.Physics.Jolt.JoltDynamicRigidBody`
- `XREngine.Scene.Physics.Jolt.JoltPhysicsBackendModule`
- `XREngine.Scene.Physics.Jolt.JoltPhysicsDiagnostics`
- `XREngine.Scene.Physics.Jolt.JoltRigidActor`
- `XREngine.Scene.Physics.Jolt.JoltScene`
- `XREngine.Scene.Physics.Jolt.JoltStaticRigidBody`

### XREngine.Runtime.Rendering.OpenGL (15)

- `XREngine.Rendering.OpenGL.OpenGLRenderer`
- `XREngine.Rendering.OpenGL.OpenGLRenderer+DelImageCallback`
- `XREngine.Rendering.OpenGL.OpenGLRenderer+GLProgramCompileLinkQueue`
- `XREngine.Rendering.OpenGL.OpenGLRenderer+GLProgramCompileLinkQueue+CompileResult`
- `XREngine.Rendering.OpenGL.OpenGLRenderer+GLProgramCompileLinkQueue+CompileStatus`
- `XREngine.Rendering.OpenGL.OpenGLRenderer+GLProgramCompileLinkQueue+ProgramBinarySnapshot`
- `XREngine.Rendering.OpenGL.OpenGLRenderer+GLProgramCompileLinkQueue+ShaderInput`
- `XREngine.Rendering.OpenGL.OpenGLRenderer+GLProgramCompileLinkQueue+TransformFeedbackLinkInfo`
- `XREngine.Rendering.OpenGL.OpenGLRenderer+GLSharedContext`
- `XREngine.Rendering.OpenGL.OpenGlRendererBackendModule`
- `XREngine.Rendering.OpenGL.OpenGlRendererBackendModuleEntry`
- `XREngine.Rendering.UI.Ultralight.OpenGLGPUDriver`
- `XREngine.Rendering.UI.Ultralight.OpenGLGPUDriver+RenderBufferEntry`
- `XREngine.Rendering.UI.Ultralight.OpenGLGPUDriver+TextureEntry`
- `XREngine.Rendering.UI.UltralightGpuWebRendererBackend`

### XREngine.Runtime.Rendering.Vulkan (5)

- `XREngine.Rendering.Vulkan.VulkanCommandRuntime+CommandScope`
- `XREngine.Rendering.Vulkan.VulkanRenderer`
- `XREngine.Rendering.Vulkan.VulkanRendererBackendModule`
- `XREngine.Rendering.Vulkan.VulkanRendererBackendModuleEntry`
- `XREngine.Rendering.VulkanRendererBackendFactory`

+## Declarations outside the lexical snapshot

A source-level pass through the new native leaves found the following additional public declarations absent from the snapshot. This section includes both pre-existing declarations moved from unclassified files and newly introduced backend adapters; absence from the snapshot alone cannot distinguish them. The preserved OVR lip-sync, Rive input, OSC component, and native platform names are therefore explicit here. The main OpenGL and Vulkan renderer leaves predate this split and are outside this supplementary pass. Nested names retain the CLR `+` separator.

### XREngine.Audio.Audio2Face (8)

- `XREngine.Components.Audio2Face3DNativeBackend`
- `XREngine.Components.Audio2Face3DNativeBridge`
- `XREngine.Components.Audio2Face3DNativeBridgeAudioConverter`
- `XREngine.Components.Audio2Face3DNativeBridgeComponent`
- `XREngine.Components.Audio2Face3DNativeBridgeSession`
- `XREngine.Components.Audio2Face3DNativeBridgeSessionConfig`
- `XREngine.Components.EAudio2Face3DNativePollResult`
- `XREngine.Components.EAudio2XBridgeResult`

### XREngine.Audio.OVRLipSync (12)

- `XREngine.Components.OVRLipSync`
- `XREngine.Components.OVRLipSync+ovrLipSyncAudioDataType`
- `XREngine.Components.OVRLipSync+ovrLipSyncCallback`
- `XREngine.Components.OVRLipSync+ovrLipSyncContext`
- `XREngine.Components.OVRLipSync+ovrLipSyncContextProvider`
- `XREngine.Components.OVRLipSync+ovrLipSyncFrame`
- `XREngine.Components.OVRLipSync+ovrLipSyncLaughterCategory`
- `XREngine.Components.OVRLipSync+ovrLipSyncResult`
- `XREngine.Components.OVRLipSync+ovrLipSyncSignals`
- `XREngine.Components.OVRLipSync+ovrLipSyncViseme`
- `XREngine.Components.OVRLipSync+Viseme`
- `XREngine.Components.OVRLipSyncBackend`

### XREngine.Runtime.Diagnostics.Desktop (3)

- `XREngine.Data.NvCompHardwareCodec`
- `XREngine.Rendering.DesktopDiagnosticsBackend`
- `XREngine.Rendering.WindowsHardwareInventory`

### XREngine.Runtime.Imaging.Magick (3)

- `XREngine.Runtime.Imaging.Magick.MagickImagingBackend`
- `XREngine.Runtime.Imaging.Magick.MagickPhysicsHeightFieldImageSource`
- `XREngine.Runtime.Imaging.Magick.MagickRuntimeImageCodec`

### XREngine.Runtime.IO.DirectStorage (4)

- `XREngine.Core.Files.DirectStorageAssetSource`
- `XREngine.Core.Files.DirectStorageBackend`
- `XREngine.Core.Files.GDeflateCompressionCodec`
- `XREngine.Core.Files.NativeDirectStorageIO+ReadBatch`

### XREngine.Runtime.Media.FFmpeg (1)

- `XREngine.Runtime.Media.FFmpeg.FfmpegMediaBackend`

### XREngine.Runtime.MeshProcessing.Meshoptimizer (1)

- `XREngine.Rendering.Meshlets.MeshOptimizerBackend`

### XREngine.Runtime.Net.Osc (10)

- `XREngine.Components.VMCCaptureComponent`
- `XREngine.Components.VMCCaptureComponent+DelDeviceTransformRecieved`
- `XREngine.Components.VMCCaptureComponent+DelMidiCCButtonRecieved`
- `XREngine.Components.VMCCaptureComponent+DelMidiCCRadialRecieved`
- `XREngine.Components.VMCCaptureComponent+DelMidiNoteRecieved`
- `XREngine.Components.VMCSenderComponent`
- `XREngine.Data.Components.FaceTrackingReceiverComponent`
- `XREngine.Data.Components.OscReceiverComponent`
- `XREngine.Data.Components.OscSenderComponent`
- `XREngine.Networking.OscNetworkBackend`

### XREngine.Runtime.Net.Sockets (2)

- `XREngine.Networking.NativeRealtimeTlsClientTunnel`
- `XREngine.Networking.SocketNetworkBackend`

### XREngine.Runtime.Physics.Authoring (2)

- `XREngine.Data.Tools.CoAcdNativeBackend`
- `XREngine.Runtime.Physics.Authoring.CoAcdPhysicsColliderAuthoringService`

### XREngine.Runtime.Physics.Jolt (1)

- `XREngine.Scene.Physics.Jolt.IJoltCharacterController`

### XREngine.Runtime.Physics.PhysX (2)

- `XREngine.Scene.Physics.Physx.Joints.IHingeJoint`
- `XREngine.Scene.Physics.Physx.Joints.IPrismaticJoint`

### XREngine.Runtime.Platform.Desktop (35)

- `System.CFileMap`
- `System.Linux`
- `System.Linux+MMapFlags`
- `System.Linux+MMapProtect`
- `System.MMapFlags`
- `System.MMapProtect`
- `System.WFileMap`
- `XREngine.CFileMap`
- `XREngine.Components.Scripting.GameCSProjLoader`
- `XREngine.Components.Scripting.GameCSProjLoader+AssemblyData`
- `XREngine.Components.Scripting.GameCSProjLoader+DynamicEngineAssemblyLoadContext`
- `XREngine.Data.WFileMap`
- `XREngine.Data.Win32`
- `XREngine.Data.Win32+FileMapAccess`
- `XREngine.Data.Win32+FileMapProtect`
- `XREngine.Data.Win32+SafeHandle`
- `XREngine.Native.APPBARDATA`
- `XREngine.Native.EAnimateWindowFlags`
- `XREngine.Native.EGetWindow_Cmd`
- `XREngine.Native.EHitTestValues`
- `XREngine.Native.EShowWindowEnum`
- `XREngine.Native.ESystemCommands`
- `XREngine.Native.EVirtualKey`
- `XREngine.Native.EWindowsMessage`
- `XREngine.Native.EWindowStyle`
- `XREngine.Native.NativeMessage`
- `XREngine.Native.NativeMethods`
- `XREngine.Native.NativeMethods+OSX`
- `XREngine.Native.NativeMethods+UNIX`
- `XREngine.Native.POINT`
- `XREngine.Native.RECT`
- `XREngine.Native.SHFILEINFO`
- `XREngine.Native.WINDOWPOS`
- `XREngine.Runtime.Platform.Desktop.DesktopPlatformBackend`
- `XREngine.Runtime.Platform.Desktop.Windowing.DesktopSilkWindowBackendFactory`

### XREngine.Runtime.Rendering.ImGui (1)

- `XREngine.Rendering.ImGuiBackend`

### XREngine.Runtime.Rendering.WebGPU (3)

- `XREngine.Rendering.WebGPU.WebGpuRendererBackendFactory`
- `XREngine.Rendering.WebGPU.WebGpuRendererBackendModule`
- `XREngine.Rendering.WebGPU.WebGpuRendererHost`

### XREngine.Runtime.Text.FreeType (3)

- `XREngine.Runtime.Text.FreeType.FreeTypeFontBackend`
- `XREngine.Runtime.Text.FreeType.FreeTypeFontCharacterEnumerator`
- `XREngine.Runtime.Text.FreeType.MsdfAtlasGenFontAtlasGenerator`

### XREngine.Runtime.UI.Rive (4)

- `XREngine.Rendering.UI.BoolInput`
- `XREngine.Rendering.UI.NumberInput`
- `XREngine.Rendering.UI.StateMachineInput`
- `XREngine.Rendering.UI.TriggerInput`

### XREngine.Runtime.UI.Skia (2)

- `XREngine.Runtime.UI.Skia.SkiaFontBackend`
- `XREngine.Runtime.UI.Skia.SkiaFontBitmapRasterizer`

### XREngine.Runtime.UI.Ultralight (1)

- `XREngine.Runtime.UI.Ultralight.UltralightUiBackend`

### XREngine.Runtime.XR.OpenVR (6)

- `XREngine.OpenVrActionBackend`
- `XREngine.OpenVrDeviceBackend`
- `XREngine.OpenVrModelCatalog`
- `XREngine.OpenVrModelLoader`
- `XREngine.OpenVrRuntimeBackend`
- `XREngine.RuntimeOpenVrApi`

### XREngine.Runtime.XR.OpenXR (1)

- `XREngine.OpenXrRuntimeBackend`

## Persisted-name findings

The snapshot marks no moved type as a persisted full-name hit. Two moved nested names were lexical simple-name candidates: `XREngine.Scene.Physics.Physx.PhysxScene+Native` and `XREngine.Rendering.API.Rendering.OpenXR.OpenXRAPI+OpenXrRenderPacingMode`. A search of `Assets/`, `Samples/`, and `XREngine.UnitTests/` for these full names in `*.asset`, `*.jsonc`, JSON, YAML, and C# test data found no stored instance. The simple word `Native` is especially non-specific.

Real serialized examples in the searched data use identities that remain in lower projects: `Samples/MonkeyBallVR/Assets/Worlds/MonkeyBallWorld.asset` has `__type: XREngine.Components.Physics.DynamicRigidBodyComponent` at lines 144 and 646, and `__type: XREngine.Data.Components.Scene.VRTrackerCollectionComponent` at line 94. `Assets/c57065a6-e460-4623-a612-9f8aa1076abb.asset` begins with `__assetType: XREngine.Rendering.XRTexture2D`. These are actual asset type tags, unlike test-source text. The loader reads those two tag names in `XREngine.Runtime.Core/Assets/Loading/AssetManager.Loading.SerializationAndCache.cs`, rewrites aliases with `XRTypeRedirectRegistry`, and resolves names through `AotRuntimeMetadataStore` before its `Type.GetType` fallback. `XREngine.Runtime.Rendering/Resources/Fonts/FontGlyphSet.cs` retains `[MemoryPackable]`, the nested `Glyph` serialization declaration, and `[XRTypeRedirect("XREngine.Rendering.FontGlyphSet")]`; the font type did not move. The snapshot's FontGlyphSet full-name occurrence did not correspond to a currently found stored asset instance. The moved Rive and SVG components retain `XRTypeRedirect` aliases in their leaf sources. `XREngine.UnitTests/Rendering/RuntimeModularizationPhase4SerializationCompatibilityTests.cs` contains old assembly-qualified Rive/SVG names as resolver test inputs, not asset instances.

`MenuOption.HotKeys` in `XREngine.Runtime.Rendering/Scene/Components/UI/Interactable/MenuOption.cs` changed its element type from Silk `Key` to engine `EKey`; no `HotKeys` field or property instance was found in the searched asset, sample, or test-data formats. The new neutral registries, window contracts, and backend module contracts have new names and no prior serialized identity. The nested OpenXR public enum/delegate names in the list above, including `OpenXRAPI+ERenderer` and `OpenXRAPI+DelRenderToFBO`, remain nested under their original containing type.

No asset rewrite is indicated by the observed full-name search. Binary or assembly-qualified serializer behavior and the source inventory's lexical coverage still require the separately scheduled validation before compatibility can be accepted.

## Shared engine host identities

The compiled Bootstrap baseline contained 152 public CLR names. Metadata-only inspection after extracting `XREngine.Runtime.Host` finds all 152: 117 in Host and 35 still in desktop Bootstrap, with no duplicates or missing names. Host adds `XREngine.IRuntimeEngineStartupPolicy` and `XREngine.RuntimeEngineStartupPolicyServices`. Both assemblies were inspected without loading their native dependencies. This establishes the compiled names, not binary compatibility or browser execution.

The following names change their assembly qualifier from `XREngine.Runtime.Bootstrap` to `XREngine.Runtime.Host`; their namespaces and type names are preserved. `SnapshotAssetReference` now resolves saved assembly-qualified names through `AotRuntimeMetadataStore.ResolveType`, which supports full-name metadata lookup across assembly movement. Published cooked loading similarly resolves registered types by full name. Published compatibility depends on including the current registrations and cooked type metadata; live asset and lifecycle qualification remains recorded in the [host ownership record](portable-engine-host-ownership.md).
- `XREngine.BootstrapAssetSerializationRegistration`
- `XREngine.BootstrapPublishedCookedAssetRegistration`
- `XREngine.EDebugPrimitiveBufferFormat`
- `XREngine.EDebugShapePopulationMode`
- `XREngine.EditorCullingDiagnosticsPreferenceOverrides`
- `XREngine.EditorCullingDiagnosticsPreferences`
- `XREngine.EditorDebugOptions`
- `XREngine.EditorDebugOverrides`
- `XREngine.EditorDiagnosticsPreferenceOverrides`
- `XREngine.EditorDiagnosticsPreferences`
- `XREngine.EditorExceptionDiagnosticsPreferenceOverrides`
- `XREngine.EditorExceptionDiagnosticsPreferences`
- `XREngine.EditorGeneralDiagnosticsPreferenceOverrides`
- `XREngine.EditorGeneralDiagnosticsPreferences`
- `XREngine.EditorOpenGLDiagnosticsPreferenceOverrides`
- `XREngine.EditorOpenGLDiagnosticsPreferences`
- `XREngine.EditorPreferences`
- `XREngine.EditorPreferences+ESceneDepthModePreference`
- `XREngine.EditorPreferences+EViewportPresentationMode`
- `XREngine.EditorPreferencesOverrides`
- `XREngine.EditorProfilerPreferenceOverrides`
- `XREngine.EditorProfilerPreferences`
- `XREngine.EditorRenderPipelineDiagnosticsPreferenceOverrides`
- `XREngine.EditorRenderPipelineDiagnosticsPreferences`
- `XREngine.EditorRuntimeEnvironmentPreferences`
- `XREngine.EditorSelectionPreferenceOverrides`
- `XREngine.EditorSelectionPreferences`
- `XREngine.EditorThemeOverrides`
- `XREngine.EditorThemeSettings`
- `XREngine.EditorViewportPreferenceOverrides`
- `XREngine.EditorViewportPreferences`
- `XREngine.EditorVisualizationDiagnosticsPreferenceOverrides`
- `XREngine.EditorVisualizationDiagnosticsPreferences`
- `XREngine.EditorVulkanDiagnosticsPreferenceOverrides`
- `XREngine.EditorVulkanDiagnosticsPreferences`
- `XREngine.Engine`
- `XREngine.Engine+AllocationRingSnapshot`
- `XREngine.Engine+AllocationScope`
- `XREngine.Engine+AllocationScopeSnapshot`
- `XREngine.Engine+CodeProfiler`
- `XREngine.Engine+CodeProfiler+CodeProfilerTimer`
- `XREngine.Engine+CodeProfiler+DelTimerCallback`
- `XREngine.Engine+CodeProfiler+LinkedScopeContext`
- `XREngine.Engine+CodeProfiler+ProfilerComponentFrameSnapshot`
- `XREngine.Engine+CodeProfiler+ProfilerComponentTimingSnapshot`
- `XREngine.Engine+CodeProfiler+ProfilerFrameSnapshot`
- `XREngine.Engine+CodeProfiler+ProfilerNodeSnapshot`
- `XREngine.Engine+CodeProfiler+ProfilerScope`
- `XREngine.Engine+CodeProfiler+ProfilerThreadSnapshot`
- `XREngine.Engine+DelBeginOperation`
- `XREngine.Engine+DelEndOperation`
- `XREngine.Engine+EffectiveSettings`
- `XREngine.Engine+EffectiveSettings+SettingSource`
- `XREngine.Engine+Input`
- `XREngine.Engine+MainThreadInvokeEntry`
- `XREngine.Engine+MainThreadInvokeMode`
- `XREngine.Engine+Physics`
- `XREngine.Engine+PlayMode`
- `XREngine.Engine+State`
- `XREngine.Engine+ThreadAllocationSnapshot`
- `XREngine.Engine+ThreadAllocationTracker`
- `XREngine.Engine+TickList`
- `XREngine.Engine+Time`
- `XREngine.Engine+WindowCloseRequestResult`
- `XREngine.EngineRenderingSettingsApplication`
- `XREngine.GameModeCompositionBootstrap`
- `XREngine.GameStartupSettings`
- `XREngine.GameStartupSettings+EMaxMirrorRecursionCount`
- `XREngine.GameState`
- `XREngine.GameWindowStartupSettings`
- `XREngine.IGameLaunchBootstrap`
- `XREngine.IGameLaunchRuntimeSmokeBootstrap`
- `XREngine.RealtimeJoinHandoff`
- `XREngine.Runtime.Bootstrap.CameraUIDrawMode`
- `XREngine.Runtime.Bootstrap.EngineRuntimeWorldHostServices`
- `XREngine.Runtime.Bootstrap.IRuntimeWorldHostCompositionServices`
- `XREngine.Runtime.Bootstrap.LightProbeCaptureMode`
- `XREngine.Runtime.Bootstrap.LightProbeMode`
- `XREngine.Runtime.Bootstrap.MeshSubmissionStrategyJsonConverter`
- `XREngine.Runtime.Bootstrap.ModelImportBackendPreference`
- `XREngine.Runtime.Bootstrap.ModelImportMaterialMode`
- `XREngine.Runtime.Bootstrap.ModelPostImportFlags`
- `XREngine.Runtime.Bootstrap.ModelPostImportFlagsJsonConverter`
- `XREngine.Runtime.Bootstrap.RuntimeAdapterBootstrap`
- `XREngine.Runtime.Bootstrap.RuntimeAdapterProfile`
- `XREngine.Runtime.Bootstrap.RuntimeApplicationProfile`
- `XREngine.Runtime.Bootstrap.RuntimeAssetBootstrap`
- `XREngine.Runtime.Bootstrap.RuntimeWorldHost`
- `XREngine.Runtime.Bootstrap.RuntimeWorldHostCompositionServices`
- `XREngine.Runtime.Bootstrap.UnitTestEditorType`
- `XREngine.Runtime.Bootstrap.UnitTestFbxLogVerbosity`
- `XREngine.Runtime.Bootstrap.UnitTestingOpenGLRenderSettings`
- `XREngine.Runtime.Bootstrap.UnitTestingOpenGLShaderLinkingSettings`
- `XREngine.Runtime.Bootstrap.UnitTestingOpenXrEyeResolutionSettings`
- `XREngine.Runtime.Bootstrap.UnitTestingRenderPipeline`
- `XREngine.Runtime.Bootstrap.UnitTestingRenderSettings`
- `XREngine.Runtime.Bootstrap.UnitTestingVrFoveationSettings`
- `XREngine.Runtime.Bootstrap.UnitTestingVrLaunchMode`
- `XREngine.Runtime.Bootstrap.UnitTestingVrSettings`
- `XREngine.Runtime.Bootstrap.UnitTestingVulkanRenderSettings`
- `XREngine.Runtime.Bootstrap.UnitTestingWorldSettings`
- `XREngine.Runtime.Bootstrap.UnitTestingWorldSettings+AtmosphericScatteringInitSettings`
- `XREngine.Runtime.Bootstrap.UnitTestingWorldSettings+ColorRgb`
- `XREngine.Runtime.Bootstrap.UnitTestingWorldSettings+ModelImportSettings`
- `XREngine.Runtime.Bootstrap.UnitTestingWorldSettings+ProbeGridCounts`
- `XREngine.Runtime.Bootstrap.UnitTestingWorldSettings+TranslationXYZ`
- `XREngine.Runtime.Bootstrap.UnitTestingWorldSettings+VolumetricFogVolumeInitSettings`
- `XREngine.Runtime.Bootstrap.UnitTestingWorldSettings+YawPitchRollDegrees`
- `XREngine.Runtime.Bootstrap.UnitTestModelImportKind`
- `XREngine.Runtime.Bootstrap.UnitTestWorldKind`
- `XREngine.Timers.EngineTimer`
- `XREngine.Timers.EngineTimer+DeltaManager`
- `XREngine.Timers.EngineTimer+ExplicitFrameScope`
- `XREngine.Timers.EngineTimerTerminalFault`
- `XREngine.VRGameStartupSettings`2`
- `XREngine.WorldStateSnapshot`
- `XREngine.XREngineVrRuntimeJsonContext`

