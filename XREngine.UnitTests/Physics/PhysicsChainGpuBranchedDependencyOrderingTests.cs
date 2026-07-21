using System.Numerics;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Shouldly;
using Silk.NET.OpenGL;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainGpuBranchedDependencyOrderingTests : GpuTestBase
{
    private const int DispatchCount = 128;
    private const float SegmentLength = 0.1f;

    [Test]
    public unsafe void RepeatedWorkgroupDispatch_PreservesBranchedDepthDependencies()
    {
        RunWithGLContext(gl =>
        {
            AssertHardwareComputeOrInconclusive(gl);
            uint shader = CompileComputeShader(
                gl,
                LoadShaderSource(Path.Combine("Compute", "PhysicsChain", "PhysicsChainBranched.comp")));
            uint program = CreateComputeProgram(gl, shader);

            ParticleState[] particles = CreateParticles();
            ParticleStatic[] particleStatic = CreateParticleStatic();
            Matrix4x4[] transforms = CreateTransforms();
            ColliderData[] colliders = [new()];
            PerTreeParams[] treeParams =
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
                    DepthRangeOffset = 0,
                    DepthRangeCount = 4,
                },
            ];
            uint[] activeTreeIds = [0u];
            uint[] activeWorkCounters = [0u, 1u, 0u, 0u];
            DepthRange[] depthRanges =
            [
                new(0u, 1u),
                new(1u, 1u),
                new(2u, 2u),
                new(4u, 1u),
            ];
            uint[] depthParticleIds = [0u, 1u, 2u, 3u, 4u];

            uint[] buffers =
            [
                UploadBuffer(gl, particles, BufferUsageARB.DynamicDraw),
                UploadBuffer(gl, particleStatic, BufferUsageARB.StaticDraw),
                UploadBuffer(gl, transforms, BufferUsageARB.StaticDraw),
                UploadBuffer(gl, colliders, BufferUsageARB.StaticDraw),
                UploadBuffer(gl, treeParams, BufferUsageARB.DynamicDraw),
                UploadBuffer(gl, activeTreeIds, BufferUsageARB.DynamicDraw),
                UploadBuffer(gl, activeWorkCounters, BufferUsageARB.DynamicDraw),
                UploadBuffer(gl, depthRanges, BufferUsageARB.StaticDraw),
                UploadBuffer(gl, depthParticleIds, BufferUsageARB.StaticDraw),
            ];

            try
            {
                BindInputs(gl, buffers);
                gl.UseProgram(program);
                SetUniform(gl, program, "ApplyObjectMove", 1);
                SetUniform(gl, program, "ParticleCount", particles.Length);
                SetUniform(gl, program, "TreeCount", 1);
                SetUniform(gl, program, "ActiveTreeIdBase", 0u);
                SetUniform(gl, program, "ActiveTreeIdCapacity", 1u);
                SetUniform(gl, program, "ActiveTreeBucket", 1u);
                SetUniform(gl, program, "DepthRangeTotalCount", (uint)depthRanges.Length);
                SetUniform(gl, program, "DepthParticleIdCount", (uint)depthParticleIds.Length);

                for (int iteration = 0; iteration < DispatchCount; ++iteration)
                {
                    gl.DispatchCompute(1u, 1u, 1u);
                    gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
                }

                ReadBuffer(gl, buffers[0], particles);
                AssertFiniteAndConstrained(particles, particleStatic);
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

    private static ParticleState[] CreateParticles()
    {
        Vector3[] positions =
        [
            Vector3.Zero,
            new(0.0f, -0.1f, 0.0f),
            new(-0.05f, -0.18660255f, 0.0f),
            new(0.05f, -0.18660255f, 0.0f),
            new(0.05f, -0.28660256f, 0.0f),
        ];
        var particles = new ParticleState[positions.Length];
        for (int index = 0; index < particles.Length; ++index)
            particles[index] = new ParticleState(positions[index]);
        return particles;
    }

    private static ParticleStatic[] CreateParticleStatic()
    {
        int[] parents = [-1, 0, 1, 1, 3];
        Vector3[] localOffsets =
        [
            Vector3.Zero,
            new(0.0f, -SegmentLength, 0.0f),
            new(-0.05f, -0.08660254f, 0.0f),
            new(0.05f, -0.08660254f, 0.0f),
            new(0.0f, -SegmentLength, 0.0f),
        ];
        var particles = new ParticleStatic[parents.Length];
        for (int index = 0; index < particles.Length; ++index)
        {
            particles[index] = new ParticleStatic
            {
                TransformLocalPosition = localOffsets[index],
                ParentIndex = parents[index],
                Damping = 0.05f,
                Elasticity = 0.1f,
                Stiffness = 0.25f,
                Radius = 0.01f,
                BoneLength = index == 0 ? 0.0f : SegmentLength,
            };
        }
        return particles;
    }

    private static Matrix4x4[] CreateTransforms()
    {
        ParticleState[] particles = CreateParticles();
        var transforms = new Matrix4x4[particles.Length];
        for (int index = 0; index < transforms.Length; ++index)
            transforms[index] = Matrix4x4.CreateTranslation(particles[index].Position);
        return transforms;
    }

    private static unsafe uint UploadBuffer<T>(GL gl, T[] data, BufferUsageARB usage)
        where T : unmanaged
    {
        uint buffer = gl.GenBuffer();
        fixed (T* dataPointer = data)
        {
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);
            gl.BufferData(BufferTargetARB.ShaderStorageBuffer, (nuint)(data.Length * sizeof(T)), dataPointer, usage);
        }
        return buffer;
    }

    private static void BindInputs(GL gl, uint[] buffers)
    {
        uint[] bindings = [0u, 1u, 3u, 4u, 5u, 6u, 7u, 8u, 9u];
        for (int index = 0; index < buffers.Length; ++index)
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, bindings[index], buffers[index]);
    }

    private static void SetUniform(GL gl, uint program, string name, int value)
        => gl.Uniform1(gl.GetUniformLocation(program, name), value);

    private static void SetUniform(GL gl, uint program, string name, uint value)
        => gl.Uniform1(gl.GetUniformLocation(program, name), value);

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

    private static void AssertFiniteAndConstrained(ParticleState[] particles, ParticleStatic[] particleStatic)
    {
        for (int index = 0; index < particles.Length; ++index)
        {
            Vector3 position = particles[index].Position;
            float.IsFinite(position.X).ShouldBeTrue();
            float.IsFinite(position.Y).ShouldBeTrue();
            float.IsFinite(position.Z).ShouldBeTrue();
            int parentIndex = particleStatic[index].ParentIndex;
            if (parentIndex < 0)
                continue;
            MathF.Abs(Vector3.Distance(position, particles[parentIndex].Position) - SegmentLength)
                .ShouldBeLessThanOrEqualTo(0.001f);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ParticleState
    {
        public Vector3 Position;
        public float Padding0;
        public Vector3 PrevPosition;
        public float Padding1;
        public int IsColliding;
        public Vector3 Padding2;
        public Vector3 PreviousPhysicsPosition;
        public float Padding3;

        public ParticleState(Vector3 position)
        {
            Position = position;
            PrevPosition = position;
            PreviousPhysicsPosition = position;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ParticleStatic
    {
        public Vector3 TransformLocalPosition;
        public float Padding0;
        public int ParentIndex;
        public float Damping;
        public float Elasticity;
        public float Stiffness;
        public float Inert;
        public float Friction;
        public float Radius;
        public float BoneLength;
        public int TreeIndex;
        public int Padding1;
        public int Padding2;
        public int Padding3;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ColliderData
    {
        public Vector4 Center;
        public Vector4 Params;
        public Vector4 Orientation;
        public int Type;
        public int Padding0;
        public int Padding1;
        public int Padding2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PerTreeParams
    {
        public float DeltaTime;
        public float ObjectScale;
        public float Weight;
        public int FreezeAxis;
        public Vector3 Force;
        public int ColliderCount;
        public Vector3 Gravity;
        public int ColliderOffset;
        public Vector3 ObjectMove;
        public float Padding0;
        public Vector3 RestGravity;
        public int ParticleOffset;
        public int ParticleCount;
        public int LoopCount;
        public int DepthRangeOffset;
        public int DepthRangeCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct DepthRange(uint ParticleIdOffset, uint ParticleCount);
}
