# Rendering API Reference

XRENGINE's rendering API provides comprehensive graphics capabilities with support for multiple graphics APIs, advanced shaders, and VR-optimized rendering.

## Core Rendering Classes

### AbstractRenderer
Base class for all renderers (OpenGL, Vulkan, etc.).

```csharp
public abstract class AbstractRenderer : XRBase
{
    public abstract void Initialize();
    public abstract void Render();
    public abstract void CleanUp();
    
    public abstract void SetViewport(Viewport viewport);
    public abstract void Clear(ClearFlags flags, Color color);
    public abstract void Present();
}
```

### Renderer
Main rendering interface that manages the rendering pipeline.

```csharp
public class Renderer : XRBase
{
    public AbstractRenderer? CurrentRenderer { get; }
    public RenderPipeline Pipeline { get; }
    public RenderState State { get; }
    
    public void Initialize(ERenderLibrary library);
    public void Render();
    public void SetPipeline(RenderPipeline pipeline);
}
```

## Render Pipeline

### RenderPipeline
Manages the rendering pipeline and render passes.

```csharp
public class RenderPipeline : XRBase
{
    public List<RenderPass> Passes { get; }
    public RenderTarget? MainTarget { get; set; }
    public Camera? MainCamera { get; set; }
    
    public void AddPass(RenderPass pass);
    public void RemovePass(RenderPass pass);
    public void Render();
}
```

### RenderPass
Individual rendering pass in the pipeline.

```csharp
public abstract class RenderPass : XRBase
{
    public string Name { get; set; }
    public bool Enabled { get; set; }
    public RenderTarget? Target { get; set; }
    
    public abstract void Execute(RenderContext context);
    public virtual void Setup(RenderContext context) { }
    public virtual void Cleanup(RenderContext context) { }
}
```

## Render Targets and Textures

### RenderTarget
Framebuffer for rendering to textures.

```csharp
public class RenderTarget : XRBase
{
    public Vector2 Size { get; }
    public List<Texture> ColorAttachments { get; }
    public Texture? DepthAttachment { get; set; }
    public Texture? StencilAttachment { get; set; }
    
    public void Bind();
    public void Unbind();
    public void Resize(Vector2 newSize);
}
```

### Texture
Texture resource for images and render targets.

```csharp
public class Texture : XRBase
{
    public Vector2 Size { get; }
    public ETextureFormat Format { get; }
    public ETextureFilter Filter { get; set; }
    public ETextureWrapMode WrapMode { get; set; }
    
    public void Bind(int unit = 0);
    public void Unbind();
    public void SetData(byte[] data);
    public void GenerateMipmaps();
}
```

## Shaders

### ShaderProgram
Compiled shader program with vertex and fragment shaders.

```csharp
public class ShaderProgram : XRBase
{
    public uint ProgramID { get; }
    public Dictionary<string, int> Uniforms { get; }
    
    public void Use();
    public void SetUniform(string name, float value);
    public void SetUniform(string name, Vector3 value);
    public void SetUniform(string name, Matrix4x4 value);
    public void SetUniform(string name, Texture texture, int unit);
}
```

### Shader Types
```csharp
public enum EShaderType
{
    Vertex,
    Fragment,
    Geometry,
    Compute,
    TessellationControl,
    TessellationEvaluation
}
```

## Materials

### Material
Material with shader and texture properties.

```csharp
public class Material : XRBase
{
    public ShaderProgram? Shader { get; set; }
    public Dictionary<string, object> Properties { get; }
    
    public void SetProperty(string name, object value);
    public T? GetProperty<T>(string name);
    public void Bind();
    public void Unbind();
}
```

### PBRMaterial
Physically-based rendering material.

```csharp
public class PBRMaterial : Material
{
    public Vector3 Albedo { get; set; }
    public float Metallic { get; set; }
    public float Roughness { get; set; }
    public Vector3 Emissive { get; set; }
    public float NormalScale { get; set; }
    
    public Texture? AlbedoMap { get; set; }
    public Texture? MetallicRoughnessMap { get; set; }
    public Texture? NormalMap { get; set; }
    public Texture? EmissiveMap { get; set; }
    public Texture? AOMap { get; set; }
}
```

## Meshes and Geometry

### Mesh
3D mesh with vertices, indices, and attributes.

```csharp
public class Mesh : XRBase
{
    public List<Vector3> Vertices { get; }
    public List<Vector3> Normals { get; }
    public List<Vector2> TexCoords { get; }
    public List<uint> Indices { get; }
    
    public void Upload();
    public void Render();
    public void RenderInstanced(int count);
}
```

