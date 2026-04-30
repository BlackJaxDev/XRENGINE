//using System.ComponentModel;
//using XREngine.Data.Core;

//namespace XREngine.Rendering.Models.Materials
//{
//    public class AlphaTest : XRBase
//    {
//        private bool _useConstantAlpha;
//        private float _constantAlphaValue;
//        private bool _useAlphaToCoverage;
//        private ELogicGate _logicGate = ELogicGate.And;
//        private float _ref1;
//        private EComparison _comp1;
//        private float @ref;
//        private EComparison comp = EComparison.Always;

//        [Browsable(false)]
//        public bool IsEnabled => Enabled == ERenderParamUsage.Enabled;
//        [Browsable(false)]
//        public bool IsDisable => Enabled == ERenderParamUsage.Disabled;
//        [Browsable(false)]
//        public bool IsUnchanged => Enabled == ERenderParamUsage.Unchanged;

//        public ERenderParamUsage Enabled { get; set; } = ERenderParamUsage.Disabled;
//        public bool UseConstantAlpha { get => _useConstantAlpha; set => _useConstantAlpha = value; }
//        public float ConstantAlphaValue { get => _constantAlphaValue; set => _constantAlphaValue = value; }
//        public bool UseAlphaToCoverage { get => _useAlphaToCoverage; set => _useAlphaToCoverage = value; }
//        public float Ref { get => @ref; set => @ref = value; }
//        public float Ref1 { get => _ref1; set => _ref1 = value; }
//        public EComparison Comp { get => comp; set => comp = value; }
//        public EComparison Comp1 { get => _comp1; set => _comp1 = value; }
//        public ELogicGate LogicGate { get => _logicGate; set => _logicGate = value; }
//    }
//}
