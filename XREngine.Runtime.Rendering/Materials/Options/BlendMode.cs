using XREngine.Data.Core;

namespace XREngine.Rendering.Models.Materials
{
    [Serializable]
    public class BlendMode : XRBase
    {
        private ERenderParamUsage _enabled = ERenderParamUsage.Disabled;
        private EBlendEquationMode _rgbEquation = EBlendEquationMode.FuncAdd;
        private EBlendEquationMode _alphaEquation = EBlendEquationMode.FuncAdd;
        private EBlendingFactor _rgbSrcFactor = EBlendingFactor.ConstantColor;
        private EBlendingFactor _alphaSrcFactor = EBlendingFactor.ConstantAlpha;
        private EBlendingFactor _rgbDstFactor = EBlendingFactor.ConstantColor;
        private EBlendingFactor _alphaDstFactor = EBlendingFactor.ConstantAlpha;

        public bool IsEnabled => Enabled == ERenderParamUsage.Enabled;
        public bool IsDisable => Enabled == ERenderParamUsage.Disabled;
        public bool IsUnchanged => Enabled == ERenderParamUsage.Unchanged;

        public ERenderParamUsage Enabled
        {
            get => _enabled;
            set => SetField(ref _enabled, value);
        }
        public EBlendEquationMode RgbEquation
        {
            get => _rgbEquation;
            set => SetField(ref _rgbEquation, value);
        }
        public EBlendEquationMode AlphaEquation
        {
            get => _alphaEquation;
            set => SetField(ref _alphaEquation, value);
        }
        public EBlendingFactor RgbSrcFactor
        {
            get => _rgbSrcFactor;
            set => SetField(ref _rgbSrcFactor, value);
        }
        public EBlendingFactor AlphaSrcFactor
        {
            get => _alphaSrcFactor;
            set => SetField(ref _alphaSrcFactor, value);
        }
        public EBlendingFactor RgbDstFactor
        {
            get => _rgbDstFactor;
            set => SetField(ref _rgbDstFactor, value);
        }
        public EBlendingFactor AlphaDstFactor
        {
            get => _alphaDstFactor;
            set => SetField(ref _alphaDstFactor, value);
        }

        public static BlendMode EnabledTransparent() => new()
        {
            Enabled = ERenderParamUsage.Enabled,
            RgbSrcFactor = EBlendingFactor.SrcAlpha,
            AlphaSrcFactor = EBlendingFactor.SrcAlpha,
            RgbDstFactor = EBlendingFactor.OneMinusSrcAlpha,
            AlphaDstFactor = EBlendingFactor.OneMinusSrcAlpha,
            RgbEquation = EBlendEquationMode.FuncAdd,
            AlphaEquation = EBlendEquationMode.FuncAdd
        };
        public static BlendMode EnabledOpaque() => new()
        {
            Enabled = ERenderParamUsage.Enabled,
            RgbSrcFactor = EBlendingFactor.One,
            AlphaSrcFactor = EBlendingFactor.One,
            RgbDstFactor = EBlendingFactor.Zero,
            AlphaDstFactor = EBlendingFactor.Zero,
            RgbEquation = EBlendEquationMode.FuncAdd,
            AlphaEquation = EBlendEquationMode.FuncAdd
        };
        public static BlendMode Unchanged() => new()
        {
            Enabled = ERenderParamUsage.Unchanged
        };
        public static BlendMode Disabled() => new()
        {
            Enabled = ERenderParamUsage.Disabled
        };

        public override string ToString()
            => Enabled == ERenderParamUsage.Unchanged 
                ? "Unchanged" 
                : Enabled == ERenderParamUsage.Disabled 
                    ? "Disabled" 
                    : $"[RGB: {RgbEquation} {RgbSrcFactor} {RgbDstFactor}] - [Alpha: {AlphaEquation} {AlphaSrcFactor} {AlphaDstFactor}]";
    }
}
