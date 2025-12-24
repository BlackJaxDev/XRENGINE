using Silk.NET.OpenGL;
using XREngine.Data.Rendering;
using static XREngine.Rendering.OpenGL.OpenGLRenderer;

namespace XREngine.Rendering.OpenGL
{
    public class GLRenderQuery(OpenGLRenderer renderer, XRRenderQuery query) : GLObject<XRRenderQuery>(renderer, query)
    {
        protected override void LinkData()
        {

        }
        protected override void UnlinkData()
        {

        }
        public override EGLObjectType Type => EGLObjectType.Query;

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