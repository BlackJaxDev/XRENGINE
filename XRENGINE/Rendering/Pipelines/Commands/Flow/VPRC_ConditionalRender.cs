using System.Numerics;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Pipelines.Commands
{
    public enum EConditionalRenderComparison
    {
        Truthy,
        Equal,
        NotEqual,
        Greater,
        GreaterOrEqual,
        Less,
        LessOrEqual,
    }

    /// <summary>
    /// Executes a nested command body when the specified pipeline variable resolves truthy.
    /// This is intended to pair with authored query/readback commands that publish results into the variable store.
    /// </summary>
    [RenderPipelineScriptCommand]
    public sealed class VPRC_ConditionalRender : ViewportRenderCommand
    {
        private ViewportRenderCommandContainer? _body;

        public string? VariableName { get; set; }
        public EConditionalRenderComparison Comparison { get; set; } = EConditionalRenderComparison.Truthy;
        public bool Invert { get; set; }
        public bool ExecuteWhenMissing { get; set; }
        public bool? BoolValue { get; set; }
        public int? IntValue { get; set; }
        public uint? UIntValue { get; set; }
        public float? FloatValue { get; set; }
        public float FloatTolerance { get; set; } = 0.0001f;
        public Vector2? Vector2Value { get; set; }
        public Vector3? Vector3Value { get; set; }
        public Vector4? Vector4Value { get; set; }
        public Matrix4x4? Matrix4Value { get; set; }
        public string? StringValue { get; set; }

        public ViewportRenderCommandContainer? Body
        {
            get => _body;
            set
            {
                _body = value;
                AttachPipeline(_body);
            }
        }

        public override bool NeedsCollecVisible => Body is not null;

        protected override void Execute()
        {
            bool shouldExecute = EvaluateCondition();
            if (Invert)
                shouldExecute = !shouldExecute;

            if (!shouldExecute)
                return;

            using var branchScope = ActivePipelineInstance.PushRenderGraphBranchScope();
            Body?.Execute();
        }

        public override void CollectVisible()
            => Body?.CollectVisible();

        public override void SwapBuffers()
            => Body?.SwapBuffers();

        internal override void OnAttachedToContainer()
        {
            base.OnAttachedToContainer();
            AttachPipeline(_body);
        }

        internal override void OnParentPipelineAssigned()
        {
            base.OnParentPipelineAssigned();
            AttachPipeline(_body);
        }

        internal override void DescribeRenderPass(RenderGraphDescribeContext context)
        {
            base.DescribeRenderPass(context);
            Body?.BuildRenderPassMetadata(context);
        }

        private bool EvaluateCondition()
        {
            if (string.IsNullOrWhiteSpace(VariableName))
                return false;

            var variables = ActivePipelineInstance.Variables;
            string variableName = VariableName!;

            if (variables.TryGet(variableName, out bool boolValue))
                return EvaluateBool(boolValue);

            if (variables.TryGet(variableName, out int intValue))
                return EvaluateInt(intValue);

            if (variables.TryGet(variableName, out uint uintValue))
                return EvaluateUInt(uintValue);

            if (variables.TryGet(variableName, out float floatValue))
                return EvaluateFloat(floatValue);

            if (variables.TryGet(variableName, out Vector2 vector2Value))
                return EvaluateVector2(vector2Value);

            if (variables.TryGet(variableName, out Vector3 vector3Value))
                return EvaluateVector3(vector3Value);

            if (variables.TryGet(variableName, out Vector4 vector4Value))
                return EvaluateVector4(vector4Value);

            if (variables.TryGet(variableName, out Matrix4x4 matrix4Value))
                return EvaluateMatrix4(matrix4Value);

            if (variables.TryGet(variableName, out string? stringValue))
                return EvaluateString(stringValue);

            return ExecuteWhenMissing;
        }

        private bool EvaluateBool(bool value)
            => Comparison switch
            {
                EConditionalRenderComparison.Equal => value == (BoolValue ?? true),
                EConditionalRenderComparison.NotEqual => value != (BoolValue ?? true),
                _ => value,
            };

        private bool EvaluateInt(int value)
        {
            if (Comparison == EConditionalRenderComparison.Truthy)
                return value != 0;

            int compareValue = IntValue
                ?? (UIntValue.HasValue ? unchecked((int)UIntValue.Value) : 0);
            return CompareNumbers(value, compareValue);
        }

        private bool EvaluateUInt(uint value)
        {
            if (Comparison == EConditionalRenderComparison.Truthy)
                return value != 0u;

            uint compareValue = UIntValue
                ?? (IntValue.HasValue && IntValue.Value >= 0 ? (uint)IntValue.Value : 0u);
            return CompareNumbers(value, compareValue);
        }

        private bool EvaluateFloat(float value)
        {
            if (Comparison == EConditionalRenderComparison.Truthy)
                return MathF.Abs(value) > float.Epsilon;

            float compareValue = FloatValue
                ?? (IntValue.HasValue ? IntValue.Value : UIntValue.HasValue ? UIntValue.Value : 0.0f);
            return CompareNumbers(value, compareValue);
        }

        private bool EvaluateString(string? value)
        {
            if (Comparison == EConditionalRenderComparison.Truthy)
            {
                if (string.IsNullOrWhiteSpace(value))
                    return false;

                if (bool.TryParse(value, out bool parsedBool))
                    return parsedBool;

                if (int.TryParse(value, out int parsedInt))
                    return parsedInt != 0;

                if (float.TryParse(value, out float parsedFloat))
                    return MathF.Abs(parsedFloat) > float.Epsilon;

                return true;
            }

            string compareValue = StringValue ?? string.Empty;
            int comparison = string.Compare(value, compareValue, StringComparison.OrdinalIgnoreCase);
            return Comparison switch
            {
                EConditionalRenderComparison.Equal => comparison == 0,
                EConditionalRenderComparison.NotEqual => comparison != 0,
                EConditionalRenderComparison.Greater => comparison > 0,
                EConditionalRenderComparison.GreaterOrEqual => comparison >= 0,
                EConditionalRenderComparison.Less => comparison < 0,
                EConditionalRenderComparison.LessOrEqual => comparison <= 0,
                _ => !string.IsNullOrWhiteSpace(value),
            };
        }

        private bool EvaluateVector2(Vector2 value)
        {
            if (Comparison == EConditionalRenderComparison.Truthy)
                return Magnitude(value) > FloatTolerance;

            if ((Comparison == EConditionalRenderComparison.Equal || Comparison == EConditionalRenderComparison.NotEqual) && Vector2Value.HasValue)
                return CompareApprox(value, Vector2Value.Value);

            return CompareNumbers(Magnitude(value), ResolveMagnitudeCompareValue(Vector2Value?.Length()));
        }

        private bool EvaluateVector3(Vector3 value)
        {
            if (Comparison == EConditionalRenderComparison.Truthy)
                return Magnitude(value) > FloatTolerance;

            if ((Comparison == EConditionalRenderComparison.Equal || Comparison == EConditionalRenderComparison.NotEqual) && Vector3Value.HasValue)
                return CompareApprox(value, Vector3Value.Value);

            return CompareNumbers(Magnitude(value), ResolveMagnitudeCompareValue(Vector3Value?.Length()));
        }

        private bool EvaluateVector4(Vector4 value)
        {
            if (Comparison == EConditionalRenderComparison.Truthy)
                return Magnitude(value) > FloatTolerance;

            if ((Comparison == EConditionalRenderComparison.Equal || Comparison == EConditionalRenderComparison.NotEqual) && Vector4Value.HasValue)
                return CompareApprox(value, Vector4Value.Value);

            return CompareNumbers(Magnitude(value), ResolveMagnitudeCompareValue(Vector4Value?.Length()));
        }

        private bool EvaluateMatrix4(Matrix4x4 value)
        {
            if (Comparison == EConditionalRenderComparison.Truthy)
                return Magnitude(value) > FloatTolerance;

            if ((Comparison == EConditionalRenderComparison.Equal || Comparison == EConditionalRenderComparison.NotEqual) && Matrix4Value.HasValue)
                return CompareApprox(value, Matrix4Value.Value);

            return CompareNumbers(Magnitude(value), ResolveMagnitudeCompareValue(Matrix4Value.HasValue ? Magnitude(Matrix4Value.Value) : null));
        }

        private float ResolveMagnitudeCompareValue(float? typedValue)
            => FloatValue ?? typedValue ?? (IntValue.HasValue ? IntValue.Value : UIntValue.HasValue ? UIntValue.Value : 0.0f);

        private bool CompareNumbers<T>(T value, T compareValue) where T : IComparable<T>
            => Comparison switch
            {
                EConditionalRenderComparison.Equal => value.CompareTo(compareValue) == 0,
                EConditionalRenderComparison.NotEqual => value.CompareTo(compareValue) != 0,
                EConditionalRenderComparison.Greater => value.CompareTo(compareValue) > 0,
                EConditionalRenderComparison.GreaterOrEqual => value.CompareTo(compareValue) >= 0,
                EConditionalRenderComparison.Less => value.CompareTo(compareValue) < 0,
                EConditionalRenderComparison.LessOrEqual => value.CompareTo(compareValue) <= 0,
                _ => value.CompareTo(default!) != 0,
            };

        private bool CompareApprox(float value, float compareValue)
            => Comparison switch
            {
                EConditionalRenderComparison.Equal => MathF.Abs(value - compareValue) <= FloatTolerance,
                EConditionalRenderComparison.NotEqual => MathF.Abs(value - compareValue) > FloatTolerance,
                EConditionalRenderComparison.Greater => value > compareValue,
                EConditionalRenderComparison.GreaterOrEqual => value >= compareValue,
                EConditionalRenderComparison.Less => value < compareValue,
                EConditionalRenderComparison.LessOrEqual => value <= compareValue,
                _ => MathF.Abs(value) > FloatTolerance,
            };

        private bool CompareApprox(Vector2 value, Vector2 compareValue)
            => CompareApprox(Magnitude(value - compareValue), 0.0f);

        private bool CompareApprox(Vector3 value, Vector3 compareValue)
            => CompareApprox(Magnitude(value - compareValue), 0.0f);

        private bool CompareApprox(Vector4 value, Vector4 compareValue)
            => CompareApprox(Magnitude(value - compareValue), 0.0f);

        private bool CompareApprox(Matrix4x4 value, Matrix4x4 compareValue)
            => CompareApprox(Magnitude(Subtract(value, compareValue)), 0.0f);

        private static float Magnitude(Vector2 value)
            => value.Length();

        private static float Magnitude(Vector3 value)
            => value.Length();

        private static float Magnitude(Vector4 value)
            => value.Length();

        private static float Magnitude(Matrix4x4 value)
        {
            float sum =
                (value.M11 * value.M11) + (value.M12 * value.M12) + (value.M13 * value.M13) + (value.M14 * value.M14) +
                (value.M21 * value.M21) + (value.M22 * value.M22) + (value.M23 * value.M23) + (value.M24 * value.M24) +
                (value.M31 * value.M31) + (value.M32 * value.M32) + (value.M33 * value.M33) + (value.M34 * value.M34) +
                (value.M41 * value.M41) + (value.M42 * value.M42) + (value.M43 * value.M43) + (value.M44 * value.M44);
            return MathF.Sqrt(sum);
        }

        private static Matrix4x4 Subtract(Matrix4x4 left, Matrix4x4 right)
            => new(
                left.M11 - right.M11, left.M12 - right.M12, left.M13 - right.M13, left.M14 - right.M14,
                left.M21 - right.M21, left.M22 - right.M22, left.M23 - right.M23, left.M24 - right.M24,
                left.M31 - right.M31, left.M32 - right.M32, left.M33 - right.M33, left.M34 - right.M34,
                left.M41 - right.M41, left.M42 - right.M42, left.M43 - right.M43, left.M44 - right.M44);

        private void AttachPipeline(ViewportRenderCommandContainer? container)
        {
            var pipeline = CommandContainer?.ParentPipeline;
            if (container is not null && pipeline is not null && !ReferenceEquals(container.ParentPipeline, pipeline))
                container.ParentPipeline = pipeline;
        }
    }
}