### VertexBuffer
GPU buffer for vertex data.

```csharp
public class VertexBuffer : XRBase
{
    public uint BufferID { get; }
    public int VertexCount { get; }
    public int Stride { get; }
    
    public void Bind();
    public void Unbind();
    public void SetData<T>(T[] data) where T : struct;
}
```

### IndexBuffer
GPU buffer for index data.

```csharp
public class IndexBuffer : XRBase
{
    public uint BufferID { get; }
    public int IndexCount { get; }
    
    public void Bind();
    public void Unbind();
    public void SetData(uint[] indices);
}
```

## Camera System

### Camera
Camera with projection and view matrices.

```csharp
public class Camera : XRBase
{
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; }
    public float FieldOfView { get; set; }
    public float NearClipPlane { get; set; }
    public float FarClipPlane { get; set; }
    public Vector2 ViewportSize { get; set; }
    
    public Matrix4x4 ViewMatrix { get; }
    public Matrix4x4 ProjectionMatrix { get; }
    public Frustum Frustum { get; }
    
    public void LookAt(Vector3 target);
    public void SetPerspective(float fov, float aspect, float near, float far);
    public void SetOrthographic(float width, float height, float near, float far);
}
```

### VR Camera
VR-specific camera with stereo rendering support.

```csharp
public class VRCamera : Camera
{
    public EVREye Eye { get; set; }
    public float IPD { get; set; }
    public Matrix4x4 EyeProjectionMatrix { get; }
    public Matrix4x4 EyeViewMatrix { get; }
    
    public void SetEyeProjection(EVREye eye, Matrix4x4 projection);
    public void SetEyeView(EVREye eye, Matrix4x4 view);
}
```

## Lighting

### Light
Base class for all light types.

```csharp
public abstract class Light : XRBase
{
    public Vector3 Position { get; set; }
    public Vector3 Direction { get; set; }
    public Color Color { get; set; }
    public float Intensity { get; set; }
    public bool CastShadows { get; set; }
    
    public abstract void Bind(ShaderProgram shader, int index);
}
```

### DirectionalLight
Directional light (sun-like).

```csharp
public class DirectionalLight : Light
{
    public Vector3 Direction { get; set; }
    public float ShadowDistance { get; set; }
    public int ShadowMapSize { get; set; }
    
    public override void Bind(ShaderProgram shader, int index);
}
```

### PointLight
Omnidirectional point light.

```csharp
public class PointLight : Light
{
    public float Range { get; set; }
    public float Attenuation { get; set; }
    
    public override void Bind(ShaderProgram shader, int index);
}
```

### SpotLight
Directional cone light.

```csharp
public class SpotLight : Light
{
    public Vector3 Direction { get; set; }
    public float Range { get; set; }
    public float SpotAngle { get; set; }
    public float SpotBlend { get; set; }
    
    public override void Bind(ShaderProgram shader, int index);
}
```

## Render Commands

### RenderCommand
Command for rendering operations.

```csharp
public class RenderCommand
{
    public Mesh? Mesh { get; set; }
    public Material? Material { get; set; }
    public Matrix4x4 Transform { get; set; }
    public int RenderPass { get; set; }
    public float Distance { get; set; }
}
```

### RenderQueue
Queue for organizing render commands.

```csharp
public class RenderQueue
{
    public void AddCommand(RenderCommand command);
    public void Sort();
    public void Clear();
    public List<RenderCommand> GetCommands();
}
```

## VR Rendering

### Stereo Rendering
VR stereo rendering support.

```csharp
public class StereoRenderer : AbstractRenderer
{
    public bool SinglePassStereo { get; set; }
    public bool ParallelRendering { get; set; }
    
    public void RenderStereo(Camera leftEye, Camera rightEye);
    public void RenderSinglePass(Camera leftEye, Camera rightEye);
}
```

### VR Render Target
VR-specific render target with stereo support.

```csharp
public class VRRenderTarget : RenderTarget
{
    public Vector2 EyeSize { get; }
    public Texture? LeftEyeTexture { get; }
    public Texture? RightEyeTexture { get; }
    
    public void BindEye(EVREye eye);
    public void UnbindEye();
}
```

## Compute Shaders

### ComputeShader
GPU compute shader for parallel processing.

```csharp
public class ComputeShader : XRBase
{
    public uint ShaderID { get; }
    public Dictionary<string, int> Uniforms { get; }
    
    public void Use();
    public void Dispatch(int groupsX, int groupsY, int groupsZ);
    public void SetUniform(string name, float value);
    public void SetUniform(string name, Vector3 value);
}
```

