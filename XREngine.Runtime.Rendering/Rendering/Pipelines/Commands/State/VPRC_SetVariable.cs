using System.Numerics;
using System.Globalization;

namespace XREngine.Rendering.Pipelines.Commands
{
    [RenderPipelineScriptCommand]
    public sealed class VPRC_SetVariable : ViewportRenderCommand
    {
        public string VariableName { get; set; } = string.Empty;
        public bool ClearVariable { get; set; }
        public bool? BoolValue { get; set; }
        public int? IntValue { get; set; }
        public uint? UIntValue { get; set; }
        public float? FloatValue { get; set; }
        public Vector2? Vector2Value { get; set; }
        public Vector3? Vector3Value { get; set; }
        public Vector4? Vector4Value { get; set; }
        public Matrix4x4? Matrix4Value { get; set; }
        public string? StringValue { get; set; }
        public string? TextureResourceName { get; set; }
        public string? FrameBufferResourceName { get; set; }
        public string? BufferResourceName { get; set; }
        public string? RenderBufferResourceName { get; set; }
        public Func<object?>? ValueEvaluator { get; set; }

        protected override void Execute()
        {
            if (string.IsNullOrWhiteSpace(VariableName))
                return;

            var variables = ActivePipelineInstance.Variables;

            if (ClearVariable)
            {
                variables.Remove(VariableName);
                return;
            }

            if (ValueEvaluator is not null)
            {
                SetValue(ValueEvaluator());
                return;
            }

            if (!string.IsNullOrWhiteSpace(TextureResourceName))
            {
                if (ActivePipelineInstance.TryGetTexture(TextureResourceName, out XRTexture? texture) && texture is not null)
                    variables.SetTexture(VariableName, texture);
                else
                    variables.Set(VariableName, TextureResourceName);
                return;
            }

            if (!string.IsNullOrWhiteSpace(FrameBufferResourceName))
            {
                if (ActivePipelineInstance.TryGetFBO(FrameBufferResourceName, out XRFrameBuffer? frameBuffer) && frameBuffer is not null)
                    variables.SetFrameBuffer(VariableName, frameBuffer);
                else
                    variables.Set(VariableName, FrameBufferResourceName);
                return;
            }

            if (!string.IsNullOrWhiteSpace(BufferResourceName))
            {
                if (ActivePipelineInstance.TryGetBuffer(BufferResourceName, out XRDataBuffer? buffer) && buffer is not null)
                    variables.SetBuffer(VariableName, buffer);
                else
                    variables.Set(VariableName, BufferResourceName);
                return;
            }

            if (!string.IsNullOrWhiteSpace(RenderBufferResourceName))
            {
                if (ActivePipelineInstance.TryGetRenderBuffer(RenderBufferResourceName, out XRRenderBuffer? renderBuffer) && renderBuffer is not null)
                    variables.SetRenderBuffer(VariableName, renderBuffer);
                else
                    variables.Set(VariableName, RenderBufferResourceName);
                return;
            }

            if (Matrix4Value.HasValue)
                variables.Set(VariableName, Matrix4Value.Value);
            else if (Vector4Value.HasValue)
                variables.Set(VariableName, Vector4Value.Value);
            else if (Vector3Value.HasValue)
                variables.Set(VariableName, Vector3Value.Value);
            else if (Vector2Value.HasValue)
                variables.Set(VariableName, Vector2Value.Value);
            else if (FloatValue.HasValue)
                variables.Set(VariableName, FloatValue.Value);
            else if (UIntValue.HasValue)
                variables.Set(VariableName, UIntValue.Value);
            else if (IntValue.HasValue)
                variables.Set(VariableName, IntValue.Value);
            else if (BoolValue.HasValue)
                variables.Set(VariableName, BoolValue.Value);
            else if (StringValue is not null)
                variables.Set(VariableName, StringValue);
        }

