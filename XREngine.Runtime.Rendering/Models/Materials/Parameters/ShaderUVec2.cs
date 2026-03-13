using System.ComponentModel;
using XREngine.Data.Vectors;

namespace XREngine.Rendering.Models.Materials
{
    public class ShaderUVector2 : ShaderVar
    {
        [Browsable(false)]
        public override EShaderVarType TypeName => EShaderVarType._uvec2;
        [Category(CategoryName)]
        public UVector2 Value { get => _value; set { _value = value; OnValueChanged(); } }
        protected override void SetProgramUniform(XRRenderProgram program, string location)
            => program.Uniform(location, _value);
        internal override string GetShaderValueString()
            => string.Format("uVector2({0}, {1})", _value.X.ToString(), _value.Y.ToString());
        [Browsable(false)]
        public override object GenericValue => Value;

        private UVector2 _value;

        public ShaderUVector2() : this(new UVector2(), NoName) { }
        public ShaderUVector2(UVector2 defaultValue, string name)
            : this(defaultValue, name, null) { }
        public ShaderUVector2(UVector2 defaultValue, string name, IShaderVarOwner? owner)
            : base(name, owner)
        {
            _value = defaultValue;
            _fields.Add(".x", new ShaderUInt(defaultValue.X, "X", this));
            _fields.Add(".y", new ShaderUInt(defaultValue.Y, "Y", this));
        }
    }
}
