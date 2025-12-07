using MemoryPack;
using XREngine.Data.Core;

namespace XREngine.Animation
{
    [MemoryPackable]
    public partial class AnimTransitionCondition : XRBase
    {
        public enum EComparison
        {
            Equal,
            NotEqual,

            GreaterThan,
            LessThan,

            GreaterThanOrEqual,
            LessThanOrEqual,

            IsTrue,
            IsFalse,
        }

        private EComparison _comparison;
        public EComparison Comparison
        {
            get => _comparison;
            set => SetField(ref _comparison, value);
        }

        private float _comparisonFloat;
        public float ComparisonFloat
        {
            get => _comparisonFloat;
            set => SetField(ref _comparisonFloat, value);
        }

        private int _comparisonInt;
        public int ComparisonInt
        {
            get => _comparisonInt;
            set => SetField(ref _comparisonInt, value);
        }

        private bool _comparisonBool;
        public bool ComparisonBool
        {
            get => _comparisonBool;
            set => SetField(ref _comparisonBool, value);
        }

        [MemoryPackConstructor]
        public AnimTransitionCondition() { }

        public AnimTransitionCondition(string paramName, EComparison comparison, float comparisonValue)
        {
            ParameterName = paramName;
            Comparison = comparison;
            ComparisonFloat = comparisonValue;
        }

        public AnimTransitionCondition(string paramName, bool isThisState)
        {
            ParameterName = paramName;
            Comparison = isThisState ? EComparison.IsTrue : EComparison.IsFalse;
        }

        private string _parameterName = "";
        public string ParameterName
        {
            get => _parameterName;
            set => SetField(ref _parameterName, value);
        }

        public bool Evaluate(IDictionary<string, AnimVar> variables)
        {
            if (!variables.TryGetValue(ParameterName, out var variable))
                return false;

            return Comparison switch
            {
                EComparison.Equal => variable.ValueEquals(this),
                EComparison.NotEqual => !variable.ValueEquals(this),
                EComparison.GreaterThan => variable.GreaterThan(this),
                EComparison.LessThan => variable.LessThan(this),
                EComparison.GreaterThanOrEqual => !variable.LessThan(this),
                EComparison.LessThanOrEqual => !variable.GreaterThan(this),
                EComparison.IsTrue => variable.IsTrue(),
                EComparison.IsFalse => !variable.IsTrue(),
                _ => true,
            };
        }
    }
}
