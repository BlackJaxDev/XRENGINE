using System.ComponentModel;

namespace XREngine.Rendering.Models.Materials
{
    public class ShaderUInt : ShaderVar<uint>,
        IUniformable,
        IShaderUnsignedIntType,
        IShaderUnsignedType,
        IShaderNonDecimalType, 
        IShaderNumericType,
        IShaderNonVectorType
    {
        [Browsable(false)]
        public override EShaderVarType TypeName => EShaderVarType._uint;

        protected override void SetProgramUniform(XRRenderProgram program, string location)
            => program.Uniform(location, Value);

        [Browsable(false)]
        public unsafe uint* Data { get { fixed (uint* ptr = &_value) return ptr; } }

        public ShaderUInt()
            : this(0u, NoName) { }
        public ShaderUInt(uint defaultValue, string name) 
            : this(defaultValue, name, null) { }
        public ShaderUInt(uint defaultValue, string name, IShaderVarOwner? owner)
            : base(defaultValue, name, owner) { }
    }
}
