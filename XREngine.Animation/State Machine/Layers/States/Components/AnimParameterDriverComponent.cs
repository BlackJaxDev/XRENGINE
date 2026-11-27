using MemoryPack;

namespace XREngine.Animation
{
    [MemoryPackable]
    public partial class AnimParameterDriverComponent : AnimStateComponent
    {
        public enum EOperation
        {
            /// <summary>
            /// Set the destination parameter to a constant value.
            /// Source parameter is ignored.
            /// </summary>
            Set,
            /// <summary>
            /// Adds the constant value to the source parameter and sets the destination parameter to the result.
            /// </summary>
            Add,
            /// <summary>
            /// Subtracts the constant value from the source parameter and sets the destination parameter to the result.
            /// </summary>
            Subtract,
            /// <summary>
            /// Multiplies the source parameter by the constant value and sets the destination parameter to the result.
            /// </summary>
            Multiply,
            /// <summary>
            /// Divides the source parameter by the constant value and sets the destination parameter to the result.
            /// </summary>
            Divide,
            /// <summary>
            /// Sets the destination parameter to the minimum of the source parameter and the constant value.
            /// </summary>
            Min,
            /// <summary>
            /// Sets the destination parameter to the maximum of the source parameter and the constant value.
            /// </summary>
            Max,
            /// <summary>
            /// Sets the destination parameter to a random value.
            /// Source parameter is ignored.
            /// </summary>
            Random,
            /// <summary>
            /// Sets the destination parameter to the source parameter.
            /// </summary>
            Copy,
        }

        private bool _executeLocally = true;
        public bool ExecuteLocally
        {
            get => _executeLocally;
            set => SetField(ref _executeLocally, value);
        }

        private bool _executeRemotely = true;
        public bool ExecuteRemotely
        {
            get => _executeRemotely;
            set => SetField(ref _executeRemotely, value);
        }

        private string _dstParameterName = "";
        /// <summary>
        /// The name of the parameter to set.
        /// </summary>
        public string DstParameterName
        {
            get => _dstParameterName;
            set => SetField(ref _dstParameterName, value);
        }

        private string _srcParameterName = "";
        /// <summary>
        /// The name of the parameter to use as a source for the operation.
        /// </summary>
        public string SrcParameterName
        {
            get => _srcParameterName;
            set => SetField(ref _srcParameterName, value);
        }

        private EOperation _operation = EOperation.Set;
        /// <summary>
        /// The operation to perform on the source parameter.
        /// </summary>
        public EOperation Operation
        {
            get => _operation;
            set => SetField(ref _operation, value);
        }

        private float _constantValue = 0.0f;
        public float ConstantValue
        {
            get => _constantValue;
            set => SetField(ref _constantValue, value);
        }

        public bool ConstantValueBool
        {
            get => ConstantValue > 0.0f;
            set => ConstantValue = value ? 1.0f : 0.0f;
        }

        public int ConstantValueInt
        {
            get => (int)ConstantValue;
            set => ConstantValue = value;
        }

        private float _randomMin = 0.0f;
        /// <summary>
        /// The minimum value for the random operation.
        /// Inclusive, so the result will be in the range [RandomMin, RandomMax).
        /// </summary>
        public float RandomMin
        {
            get => _randomMin;
            set => SetField(ref _randomMin, value);
        }

        private float _randomMax = 0.0f;
        /// <summary>
        /// The maximum value for the random operation.
        /// Exclusive for float targets, inclusive for integer targets.
        /// </summary>
        public float RandomMax
        {
            get => _randomMax;
            set => SetField(ref _randomMax, value);
        }

        private float _randomChance = 0.5f;
        /// <summary>
        /// Probability in [0, 1] of setting a boolean destination to true during <see cref="EOperation.Random"/>.
        /// </summary>
        public float RandomChance
        {
            get => _randomChance;
            set => SetField(ref _randomChance, value);
        }

        private bool _convertRange;
        /// <summary>
        /// Remaps the source value from [<see cref="SourceMin"/>, <see cref="SourceMax"/>]
        /// to [<see cref="DestMin"/>, <see cref="DestMax"/>] during <see cref="EOperation.Copy"/>.
        /// </summary>
        public bool ConvertRange
        {
            get => _convertRange;
            set => SetField(ref _convertRange, value);
        }

