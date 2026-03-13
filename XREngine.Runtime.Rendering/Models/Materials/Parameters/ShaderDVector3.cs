using System.ComponentModel;
using XREngine.Data.Vectors;

namespace XREngine.Rendering.Models.Materials
{
    public class ShaderDVector3 : ShaderVar
    {
        [Browsable(false)]
        public override EShaderVarType TypeName => EShaderVarType._dvec3;
        [Category(CategoryName)]
        public DVector3 Value { get => _value; set { _value = value; OnValueChanged(); } }
        protected override void SetProgramUniform(XRRenderProgram program, string location)
            => program.Uniform(location, _value);
        internal override string GetShaderValueString()
            => $"dVector3({_value.X:0.0######}, {_value.Y:0.0######}, {_value.Z:0.0######})";
        [Browsable(false)]
        public override object GenericValue => Value;

        private DVector3 _value;

        public ShaderDVector3() : this(new DVector3(), NoName) { }
        public ShaderDVector3(DVector3 defaultValue, string name)
            : this(defaultValue, name, null) { }
        public ShaderDVector3(DVector3 defaultValue, string name, IShaderVarOwner? owner)
            : base(name, owner)
        {
            _value = defaultValue;
            _fields.Add(".x", new ShaderDouble(defaultValue.X, "X", this));
            _fields.Add(".y", new ShaderDouble(defaultValue.Y, "Y", this));
            _fields.Add(".z", new ShaderDouble(defaultValue.Z, "Z", this));
        }
    }
}
