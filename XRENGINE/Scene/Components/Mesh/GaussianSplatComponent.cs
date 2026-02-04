using Extensions;
using System.Numerics;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Gaussian;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Components.Scene.Mesh;

/// <summary>
/// Component that loads gaussian splat data and renders it using instanced point sprites.
/// </summary>
[Serializable]
public class GaussianSplatComponent : ModelComponent
{
    public GaussianSplatComponent()
    {
        Meshes.PostAnythingAdded += MeshAdded;
        Meshes.PostAnythingRemoved += MeshRemoved;
    }

    private string? _sourcePath;
    public string? SourcePath
    {
        get => _sourcePath;
        set
        {
            if (SetField(ref _sourcePath, value) && !string.IsNullOrWhiteSpace(value))
                LoadFromFile(value);
        }
    }

    private GaussianSplatCloud? _cloud;
    public GaussianSplatCloud? Cloud
    {
        get => _cloud;
        set
        {
            if (SetField(ref _cloud, value))
                RebuildModel();
        }
    }

    private float _pointScale = 3.0f;
    /// <summary>
    /// Multiplier applied to each gaussian radius before computing point size.
    /// </summary>
    public float PointScale
    {
        get => _pointScale;
        set
        {
            if (SetField(ref _pointScale, value))
                RebuildModel();
        }
    }

    public XRMaterial? OverrideMaterial { get; set; }

    private int _activeInstanceCount;

    public void LoadFromFile(string path)
    {
        try
        {
            Cloud = GaussianSplatCloud.Load(path);
        }
        catch (Exception ex)
        {
            Debug.RenderingException(ex, $"Failed to load gaussian splat data from '{path}'.");
        }
    }

    private void RebuildModel()
    {
        _activeInstanceCount = 0;

        if (Cloud is null || Cloud.Count == 0)
        {
            Model = null;
            return;
        }

        GaussianMeshBuilder builder = new(Cloud, PointScale);
        (XRMesh mesh, AABB bounds, int instanceCount) = builder.Build();

        _activeInstanceCount = instanceCount;

        XRMaterial material = OverrideMaterial ?? GaussianMaterialFactory.Create();

        SubMesh subMesh = new(new SubMeshLOD(material, mesh, float.PositiveInfinity))
        {
            Bounds = bounds,
            CullingBounds = bounds,
        };

        Model = new Model(subMesh);
    }

    private void MeshAdded(RenderableMesh mesh)
    {
        foreach (var lod in mesh.LODs)
            lod.Renderer.SettingUniforms += RendererOnSettingUniforms;

        UpdateRenderCommandInstances(mesh);
    }

    private void MeshRemoved(RenderableMesh mesh)
    {
        foreach (var lod in mesh.LODs)
            lod.Renderer.SettingUniforms -= RendererOnSettingUniforms;
    }

    private void UpdateRenderCommandInstances(RenderableMesh mesh)
    {
        uint instances = _activeInstanceCount > 0 ? (uint)_activeInstanceCount : 1u;

        foreach (var command in mesh.RenderInfo.RenderCommands)
            if (command is RenderCommandMesh3D meshCommand)
                meshCommand.Instances = instances;
    }

    private static void RendererOnSettingUniforms(XRRenderProgram vertexProgram, XRRenderProgram _)
    {
        var viewport = Engine.Rendering.State.RenderingViewport;
        if (viewport is null)
            return;

        vertexProgram.Uniform(EEngineUniform.ScreenWidth.ToString(), (float)viewport.Width);
        vertexProgram.Uniform(EEngineUniform.ScreenHeight.ToString(), (float)viewport.Height);
    }

