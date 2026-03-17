using System.Numerics;

namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_PushShaderGlobals : ViewportStateRenderCommand<VPRC_PopShaderGlobals>
    {
        public Dictionary<string, bool> BoolUniforms { get; } = [];
        public Dictionary<string, int> IntUniforms { get; } = [];
        public Dictionary<string, uint> UIntUniforms { get; } = [];
        public Dictionary<string, float> FloatUniforms { get; } = [];
        public Dictionary<string, Vector2> Vector2Uniforms { get; } = [];
        public Dictionary<string, Vector3> Vector3Uniforms { get; } = [];
        public Dictionary<string, Vector4> Vector4Uniforms { get; } = [];
        public Dictionary<string, Matrix4x4> Matrix4Uniforms { get; } = [];

        protected override void Execute()
        {
            var globals = new XRRenderPipelineInstance.RenderingState.ScopedShaderGlobals();

            Copy(BoolUniforms, globals.BoolUniforms);
            Copy(IntUniforms, globals.IntUniforms);
            Copy(UIntUniforms, globals.UIntUniforms);
            Copy(FloatUniforms, globals.FloatUniforms);
            Copy(Vector2Uniforms, globals.Vector2Uniforms);
            Copy(Vector3Uniforms, globals.Vector3Uniforms);
            Copy(Vector4Uniforms, globals.Vector4Uniforms);
            Copy(Matrix4Uniforms, globals.Matrix4Uniforms);

            ActivePipelineInstance.RenderState.PushShaderGlobals(globals);
        }

        private static void Copy<T>(Dictionary<string, T> source, Dictionary<string, T> destination) where T : struct
        {
            foreach (var pair in source)
                destination[pair.Key] = pair.Value;
        }
    }
}