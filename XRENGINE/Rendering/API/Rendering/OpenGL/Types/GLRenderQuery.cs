using Silk.NET.OpenGL;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using static XREngine.Rendering.OpenGL.OpenGLRenderer;

namespace XREngine.Rendering.OpenGL
{
    public enum ESamplerParameter
    {
        MinFilter,
        MagFilter,
        MinLod,
        MaxLod,
        WrapS,
        WrapT,
        WrapR,
        CompareMode,
        CompareFunc,
        BorderColor,
        LodBias,
    }
    public enum EMinFilter
    {
        Nearest,
        Linear,
        NearestMipmapNearest,
        LinearMipmapNearest,
        NearestMipmapLinear,
        LinearMipmapLinear,
    }
    public enum EMagFilter
    {
        Nearest,
        Linear,
    }
    public enum EWrapMode
    {
        Repeat,
        MirroredRepeat,
        ClampToEdge,
        ClampToBorder,
        MirrorClampToEdge,
    }
    public enum ECompareMode
    {
        None,
        CompareRefToTexture,
    }
    public enum ECompareFunc
    {
        Less,
        LessOrEqual,
        Greater,
        GreaterOrEqual,
        Equal,
        NotEqual,
        Always,
        Never,
    }

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
    public class GLRenderQuery(OpenGLRenderer renderer, XRRenderQuery query) : GLObject<XRRenderQuery>(renderer, query)
    {
        protected override void LinkData()
        {

        }
        protected override void UnlinkData()
        {

        }
        public override GLObjectType Type => GLObjectType.Query;

        public static GLEnum ToGLEnum(EQueryTarget target)
            => target switch
            {
                EQueryTarget.TimeElapsed => GLEnum.TimeElapsed,
                EQueryTarget.SamplesPassed => GLEnum.SamplesPassed,
                EQueryTarget.AnySamplesPassed => GLEnum.AnySamplesPassed,
                EQueryTarget.PrimitivesGenerated => GLEnum.PrimitivesGenerated,
                EQueryTarget.TransformFeedbackPrimitivesWritten => GLEnum.TransformFeedbackPrimitivesWritten,
                EQueryTarget.AnySamplesPassedConservative => GLEnum.AnySamplesPassedConservative,
                EQueryTarget.Timestamp => GLEnum.Timestamp,
                _ => GLEnum.TimeElapsed
            };

        public void BeginQuery(EQueryTarget target)
        {
            if (Data.CurrentQuery != null)
                EndQuery();

            Data.CurrentQuery = target;
            Api.BeginQuery(ToGLEnum(target), BindingId);
        }

        public void EndQuery()
        {
            if (Data.CurrentQuery is null)
                return;

            Api.EndQuery(ToGLEnum(Data.CurrentQuery.Value));
            Data.CurrentQuery = null;
        }

        public long EndAndGetQuery()
        {
            EndQuery();
            return GetQueryObject(EGetQueryObject.QueryResult);
        }

        public void QueryCounter()
        {
            if (Data.CurrentQuery == EQueryTarget.Timestamp)
                Api.QueryCounter(BindingId, ToGLEnum(Data.CurrentQuery.Value));
        }

        public long GetQueryObject(EGetQueryObject obj)
            => Api.GetQueryObject(BindingId, ToGLEnum(obj));

        public static GLEnum ToGLEnum(EGetQueryObject obj)
            => obj switch
            {
                EGetQueryObject.QueryResult => GLEnum.QueryResult,
                EGetQueryObject.QueryResultAvailable => GLEnum.QueryResultAvailable,
                EGetQueryObject.QueryResultNoWait => GLEnum.QueryResultNoWait,
                _ => GLEnum.QueryResult
            };

        public void AwaitResult()
        {
            long result = 0L;
            while (result == 0)
                result = GetQueryObject(EGetQueryObject.QueryResultAvailable);
        }

        public void AwaitResult(Action<GLRenderQuery> onReady)
        {
            switch (onReady)
            {
                case null:
                    AwaitResult();
                    break;
                default:
                    Task.Run(() => AwaitResult()).ContinueWith(t => onReady(this));
                    break;
            }
        }

        public async Task AwaitResultAsync()
            => await Task.Run(() => AwaitResult());

        public async Task<long> AwaitLongResultAsync()
        {
            await Task.Run(() => AwaitResult());
            return GetQueryObject(EGetQueryObject.QueryResult);
        }
    }
}