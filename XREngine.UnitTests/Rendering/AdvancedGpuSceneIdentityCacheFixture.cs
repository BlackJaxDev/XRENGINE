using System.Numerics;
using System.Reflection;
using XREngine.Core.Files;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;

namespace XREngine.UnitTests.Rendering;

internal sealed class AdvancedGpuSceneIdentityCacheFixture : IDisposable
{
    private static readonly PropertyInfo SnapshotProperty = typeof(RenderCommandMesh3D).GetProperty(
        "CanonicalDrawIdentitySnapshot",
        BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo SourceLookupField = typeof(GPUScene).GetField(
        "_commandIndexLookup", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private readonly IRuntimeShaderServices? _previousShaderServices;
    private readonly List<AdvancedGpuSceneIdentityTestRenderable> _owners = [];
    private readonly List<RenderCommandMesh3D> _commands = [];
    private ulong _frameId;

    public AdvancedGpuSceneIdentityCacheFixture()
    {
        _previousShaderServices = RuntimeShaderServices.Current;
        RuntimeShaderServices.Current = new GltfImportTestUtilities.TestRuntimeShaderServices();
    }

    public GPUScene Scene { get; } = new();

    public AdvancedGpuScenePublisher Publisher { get; } = new();

    public RenderInfo3D AddSource(out RenderCommandMesh3D command, int primitiveCount = 1)
    {
        if (primitiveCount < 1)
            throw new ArgumentOutOfRangeException(nameof(primitiveCount));
        int pass = (int)EDefaultRenderPass.OpaqueForward;
        XRMesh mesh = XRMesh.CreateTriangles(Vector3.Zero, Vector3.UnitX, Vector3.UnitY);
        XRMaterial material = new() { RenderPass = pass };
        (XRMesh mesh, XRMaterial material)[] primitives = new (XRMesh, XRMaterial)[primitiveCount];
        for (int index = 0; index < primitiveCount; ++index)
            primitives[index] = (mesh, material);
        XRMeshRenderer renderer = new(primitives);
        command = new RenderCommandMesh3D(pass) { Mesh = renderer };
        AdvancedGpuSceneIdentityTestRenderable owner = new();
        RenderInfo3D info = RenderInfo3D.New(owner, command);
        owner.RenderedObjects = [info];
        _owners.Add(owner);
        _commands.Add(command);
        Scene.Add(info);
        return info;
    }

    public void SetPrimitiveCount(RenderInfo3D info, RenderCommandMesh3D command, int count)
    {
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(count));
        XRMeshRenderer renderer = command.Mesh!;
        XRMesh mesh = renderer.Submeshes[0].Mesh!;
        XRMaterial material = renderer.Submeshes[0].Material!;
        Scene.Remove(info);
        while (renderer.Submeshes.Count > count)
            renderer.Submeshes.RemoveAt(renderer.Submeshes.Count - 1);
        while (renderer.Submeshes.Count < count)
            renderer.Submeshes.Add(new XRMeshRenderer.SubMesh
            {
                Mesh = mesh,
                Material = material,
                InstanceCount = 1,
            });
        Scene.Add(info);
    }

    public void Publish()
    {
        foreach (RenderCommandMesh3D command in _commands)
            command.SwapBuffers();
        Scene.SwapCommandBuffers();
        ulong frameId = ++_frameId;
        Publisher.Publish(Scene, frameId, AdvancedGlobalResourceCapture.Empty(frameId));
    }

    public void PublishWithMissingSource(bool nullEntry)
    {
        foreach (RenderCommandMesh3D command in _commands)
            command.SwapBuffers();
        Scene.SwapCommandBuffers();
        var lookup = (Dictionary<uint, (IRenderCommandMesh command, int subMeshIndex, GpuSceneMeshCommandSnapshot snapshot)>)
            SourceLookupField.GetValue(Scene)!;
        (IRenderCommandMesh command, int subMeshIndex, GpuSceneMeshCommandSnapshot snapshot) original = lookup[0u];
        if (nullEntry)
            lookup[0u] = (null!, original.subMeshIndex, original.snapshot);
        else
            lookup.Remove(0u);
        try
        {
            ulong frameId = ++_frameId;
            Publisher.Publish(Scene, frameId, AdvancedGlobalResourceCapture.Empty(frameId));
        }
        finally
        {
            lookup[0u] = original;
        }
    }

    public static AdvancedGpuSceneDrawIdentitySnapshot Snapshot(RenderCommandMesh3D command)
        => (AdvancedGpuSceneDrawIdentitySnapshot)SnapshotProperty.GetValue(command)!;

    public void Dispose()
    {
        try
        {
            Publisher.Dispose();
        }
        finally
        {
            try
            {
                Scene.Destroy();
            }
            finally
            {
                RuntimeShaderServices.Current = _previousShaderServices;
            }
        }
    }
}
