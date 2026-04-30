using Silk.NET.OpenGL;
using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using static XREngine.Rendering.XRRenderProgram;

namespace XREngine.Rendering.OpenGL
{
    public unsafe partial class OpenGLRenderer
    {
        public partial class GLRenderProgram
        {
            #region Uniforms
            public void Uniform(EEngineUniform name, Vector2 p)
                => Uniform(name.ToStringFast(), p);
            public void Uniform(EEngineUniform name, Vector3 p)
                => Uniform(name.ToStringFast(), p);
            public void Uniform(EEngineUniform name, Vector4 p)
                => Uniform(name.ToStringFast(), p);
            public void Uniform(EEngineUniform name, Quaternion p)
                => Uniform(name.ToStringFast(), p);
            public void Uniform(EEngineUniform name, int p)
                => Uniform(name.ToStringFast(), p);
            public void Uniform(EEngineUniform name, float p)
                => Uniform(name.ToStringFast(), p);
            public void Uniform(EEngineUniform name, uint p)
                => Uniform(name.ToStringFast(), p);
            public void Uniform(EEngineUniform name, double p)
                => Uniform(name.ToStringFast(), p);
            public void Uniform(EEngineUniform name, Matrix4x4 p)
                => Uniform(name.ToStringFast(), p);
            public void Uniform(EEngineUniform name, bool p)
                => Uniform(name.ToStringFast(), p);

            public void Uniform(string name, Vector2 p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, Vector3 p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, Vector4 p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, Quaternion p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, int p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, float p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, uint p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, double p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, Matrix4x4 p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, bool p)
                => Uniform(GetUniformLocation(name), p);

            public void Uniform(int location, Vector2 p)
            {
                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.FloatVec2))
                    return;