        private float _sourceMin = 0.0f;
        public float SourceMin
        {
            get => _sourceMin;
            set => SetField(ref _sourceMin, value);
        }

        private float _sourceMax = 1.0f;
        public float SourceMax
        {
            get => _sourceMax;
            set => SetField(ref _sourceMax, value);
        }

        private float _destMin = 0.0f;
        public float DestMin
        {
            get => _destMin;
            set => SetField(ref _destMin, value);
        }

        private float _destMax = 1.0f;
        public float DestMax
        {
            get => _destMax;
            set => SetField(ref _destMax, value);
        }

        public override void StateEntered(AnimState state, IDictionary<string, AnimVar> variables)
        {
            if (!variables.TryGetValue(DstParameterName, out AnimVar? dstVar))
                return;

            string effectiveSourceName = string.IsNullOrEmpty(SrcParameterName)
                ? DstParameterName
                : SrcParameterName;

            AnimVar? srcVar;
            switch (Operation)
            {
                case EOperation.Set:
                    AssignTargetValue(dstVar, ConstantValue);
                    break;
                case EOperation.Add:
                    if (variables.TryGetValue(effectiveSourceName, out srcVar))
                        AssignTargetValue(dstVar, srcVar.FloatValue + ConstantValue);
                    break;
                case EOperation.Subtract:
                    if (variables.TryGetValue(effectiveSourceName, out srcVar))
                        AssignTargetValue(dstVar, srcVar.FloatValue - ConstantValue);
                    break;
                case EOperation.Multiply:
                    if (variables.TryGetValue(effectiveSourceName, out srcVar))
                        AssignTargetValue(dstVar, srcVar.FloatValue * ConstantValue);
                    break;
                case EOperation.Divide:
                    if (variables.TryGetValue(effectiveSourceName, out srcVar) && ConstantValue != 0.0f)
                        AssignTargetValue(dstVar, srcVar.FloatValue / ConstantValue);
                    break;
                case EOperation.Min:
                    if (variables.TryGetValue(effectiveSourceName, out srcVar))
                        AssignTargetValue(dstVar, Math.Min(srcVar.FloatValue, ConstantValue));
                    break;
                case EOperation.Max:
                    if (variables.TryGetValue(effectiveSourceName, out srcVar))
                        AssignTargetValue(dstVar, Math.Max(srcVar.FloatValue, ConstantValue));
                    break;
                case EOperation.Random:
                    ApplyRandomValue(dstVar);
                    break;
                case EOperation.Copy:
                    if (variables.TryGetValue(effectiveSourceName, out srcVar))
                    {
                        float copied = srcVar.FloatValue;
                        if (ConvertRange)
                        {
                            float span = SourceMax - SourceMin;
                            if (MathF.Abs(span) > float.Epsilon)
                            {
                                float t = (copied - SourceMin) / span;
                                copied = DestMin + t * (DestMax - DestMin);
                            }
                            else
                            {
                                copied = DestMin;
                            }
                        }
                        AssignTargetValue(dstVar, copied);
                    }
                    break;
            }
        }

        private void ApplyRandomValue(AnimVar dstVar)
        {
            if (dstVar is AnimBool animBool)
            {
                animBool.BoolValue = Random.Shared.NextSingle() < Math.Clamp(RandomChance, 0.0f, 1.0f);
                return;
            }

            if (dstVar is AnimInt animInt)
            {
                int min = (int)MathF.Round(MathF.Min(RandomMin, RandomMax));
                int max = (int)MathF.Round(MathF.Max(RandomMin, RandomMax));
                animInt.IntValue = max > min && max < int.MaxValue
                    ? Random.Shared.Next(min, max + 1)
                    : min;
                return;
            }

            dstVar.FloatValue = RandomMin + Random.Shared.NextSingle() * (RandomMax - RandomMin);
        }

        private static void AssignTargetValue(AnimVar dstVar, float value)
        {
            switch (dstVar)
            {
                case AnimBool animBool:
                    animBool.BoolValue = value > 0.0f;
                    break;
                case AnimInt animInt:
                    animInt.IntValue = float.IsFinite(value) ? (int)MathF.Round(value) : 0;
                    break;
                default:
                    dstVar.FloatValue = value;
                    break;
            }
        }
    }
}
