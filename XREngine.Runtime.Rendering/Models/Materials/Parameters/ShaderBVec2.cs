using System.ComponentModel;
using XREngine.Data.Vectors;

namespace XREngine.Rendering.Models.Materials
{
    public class ShaderBVector2 : ShaderVar
    {
        [Browsable(false)]
        public override EShaderVarType TypeName => EShaderVarType._bvec2;
        [Category(CategoryName)]
        public BoolVector2 Value { get => _value; set { _value = value; OnValueChanged(); } }
        protected override void SetProgramUniform(XRRenderProgram program, string location)
            => program.Uniform(location, _value);
        internal override string GetShaderValueString()
            => string.Format("bVector2({0}, {1})", _value.X, _value.Y);
        [Browsable(false)]
        public override object GenericValue => Value;

        private BoolVector2 _value;

        public ShaderBVector2() : this(new BoolVector2(), NoName) { }
        public ShaderBVector2(BoolVector2 defaultValue, string name)
            : this(defaultValue, name, null) { }
        public ShaderBVector2(BoolVector2 defaultValue, string name, IShaderVarOwner? owner) 
            : base(name, owner)
        {
            _value = defaultValue;
            _fields.Add(".x", new ShaderBool(defaultValue.X, "X", this));
            _fields.Add(".y", new ShaderBool(defaultValue.Y, "Y", this));
        }
    }
}
