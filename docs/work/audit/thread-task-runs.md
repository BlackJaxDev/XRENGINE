# Thread and task run report

Generated: 2026-02-26 12:20:14

Scan roots:
- XRENGINE
- XREngine.Animation
- XREngine.Audio
- XREngine.Data
- XREngine.Editor
- XREngine.Extensions
- XREngine.Input
- XREngine.Modeling
- XREngine.Profiler
- XREngine.Profiler.UI
- XREngine.Server
- XREngine.UnitTests
- XREngine.VRClient

Search categories:
- Thread Creations: `new Thread(...)`
- Thread Starts: `<thread>.Start(...)` and `new Thread(...).Start(...)`
- Thread Stops: `<thread>.Join(...)`, `<thread>.Interrupt(...)`, `<thread>.Abort(...)`
- Task Runs: `Task.Run(...)`, `Task.Factory.StartNew(...)`, `<task>.Start(...)`, `new Task(...).Start(...)`

Excluded paths (regex):
- \\Submodules\\|\\Build\\Submodules\\|\\ThirdParty\\|\\bin\\|\\obj\\|\\docs\\docfx\\|\\docs\\api\\|\\docs\\licenses\\|\\docs\\work\\

Notes:
- Comment-only lines (//, ///, /*, *, */) are skipped to reduce false positives.
- Matches inside string literals are skipped to reduce false positives.


## XREngine.Data/ArchiveExtractor.cs
- [Task Runs] L58 C16: Task.Run( :: => Task.Run(() => Extract(packagePath, destinationFolderPath, overwrite, progress, cancellationToken), cancellationToken);


## XREngine.Data/Core/Assets/XRAsset.cs
- [Task Runs] L307 C22: Task.Run( :: => await Task.Run(() => Load3rdParty(filePath));
- [Task Runs] L338 C22: Task.Run( :: => await Task.Run(() => Import3rdParty(filePath, importOptions));
- [Task Runs] L399 C22: Task.Run( :: => await Task.Run(() => Reload(path));


## XREngine.Data/Core/Events/XRBoolEvent.cs
- [Task Runs] L48 C71: Task.Run( :: var results = await Task.WhenAll(snapshot.Select(a => Task.Run(() => a.Invoke(item))));
- [Task Runs] L123 C50: Task.Run( :: var tasks = snapshot.Select(a => Task.Run(() => a.Invoke(item))).ToList();


## XREngine.Data/Core/Events/XREvent.cs
- [Task Runs] L140 C61: Task.Run( :: await Task.WhenAll(snapshot.Select(a => Task.Run(() => a.Invoke(item))));


## XREngine.Data/Profiling/UdpProfilerSender.cs
- [Thread Creations] L76 C29: new Thread( :: _senderThread = new Thread(() => SenderLoop(port, _cts.Token))
- [Thread Starts] L83 C13: _senderThread.Start( :: _senderThread.Start();


## XREngine.Data/Tools/CoACD.cs
- [Task Runs] L19 C16: Task.Run( :: => Task.Run(() => Calculate(positions, triangleIndices, parameters), cancellationToken);


## XREngine.Editor/Mcp/McpServerHost.cs
- [Task Runs] L168 C29: Task.Run( :: _listenerTask = Task.Run(() => ListenLoopAsync(_cts.Token));
- [Task Runs] L222 C21: Task.Run( :: _ = Task.Run(() => HandleContextAsync(context, token), token);


## XREngine.Editor/Unit Tests/Default/UnitTestingWorld.Models.cs
- [Task Runs] L102 C25: Task.Run( :: Task.Run(ImportAnimated).ContinueWith(nodeTask => OnFinishedImportingAvatar(nodeTask.Result, characterParentNode));


## XREngine.Profiler/UdpProfilerReceiver.cs
- [Thread Creations] L97 C19: new Thread( :: _thread = new Thread(ReceiverLoop)
- [Thread Starts] L107 C9: _thread.Start( :: _thread.Start();
- [Thread Stops] L120 C9: _thread.Join( :: _thread.Join(500);


## XRENGINE/Core/Engine/AssetManager.Loading.cs
- [Task Runs] L471 C26: Task.Run( :: return await Task.Run(() =>
- [Task Runs] L483 C26: Task.Run( :: return await Task.Run(() =>


## XRENGINE/Core/Files/DirectStorageIO.cs
- [Task Runs] L459 C28: Task.Run( :: tasks[i] = Task.Run(() =>


## XRENGINE/Core/Time/EngineTimer.cs
- [Task Runs] L164 C26: Task.Run( :: UpdateTask = Task.Run(UpdateThread);
- [Task Runs] L165 C34: Task.Run( :: CollectVisibleTask = Task.Run(CollectVisibleThread);
- [Task Runs] L166 C31: Task.Run( :: FixedUpdateTask = Task.Run(FixedUpdateThread);


## XRENGINE/Engine/Engine.Lifecycle.cs
- [Task Runs] L93 C21: Task.Run( :: Task.Run(async () => await InitializeVR(vrSettings, startupSettings.RunVRInPlace));


## XRENGINE/Engine/Engine.VRState.cs
- [Task Runs] L401 C26: Task.Run( :: => await Task.Run(() =>


## XRENGINE/Engine/Subclasses/Engine.CodeProfiler.cs
- [Thread Creations] L273 C36: new Thread( :: _statsThread = new Thread(StatsThreadMain)
- [Thread Starts] L279 C21: _statsThread.Start( :: _statsThread.Start(_statsThreadCts.Token);


## XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Debug.cs
- [Task Runs] L72 C31: Task.Run( :: Task tp = Task.Run(PopulatePoints);
- [Task Runs] L73 C31: Task.Run( :: Task tl = Task.Run(PopulateLines);
- [Task Runs] L74 C31: Task.Run( :: Task tt = Task.Run(PopulateTriangles);


## XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.SecondaryContext.cs
- [Thread Creations] L44 C31: new Thread( :: _thread = new Thread(() => RunContext(templateWindow, _cts.Token))
- [Thread Starts] L49 C21: _thread.Start( :: _thread.Start();


## XRENGINE/Jobs/JobManager.cs
- [Thread Creations] L116 C31: new Thread( :: _workers[i] = new Thread(WorkerLoop)
- [Task Runs] L250 C39: Task.Factory.StartNew( :: _deferredWorkerTask = Task.Factory.StartNew(
- [Task Runs] L612 C37: Task.Run( :: _remoteWorkerTask = Task.Run(RemoteWorkerLoop, _cts.Token);


## XRENGINE/Rendering/API/Rendering/Objects/Meshes/XRMesh.Geometry.cs
- [Task Runs] L108 C25: Task.Run( :: _ = Task.Run(GenerateBVH);
- [Task Runs] L122 C20: Task.Run( :: var task = Task.Run(GenerateBVH);


## XRENGINE/Rendering/API/Rendering/Objects/Textures/2D/Mipmap2D.cs
- [Task Runs] L277 C19: Task.Run( :: await Task.Run(() => Resize(width, height));
- [Task Runs] L281 C19: Task.Run( :: await Task.Run(() => InterpolativeResize(width, height, method));
- [Task Runs] L285 C19: Task.Run( :: await Task.Run(() => AdaptiveResize(width, height));


## XRENGINE/Rendering/API/Rendering/Objects/Textures/2D/XRTexture2D.cs
- [Task Runs] L173 C29: Task.Run( :: Task loadTask = Task.Run(() =>
- [Task Runs] L250 C32: Task.Run( :: Task previewTask = Task.Run(() =>


## XRENGINE/Rendering/API/Rendering/OpenGL/OpenGLRenderer.cs
- [Task Runs] L2315 C25: Task.Run( :: Task.Run(MakeImage);
- [Task Runs] L2328 C17: Task.Run( :: Task.Run(() => imageCallback(XRTexture.NewImage(w, h, pixelFormat, pixelType, data), layer, 0));
- [Task Runs] L2375 C25: Task.Run( :: Task.Run(MakeImage);
- [Task Runs] L2388 C17: Task.Run( :: Task.Run(() => imageCallback(XRTexture.NewImage(w, h, pixelFormat, pixelType, data), 0));
- [Task Runs] L2482 C21: Task.Run( :: Task.Run(() => pixelCallback(color));
- [Task Runs] L2517 C25: Task.Run( :: Task.Run(() => depthCallback(depth));


## XRENGINE/Rendering/API/Rendering/OpenGL/Types/Buffers/GLDataBuffer.cs
- [Task Runs] L327 C21: Task.Run( :: Task.Run(EnqueueCopy);


## XRENGINE/Rendering/API/Rendering/OpenGL/Types/Mesh Renderer/GLMeshRenderer.Lifecycle.cs
- [Task Runs] L78 C21: Task.Run( :: Task.Run(GenProgramsAndBuffers);


## XRENGINE/Rendering/API/Rendering/OpenGL/Types/Queries/GLRenderQuery.cs
- [Task Runs] L89 C21: Task.Run( :: Task.Run(() => AwaitResult()).ContinueWith(t => onReady(this));
- [Task Runs] L95 C22: Task.Run( :: => await Task.Run(() => AwaitResult());
- [Task Runs] L99 C19: Task.Run( :: await Task.Run(() => AwaitResult());


## XRENGINE/Rendering/API/Rendering/OpenXR/OpenXRAPI.FrameLifecycle.cs
- [Thread Creations] L687 C40: new Thread( :: _openXrLeftCollectWorker = new Thread(() => OpenXrParallelCollectWorkerLoop(leftEye: true))
- [Thread Creations] L692 C41: new Thread( :: _openXrRightCollectWorker = new Thread(() => OpenXrParallelCollectWorkerLoop(leftEye: false))
- [Thread Starts] L698 C13: _openXrLeftCollectWorker.Start( :: _openXrLeftCollectWorker.Start();
- [Thread Starts] L699 C13: _openXrRightCollectWorker.Start( :: _openXrRightCollectWorker.Start();


## XRENGINE/Rendering/API/Rendering/Vulkan/Drawing.Readback.cs
- [Task Runs] L536 C55: Task.Run( :: var readbackTask = System.Threading.Tasks.Task.Run(() =>


## XRENGINE/Rendering/API/Rendering/Vulkan/Objects/CommandBuffers.cs
- [Task Runs] L2666 C36: Task.Run( :: tasks[index] = Task.Run(() =>


## XRENGINE/Rendering/Compute/SkinnedMeshBvhScheduler.cs
- [Task Runs] L134 C20: Task.Run( :: var task = Task.Run(() => BuildBvh(mesh, triangles, positions));


## XRENGINE/Rendering/VideoStreaming/FFmpegStreamDecoder.cs
- [Task Runs] L168 C16: Task.Run( :: return Task.Run(() =>
- [Thread Creations] L207 C33: new Thread( :: _decodeThread = new Thread(DecodeLoop)
- [Thread Starts] L212 C17: _decodeThread.Start( :: _decodeThread.Start();


## XRENGINE/Scene/Components/Animation/MotionCapture/FaceMotion3DCaptureComponent.cs
- [Thread Starts] L298 C17: clientThread.Start( :: clientThread.Start();


## XRENGINE/Scene/Components/Audio/Converters/MicrophoneComponent.ElevenLabsConverter.cs
- [Task Runs] L140 C27: Task.Run( :: await Task.Run(ProcessQueueAsync);


## XRENGINE/Scene/Components/Audio/Converters/MicrophoneComponent.RVCConverter.cs
- [Task Runs] L151 C27: Task.Run( :: await Task.Run(ProcessQueueAsync);
- [Task Runs] L303 C44: Task.Run( :: bool completed = await Task.Run(() => process.WaitForExit(30000)); // 30 second timeout


## XRENGINE/Scene/Components/Networking/NetworkDiscoveryComponent.cs
- [Task Runs] L304 C27: Task.Run( :: _listenTask = Task.Run(() => ListenLoopAsync(token), token);
- [Task Runs] L313 C30: Task.Run( :: _broadcastTask = Task.Run(() => BroadcastLoopAsync(token), token);


## XRENGINE/Scene/Components/Networking/TcpClientComponent.cs
- [Task Runs] L317 C32: Task.Run( :: _receiveTask = Task.Run(() => ReceiveLoopAsync(stream, token));


## XRENGINE/Scene/Components/Networking/TcpServerComponent.cs
- [Task Runs] L193 C31: Task.Run( :: _acceptTask = Task.Run(() => AcceptLoopAsync(listener, _listenerCts.Token));
- [Task Runs] L292 C33: Task.Run( :: state.ReceiveTask = Task.Run(() => ReceiveClientLoopAsync(state, descriptor, serverToken));


## XRENGINE/Scene/Components/Networking/UdpSocketComponent.cs
- [Task Runs] L232 C32: Task.Run( :: _receiveTask = Task.Run(() => ReceiveLoopAsync(client, _receiveCts.Token));


## XRENGINE/Scene/Components/Networking/WebhookListenerComponent.cs
- [Task Runs] L128 C27: Task.Run( :: _listenTask = Task.Run(() => ListenLoopAsync(_listener, _listenerCts.Token));
- [Task Runs] L194 C21: Task.Run( :: _ = Task.Run(() => HandleContextAsync(context!, token));


## XRENGINE/Scene/Components/Networking/WebSocketClientComponent.cs
- [Task Runs] L266 C32: Task.Run( :: _receiveTask = Task.Run(() => ReceiveLoopAsync(socket, timeoutCts.Token));


## XRENGINE/Scene/Components/Physics/PhysicsChainComponent.cs
- [Thread Creations] L938 C21: new Thread( :: var t = new Thread(ThreadProc)
- [Thread Starts] L942 C13: t.Start( :: t.Start();


## XRENGINE/Scene/Components/VR/VRDeviceModelComponent.cs
- [Task Runs] L60 C13: Task.Run( :: Task.Run(() => LoadDeviceAsync(d, m));


## XRENGINE/Scene/Physics/Physx/InstancedDebugVisualizer.cs
- [Task Runs] L201 C39: Task.Run( :: _renderTasks[0] = Task.Run(() => Parallel.For(0, (int)PointCount, SetPointAt));
- [Task Runs] L204 C39: Task.Run( :: _renderTasks[1] = Task.Run(() => Parallel.For(0, (int)LineCount, SetLineAt));
- [Task Runs] L207 C39: Task.Run( :: _renderTasks[2] = Task.Run(() => Parallel.For(0, (int)TriangleCount, SetTriangleAt));


---
Category totals:
- Thread Creations: 9
- Thread Starts: 9
- Thread Stops: 1
- Task Runs: 62
Total matches: 81
