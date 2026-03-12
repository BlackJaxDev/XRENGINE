using System.ComponentModel;
using XREngine.Data.Core;

namespace XREngine.Rendering.Models.Materials
{
    [Serializable]
    public class StencilTest : XRBase
    {
        private StencilTestFace 
            _frontFace = new(),
            _backFace = new();

        [Browsable(false)]
        public bool IsEnabled => Enabled == ERenderParamUsage.Enabled;

        [Browsable(false)]
        public bool IsDisabled => Enabled == ERenderParamUsage.Disabled;

        [Browsable(false)]
        public bool IsUnchanged => Enabled == ERenderParamUsage.Unchanged;
        
        public ERenderParamUsage Enabled { get; set; } = ERenderParamUsage.Unchanged;
        public StencilTestFace FrontFace
        {
            get => _frontFace;
            set => SetField(ref _frontFace, value ?? new StencilTestFace());
        }
        public StencilTestFace BackFace
        {
            get => _backFace;
            set => SetField(ref _backFace, value ?? new StencilTestFace());
        }

        public override string ToString()
            => Enabled == ERenderParamUsage.Unchanged 
                ? "Unchanged"
                : Enabled == ERenderParamUsage.Disabled 
                    ? "Disabled" 
                    : $"[Front: {FrontFace}] - [Back: {BackFace}]";
    }
}