                Api.ProgramUniform2(BindingId, location, p);
            }
            public void Uniform(int location, Vector3 p)
            {
                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.FloatVec3))
                    return;

                Api.ProgramUniform3(BindingId, location, p);
            }
            public void Uniform(int location, Vector4 p)
            {
                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.FloatVec4))
                    return;

                Api.ProgramUniform4(BindingId, location, p);
            }
            public void Uniform(int location, Quaternion p)
            {
                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.FloatVec4))
                    return;

                Api.ProgramUniform4(BindingId, location, p);
            }
            public void Uniform(int location, int p)
            {
                if (!MarkUniformBinding(location)) 
                    return;

                // int uniforms are also used for samplers, so accept Int, Bool, and all sampler types
                if (!ValidateUniformType(location, GLEnum.Int, GLEnum.Bool, GLEnum.Sampler2D, GLEnum.Sampler3D, GLEnum.SamplerCube, 
                    GLEnum.Sampler2DShadow, GLEnum.Sampler2DArray, GLEnum.SamplerCubeShadow, GLEnum.IntSampler2D, GLEnum.IntSampler3D,
                    GLEnum.UnsignedIntSampler2D, GLEnum.UnsignedIntSampler3D, GLEnum.Sampler2DRect, GLEnum.Sampler2DRectShadow,
                    GLEnum.Sampler1D, GLEnum.Sampler1DShadow, GLEnum.Sampler1DArray, GLEnum.Sampler1DArrayShadow, GLEnum.Sampler2DArrayShadow,
                    GLEnum.SamplerBuffer, GLEnum.Sampler2DMultisample, GLEnum.Sampler2DMultisampleArray, GLEnum.IntSampler2DArray,
                    GLEnum.Image2D, GLEnum.Image3D, GLEnum.ImageCube, GLEnum.Image2DArray))
                    return;

                Api.ProgramUniform1(BindingId, location, p);
            }
            public void Uniform(int location, float p)
            {
                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.Float))
                    return;

                Api.ProgramUniform1(BindingId, location, p);
            }
            public void Uniform(int location, uint p)
            {
                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.UnsignedInt))
                    return;

                Api.ProgramUniform1(BindingId, location, p);
            }
            public void Uniform(int location, double p)
            {
                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.Double))
                    return;

                Api.ProgramUniform1(BindingId, location, p);
            }
            public void Uniform(int location, Matrix4x4 p)
            {
                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.FloatMat4))
                    return;

                Api.ProgramUniformMatrix4(BindingId, location, 1, false, &p.M11);
            }
            public void Uniform(int location, bool p)
            {
                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.Bool, GLEnum.Int))
                    return;

                Api.ProgramUniform1(BindingId, location, p ? 1 : 0);
            }

            public void Uniform(string name, Vector2[] p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, Vector3[] p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, Vector4[] p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, Quaternion[] p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, int[] p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, float[] p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, Span<float> p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, uint[] p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, double[] p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, Matrix4x4[] p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, bool[] p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, BoolVector2 p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, BoolVector3 p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, BoolVector4 p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, BoolVector2[] p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, BoolVector3[] p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, BoolVector4[] p)
                => Uniform(GetUniformLocation(name), p);

            public void Uniform(int location, IVector2 p)
            {
                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.IntVec2))
                    return;

                Api.ProgramUniform2(BindingId, location, p.X, p.Y);
            }
            public void Uniform(int location, IVector3 p)
            {
                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.IntVec3))
                    return;

                Api.ProgramUniform3(BindingId, location, p.X, p.Y, p.Z);
            }
            public void Uniform(int location, IVector4 p)
            {
                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.IntVec4))
                    return;

                Api.ProgramUniform4(BindingId, location, p.X, p.Y, p.Z, p.W);
            }
            public void Uniform(int location, IVector2[] p)
            {
                if (!MarkUniformBinding(location) || p.Length == 0)
                    return;

                if (!ValidateUniformType(location, GLEnum.IntVec2))
                    return;

                fixed (IVector2* ptr = p)
                {
                    Api.ProgramUniform2(BindingId, location, (uint)p.Length, (int*)ptr);
                }
            }
            public void Uniform(int location, IVector3[] p)
            {
                if (!MarkUniformBinding(location) || p.Length == 0)
                    return;

                if (!ValidateUniformType(location, GLEnum.IntVec3))
                    return;

                fixed (IVector3* ptr = p)
                {
                    Api.ProgramUniform3(BindingId, location, (uint)p.Length, (int*)ptr);
                }
            }
            public void Uniform(int location, IVector4[] p)
            {
                if (!MarkUniformBinding(location) || p.Length == 0)
                    return;

                if (!ValidateUniformType(location, GLEnum.IntVec4))
                    return;

                fixed (IVector4* ptr = p)
                {
                    Api.ProgramUniform4(BindingId, location, (uint)p.Length, (int*)ptr);
                }
            }

            public void Uniform(string name, IVector2 p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, IVector3 p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, IVector4 p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, IVector2[] p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, IVector3[] p)
                => Uniform(GetUniformLocation(name), p);
            public void Uniform(string name, IVector4[] p)
                => Uniform(GetUniformLocation(name), p);

            public void Uniform(int location, Vector2[] p)
            {
                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.FloatVec2))
                    return;

                fixed (Vector2* ptr = p)
                {
                    Api.ProgramUniform2(BindingId, location, (uint)p.Length, (float*)ptr);
                }
            }
            public void Uniform(int location, Vector3[] p)
            {
                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.FloatVec3))
                    return;

                fixed (Vector3* ptr = p)
                {
                    Api.ProgramUniform3(BindingId, location, (uint)p.Length, (float*)ptr);
                }
            }
            public void Uniform(int location, Vector4[] p)
            {
                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.FloatVec4))
                    return;

                fixed (Vector4* ptr = p)
                {
                    Api.ProgramUniform4(BindingId, location, (uint)p.Length, (float*)ptr);
                }
            }
            public void Uniform(int location, Quaternion[] p)
            {
                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.FloatVec4))
                    return;

                fixed (Quaternion* ptr = p)
                {
                    Api.ProgramUniform4(BindingId, location, (uint)p.Length, (float*)ptr);
                }
            }
            public void Uniform(int location, int[] p)
            {
                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.Int, GLEnum.Bool))
                    return;

                fixed (int* ptr = p)
                {
                    Api.ProgramUniform1(BindingId, location, (uint)p.Length, ptr);
                }
            }
            public void Uniform(int location, float[] p)
            {
                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.Float))
                    return;

                fixed (float* ptr = p)
                {
                    Api.ProgramUniform1(BindingId, location, (uint)p.Length, ptr);
                }
            }
            public void Uniform(int location, Span<float> p)
            {
                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.Float))
                    return;

                unsafe
                {
                    fixed (float* ptr = p)
                    {
                        Api.ProgramUniform1(BindingId, location, (uint)p.Length, ptr);
                    }
                }
            }
            public void Uniform(int location, uint[] p)
            {
                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.UnsignedInt))
                    return;

                fixed (uint* ptr = p)
                {
                    Api.ProgramUniform1(BindingId, location, (uint)p.Length, ptr);
                }
            }
            public void Uniform(int location, double[] p)
            {
                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.Double))
                    return;

                fixed (double* ptr = p)
                {
                    Api.ProgramUniform1(BindingId, location, (uint)p.Length, ptr);
                }
            }
            public void Uniform(int location, Matrix4x4[] p)
            {
                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.FloatMat4))
                    return;

                fixed (Matrix4x4* ptr = p)
                {
                    Api.ProgramUniformMatrix4(BindingId, location, (uint)p.Length, false, (float*)ptr);
                }
            }
            public void Uniform(int location, bool[] p)
            {
                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.Bool, GLEnum.Int))
                    return;

                int[] conv = new int[p.Length];
                for (int i = 0; i < p.Length; i++)
                    conv[i] = p[i] ? 1 : 0;

                fixed (int* ptr = conv)
                {
                    Api.ProgramUniform1(BindingId, location, (uint)conv.Length, ptr);
                }
            }

            public void Uniform(int location, BoolVector2 p)
            {
                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.BoolVec2, GLEnum.IntVec2))
                    return;

                Api.ProgramUniform2(BindingId, location, p.X ? 1 : 0, p.Y ? 1 : 0);
            }
            public void Uniform(int location, BoolVector3 p)
            {
                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.BoolVec3, GLEnum.IntVec3))
                    return;

                Api.ProgramUniform3(BindingId, location, p.X ? 1 : 0, p.Y ? 1 : 0, p.Z ? 1 : 0);
            }
            public void Uniform(int location, BoolVector4 p)
            {
                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.BoolVec4, GLEnum.IntVec4))
                    return;

                Api.ProgramUniform4(BindingId, location, p.X ? 1 : 0, p.Y ? 1 : 0, p.Z ? 1 : 0, p.W ? 1 : 0);
            }

            public void Uniform(int location, BoolVector2[] p)
            {
                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.BoolVec2, GLEnum.IntVec2))
                    return;

                int[] conv = new int[p.Length * 2];
                for (int i = 0; i < p.Length; i++)
                {
                    conv[i * 2] = p[i].X ? 1 : 0;
                    conv[i * 2 + 1] = p[i].Y ? 1 : 0;
                }
                fixed (int* ptr = conv)
                {
                    Api.ProgramUniform2(BindingId, location, (uint)p.Length, ptr);
                }
            }
            public void Uniform(int location, BoolVector3[] p)
            {
                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.BoolVec3, GLEnum.IntVec3))
                    return;

                int[] conv = new int[p.Length * 3];
                for (int i = 0; i < p.Length; i++)
                {
                    conv[i * 3] = p[i].X ? 1 : 0;
                    conv[i * 3 + 1] = p[i].Y ? 1 : 0;
                    conv[i * 3 + 2] = p[i].Z ? 1 : 0;
                }

                fixed (int* ptr = conv)
                {
                    Api.ProgramUniform3(BindingId, location, (uint)p.Length, ptr);
                }
            }
            public void Uniform(int location, BoolVector4[] p)
            {
                if (!MarkUniformBinding(location))
                    return;

                if (!ValidateUniformType(location, GLEnum.BoolVec4, GLEnum.IntVec4))
                    return;

                int[] conv = new int[p.Length * 4];
                for (int i = 0; i < p.Length; i++)
                {
                    conv[i * 4] = p[i].X ? 1 : 0;
                    conv[i * 4 + 1] = p[i].Y ? 1 : 0;
                    conv[i * 4 + 2] = p[i].Z ? 1 : 0;
                    conv[i * 4 + 3] = p[i].W ? 1 : 0;
                }
                fixed (int* ptr = conv)
                {
                    Api.ProgramUniform4(BindingId, location, (uint)p.Length, ptr);
                }
            }
            #endregion
        }
    }
}