    private sealed record GaussianMeshBuilder(GaussianSplatCloud Cloud, float RadiusScale)
    {
        public (XRMesh mesh, AABB bounds, int instanceCount) Build()
        {
            int count = Cloud.Count;
            if (count == 0)
                return (new XRMesh([]), new AABB(), 0);

            var splats = Cloud.Splats;

            Vector3[] positions = new Vector3[count];
            Vector4[] colors = new Vector4[count];
            Vector4[] scales = new Vector4[count];
            Vector4[] rotations = new Vector4[count];

            Vector3 min = new(float.PositiveInfinity);
            Vector3 max = new(float.NegativeInfinity);

            for (int i = 0; i < count; i++)
            {
                var splat = splats[i];
                Vector3 position = splat.Position;
                Vector3 scaledExtents = splat.Scale * RadiusScale;

                positions[i] = position;
                colors[i] = splat.ColorWithOpacity;
                scales[i] = new Vector4(scaledExtents, scaledExtents.Length());
                rotations[i] = splat.Rotation.ToVector4();

                Vector3 localMin = position - scaledExtents;
                Vector3 localMax = position + scaledExtents;
                min = Vector3.Min(min, localMin);
                max = Vector3.Max(max, localMax);
            }

            AABB bounds = new(min, max);

            XRMesh mesh = XRMesh.CreatePoints(Vector3.Zero);
            mesh.SupportsBillboarding = false;
            mesh.Points = [0];

            ConfigureInstancedBuffers(mesh, positions, colors, scales, rotations);

            return (mesh, bounds, count);
        }

        private static void ConfigureInstancedBuffers(
            XRMesh mesh,
            IList<Vector3> positions,
            IList<Vector4> colors,
            IList<Vector4> scales,
            IList<Vector4> rotations)
        {
            mesh.Buffers.RemoveBuffer(ECommonBufferType.InterleavedVertex.ToString());
            mesh.Buffers.RemoveBuffer(ECommonBufferType.Position.ToString());
            mesh.Buffers.RemoveBuffer($"{ECommonBufferType.Color}0");
            mesh.Buffers.RemoveBuffer($"{ECommonBufferType.Color}1");
            mesh.Buffers.RemoveBuffer($"{ECommonBufferType.Color}2");

            mesh.InterleavedVertexBuffer?.Destroy();
            mesh.Interleaved = false;

            XRDataBuffer positionBuffer = mesh.Buffers.SetBufferRaw(
                positions,
                ECommonBufferType.Position.ToString(),
                instanceDivisor: 1);
            mesh.PositionsBuffer = positionBuffer;

            XRDataBuffer color0 = mesh.Buffers.SetBufferRaw(
                colors,
                $"{ECommonBufferType.Color}0",
                instanceDivisor: 1);
            XRDataBuffer color1 = mesh.Buffers.SetBufferRaw(
                scales,
                $"{ECommonBufferType.Color}1",
                instanceDivisor: 1);
            XRDataBuffer color2 = mesh.Buffers.SetBufferRaw(
                rotations,
                $"{ECommonBufferType.Color}2",
                instanceDivisor: 1);

            mesh.ColorBuffers = [color0, color1, color2];
            mesh.ColorCount = (uint)mesh.ColorBuffers.Length;
        }
    }

    private static class GaussianMaterialFactory
    {
        private static XRMaterial? _material;

        public static XRMaterial Create()
        {
            if (_material != null)
                return _material;

            XRShader vertex = ShaderHelper.GaussianSplatVertex()!;
            XRShader fragment = ShaderHelper.GaussianSplatFragment()!;

            XRMaterial material = new([fragment])
            {
                RenderPass = (int)EDefaultRenderPass.TransparentForward,
                RenderOptions = new RenderingParameters()
                {
                    CullMode = ECullMode.None,
                    DepthTest = new DepthTest()
                    {
                        Enabled = ERenderParamUsage.Enabled,
                        Function = EComparison.Lequal,
                        UpdateDepth = false,
                    },
                    BlendModeAllDrawBuffers = BlendMode.EnabledTransparent(),
                    RequiredEngineUniforms = EUniformRequirements.Camera | EUniformRequirements.ViewportDimensions,
                }
            };

            material.Shaders.Add(vertex);

            _material = material;
            return material;
        }
    }
}
