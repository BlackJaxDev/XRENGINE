using Extensions;
using System.Drawing;
using System.Numerics;
using XREngine.Components;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.UI
{
    /// <summary>
    /// A basic UI component that renders a quad with a material.
    /// </summary>
    [XRComponentEditor("XREngine.Editor.ComponentEditors.UIMaterialComponentEditor")]
    public class UIMaterialComponent : UIRenderableComponent
    {
        public UIMaterialComponent()
            : this(XRMaterial.CreateUnlitColorMaterialForward(Color.Magenta), false) { }

        public UIMaterialComponent(XRMaterial quadMaterial, bool flipVerticalUVCoord = false)
        {
            _flipVerticalUVCoord = flipVerticalUVCoord;
            RenderPass = quadMaterial.RenderPass;
            quadMaterial.RenderOptions = _renderParameters;
            RemakeMesh(quadMaterial);
        }

        private bool _flipVerticalUVCoord = false;
        public bool FlipVerticalUVCoord
        {
            get => _flipVerticalUVCoord;
            set => SetField(ref _flipVerticalUVCoord, value);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(FlipVerticalUVCoord):
                    RemakeMesh();
                    break;
            }
        }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
            RemakeMesh();
        }

        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
            Mesh?.Destroy();
            Mesh = null;
        }

        private void RemakeMesh()
        {
            if (Material is null)
            {
                var mat = XRMaterial.CreateUnlitColorMaterialForward(Color.Magenta);
                mat.RenderOptions = _renderParameters;
                Material = mat;
            }
            RemakeMesh(Material);
        }

        private void RemakeMesh(XRMaterial material)
        {
            Mesh?.Destroy();
            Mesh = new XRMeshRenderer(XRMesh.Create(VertexQuad.PosZ(1.0f, true, 0.0f, FlipVerticalUVCoord)), material);
        }

        private readonly RenderingParameters _renderParameters = new()
        {
            CullMode = ECullMode.None,
            DepthTest = new()
            {
                Enabled = ERenderParamUsage.Disabled,
                Function = EComparison.Always
            },
            BlendModeAllDrawBuffers = BlendMode.EnabledTransparent(),
        };

        public XRTexture? Texture(int index)
            => (Material?.Textures?.IndexInRange(index) ?? false)
                ? Material.Textures[index]
                : null;

        public T? Texture<T>(int index) where T : XRTexture
            => (Material?.Textures?.IndexInRange(index) ?? false)
                ? Material.Textures[index] as T
                : null;

        /// <summary>
        /// Retrieves the linked material's uniform parameter at the given index.
        /// Use this to set uniform values to be passed to the shader.
        /// </summary>
        public T2? Parameter<T2>(int index) where T2 : ShaderVar
            => Mesh?.Parameter<T2>(index);

        /// <summary>
        /// Retrieves the linked material's uniform parameter with the given name.
        /// Use this to set uniform values to be passed to the shader.
        /// </summary>
        public T2? Parameter<T2>(string name) where T2 : ShaderVar
            => Mesh?.Parameter<T2>(name);

        protected override Matrix4x4 GetRenderWorldMatrix(UIBoundableTransform tfm)
        {
            var w = tfm.ActualWidth;
            var h = tfm.ActualHeight;
            return Matrix4x4.CreateScale(w, h, 1.0f) * base.GetRenderWorldMatrix(tfm);
        }

        #region Batched Rendering

        /// <summary>
        /// Material quads support batching unless they have clip-to-bounds enabled
        /// or use textures (which require per-instance texture binds).
        /// </summary>
        public override bool SupportsBatchedRendering
            => !ClipToBounds && (Material?.Textures is null || Material.Textures.Count == 0);

        protected override bool RegisterWithBatchCollector(UIBatchCollector collector)
        {
            var tfm = BoundableTransform;
            var worldMatrix = GetRenderWorldMatrix(tfm);

            // Read the per-instance color from the material's MatColor parameter
            var colorParam = Material?.Parameter<ShaderVector4>("MatColor");
            var color = colorParam?.Value ?? new Vector4(1.0f, 0.0f, 1.0f, 1.0f);

            var bottomLeft = tfm.ActualLocalBottomLeftTranslation;
            var bounds = new Vector4(bottomLeft.X, bottomLeft.Y, tfm.ActualWidth, tfm.ActualHeight);

            collector.AddMaterialQuad(RenderPass, in worldMatrix, in color, in bounds);
            return true;
        }

        #endregion
    }
}
