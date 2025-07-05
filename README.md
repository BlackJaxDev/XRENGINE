# XRENGINE

A high-performance game engine specifically designed for Virtual Reality (VR), Augmented Reality (AR), and Mixed Reality (MR/XR) applications. Built with modern graphics APIs and advanced physics engines to deliver immersive experiences with realistic character animations and physics simulation.

## 🚀 Key Features

### **XR & VR Support**
- **OpenXR Integration**: Full OpenXR standard support for cross-platform VR/AR compatibility
- **SteamVR Support**: Native SteamVR integration with action manifest system
- **Multi-View Rendering**: Optimized stereo rendering with OVR MultiView and NVIDIA stereo extensions
- **Parallel Eye Rendering**: Vulkan-based parallel rendering for improved performance
- **VR Device Tracking**: Comprehensive tracking for headsets, controllers, and body trackers
- **VR IK System**: Advanced inverse kinematics for realistic VR character movement

### **Graphics & Rendering**
- **Multi-API Support**: 
  - ✅ **OpenGL 4.6** (Fully Supported)
  - 🚧 **Vulkan** (In Development)
  - 🚧 **DirectX 12** (Planned)
- **Modern Rendering Pipeline**: Deferred lighting, post-processing, and advanced shader systems
- **Compute Shaders**: GPU-accelerated physics and animation calculations
- **Multi-Threaded Rendering**: Parallel rendering pipeline for optimal performance
- **Advanced Materials**: PBR materials with support for various texture types and effects

### **Physics Engines**
- **PhysX Integration**: ✅ Fully implemented with GPU acceleration and advanced features
- **Jolt Physics**: 🚧 In development - High-performance physics simulation
- **Jitter Physics**: 🚧 In development - Lightweight physics for mobile/AR applications
- **Character Controllers**: Advanced character movement with climbing, sliding, and collision detection
- **GPU Physics**: Compute shader-based physics chains for cloth, hair, and soft body simulation

### **Character Animation System**
- **Skeletal Animation**: Full skeletal mesh support with bone-based animation
- **Animation State Machine**: Complex animation blending and state management
- **Inverse Kinematics**: Advanced IK solvers including VRIK for VR applications
- **Humanoid System**: Complete humanoid character system with full body IK
- **Blend Shapes**: Facial animation and morph target support
- **Animation Clips**: Support for multiple animation formats and keyframe interpolation
- **GPU Skinning**: Compute shader-based skeletal animation for high performance

### **Asset Pipeline**
- **Model Import**: Support for FBX, OBJ, and other 3D model formats
- **Texture Support**: Various texture formats with compression and streaming
- **Audio System**: OpenAL integration with Steam Audio in development
- **Material System**: Advanced material editor with PBR workflow
- **Asset Management**: Efficient asset loading and memory management

### **Development Tools**
- **Editor Framework**: Comprehensive editor with scene management and debugging tools
- **Unit Testing**: Built-in unit testing framework for engine components
- **Profiling**: Performance profiling and optimization tools
- **Debug Visualization**: Real-time debugging and visualization tools

## 🏗️ Architecture

### **Core Systems**
- **Scene Hierarchy**: Tree-based scene management with SceneNodes and components
- **Parallel Tick System**: Sequential tick groups executed in parallel with depth-sorted transform updates
- **Asynchronous Rendering**: Parallel collect visible and render threads working alongside update thread
- **Physics Integration**: Unified physics interface supporting multiple engines
- **Input System**: Multi-device input handling for VR controllers and traditional input

### **Game Loop Architecture**
```
┌─────────────────────────────────────────────────────────────────────────────┐
│                             5 Main Threads                                  │
├─────────────────────────────────────────────────────────────────────────────┤
│  Update Thread    │  Fixed Update Thread   │  Job Processing Thread         │
│  • Scene Updates  │  • Physics Simulation  │  • Async Tasks                 │
│  • Input Handling │  • Character Movement  │  • Background Processing       │
│  • Animation      │  • Collision Detection │  • Asset Loading               │
└─────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                    Synchronized Rendering Threads                           │
├─────────────────────────────────────────────────────────────────────────────┤
│  Collect Visible Thread  │  Render Thread                                   │
│  • Frustum Culling       │  • Graphics API Calls                            │
│  • LOD Selection         │  • Shader Execution                              │
│  • Command Generation    │  • Buffer Swapping                               │
└─────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                    Thread Synchronization                                   │
├─────────────────────────────────────────────────────────────────────────────┤
│  Collect Visible ←→ Render Threads                                          │
│  • Wait for each other to complete                                          │
│  • Synchronized buffer swapping                                             │
│  • Maintain frame timing consistency                                        │
└─────────────────────────────────────────────────────────────────────────────┘
```

