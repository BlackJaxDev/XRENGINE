using System.ComponentModel;
using XREngine.Data.Vectors;

namespace XREngine.Rendering.Models.Materials
{
    public class ShaderDVector4 : ShaderVar
    {
        [Browsable(false)]
        public override EShaderVarType TypeName => EShaderVarType._dvec4;
        [Category(CategoryName)]
        public DVector4 Value { get => _value; set { _value = value; OnValueChanged(); } }
        protected override void SetProgramUniform(XRRenderProgram program, string location)
            => program.Uniform(location, _value);
        internal override string GetShaderValueString()
            => $"dVector4({_value.X:0.0######}, {_value.Y:0.0######}, {_value.Z:0.0######}, {_value.W:0.0######})";
        [Browsable(false)]
        public override object GenericValue => Value;

        private DVector4 _value;

        public ShaderDVector4() : this(new DVector4(), NoName) { }
        public ShaderDVector4(DVector4 defaultValue, string name)
            : this(defaultValue, name, null) { }
        public ShaderDVector4(DVector4 defaultValue, string name, IShaderVarOwner? owner)
            : base(name, owner)
        {
            _value = defaultValue;
            _fields.Add(".x", new ShaderDouble(defaultValue.X, "X", this));
            _fields.Add(".y", new ShaderDouble(defaultValue.Y, "Y", this));
            _fields.Add(".z", new ShaderDouble(defaultValue.Z, "Z", this));
            _fields.Add(".w", new ShaderDouble(defaultValue.W, "W", this));
        }
    }
}
