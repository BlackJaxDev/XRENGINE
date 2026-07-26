namespace XREngine.Rendering.Models.Materials.Functions
{
    [XREngine.Core.Attributes.XRTypeRedirect("XREngine.Rendering.Models.Materials.Functions.MatFuncOverload")]
    public class MatFuncOverload
    {
        public EGLSLVersion Version { get; }
        public EGenShaderVarType[] Inputs { get; }
        public EGenShaderVarType[] Outputs { get; }
        public MatFuncOverload(EGLSLVersion version, EGenShaderVarType[] outputs, EGenShaderVarType[] inputs)
        {
            Version = version;
            Inputs = inputs;
            Outputs = outputs;
        }
        public MatFuncOverload(EGLSLVersion version, EGenShaderVarType output, EGenShaderVarType[] inputs)
        {
            Version = version;
            Inputs = inputs;
            Outputs = [output];
        }
        public MatFuncOverload(EGLSLVersion version, EGenShaderVarType[] inout)
        {
            Version = version;
            Inputs = inout;
            Outputs = inout;
        }
        public MatFuncOverload(EGLSLVersion version, EGenShaderVarType inout)
        {
            Version = version;
            Inputs = [inout];
            Outputs = [inout];
        }
        public MatFuncOverload(EGLSLVersion version, EGenShaderVarType[] inOrOutOnly, bool isIn)
        {
            Version = version;
            Inputs = isIn ? inOrOutOnly : [];
            Outputs = !isIn ? inOrOutOnly : [];
        }
        public MatFuncOverload(EGLSLVersion version, EGenShaderVarType inOrOutOnly, bool isIn)
        {
            Version = version;
            Inputs = isIn ? [inOrOutOnly] : [];
            Outputs = !isIn ? [inOrOutOnly] : [];
        }
    }
}
