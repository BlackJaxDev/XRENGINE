using System.ComponentModel;

namespace XREngine.Rendering.Models.Materials
{
    public class ShaderBool : ShaderVar<bool>, 
        IUniformable,
        IShaderBooleanType,
        IShaderNonVectorType
    {
        [Browsable(false)]
        public override EShaderVarType TypeName => EShaderVarType._bool;

        protected override void SetProgramUniform(XRRenderProgram program, string location)
            => program.Uniform(location, Value);

        [Browsable(false)]
        public unsafe bool* Data { get { fixed (bool* ptr = &_value) return ptr; } }

        internal override string GetShaderValueString()
            => Value.ToString().ToLowerInvariant();

        public ShaderBool()
            : this(false, NoName) { }
        public ShaderBool(string name)
            : this(false, name, null) { }
        public ShaderBool(bool defaultValue, string name)
            : this(defaultValue, name, null) { }
        public ShaderBool(bool defaultValue, string name, IShaderVarOwner? owner)
            : base(name, owner) => Value = defaultValue;
    }
}