### Compute Buffer
Buffer for compute shader data.

```csharp
public class ComputeBuffer : XRBase
{
    public uint BufferID { get; }
    public int Size { get; }
    
    public void Bind(int binding);
    public void Unbind();
    public void SetData<T>(T[] data) where T : struct;
    public T[] GetData<T>() where T : struct;
}
```

## Example: Basic Rendering Setup

```csharp
// Initialize renderer
var renderer = new Renderer();
renderer.Initialize(ERenderLibrary.OpenGL);

// Create render target
var renderTarget = new RenderTarget(new Vector2(1920, 1080));
renderTarget.AddColorAttachment(ETextureFormat.RGBA8);
renderTarget.SetDepthAttachment(ETextureFormat.Depth24);

// Create shader
var vertexShader = new Shader(EShaderType.Vertex, vertexSource);
var fragmentShader = new Shader(EShaderType.Fragment, fragmentSource);
var shaderProgram = new ShaderProgram(vertexShader, fragmentShader);

// Create material
var material = new PBRMaterial
{
    Shader = shaderProgram,
    Albedo = new Vector3(1, 1, 1),
    Metallic = 0.0f,
    Roughness = 0.5f
};

// Create mesh
var mesh = new Mesh();
mesh.Vertices.AddRange(vertices);
mesh.Indices.AddRange(indices);
mesh.Upload();

// Render loop
while (running)
{
    renderTarget.Bind();
    renderer.Clear(ClearFlags.Color | ClearFlags.Depth, Color.Black);
    
    shaderProgram.Use();
    material.Bind();
    mesh.Render();
    
    renderTarget.Unbind();
    renderer.Present();
}
```

## Example: VR Rendering

```csharp
// Create VR camera
var vrCamera = new VRCamera();
vrCamera.FieldOfView = 90.0f;
vrCamera.NearClipPlane = 0.1f;
vrCamera.FarClipPlane = 1000.0f;

// Create VR render target
var vrTarget = new VRRenderTarget(new Vector2(1920, 1080));

// VR render loop
while (running)
{
    // Update VR poses
    var leftEyePose = GetVREyePose(EVREye.Left);
    var rightEyePose = GetVREyePose(EVREye.Right);
    
    // Render left eye
    vrTarget.BindEye(EVREye.Left);
    vrCamera.SetEyeView(EVREye.Left, leftEyePose.View);
    vrCamera.SetEyeProjection(EVREye.Left, leftEyePose.Projection);
    RenderScene(vrCamera);
    
    // Render right eye
    vrTarget.BindEye(EVREye.Right);
    vrCamera.SetEyeView(EVREye.Right, rightEyePose.View);
    vrCamera.SetEyeProjection(EVREye.Right, rightEyePose.Projection);
    RenderScene(vrCamera);
    
    // Submit to VR system
    SubmitVRFrames(vrTarget);
}
```

## Example: Compute Shader

```csharp
// Create compute shader
var computeShader = new ComputeShader(computeSource);

// Create buffers
var inputBuffer = new ComputeBuffer<float>(1024);
var outputBuffer = new ComputeBuffer<float>(1024);

// Set up data
inputBuffer.SetData(inputData);

// Dispatch compute shader
computeShader.Use();
computeShader.SetUniform("inputSize", 1024);
inputBuffer.Bind(0);
outputBuffer.Bind(1);
computeShader.Dispatch(4, 1, 1); // 1024 / 256 = 4 groups

// Get results
var results = outputBuffer.GetData();
```

## Performance Optimization

### Render State Management
```csharp
public class RenderState
{
    public void SetDepthTest(bool enabled);
    public void SetBlending(bool enabled);
    public void SetCulling(ECullMode mode);
    public void SetViewport(Viewport viewport);
}
```

### Batch Rendering
```csharp
public class BatchRenderer
{
    public void AddMesh(Mesh mesh, Material material, Matrix4x4 transform);
    public void Render();
    public void Clear();
}
```

## Configuration

### Rendering Settings
```json
{
  "Rendering": {
    "RenderLibrary": "OpenGL",
    "TargetFramesPerSecond": 90,
    "VSync": false,
    "StereoRendering": true,
    "SinglePassStereo": true,
    "ParallelRendering": false,
    "ShadowQuality": "High",
    "AntiAliasing": "MSAA_4x"
  }
}
```

## Related Documentation
- [Component System](../components.md)
- [Scene System](../scene.md)
- [Physics System](../physics.md)
- [Animation System](../animation.md)
- [VR Development](../vr-development.md) 