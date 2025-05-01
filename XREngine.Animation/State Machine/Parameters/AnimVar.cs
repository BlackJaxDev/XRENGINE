using XREngine.Data.Core;

namespace XREngine.Animation
{
    public abstract class AnimVar(string name) : XRBase
    {
        private string _parameterName = name;
        public string ParameterName
        {
            get => _parameterName;
            set => SetField(ref _parameterName, value);
        }

        protected abstract void SetBool(bool value);
        protected abstract void SetFloat(float value);
        protected abstract void SetInt(int value);

        protected abstract bool GetBool();
        protected abstract float GetFloat();
        protected abstract int GetInt();

        public float FloatValue
        {
            get => GetFloat();
            set => SetFloat(value);
        }

        public int IntValue
        {
            get => GetInt();
            set => SetInt(value);
        }

        public bool BoolValue
        {
            get => GetBool();
            set => SetBool(value);
        }

        public abstract int CalcBitCount();

        public abstract bool GreaterThan(AnimTransitionCondition condition);
        public abstract bool IsTrue();
        public abstract bool LessThan(AnimTransitionCondition condition);
        public abstract bool ValueEquals(AnimTransitionCondition condition);
        public abstract void WriteBits(byte[] data, ref int bitOffset);
    }
}
