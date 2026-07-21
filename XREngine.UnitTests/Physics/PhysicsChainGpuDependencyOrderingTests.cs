using System.Numerics;
using NUnit.Framework;
using Shouldly;
using Silk.NET.OpenGL;
using XREngine.Rendering.Compute;

namespace XREngine.UnitTests.Physics;

/// <summary>
/// Regression coverage for the shader invariant that one invocation owns a
/// whole parent-before-child tree. A particle-per-invocation implementation
/// without a global dependency barrier fails this repeated-chain test.
/// </summary>
[TestFixture]
public sealed class PhysicsChainGpuDependencyOrderingTests : GpuTestBase
{
    private const int DispatchCount = 128;
    private const float SegmentLength = 0.1f;

    [Test]
    public unsafe void RepeatedDispatch_PreservesParentChildDependencyOrder()
    {
        RunWithGLContext(gl =>
        {
            AssertHardwareComputeOrInconclusive(gl);
            uint shader = CompileComputeShader(
                gl,
                LoadShaderSource(Path.Combine("Compute", "PhysicsChain", "PhysicsChain.comp")));
            uint program = CreateComputeProgram(gl, shader);

            GPUPhysicsChainDispatcher.GPUParticleData[] particles = CreateParticles();
            GPUPhysicsChainDispatcher.GPUParticleStaticData[] particleStatic = CreateParticleStatic();
            Matrix4x4[] transforms = CreateTransforms();
            GPUPhysicsChainDispatcher.GPUColliderData[] colliders = [new()];
            GPUPhysicsChainDispatcher.GPUPerTreeParams[] treeParams =
            [
                new()
                {
                    DeltaTime = 1.0f / 120.0f,
                    ObjectScale = 1.0f,
                    Weight = 1.0f,
                    Gravity = new Vector3(4.0f, -9.81f, 1.5f),
                    Force = new Vector3(0.25f, 0.0f, -0.1f),
                    ParticleOffset = 0,
                    ParticleCount = particles.Length,
                    LoopCount = 3,
                },
            ];

            uint[] buffers =
            [
                UploadBuffer(gl, particles, BufferUsageARB.DynamicDraw),
                UploadBuffer(gl, particleStatic, BufferUsageARB.StaticDraw),
                UploadBuffer(gl, transforms, BufferUsageARB.StaticDraw),
                UploadBuffer(gl, colliders, BufferUsageARB.StaticDraw),
                UploadBuffer(gl, treeParams, BufferUsageARB.DynamicDraw),
            ];

            try
            {
                BindInputs(gl, buffers);
                gl.UseProgram(program);
                gl.Uniform1(gl.GetUniformLocation(program, "ApplyObjectMove"), 1);
                gl.Uniform1(gl.GetUniformLocation(program, "ParticleCount"), particles.Length);
                gl.Uniform1(gl.GetUniformLocation(program, "TreeCount"), 1);

                for (int iteration = 0; iteration < DispatchCount; ++iteration)
                {
                    gl.DispatchCompute(1u, 1u, 1u);
                    gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
                }

                ReadBuffer(gl, buffers[0], particles);
                AssertFiniteAndConstrained(particles);
            }
            finally
            {
                for (int index = 0; index < buffers.Length; ++index)
                    gl.DeleteBuffer(buffers[index]);
                gl.DeleteProgram(program);
                gl.DeleteShader(shader);
            }
        });
    }

    private static GPUPhysicsChainDispatcher.GPUParticleData[] CreateParticles()
    {
        var particles = new GPUPhysicsChainDispatcher.GPUParticleData[8];
        for (int index = 0; index < particles.Length; ++index)
        {
            Vector3 position = new(0.0f, -SegmentLength * index, 0.0f);
            particles[index] = new GPUPhysicsChainDispatcher.GPUParticleData
            {
                Position = position,
                PrevPosition = position,
                PreviousPhysicsPosition = position,
            };
        }
        return particles;
    }

    private static GPUPhysicsChainDispatcher.GPUParticleStaticData[] CreateParticleStatic()
    {
        var particles = new GPUPhysicsChainDispatcher.GPUParticleStaticData[8];
        for (int index = 0; index < particles.Length; ++index)
        {
            particles[index] = new GPUPhysicsChainDispatcher.GPUParticleStaticData
            {
                TransformLocalPosition = index == 0 ? Vector3.Zero : new Vector3(0.0f, -SegmentLength, 0.0f),
                ParentIndex = index - 1,
                Damping = 0.05f,
                Elasticity = 0.1f,
                Stiffness = 0.25f,
                Radius = 0.01f,
                BoneLength = index == 0 ? 0.0f : SegmentLength,
                TreeIndex = 0,
            };
        }
        return particles;
    }

    private static Matrix4x4[] CreateTransforms()
    {
        var transforms = new Matrix4x4[8];
        for (int index = 0; index < transforms.Length; ++index)
            transforms[index] = Matrix4x4.CreateTranslation(0.0f, -SegmentLength * index, 0.0f);
        return transforms;
    }

    private static unsafe uint UploadBuffer<T>(GL gl, T[] data, BufferUsageARB usage)
        where T : unmanaged
    {
        uint buffer = gl.GenBuffer();
        fixed (T* dataPointer = data)
        {
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);
            gl.BufferData(
                BufferTargetARB.ShaderStorageBuffer,
                (nuint)(data.Length * sizeof(T)),
                dataPointer,
                usage);
        }
        return buffer;
    }

    private static void BindInputs(GL gl, uint[] buffers)
    {
        gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0u, buffers[0]);
        gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 1u, buffers[1]);
        gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 3u, buffers[2]);
        gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 4u, buffers[3]);
        gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 5u, buffers[4]);
    }

    private static unsafe void ReadBuffer<T>(GL gl, uint buffer, T[] destination)
        where T : unmanaged
    {
        fixed (T* destinationPointer = destination)
        {
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);
            gl.GetBufferSubData(
                BufferTargetARB.ShaderStorageBuffer,
                0,
                (nuint)(destination.Length * sizeof(T)),
                destinationPointer);
        }
    }

    private static void AssertFiniteAndConstrained(
        GPUPhysicsChainDispatcher.GPUParticleData[] particles)
    {
        for (int index = 0; index < particles.Length; ++index)
        {
            Vector3 position = particles[index].Position;
            float.IsFinite(position.X).ShouldBeTrue();
            float.IsFinite(position.Y).ShouldBeTrue();
            float.IsFinite(position.Z).ShouldBeTrue();

            if (index == 0)
                continue;

            float distance = Vector3.Distance(position, particles[index - 1].Position);
            MathF.Abs(distance - SegmentLength).ShouldBeLessThanOrEqualTo(0.001f);
        }
    }
}