        public void SetLiteralValue(object? value)
        {
            ClearVariable = false;
            BoolValue = null;
            IntValue = null;
            UIntValue = null;
            FloatValue = null;
            Vector2Value = null;
            Vector3Value = null;
            Vector4Value = null;
            Matrix4Value = null;
            StringValue = null;
            TextureResourceName = null;
            FrameBufferResourceName = null;
            BufferResourceName = null;
            RenderBufferResourceName = null;
            ValueEvaluator = null;

            switch (value)
            {
                case null:
                    ClearVariable = true;
                    return;
                case bool boolValue:
                    BoolValue = boolValue;
                    return;
                case int intValue:
                    IntValue = intValue;
                    return;
                case uint uintValue:
                    UIntValue = uintValue;
                    return;
                case float floatValue:
                    FloatValue = floatValue;
                    return;
                case double doubleValue:
                    FloatValue = (float)doubleValue;
                    return;
                case Vector2 vector2Value:
                    Vector2Value = vector2Value;
                    return;
                case Vector3 vector3Value:
                    Vector3Value = vector3Value;
                    return;
                case Vector4 vector4Value:
                    Vector4Value = vector4Value;
                    return;
                case Matrix4x4 matrixValue:
                    Matrix4Value = matrixValue;
                    return;
                case string stringValue:
                    StringValue = stringValue;
                    return;
                case Enum enumValue:
                    IntValue = Convert.ToInt32(enumValue, CultureInfo.InvariantCulture);
                    return;
            }

            TypeCode code = Type.GetTypeCode(value.GetType());
            switch (code)
            {
                case TypeCode.Byte:
                case TypeCode.SByte:
                case TypeCode.Int16:
                case TypeCode.Int32:
                    IntValue = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    return;
                case TypeCode.UInt16:
                case TypeCode.UInt32:
                    UIntValue = Convert.ToUInt32(value, CultureInfo.InvariantCulture);
                    return;
                case TypeCode.Int64:
                case TypeCode.UInt64:
                case TypeCode.Decimal:
                case TypeCode.Double:
                case TypeCode.Single:
                    FloatValue = Convert.ToSingle(value, CultureInfo.InvariantCulture);
                    return;
            }

            StringValue = value.ToString();
        }

        public void SetValue(object? value)
        {
            if (string.IsNullOrWhiteSpace(VariableName))
                return;

            var variables = ActivePipelineInstance.Variables;
            if (value is null)
            {
                variables.Remove(VariableName);
                return;
            }

            switch (value)
            {
                case bool boolValue:
                    variables.Set(VariableName, boolValue);
                    return;
                case int intValue:
                    variables.Set(VariableName, intValue);
                    return;
                case uint uintValue:
                    variables.Set(VariableName, uintValue);
                    return;
                case float floatValue:
                    variables.Set(VariableName, floatValue);
                    return;
                case double doubleValue:
                    variables.Set(VariableName, (float)doubleValue);
                    return;
                case Vector2 vector2Value:
                    variables.Set(VariableName, vector2Value);
                    return;
                case Vector3 vector3Value:
                    variables.Set(VariableName, vector3Value);
                    return;
                case Vector4 vector4Value:
                    variables.Set(VariableName, vector4Value);
                    return;
                case Matrix4x4 matrixValue:
                    variables.Set(VariableName, matrixValue);
                    return;
                case string stringValue:
                    variables.Set(VariableName, stringValue);
                    return;
                case XRTexture texture:
                    variables.SetTexture(VariableName, texture);
                    return;
                case XRFrameBuffer frameBuffer:
                    variables.SetFrameBuffer(VariableName, frameBuffer);
                    return;
                case XRDataBuffer buffer:
                    variables.SetBuffer(VariableName, buffer);
                    return;
                case XRRenderBuffer renderBuffer:
                    variables.SetRenderBuffer(VariableName, renderBuffer);
                    return;
                case Enum enumValue:
                    variables.Set(VariableName, Convert.ToInt32(enumValue, CultureInfo.InvariantCulture));
                    return;
            }

            TypeCode code = Type.GetTypeCode(value.GetType());
            switch (code)
            {
                case TypeCode.Byte:
                case TypeCode.SByte:
                case TypeCode.Int16:
                case TypeCode.Int32:
                    variables.Set(VariableName, Convert.ToInt32(value, CultureInfo.InvariantCulture));
                    return;
                case TypeCode.UInt16:
                case TypeCode.UInt32:
                    variables.Set(VariableName, Convert.ToUInt32(value, CultureInfo.InvariantCulture));
                    return;
                case TypeCode.Int64:
                case TypeCode.UInt64:
                case TypeCode.Decimal:
                case TypeCode.Double:
                case TypeCode.Single:
                    variables.Set(VariableName, Convert.ToSingle(value, CultureInfo.InvariantCulture));
                    return;
            }

            variables.Set(VariableName, value.ToString() ?? string.Empty);
        }
    }
}
