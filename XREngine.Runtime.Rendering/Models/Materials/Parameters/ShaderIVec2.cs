using System.ComponentModel;
using XREngine.Data.Vectors;

namespace XREngine.Rendering.Models.Materials
{
    public class ShaderIVector2 : ShaderVar
    {
        [Browsable(false)]
        public override EShaderVarType TypeName => EShaderVarType._ivec2;
        [Category(CategoryName)]
        public IVector2 Value { get => _value; set { _value = value; OnValueChanged(); } }
        protected override void SetProgramUniform(XRRenderProgram program, string location)
            => program.Uniform(location, _value);
        internal override string GetShaderValueString()
            => string.Format("iVector2({0}, {1})", _value.X.ToString(), _value.Y.ToString());
        [Browsable(false)]
        public override object GenericValue => Value;

        private IVector2 _value;

        public ShaderIVector2() : this(new IVector2(), NoName) { }
        public ShaderIVector2(IVector2 defaultValue, string name)
            : this(defaultValue, name, null) { }
        public ShaderIVector2(IVector2 defaultValue, string name, IShaderVarOwner? owner)
            : base(name, owner)
        {
            _value = defaultValue;
            _fields.Add(".x", new ShaderInt(defaultValue.X, "X", this));
            _fields.Add(".y", new ShaderInt(defaultValue.Y, "Y", this));
        }
    }
}
