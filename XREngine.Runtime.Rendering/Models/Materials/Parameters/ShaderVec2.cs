using System.ComponentModel;
using System.Numerics;

namespace XREngine.Rendering.Models.Materials
{
    public class ShaderVector2 : ShaderVar<Vector2>
    {
        [Browsable(false)]
        public override EShaderVarType TypeName => EShaderVarType._vec2;

        protected override void SetProgramUniform(XRRenderProgram program, string location)
            => program.Uniform(location, Value);

        internal override string GetShaderValueString()
             => $"Vector2({_value.X:0.0######}f, {_value.Y:0.0######}f)";

        public ShaderVector2()
            : this(new Vector2(), NoName) { }
        public ShaderVector2(Vector2 defaultValue, string name)
            : this(defaultValue, name, null) { }
        public ShaderVector2(Vector2 defaultValue, string name, IShaderVarOwner? owner)
            : base(defaultValue, name, owner)
        {
            _fields.Add(".x", new ShaderFloat(defaultValue.X, "X", this));
            _fields.Add(".y", new ShaderFloat(defaultValue.Y, "Y", this));
        }
    }
}
