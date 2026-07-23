using System.Numerics;
using NUnit.Framework;
using Shouldly;
using Silk.NET.OpenGL;
using XREngine.Rendering.Compute;

namespace XREngine.UnitTests.Physics;

/// <summary>
/// GPU integration tests for the physics-chain compute shader's current
/// tree-owned dispatch contract.
/// </summary>
[TestFixture]
public class PhysicsChainComputeIntegrationTests : GpuTestBase
{
    private const uint ParticlesBinding = 0;
    private const uint ParticleStaticBinding = 1;
    private const uint TransformMatricesBinding = 3;
    private const uint CollidersBinding = 4;
    private const uint PerTreeParamsBinding = 5;

    [Test]
    public void PhysicsChain_ComputeShader_CompilesSuccessfully()
        => CompileShaderSuccessfully(Path.Combine("PhysicsChain", "PhysicsChain.comp"));

    [Test]
    public void PhysicsChainActiveWork_ComputeShader_CompilesSuccessfully()
        => CompileShaderSuccessfully(Path.Combine("PhysicsChain", "PhysicsChainActiveWork.comp"));

    [Test]
    public void PhysicsChainReadbackGather_ComputeShader_CompilesSuccessfully()
        => CompileShaderSuccessfully(Path.Combine("PhysicsChain", "PhysicsChainReadbackGather.comp"));

    [Test]
    public void SkipUpdateParticles_ComputeShader_CompilesSuccessfully()
        => CompileShaderSuccessfully(Path.Combine("PhysicsChain", "SkipUpdateParticles.comp"));

    [TestCase("PhysicsChainBonePalette.comp")]
    [TestCase("PhysicsChainBounds.comp")]
    [TestCase("PhysicsChainBoundsToScene.comp")]
    [TestCase("PhysicsChainBranched.comp")]
    [TestCase("PhysicsChainDebugDraw.comp")]
    public void PhysicsChainAuxiliary_ComputeShaders_CompileSuccessfully(string fileName)
        => CompileShaderSuccessfully(Path.Combine("PhysicsChain", fileName));

    [Test]
    public void PhysicsChain_HasCurrentDispatchUniforms()
    {
        var (gl, window) = CreateGLContext();
        if (gl is null || window is null)
        {
            Assert.Inconclusive("Could not create OpenGL context");
            return;
        }

        try
        {
            (uint shader, uint program) = CompilePhysicsChainProgram(gl);

            foreach (string uniformName in new[] { "ApplyObjectMove", "ParticleCount", "TreeCount" })
                gl.GetUniformLocation(program, uniformName).ShouldBeGreaterThanOrEqualTo(0, $"Expected active uniform '{uniformName}'.");

            foreach (string removedUniformName in new[] { "DeltaTime", "ObjectScale", "Weight", "Force", "Gravity", "ObjectMove", "FreezeAxis", "ColliderCount" })
                gl.GetUniformLocation(program, removedUniformName).ShouldBe(-1, $"'{removedUniformName}' must live in PerTreeParams, not the global uniform contract.");

            gl.DeleteProgram(program);
            gl.DeleteShader(shader);
        }
        finally
        {
            window.Close();
            window.Dispose();
        }
    }

    [Test]
    public unsafe void VerletIntegration_ProducesExpectedMotion()
    {
        var (gl, window) = CreateGLContext();
        if (gl is null || window is null)
        {
            Assert.Inconclusive("Could not create OpenGL context");
            return;
        }

        try
        {
            (uint shader, uint program) = CompilePhysicsChainProgram(gl);

            GPUPhysicsChainDispatcher.GPUParticleData[] particles =
            [
                new() { Position = Vector3.Zero, PrevPosition = Vector3.Zero },
                new() { Position = new Vector3(0.0f, -0.1f, 0.0f), PrevPosition = new Vector3(0.0f, -0.1f, 0.0f) },
            ];
            GPUPhysicsChainDispatcher.GPUParticleStaticData[] particleStatic =
            [
                CreateParticleStatic(parentIndex: -1, localPosition: Vector3.Zero, boneLength: 0.0f),
                CreateParticleStatic(parentIndex: 0, localPosition: new Vector3(0.0f, -0.1f, 0.0f), boneLength: 0.1f),
            ];
            Matrix4x4[] transforms =
            [
                Matrix4x4.Identity,
                Matrix4x4.CreateTranslation(0.0f, -0.1f, 0.0f),
            ];
            GPUPhysicsChainDispatcher.GPUColliderData[] colliders = [new()];
            GPUPhysicsChainDispatcher.GPUPerTreeParams[] treeParams =
            [
                CreateTreeParams(
                    deltaTime: 1.0f,
                    gravity: Vector3.UnitX,
                    colliderCount: 0,
                    particleCount: particles.Length),
            ];

            uint[] buffers = UploadAndBindInputs(gl, particles, particleStatic, transforms, colliders, treeParams);
            Dispatch(gl, program, particles.Length, treeParams.Length);
            ReadBuffer(gl, buffers[0], particles);

            Vector3.Distance(particles[0].Position, Vector3.Zero).ShouldBeLessThanOrEqualTo(0.001f);

            Vector3 expected = ComputeExpectedChildPosition(
                parentPosition: Vector3.Zero,
                childPosition: new Vector3(0.0f, -0.1f, 0.0f),
                childPrevPosition: new Vector3(0.0f, -0.1f, 0.0f),
                damping: 0.0f,
                gravity: Vector3.UnitX,
                force: Vector3.Zero,
                restGravity: Vector3.Zero,
                objectScale: 1.0f,
                deltaTime: 1.0f,
                boneLength: 0.1f);

            Vector3.Distance(particles[1].Position, expected).ShouldBeLessThanOrEqualTo(0.001f);

            DeleteBuffers(gl, buffers);
            gl.DeleteProgram(program);
            gl.DeleteShader(shader);
        }
        finally
        {
            window.Close();
            window.Dispose();
        }
    }

