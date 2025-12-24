using Silk.NET.OpenGL;
using XREngine.Data.Rendering;
using static XREngine.Rendering.OpenGL.OpenGLRenderer;

namespace XREngine.Rendering.OpenGL
{
    public class GLTransformFeedback(OpenGLRenderer renderer, XRTransformFeedback data) : GLObject<XRTransformFeedback>(renderer, data)
    {
        protected override void LinkData()
        {

        }
        protected override void UnlinkData()
        {

        }
        public override EGLObjectType Type => EGLObjectType.TransformFeedback;
        
        public void Bind()
            => Api.BindTransformFeedback(GLEnum.TransformFeedback, BindingId);

        public void Begin()
            => Api.BeginTransformFeedback(GLEnum.Points);

        public void End()
            => Api.EndTransformFeedback();

        //TODO: Implement these functions
        //public void F()
        //{
        //    Api.TransformFeedbackBufferBase(GLEnum.TransformFeedback, 0, FeedbackBuffer.BufferId);
        //    Api.TransformFeedbackVaryings(GLEnum.TransformFeedback, Names.Length, Names, GLEnum.InterleavedAttribs);
        //    Api.TransformFeedbackBufferRange(GLEnum.TransformFeedback, 0, FeedbackBuffer.BufferId, 0, FeedbackBuffer.Size);
        //    Api.DrawTransformFeedback(GLEnum.Points, FeedbackBuffer.BufferId);
        //    Api.DrawTransformFeedbackInstanced(GLEnum.Points, FeedbackBuffer.BufferId, 1);
        //    Api.DrawTransformFeedbackStream(GLEnum.Points, FeedbackBuffer.BufferId, 0);
        //    Api.DrawTransformFeedbackStreamInstanced(GLEnum.Points, FeedbackBuffer.BufferId, 0, 1);
        //    Api.GetTransformFeedbackVarying(GLEnum.TransformFeedback, 0, out _, out _, out _, out _);
        //    Api.GetTransformFeedback(0, 0, 0);
        //    Api.GetTransformFeedbacki64(GLEnum.TransformFeedback, GLEnum.TransformFeedbackBufferBinding, 0);
        //}

        public void Pause()
            => Api.PauseTransformFeedback();

        public void Resume()
            => Api.ResumeTransformFeedback();

        public void Unbind(EFramebufferTarget type)
            => Api.BindTransformFeedback(GLEnum.TransformFeedback, 0);
    }
}