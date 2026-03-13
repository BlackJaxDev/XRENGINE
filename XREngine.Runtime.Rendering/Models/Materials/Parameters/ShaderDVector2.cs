using System.ComponentModel;
using XREngine.Data.Vectors;

namespace XREngine.Rendering.Models.Materials
{
    public class ShaderDVector2 : ShaderVar
    {
        [Browsable(false)]
        public override EShaderVarType TypeName => EShaderVarType._dvec2;
        [Category(CategoryName)]
        public DVector2 Value { get => _value; set { _value = value; OnValueChanged(); } }
        protected override void SetProgramUniform(XRRenderProgram program, string location)
            => program.Uniform(location, _value);
        internal override string GetShaderValueString()
            => $"dVector2({_value.X:0.0######}, {_value.Y:0.0######})";
        [Browsable(false)]
        public override object GenericValue => Value;

        private DVector2 _value;

        public ShaderDVector2() : this(new DVector2(), NoName) { }
        public ShaderDVector2(DVector2 defaultValue, string name)
            : this(defaultValue, name, null) { }
        public ShaderDVector2(DVector2 defaultValue, string name, IShaderVarOwner? owner)
            : base(name, owner)
        {
            _value = defaultValue;
            _fields.Add(".x", new ShaderDouble(defaultValue.X, "X", this));
            _fields.Add(".y", new ShaderDouble(defaultValue.Y, "Y", this));
        }
    }
}
