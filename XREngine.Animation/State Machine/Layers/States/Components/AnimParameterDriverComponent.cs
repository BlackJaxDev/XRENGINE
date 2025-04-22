namespace XREngine.Animation
{
    public class AnimParameterDriverComponent : AnimStateComponent
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
        /// Exclusive, so the result will be in the range [RandomMin, RandomMax).
        /// </summary>
        public float RandomMax
        {
            get => _randomMax;
            set => SetField(ref _randomMax, value);
        }

        public override void StateEntered(AnimState state, IDictionary<string, AnimVar> variables)
        {
            if (!variables.TryGetValue(DstParameterName, out AnimVar? dstVar))
                return;

            AnimVar? srcVar;
            switch (Operation)
            {
                case EOperation.Set:
                    dstVar.FloatValue = ConstantValue;
                    break;
                case EOperation.Add:
                    if (variables.TryGetValue(SrcParameterName, out srcVar))
                        dstVar.FloatValue = srcVar.FloatValue + ConstantValue;
                    break;
                case EOperation.Subtract:
                    if (variables.TryGetValue(SrcParameterName, out srcVar))
                        dstVar.FloatValue = srcVar.FloatValue - ConstantValue;
                    break;
                case EOperation.Multiply:
                    if (variables.TryGetValue(SrcParameterName, out srcVar))
                        dstVar.FloatValue = srcVar.FloatValue * ConstantValue;
                    break;
                case EOperation.Divide:
                    if (variables.TryGetValue(SrcParameterName, out srcVar) && ConstantValue != 0.0f)
                        dstVar.FloatValue = srcVar.FloatValue / ConstantValue;
                    break;
                case EOperation.Min:
                    if (variables.TryGetValue(SrcParameterName, out srcVar))
                        dstVar.FloatValue = Math.Min(srcVar.FloatValue, ConstantValue);
                    break;
                case EOperation.Max:
                    if (variables.TryGetValue(SrcParameterName, out srcVar))
                        dstVar.FloatValue = Math.Max(srcVar.FloatValue, ConstantValue);
                    break;
                case EOperation.Random:
                    dstVar.FloatValue = RandomMin + Random.Shared.NextSingle() * (RandomMax - RandomMin);
                    break;
                case EOperation.Copy:
                    if (variables.TryGetValue(SrcParameterName, out srcVar))
                        dstVar.FloatValue = srcVar.FloatValue;
                    break;
            }
        }
    }
}
