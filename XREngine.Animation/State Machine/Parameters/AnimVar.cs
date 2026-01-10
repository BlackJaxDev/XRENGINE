using MemoryPack;
using XREngine.Data.Core;

namespace XREngine.Animation
{
    public abstract class AnimVar(string name) : XRBase
    {
        [MemoryPackIgnore]
        public AnimStateMachine? StateMachine { get; internal set; }

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

        private ushort _hash = CreateSmallHash(name);
        public ushort Hash
        {
            get => _hash;
            private set => SetField(ref _hash, value);
        }

        internal static ushort CreateSmallHash(string parameterName)
        {
            // Simple FNV-1a hash truncated to 16 bits.
            const ushort fnvPrime = 0x0100 + 0x93; // 16777619
            ushort hash = 0x811C; // 2166136261
            foreach (char c in parameterName)
            {
                hash ^= (byte)(c & 0xFF);
                hash *= fnvPrime;
                hash ^= (byte)((c >> 8) & 0xFF);
                hash *= fnvPrime;
            }
            return hash;
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(ParameterName):
                    Hash = CreateSmallHash(ParameterName);
                    break;
            }
        }

        public abstract int CalcBitCount();

        public abstract bool GreaterThan(AnimTransitionCondition condition);
        public abstract bool IsTrue();
        public abstract bool LessThan(AnimTransitionCondition condition);
        public abstract bool ValueEquals(AnimTransitionCondition condition);
        public abstract void WriteBits(byte[] data, ref int bitOffset);
        public abstract void ReadBits(byte[]? bytes, ref int bitOffset);
    }
}
