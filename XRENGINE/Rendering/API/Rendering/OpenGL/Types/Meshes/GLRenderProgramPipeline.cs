using Silk.NET.OpenGL;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        public class GLRenderProgramPipeline(OpenGLRenderer renderer, XRRenderProgramPipeline data) : GLObject<XRRenderProgramPipeline>(renderer, data)
        {
            private bool _validationLogged;

            public override EGLObjectType Type => EGLObjectType.ProgramPipeline;

            public void Bind()
                => Api.BindProgramPipeline(BindingId);
            public void Set(EProgramStageMask mask, GLRenderProgram program)
                => Api.UseProgramStages(BindingId, ToUseProgramStageMask(mask), program.BindingId);
            public void Clear(EProgramStageMask mask)
                => Api.UseProgramStages(BindingId, ToUseProgramStageMask(mask), 0);
            public void SetActive(GLRenderProgram? program)
                => Api.ActiveShaderProgram(BindingId, program?.BindingId ?? 0);

            /// <summary>
            /// Validates the program pipeline and logs any errors once.
            /// OpenGL separable program pipelines can silently fail if stage interfaces don't match;
            /// this surfaces those issues.
            /// </summary>
            public void Validate()
            {
                if (_validationLogged)
                    return;

                Api.ValidateProgramPipeline(BindingId);

                // GL_VALIDATE_STATUS = 0x8B83, GL_INFO_LOG_LENGTH = 0x8B84
                // Silk.NET PipelineParameterName may not expose these directly.
                int status = 0;
                Api.GetProgramPipeline(BindingId, (PipelineParameterName)0x8B83, out status);
                if (status != 0)
                    return; // valid

                int logLength = 0;
                Api.GetProgramPipeline(BindingId, (PipelineParameterName)0x8B84, out logLength);

                _validationLogged = true;

                if (logLength > 0)
                {
                    byte* buf = stackalloc byte[logLength];
                    uint actualLen;
                    Api.GetProgramPipelineInfoLog(BindingId, (uint)logLength, &actualLen, buf);
                    string infoLog = System.Text.Encoding.UTF8.GetString(buf, (int)actualLen);
                    Debug.OpenGLWarning($"[PipelineValidation] pipeline {BindingId}: {infoLog}");
                }
                else
                {
                    Debug.OpenGLWarning($"[PipelineValidation] pipeline {BindingId} failed validation with no info log");
                }
            }

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