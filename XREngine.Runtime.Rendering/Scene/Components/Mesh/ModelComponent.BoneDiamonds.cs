using System.ComponentModel;
using System.Collections.Generic;
using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Scene.Mesh
{
    public partial class ModelComponent
    {
        private const float BoneDiamondMinSegmentLengthSquared = 1.0e-8f;
        private const float BoneDiamondRadiusScale = 0.14f;
        private const float BoneDiamondMinRadius = 0.005f;
        private const string BoneDiamondFragmentShaderSource = """
#version 450

layout(location = 0) out vec4 OutColor;
layout(location = 0) in vec3 FragPos;
layout(location = 1) in vec3 FragNorm;

uniform vec4 MatColor;
uniform vec3 CameraPosition;
uniform vec3 CameraForward;

void main()
{
    vec3 normal = normalize(FragNorm);
    vec3 toCamera = CameraPosition - FragPos;
    float distSq = dot(toCamera, toCamera);
    vec3 viewDir = distSq > 1.0e-8 ? toCamera * inversesqrt(distSq) : normalize(-CameraForward);
    float facing = clamp(abs(dot(normal, viewDir)), 0.0, 1.0);
    float shade = mix(0.30, 0.96, pow(facing, 0.70));
    float edgeGlow = pow(1.0 - facing, 1.8) * 0.16;
    float alpha = MatColor.a * mix(0.35, 0.95, facing);
    vec3 color = MatColor.rgb * shade + vec3(edgeGlow);

    OutColor = vec4(color * alpha, alpha);
}
""";

        private static readonly Lazy<XRMeshRenderer> BoneDiamondRenderer = new(CreateBoneDiamondRenderer);

        private readonly RenderInfo3D _boneDiamondRenderInfo;
        private readonly List<(TransformBase bone, Vector3? fallbackTip)> _boneDiamondLinks = new(64);
        private bool _renderUtilizedBoneDiamonds;

        public ModelComponent()
        {
            _boneDiamondRenderInfo = RenderInfo3D.New(
                this,
                new RenderCommandMethod3D(EDefaultRenderPass.OnTopForward, RenderBoneDiamonds));
            _boneDiamondRenderInfo.CastsShadows = false;
            _boneDiamondRenderInfo.ReceivesShadows = false;
            _boneDiamondRenderInfo.VisibleInLightingProbes = false;
            _boneDiamondRenderInfo.IsVisible = false;
        }

        [Category("Debug")]
        [DisplayName("Render Bone Diamonds")]
        [Description("Renders semi-transparent diamond mesh overlays for every transform referenced by this model's mesh bone bindings.")]
        public bool RenderUtilizedBoneDiamonds
        {
            get => _renderUtilizedBoneDiamonds;
            set => SetField(ref _renderUtilizedBoneDiamonds, value);
        }

        protected override void OnComponentDeactivated()
        {
            _boneDiamondRenderInfo.WorldInstance = null;
            base.OnComponentDeactivated();
        }

        private void SyncBoneDiamondRenderInfoWithWorld()
        {
            _boneDiamondRenderInfo.IsVisible = RenderUtilizedBoneDiamonds;
            _boneDiamondRenderInfo.WorldInstance = IsActiveInHierarchy
                ? World as IRuntimeRenderInfo3DRegistrationTarget
                : null;
        }

        private void RenderBoneDiamonds()
        {
            if (!RenderUtilizedBoneDiamonds || RuntimeEngine.Rendering.State.IsShadowPass)
                return;

            _boneDiamondLinks.Clear();
            for (int i = 0; i < Meshes.Count; i++)
                Meshes[i]?.AppendUtilizedBoneDiamondLinks(_boneDiamondLinks);

            XRMeshRenderer renderer = BoneDiamondRenderer.Value;
            for (int i = 0; i < _boneDiamondLinks.Count; i++)
            {
                (TransformBase bone, Vector3? fallbackTip) = _boneDiamondLinks[i];
                if (!TryCreateBoneDiamondMatrix(bone, fallbackTip, out Matrix4x4 matrix))
                    continue;

                renderer.Render(matrix, matrix);
            }
        }

        private static XRMeshRenderer CreateBoneDiamondRenderer()
        {
            XRMesh mesh = CreateBoneDiamondMesh();
            XRMaterial material = CreateBoneDiamondMaterial();

            return new XRMeshRenderer(mesh, material)
            {
                Name = "Model Bone Diamond Overlay",
                GenerationPriority = EMeshGenerationPriority.Interactive,
            };
        }

        private static XRMaterial CreateBoneDiamondMaterial()
        {
            ShaderVar[] parameters =
            [
                new ShaderVector4(new Vector4(0.62f, 0.64f, 0.66f, 0.24f), "MatColor"),
            ];

            XRMaterial material = new(parameters, new XRShader(EShaderType.Fragment, BoneDiamondFragmentShaderSource));
            material.Name = "Model Bone Diamond Overlay";
            material.ShaderProgramPriority = EProgramPriority.Interactive;
            material.TransparencyMode = ETransparencyMode.PremultipliedAlpha;
            material.RenderPass = (int)EDefaultRenderPass.OnTopForward;
            material.RenderOptions.RequiredEngineUniforms = EUniformRequirements.Camera;
            material.RenderOptions.CullMode = ECullMode.None;
            material.RenderOptions.DepthTest.Enabled = ERenderParamUsage.Disabled;
            material.RenderOptions.DepthTest.UpdateDepth = false;
            material.RenderOptions.BlendModeAllDrawBuffers = new BlendMode()
            {
                Enabled = ERenderParamUsage.Enabled,
                RgbSrcFactor = EBlendingFactor.One,
                RgbDstFactor = EBlendingFactor.OneMinusSrcAlpha,
                AlphaSrcFactor = EBlendingFactor.One,
                AlphaDstFactor = EBlendingFactor.OneMinusSrcAlpha,
            };
            material.RenderOptions.BlendModesPerDrawBuffer = null;
            material.RenderOptions.AlphaToCoverage = ERenderParamUsage.Disabled;
            material.RenderOptions.ExcludeFromGpuIndirect = true;
            return material;
        }

        private static XRMesh CreateBoneDiamondMesh()
        {
            Vector3 root = new(0.0f, 0.0f, 0.0f);
            Vector3 body0 = new(1.0f, 0.35f, 0.0f);
            Vector3 body1 = new(0.0f, 0.35f, 1.0f);
            Vector3 body2 = new(-1.0f, 0.35f, 0.0f);
            Vector3 body3 = new(0.0f, 0.35f, -1.0f);
            Vector3 tip = new(0.0f, 1.0f, 0.0f);
            List<Vertex> vertices = new(24);
            List<ushort> indices = new(24);

            AddBoneDiamondFace(vertices, indices, root, body0, body1);
            AddBoneDiamondFace(vertices, indices, root, body1, body2);
            AddBoneDiamondFace(vertices, indices, root, body2, body3);
            AddBoneDiamondFace(vertices, indices, root, body3, body0);
            AddBoneDiamondFace(vertices, indices, tip, body1, body0);
            AddBoneDiamondFace(vertices, indices, tip, body2, body1);
            AddBoneDiamondFace(vertices, indices, tip, body3, body2);
            AddBoneDiamondFace(vertices, indices, tip, body0, body3);

            return new XRMesh(vertices, indices)
            {
                Name = "Model Bone Diamond Overlay",
            };
        }

        private static void AddBoneDiamondFace(List<Vertex> vertices, List<ushort> indices, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 normal = Vector3.Cross(b - a, c - a);
            normal = normal.LengthSquared() > BoneDiamondMinSegmentLengthSquared
                ? Vector3.Normalize(normal)
                : Vector3.UnitY;

            ushort index = (ushort)vertices.Count;
            vertices.Add(new Vertex(a, normal));
            vertices.Add(new Vertex(b, normal));
            vertices.Add(new Vertex(c, normal));
            indices.Add(index);
            indices.Add((ushort)(index + 1));
            indices.Add((ushort)(index + 2));
        }

        private static bool TryCreateBoneDiamondMatrix(TransformBase bone, Vector3? fallbackTip, out Matrix4x4 matrix)
        {
            if (!TryGetBoneDiamondSegment(bone, fallbackTip, out Vector3 root, out Vector3 tip))
            {
                matrix = Matrix4x4.Identity;
                return false;
            }

            Vector3 axis = tip - root;
            float lengthSquared = axis.LengthSquared();
            if (lengthSquared <= BoneDiamondMinSegmentLengthSquared)
            {
                matrix = Matrix4x4.Identity;
                return false;
            }

            float length = MathF.Sqrt(lengthSquared);
            Vector3 direction = axis / length;
            Vector3 seed = MathF.Abs(Vector3.Dot(direction, Vector3.UnitY)) > 0.95f
                ? Vector3.UnitX
                : Vector3.UnitY;
            Vector3 sideA = Vector3.Normalize(Vector3.Cross(direction, seed));
            Vector3 sideB = Vector3.Normalize(Vector3.Cross(direction, sideA));
            float radius = MathF.Max(length * BoneDiamondRadiusScale, BoneDiamondMinRadius);

            Vector3 xAxis = sideA * radius;
            Vector3 yAxis = axis;
            Vector3 zAxis = sideB * radius;
            matrix = new Matrix4x4(
                xAxis.X, xAxis.Y, xAxis.Z, 0.0f,
                yAxis.X, yAxis.Y, yAxis.Z, 0.0f,
                zAxis.X, zAxis.Y, zAxis.Z, 0.0f,
                root.X, root.Y, root.Z, 1.0f);
            return true;
        }

        private static bool TryGetBoneDiamondSegment(TransformBase bone, Vector3? fallbackTip, out Vector3 root, out Vector3 tip)
        {
            tip = bone.RenderTranslation;
            TransformBase? parent = bone.Parent;
            if (parent is not null)
            {
                root = parent.RenderTranslation;
                if (Vector3.DistanceSquared(root, tip) > BoneDiamondMinSegmentLengthSquared)
                    return true;
            }

            root = tip;
            for (int i = 0; i < bone.Children.Count; i++)
            {
                TransformBase? child = bone.Children[i];
                if (child is null)
                    continue;

                tip = child.RenderTranslation;
                if (Vector3.DistanceSquared(root, tip) > BoneDiamondMinSegmentLengthSquared)
                    return true;
            }

            if (fallbackTip.HasValue)
            {
                tip = fallbackTip.Value;
                if (Vector3.DistanceSquared(root, tip) > BoneDiamondMinSegmentLengthSquared)
                    return true;
            }

            return false;
        }
    }
}
