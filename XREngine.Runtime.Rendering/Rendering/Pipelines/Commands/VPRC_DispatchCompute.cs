using XREngine.Core.Files;
using System.IO;
using static XREngine.Rendering.XRRenderProgram;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public class VPRC_DispatchCompute : ViewportRenderCommand
    {
        public override void DescribeRequirements(RenderPipelineRequirements requirements)
        {
            requirements.RequireOperation("compute");
            requirements.RequireProgram(_computeProgram);
            if (Textures is { Count: > 0 }) requirements.RequireOperation("storage-images");
        }

        private XRRenderProgram _computeProgram = new(false, false);

        /// <summary>
        /// Authoritative runtime program used by both dependency discovery and execution.
        /// Its enumerable shader interface is not a serializable program representation;
        /// the explicit shader collection and identity below preserve authoring data.
        /// </summary>
        [YamlDotNet.Serialization.YamlIgnore]
        public XRRenderProgram ComputeProgram
        {
            get => _computeProgram;
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                SetField(ref _computeProgram, value);
            }
        }

        [YamlDotNet.Serialization.YamlMember(Order = 1000)]
        public EventList<XRShader> ComputeShaders
        {
            get => _computeProgram.Shaders;
            set
            {
                if (ReferenceEquals(value, _computeProgram.Shaders))
                    return;
                _computeProgram.Shaders.Clear();
                if (value is not null)
                    _computeProgram.Shaders.AddRange(value);
            }
        }

        /// <summary>Exact whole-program companion, restored after shader collection hydration.</summary>
        [YamlDotNet.Serialization.YamlMember(Order = 1001)]
        public string? CookedProgramIdentity
        {
            get => _computeProgram.CookedArtifactIdentity;
            set => _computeProgram.CookedArtifactIdentity = value;
        }

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

        public override string GpuProfilingName
        {
            get
            {
                string? path = ComputeShaderCode?.FilePath;
                return string.IsNullOrWhiteSpace(path)
                    ? base.GpuProfilingName
                    : $"{base.GpuProfilingName}[{Path.GetFileName(path)}]";
            }
        }

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
            if (_computeProgram.Shaders.Count == 0 && _computeProgram.CookedArtifactIdentity is null)
                return;

            AbstractRenderer renderer = AbstractRenderer.Current
                ?? throw new InvalidOperationException("Compute dispatch requires an active renderer.");
            _ = renderer.GetOrCreateAPIRenderObject(_computeProgram, generateNow: true)
                ?? throw new InvalidOperationException("Compute dispatch requires a backend program wrapper.");
            ActivePipelineInstance.RenderState.ApplyScopedProgramBindings(_computeProgram);

            var textures = Textures?.Select(binding => (
                binding.Unit,
                (IRenderTextureResource)binding.TextureFactory(),
                binding.Level,
                binding.Layer,
                binding.Access,
                binding.Format));
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
