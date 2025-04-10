using Extensions;
using System.Drawing;
using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.UI
{
    /// <summary>
    /// A basic UI component that renders a quad with a material.
    /// </summary>
    public class UIMaterialComponent : UIRenderableComponent
    {
        public UIMaterialComponent() 
            : this(XRMaterial.CreateUnlitColorMaterialForward(Color.Magenta)) { }
        public UIMaterialComponent(XRMaterial quadMaterial, bool flipVerticalUVCoord = false) : base()
            => Mesh = new XRMeshRenderer(XRMesh.Create(VertexQuad.PosZ(1.0f, true, 0.0f, flipVerticalUVCoord)), quadMaterial);

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
            var w = tfm.GetWidth();
            var h = tfm.GetHeight();
            return Matrix4x4.CreateScale(w, h, 1.0f) * base.GetRenderWorldMatrix(tfm);
        }
    }
}
