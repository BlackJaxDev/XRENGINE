using System.Numerics;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_PushShaderGlobals : ViewportStateRenderCommand<VPRC_PopShaderGlobals>
    {
        private readonly XRRenderPipelineInstance.RenderingState.ScopedShaderGlobals _runtimeGlobals = new();

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
            Copy(BoolUniforms, _runtimeGlobals.BoolUniforms);
            Copy(IntUniforms, _runtimeGlobals.IntUniforms);
            Copy(UIntUniforms, _runtimeGlobals.UIntUniforms);
            Copy(FloatUniforms, _runtimeGlobals.FloatUniforms);
            Copy(Vector2Uniforms, _runtimeGlobals.Vector2Uniforms);
            Copy(Vector3Uniforms, _runtimeGlobals.Vector3Uniforms);
            Copy(Vector4Uniforms, _runtimeGlobals.Vector4Uniforms);
            Copy(Matrix4Uniforms, _runtimeGlobals.Matrix4Uniforms);

            ActivePipelineInstance.RenderState.PushShaderGlobalsState(_runtimeGlobals);
        }

        private static void Copy<T>(Dictionary<string, T> source, Dictionary<string, T> destination) where T : struct
        {
            destination.Clear();
            foreach (var pair in source)
                destination[pair.Key] = pair.Value;
        }
    }
}
