using System.ComponentModel;
using XREngine.Data.Vectors;

namespace XREngine.Rendering.Models.Materials
{
    public class ShaderBVector3 : ShaderVar
    {
        [Browsable(false)]
        public override EShaderVarType TypeName => EShaderVarType._bvec3;
        [Category(CategoryName)]
        public BoolVector3 Value { get => _value; set { _value = value; OnValueChanged(); } }
        protected override void SetProgramUniform(XRRenderProgram program, string location)
            => program.Uniform(location, _value);
        internal override string GetShaderValueString()
              => string.Format("bVector3({0}, {1}, {2})",
                  _value.X.ToString().ToLowerInvariant(),
                  _value.Y.ToString().ToLowerInvariant(),
                  _value.Z.ToString().ToLowerInvariant());
        [Browsable(false)]
        public override object GenericValue => Value;

        private BoolVector3 _value;

        public ShaderBVector3() : this(new BoolVector3(), NoName) { }
        public ShaderBVector3(BoolVector3 defaultValue, string name)
            : this(defaultValue, name, null!) { }
        public ShaderBVector3(BoolVector3 defaultValue, string name, IShaderVarOwner? owner) 
            : base(name, owner)
        {
            _value = defaultValue;
            _fields.Add(".x", new ShaderBool(defaultValue.X, "X", this));
            _fields.Add(".y", new ShaderBool(defaultValue.Y, "Y", this));
            _fields.Add(".z", new ShaderBool(defaultValue.Z, "Z", this));
        }
    }
}
