using XREngine.Animation;
using XREngine.Data.Core;

namespace XREngine.Components
{
    public class AnimStateCondition : XRBase
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

        private string _variableName = "";
        public string VariableName
        {
            get => _variableName;
            set => SetField(ref _variableName, value);
        }

        public bool Evaluate(IDictionary<string, AnimVar> variables)
        {
            if (!variables.TryGetValue(VariableName, out var variable))
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
    /// <summary>
    /// Describes a condition and how to transition to a new state.
    /// </summary>
    public class AnimStateTransition : XRBase
    {
        //[Browsable(false)]
        public AnimState? Owner { get; internal set; }

        public event Action? Started;
        public event Action? Finished;

        /// <summary>
        /// The index of the next state to go to if this transition's condition method returns true.
        /// </summary>
        public AnimState? DestinationState { get; set; }
        /// <summary>
        /// The condition to test if this transition should occur; run every frame.
        /// </summary>
        public List<AnimStateCondition> Conditions { get; set; } = [];
        /// <summary>
        /// How quickly the current state should blend into the next, in seconds.
        /// </summary>
        public float BlendDuration { get; set; }
        /// <summary>
        /// The interpolation method to use to blend to the next state.
        /// </summary>
        public EAnimBlendType BlendType { get; set; }
        /// <summary>
        /// If <see cref="BlendType"/> == <see cref="EAnimBlendType.Custom"/>, 
        /// uses these keyframes to interpolate between 0.0f and 1.0f.
        /// </summary>
        public PropAnimFloat? CustomBlendFunction { get; set; }
        /// <summary>
        /// If multiple transitions evaluate to true at the same time, this dictates which transition will occur.
        /// </summary>
        public int Priority { get; set; } = 0;
        public string Name { get; set; } = "";

        internal void OnFinished()
        {
            Started?.Invoke();
        }
        internal void OnStarted()
        {
            Finished?.Invoke();
        }

        public bool AllConditionsValid(IDictionary<string, AnimVar> variables)
        {
            foreach (var condition in Conditions)
                if (!condition.Evaluate(variables))
                    return false;
            return true;
        }

        public bool NameEquals(string name, StringComparison comp = StringComparison.Ordinal)
            => string.Equals(Name, name, comp);
    }
}
