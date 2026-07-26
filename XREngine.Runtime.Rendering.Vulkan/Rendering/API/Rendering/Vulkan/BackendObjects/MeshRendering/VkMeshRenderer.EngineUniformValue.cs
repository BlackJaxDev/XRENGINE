using System.Numerics;

using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    public partial class VkMeshRenderer
    {
        /// <summary>
        /// Carries engine-owned uniform values without boxing them on the per-draw refresh path.
        /// The reference slot is reserved for values already boxed by the program uniform store.
        /// </summary>
        private readonly struct EngineUniformValue
        {
            private EngineUniformValue(float value) => Float = value;
            private EngineUniformValue(int value) => Int = value;
            private EngineUniformValue(uint value) => UInt = value;
            private EngineUniformValue(Vector2 value) => Vector2 = value;
            private EngineUniformValue(Vector3 value) => Vector3 = value;
            private EngineUniformValue(Vector4 value) => Vector4 = value;
            private EngineUniformValue(Matrix4x4 value) => Matrix4x4 = value;
            private EngineUniformValue(object? value) => Reference = value;

            public float Float { get; }
            public int Int { get; }
            public uint UInt { get; }
            public Vector2 Vector2 { get; }
            public Vector3 Vector3 { get; }
            public Vector4 Vector4 { get; }
            public Matrix4x4 Matrix4x4 { get; }
            public object? Reference { get; }

            public static EngineUniformValue FromProgramValue(object? value) => new(value);

            public static EngineUniformValue FromProgramValue(in ProgramUniformValue value)
            {
                if (!value.HasInlineValue)
                    return new(value.ReferenceValue);

                return value.Type switch
                {
                    EShaderVarType._float => value.Float,
                    EShaderVarType._int or EShaderVarType._bool => value.Int,
                    EShaderVarType._uint => value.UInt,
                    EShaderVarType._vec2 => value.Vector2,
                    EShaderVarType._vec3 => value.Vector3,
                    EShaderVarType._vec4 => value.Vector4,
                    EShaderVarType._mat4 => value.Matrix4x4,
                    _ => new(value.ReferenceValue),
                };
            }

            public object? ToDiagnosticObject(EShaderVarType type)
                => Reference ?? type switch
                {
                    EShaderVarType._float => Float,
                    EShaderVarType._int or EShaderVarType._bool => Int,
                    EShaderVarType._uint => UInt,
                    EShaderVarType._vec2 => Vector2,
                    EShaderVarType._vec3 => Vector3,
                    EShaderVarType._vec4 => Vector4,
                    EShaderVarType._mat4 => Matrix4x4,
                    _ => null,
                };

            public static implicit operator EngineUniformValue(float value) => new(value);
            public static implicit operator EngineUniformValue(int value) => new(value);
            public static implicit operator EngineUniformValue(uint value) => new(value);
            public static implicit operator EngineUniformValue(Vector2 value) => new(value);
            public static implicit operator EngineUniformValue(Vector3 value) => new(value);
            public static implicit operator EngineUniformValue(Vector4 value) => new(value);
            public static implicit operator EngineUniformValue(Matrix4x4 value) => new(value);
        }
    }
}
