using Extensions;
using System.ComponentModel;
using System.Numerics;
using XREngine.Core;
using Color = System.Drawing.Color;

namespace XREngine.Rendering.Models.Materials
{
    public class ShaderVector4 : ShaderVar<Vector4>
    {
        [Browsable(false)]
        public override EShaderVarType TypeName => EShaderVarType._vec4;

        protected override void SetProgramUniform(XRRenderProgram program, string location) 
            => program.Uniform(location, Value);

        internal override string GetShaderValueString()
            => $"Vector4({_value.X:0.0######}f, {_value.Y:0.0######}f, {_value.Z:0.0######}f, {_value.W:0.0######}f)";

        public ShaderVector4()
            : this(new Vector4(), NoName) { }
        public ShaderVector4(Vector4 defaultValue, string name)
            : this(defaultValue, name, null) { }
        public ShaderVector4(Vector4 defaultValue, string name, IShaderVarOwner? owner)
            : base(defaultValue, name, owner)
        {
            _fields.Add(".x", new ShaderFloat(defaultValue.X, "X", this));
            _fields.Add(".y", new ShaderFloat(defaultValue.Y, "Y", this));
            _fields.Add(".z", new ShaderFloat(defaultValue.Z, "Z", this));
            _fields.Add(".w", new ShaderFloat(defaultValue.W, "W", this));
        }

        public Color Color
        {
            get => (Color)this;
            set => Value = value.ToVector4();
        }
        
        public ShaderVector4(float x, float y, float z, float w)
            : this(x, y, z, w, NoName) { }
        public ShaderVector4(float x, float y, float z, float w, string name)
            : this(x, y, z, w, name, null) { }
        public ShaderVector4(float x, float y, float z, float w, string name, IShaderVarOwner? owner)
            : this(new Vector4(x, y, z, w), name, owner) { }

        public static implicit operator ShaderVector4(Color p)
            => new(p.R * Utility.ByteToFloat, p.G * Utility.ByteToFloat, p.B * Utility.ByteToFloat, p.A * Utility.ByteToFloat);
        public static explicit operator Color(ShaderVector4 p)
            => Color.FromArgb(p.Value.W.ToByte(), p.Value.X.ToByte(), p.Value.Y.ToByte(), p.Value.Z.ToByte());
    }
}
