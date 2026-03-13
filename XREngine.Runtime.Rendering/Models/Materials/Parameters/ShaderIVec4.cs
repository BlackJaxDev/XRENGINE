using System.ComponentModel;
using XREngine.Data.Vectors;

namespace XREngine.Rendering.Models.Materials
{
    public class ShaderIVector4 : ShaderVar
    {
        [Browsable(false)]
        public override EShaderVarType TypeName => EShaderVarType._ivec4;
        [Category(CategoryName)]
        public IVector4 Value { get => _value; set { _value = value; OnValueChanged(); } }
        protected override void SetProgramUniform(XRRenderProgram program, string location) 
            => program.Uniform(location, _value);
        internal override string GetShaderValueString()
          => string.Format("iVector4({0}, {1}, {2}, {3})",
              _value.X.ToString(),
              _value.Y.ToString(),
              _value.Z.ToString(),
              _value.W.ToString());
        [Browsable(false)]
        public override object GenericValue => Value;

        private IVector4 _value;

        public ShaderIVector4() : this(new IVector4(), NoName) { }
        public ShaderIVector4(IVector4 defaultValue, string name)
            : this(defaultValue, name, null) { }
        public ShaderIVector4(IVector4 defaultValue, string name, IShaderVarOwner? owner)
            : base(name, owner)
        {
            _value = defaultValue;
            _fields.Add(".x", new ShaderInt(defaultValue.X, "X", this));
            _fields.Add(".y", new ShaderInt(defaultValue.Y, "Y", this));
            _fields.Add(".z", new ShaderInt(defaultValue.Z, "Z", this));
            _fields.Add(".w", new ShaderInt(defaultValue.W, "W", this));
        }
    }
}
