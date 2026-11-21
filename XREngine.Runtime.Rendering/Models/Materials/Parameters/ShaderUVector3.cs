using System.ComponentModel;
using XREngine.Data.Vectors;

namespace XREngine.Rendering.Models.Materials
{
    public class ShaderUVector3 : ShaderVar
    {
        [Browsable(false)]
        public override EShaderVarType TypeName => EShaderVarType._uvec3;
        [Category(CategoryName)]
        public UVector3 Value { get => _value; set { _value = value; OnValueChanged(); } }
        protected override void SetProgramUniform(XRRenderProgram program, string location)
            => program.Uniform(location, _value);
        internal override string GetShaderValueString()
            => $"uVector3({_value.X}, {_value.Y}, {_value.Z})";
        [Browsable(false)]
        public override object GenericValue => Value;

        private UVector3 _value;

        public ShaderUVector3() : this(new UVector3(), NoName) { }
        public ShaderUVector3(UVector3 defaultValue, string name)
            : this(defaultValue, name, null) { }
        public ShaderUVector3(UVector3 defaultValue, string name, IShaderVarOwner? owner)
            : base(name, owner)
        {
            _value = defaultValue;
            _fields.Add(".x", new ShaderUInt(defaultValue.X, "X", this));
            _fields.Add(".y", new ShaderUInt(defaultValue.Y, "Y", this));
            _fields.Add(".z", new ShaderUInt(defaultValue.Z, "Z", this));
        }
    }
}