    [Test]
    public unsafe void SphereCollision_PushesParticleOut()
    {
        var (gl, window) = CreateGLContext();
        if (gl is null || window is null)
        {
            Assert.Inconclusive("Could not create OpenGL context");
            return;
        }

        try
        {
            (uint shader, uint program) = CompilePhysicsChainProgram(gl);

            GPUPhysicsChainDispatcher.GPUParticleData[] particles =
            [
                new() { Position = Vector3.Zero, PrevPosition = Vector3.Zero },
                new() { Position = new Vector3(0.05f, 0.0f, 0.0f), PrevPosition = new Vector3(0.05f, 0.0f, 0.0f) },
            ];
            GPUPhysicsChainDispatcher.GPUParticleStaticData[] particleStatic =
            [
                CreateParticleStatic(parentIndex: -1, localPosition: Vector3.Zero, boneLength: 0.0f, damping: 0.1f),
                CreateParticleStatic(parentIndex: 0, localPosition: new Vector3(0.1f, 0.0f, 0.0f), boneLength: 0.1f, damping: 0.1f),
            ];
            Matrix4x4[] transforms =
            [
                Matrix4x4.Identity,
                Matrix4x4.CreateTranslation(0.1f, 0.0f, 0.0f),
            ];
            GPUPhysicsChainDispatcher.GPUColliderData[] colliders =
            [
                new() { Center = new Vector4(Vector3.Zero, 0.1f), Type = 0 },
            ];
            GPUPhysicsChainDispatcher.GPUPerTreeParams[] treeParams =
            [
                CreateTreeParams(
                    deltaTime: 0.016f,
                    gravity: Vector3.Zero,
                    colliderCount: colliders.Length,
                    particleCount: particles.Length),
            ];

            uint[] buffers = UploadAndBindInputs(gl, particles, particleStatic, transforms, colliders, treeParams);
            Dispatch(gl, program, particles.Length, treeParams.Length);
            ReadBuffer(gl, buffers[0], particles);

            particles[1].IsColliding.ShouldBe(1);
            particles[1].Position.X.ShouldBeGreaterThan(0.05f);

            DeleteBuffers(gl, buffers);
            gl.DeleteProgram(program);
            gl.DeleteShader(shader);
        }
        finally
        {
            window.Close();
            window.Dispose();
        }
    }

    private void CompileShaderSuccessfully(string shaderRelativePath)
    {
        var (gl, window) = CreateGLContext();
        if (gl is null || window is null)
        {
            Assert.Inconclusive("Could not create OpenGL context");
            return;
        }

        try
        {
            string shaderPath = Path.Combine(ShaderBasePath, "Compute", shaderRelativePath);
            if (!File.Exists(shaderPath))
            {
                Assert.Inconclusive($"Shader file not found: {shaderPath}");
                return;
            }

            uint shader = CompileComputeShader(gl, File.ReadAllText(shaderPath));
            shader.ShouldBeGreaterThan(0u, "Compute shader should compile successfully");

            uint program = CreateComputeProgram(gl, shader);
            program.ShouldBeGreaterThan(0u, "Compute program should link successfully");

            gl.DeleteProgram(program);
            gl.DeleteShader(shader);
        }
        finally
        {
            window.Close();
            window.Dispose();
        }
    }

    private (uint Shader, uint Program) CompilePhysicsChainProgram(GL gl)
    {
        string shaderPath = Path.Combine(ShaderBasePath, "Compute", "PhysicsChain", "PhysicsChain.comp");
        if (!File.Exists(shaderPath))
            Assert.Inconclusive($"Shader file not found: {shaderPath}");

        uint shader = CompileComputeShader(gl, File.ReadAllText(shaderPath));
        return (shader, CreateComputeProgram(gl, shader));
    }

