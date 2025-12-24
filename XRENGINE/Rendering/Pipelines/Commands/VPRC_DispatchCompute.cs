using XREngine.Core.Files;
using static XREngine.Rendering.XRRenderProgram;

namespace XREngine.Rendering.Pipelines.Commands
{
    public class VPRC_DispatchCompute : ViewportRenderCommand
    {
        private readonly XRRenderProgram _computeProgram = new(false, false);

        private static uint GetOne() => 1u;

        public Func<uint> X { get; set; } = GetOne;
        public Func<uint> Y { get; set; } = GetOne;
        public Func<uint> Z { get; set; } = GetOne;

        private TextFile? _computeShaderCode;
        public TextFile? ComputeShaderCode
        {
            get => _computeShaderCode;
            set => SetField(ref _computeShaderCode, value);
        }

        public List<ComputeTextureBinding>? Textures { get; set; }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(ComputeShaderCode):
                    _computeProgram.Shaders.Clear();
                    if (ComputeShaderCode is not null)
                        _computeProgram.Shaders.Add(new XRShader(EShaderType.Compute, ComputeShaderCode));
                    break;
            }
        }

        protected override void Execute()
        {
            if (_computeProgram.Shaders.Count <= 0)
                return;

            var textures = Textures?.Select(binding => (binding.Unit, binding.TextureFactory(), binding.Level, binding.Layer, binding.Access, binding.Format));
            _computeProgram.DispatchCompute(X(), Y(), Z(), textures);
        }

        public void SetOptions(string computeCode, Func<uint>? x = null, Func<uint>? y = null, Func<uint>? z = null, List<ComputeTextureBinding>? textures = null)
        {
            ComputeShaderCode = TextFile.FromText(computeCode);
            X = x ?? GetOne;
            Y = y ?? GetOne;
            Z = z ?? GetOne;
            Textures = textures;
        }
        public void SetOptions(TextFile computeCode, Func<uint>? x = null, Func<uint>? y = null, Func<uint>? z = null, List<ComputeTextureBinding>? textures = null)
        {
            ComputeShaderCode = computeCode;
            X = x ?? GetOne;
            Y = y ?? GetOne;
            Z = z ?? GetOne;
            Textures = textures;
        }
    }

    public record struct ComputeTextureBinding(uint Unit, Func<XRTexture> TextureFactory, int Level, int? Layer, EImageAccess Access, EImageFormat Format)
    {
        public static implicit operator (uint unit, Func<XRTexture> texture, int level, int? layer, EImageAccess access, EImageFormat format)(ComputeTextureBinding value)
            => (value.Unit, value.TextureFactory, value.Level, value.Layer, value.Access, value.Format);
        public static implicit operator ComputeTextureBinding((uint unit, Func<XRTexture> texture, int level, int? layer, EImageAccess access, EImageFormat format) value)
            => new(value.unit, value.texture, value.level, value.layer, value.access, value.format);
    }
}