## 🎮 Getting Started

### **Prerequisites**
- .NET 8.0 or later
- Windows 10/11 (Primary platform)
- VR headset (for VR development)
- Graphics card supporting OpenGL 4.6 or Vulkan

### **Building the Engine**
```bash
# Clone the repository with submodules
git clone --recursive https://github.com/your-username/XRENGINE.git
cd XRENGINE

# Or if already cloned, update submodules
git submodule update --init --recursive

# Restore dependencies
dotnet restore

# Build the engine
dotnet build XRENGINE.sln

# Run the editor
dotnet run --project XREngine.Editor
```

### **Current Development State**
The engine is currently in early development. When you run the editor, it loads the **Unit Testing World** by default, which provides a comprehensive testing environment for all engine features including:

- **Physics Testing**: Various physics objects and interactions
- **Rendering Tests**: Different materials, shaders, and lighting scenarios
- **Animation System**: Character models and animation playback
- **VR Integration**: VR device testing and calibration
- **Audio System**: 3D spatial audio testing with OpenAL
- **Performance Profiling**: Built-in performance monitoring tools

This testing environment allows developers to verify engine functionality and experiment with different features while the engine continues to evolve.

## 📚 Documentation

### **Core Concepts**
- [Component System](docs/components.md)
- [Rendering Pipeline](docs/rendering.md)
- [Physics System](docs/physics.md)
- [Animation System](docs/animation.md)
- [VR Development](docs/vr-development.md)

### **API Reference**
- [Engine API](docs/api/engine.md)
- [Rendering API](docs/api/rendering.md)
- [Physics API](docs/api/physics.md)
- [Animation API](docs/api/animation.md)

## 🔧 Configuration

### **Unit Testing World Configuration**
The engine uses `UnitTestingWorldSettings.json` to configure what features are tested in the default testing environment. This file controls which test scenarios are loaded and enabled:

```json
{
  "UpdateFPS": 60.0,
  "RenderFPS": 120.0,
  "FixedFPS": 30.0
}
```

## 🎯 Performance Features

### **Optimization Techniques**
- **5-Thread Architecture**: Update, Fixed Update, Job Processing, Collect Visible, and Render threads
- **Synchronized Rendering**: Collect visible and render threads wait for each other to maintain consistency
- **Parallel Tick System**: Sequential tick groups executed in parallel for optimal performance
- **Asynchronous Transform Updates**: Depth-sorted world transform calculations
- **LOD System**: Level-of-detail management for models and textures
- **GPU Instancing**: Batch rendering for similar objects
- **Memory Management**: Efficient asset streaming and memory allocation

### **VR-Specific Optimizations**
- **Single-Pass Stereo**: Reduced draw calls for VR rendering
- **Foveated Rendering**: Support for eye-tracking based rendering
- **Time Warp**: Asynchronous time warp for smooth VR experience
- **Parallel Eye Rendering**: Simultaneous rendering for both eyes

## 🤝 Contributing

We welcome contributions! Please see our [Contributing Guidelines](CONTRIBUTING.md) for details on:
- Code style and standards
- Pull request process
- Issue reporting
- Development setup

## 📄 License

This project is licensed under the [LICENSE](LICENSE.txt) file.

## 🆘 Support

- **Documentation**: [docs/](docs/)
- **Issues**: [GitHub Issues](https://github.com/blackjaxdev/XRENGINE/issues)
- **Discussions**: [GitHub Discussions](https://github.com/blackjaxdev/XRENGINE/discussions)

---

**XRENGINE** - Empowering the future of immersive experiences through cutting-edge technology and performance-driven design.