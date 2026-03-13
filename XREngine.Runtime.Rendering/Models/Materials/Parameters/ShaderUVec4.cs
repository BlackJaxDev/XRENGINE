using System.ComponentModel;
using XREngine.Data.Vectors;

namespace XREngine.Rendering.Models.Materials
{
    public class ShaderUVector4 : ShaderVar
    {
        [Browsable(false)]
        public override EShaderVarType TypeName => EShaderVarType._uvec4;
        [Category(CategoryName)]
        public UVector4 Value { get => _value; set { _value = value; OnValueChanged(); } }
        protected override void SetProgramUniform(XRRenderProgram program, string location) 
            => program.Uniform(location, _value);
        internal override string GetShaderValueString() 
            => string.Format("uVector4({0}, {1}, {2}, {3})",
              _value.X.ToString(),
              _value.Y.ToString(),
              _value.Z.ToString(),
              _value.W.ToString());
        [Browsable(false)]
        public override object GenericValue => Value;

        private UVector4 _value;

        public ShaderUVector4() : this(new UVector4(), NoName) { }
        public ShaderUVector4(UVector4 defaultValue, string name)
            : this(defaultValue, name, null) { }
        public ShaderUVector4(UVector4 defaultValue, string name, IShaderVarOwner? owner)
            : base(name, owner)
        {
            _value = defaultValue;
            _fields.Add(".x", new ShaderUInt(defaultValue.X, "X", this));
            _fields.Add(".y", new ShaderUInt(defaultValue.Y, "Y", this));
            _fields.Add(".z", new ShaderUInt(defaultValue.Z, "Z", this));
            _fields.Add(".w", new ShaderUInt(defaultValue.W, "W", this));
        }
    }
}