    private static GPUPhysicsChainDispatcher.GPUParticleStaticData CreateParticleStatic(
        int parentIndex,
        Vector3 localPosition,
        float boneLength,
        float damping = 0.0f)
        => new()
        {
            TransformLocalPosition = localPosition,
            ParentIndex = parentIndex,
            Damping = damping,
            Radius = 0.02f,
            BoneLength = boneLength,
            TreeIndex = 0,
        };

    private static GPUPhysicsChainDispatcher.GPUPerTreeParams CreateTreeParams(
        float deltaTime,
        Vector3 gravity,
        int colliderCount,
        int particleCount)
        => new()
        {
            DeltaTime = deltaTime,
            ObjectScale = 1.0f,
            Weight = 1.0f,
            Gravity = gravity,
            ColliderCount = colliderCount,
            ColliderOffset = 0,
            ParticleOffset = 0,
            ParticleCount = particleCount,
            LoopCount = 1,
        };

    private static unsafe uint[] UploadAndBindInputs(
        GL gl,
        GPUPhysicsChainDispatcher.GPUParticleData[] particles,
        GPUPhysicsChainDispatcher.GPUParticleStaticData[] particleStatic,
        Matrix4x4[] transforms,
        GPUPhysicsChainDispatcher.GPUColliderData[] colliders,
        GPUPhysicsChainDispatcher.GPUPerTreeParams[] treeParams)
    {
        uint[] buffers =
        [
            UploadBuffer(gl, particles, BufferUsageARB.DynamicDraw),
            UploadBuffer(gl, particleStatic, BufferUsageARB.StaticDraw),
            UploadBuffer(gl, transforms, BufferUsageARB.StaticDraw),
            UploadBuffer(gl, colliders, BufferUsageARB.StaticDraw),
            UploadBuffer(gl, treeParams, BufferUsageARB.DynamicDraw),
        ];

        gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, ParticlesBinding, buffers[0]);
        gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, ParticleStaticBinding, buffers[1]);
        gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, TransformMatricesBinding, buffers[2]);
        gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, CollidersBinding, buffers[3]);
        gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, PerTreeParamsBinding, buffers[4]);
        return buffers;
    }

    private static unsafe uint UploadBuffer<T>(GL gl, T[] data, BufferUsageARB usage) where T : unmanaged
    {
        uint buffer = gl.GenBuffer();
        fixed (T* dataPtr = data)
        {
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);
            gl.BufferData(BufferTargetARB.ShaderStorageBuffer, (nuint)(data.Length * sizeof(T)), dataPtr, usage);
        }
        return buffer;
    }

    private static void Dispatch(GL gl, uint program, int particleCount, int treeCount)
    {
        gl.UseProgram(program);
        gl.Uniform1(gl.GetUniformLocation(program, "ApplyObjectMove"), 1);
        gl.Uniform1(gl.GetUniformLocation(program, "ParticleCount"), particleCount);
        gl.Uniform1(gl.GetUniformLocation(program, "TreeCount"), treeCount);
        gl.DispatchCompute(1, 1, 1);
        gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);
    }

    private static unsafe void ReadBuffer<T>(GL gl, uint buffer, T[] destination) where T : unmanaged
    {
        fixed (T* destinationPtr = destination)
        {
            gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);
            gl.GetBufferSubData(BufferTargetARB.ShaderStorageBuffer, 0, (nuint)(destination.Length * sizeof(T)), destinationPtr);
        }
    }

    private static void DeleteBuffers(GL gl, uint[] buffers)
    {
        for (int i = 0; i < buffers.Length; ++i)
            gl.DeleteBuffer(buffers[i]);
    }

    private static Vector3 ComputeExpectedChildPosition(
        Vector3 parentPosition,
        Vector3 childPosition,
        Vector3 childPrevPosition,
        float damping,
        Vector3 gravity,
        Vector3 force,
        Vector3 restGravity,
        float objectScale,
        float deltaTime,
        float boneLength)
    {
        Vector3 velocity = childPosition - childPrevPosition;
        Vector3 forceDirection = gravity.LengthSquared() > 1e-8f ? Vector3.Normalize(gravity) : Vector3.Zero;
        Vector3 projectedRestForce = forceDirection * MathF.Max(Vector3.Dot(restGravity, forceDirection), 0.0f);
        Vector3 predicted = childPosition
            + velocity * (1.0f - damping)
            + (gravity - projectedRestForce + force) * (objectScale * deltaTime);

        Vector3 difference = parentPosition - predicted;
        float length = difference.Length();
        if (length > 1e-4f)
            predicted += difference * ((length - boneLength) / length);

        return predicted;
    }
}
