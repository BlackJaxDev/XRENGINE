using Silk.NET.OpenGL;
using XREngine.Data.Colors;
using static XREngine.Rendering.OpenGL.OpenGLRenderer;

namespace XREngine.Rendering.OpenGL
{
    public class GLSampler(OpenGLRenderer renderer, XRSampler sampler) : GLObject<XRSampler>(renderer, sampler)
    {
        protected override void LinkData()
        {

        }
        protected override void UnlinkData()
        {

        }
        public override GLObjectType Type => GLObjectType.Sampler;

        public void SetParameter(ESamplerParameter parameter, int value)
            => Api.SamplerParameter(BindingId, ToGLEnum(parameter), value);

        public void SetParameter(ESamplerParameter parameter, float value)
            => Api.SamplerParameter(BindingId, ToGLEnum(parameter), value);

        public void SetMinLod(float value)
            => Api.SamplerParameter(BindingId, ToGLEnum(ESamplerParameter.MinLod), value);

        public void SetMaxLod(float value)
            => Api.SamplerParameter(BindingId, ToGLEnum(ESamplerParameter.MaxLod), value);

        public unsafe void SetBorderColor(ColorF4 value)
            => Api.SamplerParameter(BindingId, ToGLEnum(ESamplerParameter.BorderColor), (float*)&value);

        public void SetLodBias(float value)
            => Api.SamplerParameter(BindingId, ToGLEnum(ESamplerParameter.LodBias), value);

        public void SetMinFilter(EMinFilter value)
            => Api.SamplerParameter(BindingId, ToGLEnum(ESamplerParameter.MinFilter), (int)ToGLEnum(value));

        private static GLEnum ToGLEnum(EMinFilter value)
            => value switch
            {
                EMinFilter.Nearest => GLEnum.Nearest,
                EMinFilter.Linear => GLEnum.Linear,
                EMinFilter.NearestMipmapNearest => GLEnum.NearestMipmapNearest,
                EMinFilter.LinearMipmapNearest => GLEnum.LinearMipmapNearest,
                EMinFilter.NearestMipmapLinear => GLEnum.NearestMipmapLinear,
                EMinFilter.LinearMipmapLinear => GLEnum.LinearMipmapLinear,
                _ => GLEnum.Nearest,
            };

        public void SetMagFilter(EMagFilter value)
            => Api.SamplerParameter(BindingId, ToGLEnum(ESamplerParameter.MagFilter), (int)ToGLEnum(value));

        private static GLEnum ToGLEnum(EMagFilter value)
            => value switch
            {
                EMagFilter.Nearest => GLEnum.Nearest,
                EMagFilter.Linear => GLEnum.Linear,
                _ => GLEnum.Nearest,
            };

        public void SetWrapS(EWrapMode value)
            => Api.SamplerParameter(BindingId, ToGLEnum(ESamplerParameter.WrapS), (int)ToGLEnum(value));

        public void SetWrapT(EWrapMode value)
            => Api.SamplerParameter(BindingId, ToGLEnum(ESamplerParameter.WrapT), (int)ToGLEnum(value));

        public void SetWrapR(EWrapMode value)
            => Api.SamplerParameter(BindingId, ToGLEnum(ESamplerParameter.WrapR), (int)ToGLEnum(value));

        private static GLEnum ToGLEnum(EWrapMode value)
            => value switch
            {
                EWrapMode.Repeat => GLEnum.Repeat,
                EWrapMode.MirroredRepeat => GLEnum.MirroredRepeat,
                EWrapMode.ClampToEdge => GLEnum.ClampToEdge,
                EWrapMode.ClampToBorder => GLEnum.ClampToBorder,
                EWrapMode.MirrorClampToEdge => GLEnum.MirrorClampToEdge,
                _ => GLEnum.Repeat,
            };

        public void SetCompareMode(bool compareRefToTexture)
            => Api.SamplerParameter(BindingId, ToGLEnum(ESamplerParameter.CompareMode), (int)(compareRefToTexture ? GLEnum.CompareRefToTexture : GLEnum.None));

        public void SetCompareFunc(ECompareFunc value)
            => Api.SamplerParameter(BindingId, ToGLEnum(ESamplerParameter.CompareFunc), (int)ToGLEnum(value));

        private static GLEnum ToGLEnum(ECompareFunc value)
            => value switch
            {
                ECompareFunc.Less => GLEnum.Less,
                ECompareFunc.LessOrEqual => GLEnum.Lequal,
                ECompareFunc.Greater => GLEnum.Greater,
                ECompareFunc.GreaterOrEqual => GLEnum.Gequal,
                ECompareFunc.Equal => GLEnum.Equal,
                ECompareFunc.NotEqual => GLEnum.Notequal,
                ECompareFunc.Always => GLEnum.Always,
                ECompareFunc.Never => GLEnum.Never,
                _ => GLEnum.Less,
            };

        private static GLEnum ToGLEnum(ESamplerParameter parameter)
            => parameter switch
            {
                ESamplerParameter.MinFilter => GLEnum.TextureMinFilter,
                ESamplerParameter.MagFilter => GLEnum.TextureMagFilter,
                ESamplerParameter.MinLod => GLEnum.TextureMinLod,
                ESamplerParameter.MaxLod => GLEnum.TextureMaxLod,
                ESamplerParameter.WrapS => GLEnum.TextureWrapS,
                ESamplerParameter.WrapT => GLEnum.TextureWrapT,
                ESamplerParameter.WrapR => GLEnum.TextureWrapR,
                ESamplerParameter.CompareMode => GLEnum.TextureCompareMode,
                ESamplerParameter.CompareFunc => GLEnum.TextureCompareFunc,
                ESamplerParameter.BorderColor => GLEnum.TextureBorderColor,
                ESamplerParameter.LodBias => GLEnum.TextureLodBias,
                _ => GLEnum.TextureMinFilter,
            };
    }
}