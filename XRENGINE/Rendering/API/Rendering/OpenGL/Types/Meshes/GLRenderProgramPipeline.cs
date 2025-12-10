using Silk.NET.OpenGL;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        public class GLRenderProgramPipeline(OpenGLRenderer renderer, XRRenderProgramPipeline data) : GLObject<XRRenderProgramPipeline>(renderer, data)
        {
            public override EGLObjectType Type => EGLObjectType.ProgramPipeline;

            public void Bind()
                => Api.BindProgramPipeline(BindingId);
            public void Set(EProgramStageMask mask, GLRenderProgram program)
                => Api.UseProgramStages(BindingId, ToUseProgramStageMask(mask), program.BindingId);
            public void Clear(EProgramStageMask mask)
                => Api.UseProgramStages(BindingId, ToUseProgramStageMask(mask), 0);
            public void SetActive(GLRenderProgram? program)
                => Api.ActiveShaderProgram(BindingId, program?.BindingId ?? 0);

            public static UseProgramStageMask ToUseProgramStageMask(EProgramStageMask mask)
                => mask switch
                {
                    EProgramStageMask.VertexShaderBit => UseProgramStageMask.VertexShaderBit,
                    EProgramStageMask.TessControlShaderBit => UseProgramStageMask.TessControlShaderBit,
                    EProgramStageMask.TessEvaluationShaderBit => UseProgramStageMask.TessEvaluationShaderBit,
                    EProgramStageMask.GeometryShaderBit => UseProgramStageMask.GeometryShaderBit,
                    EProgramStageMask.FragmentShaderBit => UseProgramStageMask.FragmentShaderBit,
                    EProgramStageMask.ComputeShaderBit => UseProgramStageMask.ComputeShaderBit,
                    EProgramStageMask.MeshShaderBit => (UseProgramStageMask)64,
                    EProgramStageMask.TaskShaderBit => (UseProgramStageMask)128,
                    _ => 0,
                };

            protected override void LinkData()
            {

            }
            protected override void UnlinkData()
            {

            }
        }
    }
}