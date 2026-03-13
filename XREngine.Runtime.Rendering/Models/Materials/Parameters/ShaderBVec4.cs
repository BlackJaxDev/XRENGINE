using System.ComponentModel;
using XREngine.Data.Vectors;

namespace XREngine.Rendering.Models.Materials
{
    public class ShaderBVector4 : ShaderVar
    {
        [Browsable(false)]
        public override EShaderVarType TypeName => EShaderVarType._bvec4;
        [Category(CategoryName)]
        public BoolVector4 Value { get => _value; set { _value = value; OnValueChanged(); } }
        protected override void SetProgramUniform(XRRenderProgram program, string location)
            => program.Uniform(location, _value);
        internal override string GetShaderValueString()
            => string.Format("bVector4({0}, {1}, {2}, {3})",
                _value.X.ToString().ToLowerInvariant(),
                _value.Y.ToString().ToLowerInvariant(),
                _value.Z.ToString().ToLowerInvariant(),
                _value.W.ToString().ToLowerInvariant());
        [Browsable(false)]
        public override object GenericValue => Value;

        private BoolVector4 _value;

        public ShaderBVector4() : this(new BoolVector4(), NoName) { }
        public ShaderBVector4(BoolVector4 defaultValue, string name)
            : this(defaultValue, name, null) { }
        public ShaderBVector4(BoolVector4 defaultValue, string name, IShaderVarOwner? owner) 
            : base(name, owner)
        {
            _value = defaultValue;
            _fields.Add(".x", new ShaderBool(defaultValue.X, "X", this));
            _fields.Add(".y", new ShaderBool(defaultValue.Y, "Y", this));
            _fields.Add(".z", new ShaderBool(defaultValue.Z, "Z", this));
            _fields.Add(".w", new ShaderBool(defaultValue.W, "W", this));
        }
    }
}
