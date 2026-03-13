using System.ComponentModel;

namespace XREngine.Rendering.Models.Materials
{
    public class ShaderInt(int defaultValue, string name, IShaderVarOwner? owner) : ShaderVar<int>(defaultValue, name, owner),
        IUniformable,
        IShaderSignedIntType, 
        IShaderNonVectorType,
        IShaderNumericType,
        IShaderNonDecimalType,
        IShaderSignedType
    {
        [Browsable(false)]
        public override EShaderVarType TypeName => EShaderVarType._int;

        protected override void SetProgramUniform(XRRenderProgram program, string location)
            => program.Uniform(location, Value);

        [Browsable(false)]
        public unsafe int* Data { get { fixed (int* ptr = &_value) return ptr; } }

        public ShaderInt()
            : this(0, NoName) { }
        public ShaderInt(int defaultValue, string name)
            : this(defaultValue, name, null) { }
    }
}
