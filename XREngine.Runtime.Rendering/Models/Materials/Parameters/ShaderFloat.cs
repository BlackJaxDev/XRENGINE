using System.ComponentModel;

namespace XREngine.Rendering.Models.Materials
{
    public class ShaderFloat(float defaultValue, string name, IShaderVarOwner? owner) : ShaderVar<float>(defaultValue, name, owner),
        IUniformable,
        IShaderFloatType,
        IShaderNonVectorType,
        IShaderNumericType,
        IShaderDecimalType,
        IShaderSignedType
    {
        [Browsable(false)]
        public override EShaderVarType TypeName => EShaderVarType._float;

        protected override void SetProgramUniform(XRRenderProgram program, string location)
            => program.Uniform(location, Value);

        [Browsable(false)]
        public unsafe float* Data { get { fixed (float* ptr = &_value) return ptr; } }

        internal override string GetShaderValueString() 
            => $"{Value:0.0######}f";

        public ShaderFloat() : this(0.0f, NoName) { }
        public ShaderFloat(float defaultValue, string name) 
            : this(defaultValue, name, null) { }
    }
}
