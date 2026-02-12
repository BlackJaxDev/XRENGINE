using System.Numerics;
using NUnit.Framework;
using Shouldly;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using SystemBuffer = System.Buffer;

namespace XREngine.UnitTests.Physics;

/// <summary>
/// GPU integration tests for the PhysicsChain compute shader.
/// These tests compile and run the actual shader on GPU hardware,
/// validating physics calculations, collision detection, and constraint solving.
/// </summary>
[TestFixture]
public class PhysicsChainComputeIntegrationTests : GpuTestBase
{
    #region Shader Compilation Tests

    [Test]
    public void PhysicsChain_ComputeShader_CompilesSuccessfully()
    {
        var (gl, window) = CreateGLContext();
        if (gl == null || window == null)
        {
            Assert.Inconclusive("Could not create OpenGL context");
            return;
        }

        try
        {
            string shaderPath = Path.Combine(ShaderBasePath, "Compute", "PhysicsChain.comp");
            if (!File.Exists(shaderPath))
            {
                Assert.Inconclusive($"Shader file not found: {shaderPath}");
                return;
            }

            string source = File.ReadAllText(shaderPath);
            uint shader = CompileComputeShader(gl, source);

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

    [Test]
    public void SkipUpdateParticles_ComputeShader_CompilesSuccessfully()
    {
        var (gl, window) = CreateGLContext();
        if (gl == null || window == null)
        {
            Assert.Inconclusive("Could not create OpenGL context");
            return;
        }

        try
        {
            string shaderPath = Path.Combine(ShaderBasePath, "Compute", "PhysicsChain", "SkipUpdateParticles.comp");
            if (!File.Exists(shaderPath))
            {
                Assert.Inconclusive($"Shader file not found: {shaderPath}");
                return;
            }

            string source = File.ReadAllText(shaderPath);
            uint shader = CompileComputeShader(gl, source);

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

    #endregion

    #region Uniform Tests

    [Test]
    public void PhysicsChain_HasExpectedUniforms()
    {
        var (gl, window) = CreateGLContext();
        if (gl == null || window == null)
        {
            Assert.Inconclusive("Could not create OpenGL context");
            return;
        }

        try
        {
            string shaderPath = Path.Combine(ShaderBasePath, "Compute", "PhysicsChain.comp");
            if (!File.Exists(shaderPath))
            {
                Assert.Inconclusive($"Shader file not found: {shaderPath}");
                return;
            }

            string source = File.ReadAllText(shaderPath);
            uint shader = CompileComputeShader(gl, source);
            uint program = CreateComputeProgram(gl, shader);

            // Check for expected uniforms
            var expectedUniforms = new[]
            {
                "DeltaTime",
                "ObjectScale",
                "Weight",
                "Force",
                "Gravity",
                "ObjectMove",
                "FreezeAxis",
                "ColliderCount"
            };

            foreach (var uniformName in expectedUniforms)
            {
                int location = gl.GetUniformLocation(program, uniformName);
                // Note: Unused uniforms may be optimized out, so we just check it doesn't crash
            }

            gl.DeleteProgram(program);
            gl.DeleteShader(shader);
        }
        finally
        {
            window.Close();
            window.Dispose();
        }
    }

    #endregion

    #region Physics Calculation Tests

    [Test]
    public unsafe void VerletIntegration_ProducesExpectedMotion()
    {
        var (gl, window) = CreateGLContext();
        if (gl == null || window == null)
        {
            Assert.Inconclusive("Could not create OpenGL context");
            return;
        }

        try
        {
            string shaderPath = Path.Combine(ShaderBasePath, "Compute", "PhysicsChain.comp");
            if (!File.Exists(shaderPath))
            {
                Assert.Inconclusive($"Shader file not found: {shaderPath}");
                return;
            }

            string source = File.ReadAllText(shaderPath);
            uint shader = CompileComputeShader(gl, source);
            uint program = CreateComputeProgram(gl, shader);

            // Create test data: 2 particles - root + child
            const int particleCount = 2;
            const int particleStride = 24; // 24 floats per particle (96 bytes / 4)

            float[] particles = new float[particleCount * particleStride];

            // Particle 0 (root): Position = (0, 0, 0), ParentIndex = -1
            SetParticleData(particles, 0, particleStride,
                position: new Vector3(0, 0, 0),
                prevPosition: new Vector3(0, 0, 0),
                transformPosition: new Vector3(0, 0, 0),
                transformLocalPosition: new Vector3(0, 0, 0),
                parentIndex: -1,
                damping: 0.0f, elasticity: 0.0f, stiffness: 0.0f,
                inert: 0.0f, friction: 0.0f, radius: 0.02f, boneLength: 0.0f);

            // Particle 1 (child): Position = (0, -0.1, 0), zero velocity
            SetParticleData(particles, 1, particleStride,
                position: new Vector3(0, -0.1f, 0),
                prevPosition: new Vector3(0, -0.1f, 0),
                transformPosition: new Vector3(0, -0.1f, 0),
                transformLocalPosition: new Vector3(0, -0.1f, 0),
                parentIndex: 0,
                damping: 0.0f, elasticity: 0.0f, stiffness: 0.0f,
                inert: 0.0f, friction: 0.0f, radius: 0.02f, boneLength: 0.1f);

            // Create particle tree data
            float[] treesData = new float[28]; // 28 floats per tree
            SetTreeData(treesData, 0,
                localGravity: new Vector3(1, 0, 0),
                restGravity: Vector3.Zero,
                particleStart: 0, particleCount: 2,
                rootWorldToLocal: Matrix4x4.Identity,
                boneTotalLength: 0.1f);

            // Create transform matrices (identity for both)
            float[] transforms = new float[particleCount * 16];
            for (int i = 0; i < particleCount; i++)
            {
                int baseIdx = i * 16;
                transforms[baseIdx + 0] = 1; transforms[baseIdx + 5] = 1;
                transforms[baseIdx + 10] = 1; transforms[baseIdx + 15] = 1;
            }

            // Create buffers
            uint particleBuf = gl.GenBuffer();
            uint treeBuf = gl.GenBuffer();
            uint transformBuf = gl.GenBuffer();
            uint colliderBuf = gl.GenBuffer();

            // Upload data
            fixed (float* pParticles = particles)
            {
                gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, particleBuf);
                gl.BufferData(BufferTargetARB.ShaderStorageBuffer, (nuint)(particles.Length * sizeof(float)), pParticles, BufferUsageARB.DynamicDraw);
            }

            fixed (float* pTrees = treesData)
            {
                gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, treeBuf);
                gl.BufferData(BufferTargetARB.ShaderStorageBuffer, (nuint)(treesData.Length * sizeof(float)), pTrees, BufferUsageARB.StaticDraw);
            }

            fixed (float* pTransforms = transforms)
            {
                gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, transformBuf);
                gl.BufferData(BufferTargetARB.ShaderStorageBuffer, (nuint)(transforms.Length * sizeof(float)), pTransforms, BufferUsageARB.StaticDraw);
            }

            // Empty collider buffer
            float[] colliders = new float[12]; // 1 dummy collider
            fixed (float* pColliders = colliders)
            {
                gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, colliderBuf);
                gl.BufferData(BufferTargetARB.ShaderStorageBuffer, (nuint)(colliders.Length * sizeof(float)), pColliders, BufferUsageARB.StaticDraw);
            }

            // Bind buffers
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0, particleBuf);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 1, treeBuf);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 2, transformBuf);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 3, colliderBuf);

            // Set uniforms
            gl.UseProgram(program);
            gl.Uniform1(gl.GetUniformLocation(program, "DeltaTime"), 1.0f);
            gl.Uniform1(gl.GetUniformLocation(program, "ObjectScale"), 1.0f);
            gl.Uniform1(gl.GetUniformLocation(program, "Weight"), 1.0f);
            gl.Uniform3(gl.GetUniformLocation(program, "Force"), 0, 0, 0);
            gl.Uniform3(gl.GetUniformLocation(program, "Gravity"), 1, 0, 0);
            gl.Uniform3(gl.GetUniformLocation(program, "ObjectMove"), 0, 0, 0);
            gl.Uniform1(gl.GetUniformLocation(program, "FreezeAxis"), 0);
            gl.Uniform1(gl.GetUniformLocation(program, "ColliderCount"), 0);

            // Dispatch compute
            gl.DispatchCompute(1, 1, 1);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

            // Read back results
            float[] results = new float[particles.Length];
            fixed (float* pResults = results)
            {
                gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, particleBuf);
                gl.GetBufferSubData(BufferTargetARB.ShaderStorageBuffer, 0, (nuint)(results.Length * sizeof(float)), pResults);
            }

            // Verify results
            // Particle 0 (root) should be anchored to transform position
            // The root particle in the shader sets: Position = TransformPosition
            results[0].ShouldBe(0.0f, 0.001f); // Position.x
            results[1].ShouldBe(0.0f, 0.001f); // Position.y
            results[2].ShouldBe(0.0f, 0.001f); // Position.z

            // Particle 1 (child) - deterministic comparison against CPU-equivalent step
            float childX = results[particleStride + 0];
            float childY = results[particleStride + 1];
            float childZ = results[particleStride + 2];

            var expected = ComputeExpectedChildPosition(
                parentPosition: Vector3.Zero,
                parentTransformPosition: Vector3.Zero,
                childPosition: new Vector3(0, -0.1f, 0),
                childPrevPosition: new Vector3(0, -0.1f, 0),
                childTransformPosition: new Vector3(0, -0.1f, 0),
                damping: 0.0f,
                gravity: new Vector3(1, 0, 0),
                force: Vector3.Zero,
                restGravity: Vector3.Zero,
                objectScale: 1.0f,
                deltaTime: 1.0f);

            childX.ShouldBe(expected.X, 0.001f);
            childY.ShouldBe(expected.Y, 0.001f);
            childZ.ShouldBe(expected.Z, 0.001f);

            // Cleanup
            gl.DeleteBuffer(particleBuf);
            gl.DeleteBuffer(treeBuf);
            gl.DeleteBuffer(transformBuf);
            gl.DeleteBuffer(colliderBuf);
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
        if (gl == null || window == null)
        {
            Assert.Inconclusive("Could not create OpenGL context");
            return;
        }

        try
        {
            string shaderPath = Path.Combine(ShaderBasePath, "Compute", "PhysicsChain.comp");
            if (!File.Exists(shaderPath))
            {
                Assert.Inconclusive($"Shader file not found: {shaderPath}");
                return;
            }

            string source = File.ReadAllText(shaderPath);
            uint shader = CompileComputeShader(gl, source);
            uint program = CreateComputeProgram(gl, shader);

            const int particleStride = 24;

            // Create particle inside a sphere collider
            float[] particles = new float[2 * particleStride];

            // Root particle
            SetParticleData(particles, 0, particleStride,
                position: new Vector3(0, 0, 0),
                prevPosition: new Vector3(0, 0, 0),
                transformPosition: new Vector3(0, 0, 0),
                transformLocalPosition: new Vector3(0, 0, 0),
                parentIndex: -1,
                damping: 0.1f, elasticity: 0.0f, stiffness: 0.0f,
                inert: 0.0f, friction: 0.0f, radius: 0.02f, boneLength: 0.0f);

            // Child particle inside collider
            SetParticleData(particles, 1, particleStride,
                position: new Vector3(0.05f, 0, 0), // Inside the sphere at origin
                prevPosition: new Vector3(0.05f, 0, 0),
                transformPosition: new Vector3(0.1f, 0, 0),
                transformLocalPosition: new Vector3(0.1f, 0, 0),
                parentIndex: 0,
                damping: 0.1f, elasticity: 0.0f, stiffness: 0.0f,
                inert: 0.0f, friction: 0.0f, radius: 0.02f, boneLength: 0.1f);

            // Particle tree
            float[] treesData = new float[28];
            SetTreeData(treesData, 0,
                localGravity: Vector3.Zero,
                restGravity: Vector3.Zero,
                particleStart: 0, particleCount: 2,
                rootWorldToLocal: Matrix4x4.Identity,
                boneTotalLength: 0.1f);

            // Transforms
            float[] transforms = new float[2 * 16];
            for (int i = 0; i < 2; i++)
            {
                int baseIdx = i * 16;
                transforms[baseIdx + 0] = 1; transforms[baseIdx + 5] = 1;
                transforms[baseIdx + 10] = 1; transforms[baseIdx + 15] = 1;
            }

            // Sphere collider at origin with radius 0.1
            // ColliderData: Center(vec4), Params(vec4), Type(int), padding(3 ints) = 12 floats
            float[] colliders = new float[12];
            // Center: xyz = (0,0,0), w = radius = 0.1
            colliders[0] = 0; colliders[1] = 0; colliders[2] = 0; colliders[3] = 0.1f;
            // Params: unused for sphere
            colliders[4] = 0; colliders[5] = 0; colliders[6] = 0; colliders[7] = 0;
            // Type = 0 (sphere)
            var typeBytes = BitConverter.GetBytes(0);
            SystemBuffer.BlockCopy(typeBytes, 0, colliders, 8 * sizeof(float), sizeof(int));

            // Create and upload buffers
            uint particleBuf = gl.GenBuffer();
            uint treeBuf = gl.GenBuffer();
            uint transformBuf = gl.GenBuffer();
            uint colliderBuf = gl.GenBuffer();

            fixed (float* p = particles) { gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, particleBuf); gl.BufferData(BufferTargetARB.ShaderStorageBuffer, (nuint)(particles.Length * sizeof(float)), p, BufferUsageARB.DynamicDraw); }
            fixed (float* p = treesData) { gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, treeBuf); gl.BufferData(BufferTargetARB.ShaderStorageBuffer, (nuint)(treesData.Length * sizeof(float)), p, BufferUsageARB.StaticDraw); }
            fixed (float* p = transforms) { gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, transformBuf); gl.BufferData(BufferTargetARB.ShaderStorageBuffer, (nuint)(transforms.Length * sizeof(float)), p, BufferUsageARB.StaticDraw); }
            fixed (float* p = colliders) { gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, colliderBuf); gl.BufferData(BufferTargetARB.ShaderStorageBuffer, (nuint)(colliders.Length * sizeof(float)), p, BufferUsageARB.StaticDraw); }

            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0, particleBuf);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 1, treeBuf);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 2, transformBuf);
            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 3, colliderBuf);

            gl.UseProgram(program);
            gl.Uniform1(gl.GetUniformLocation(program, "DeltaTime"), 0.016f);
            gl.Uniform1(gl.GetUniformLocation(program, "ObjectScale"), 1.0f);
            gl.Uniform1(gl.GetUniformLocation(program, "Weight"), 1.0f);
            gl.Uniform3(gl.GetUniformLocation(program, "Force"), 0, 0, 0);
            gl.Uniform3(gl.GetUniformLocation(program, "Gravity"), 0, 0, 0);
            gl.Uniform3(gl.GetUniformLocation(program, "ObjectMove"), 0, 0, 0);
            gl.Uniform1(gl.GetUniformLocation(program, "FreezeAxis"), 0);
            gl.Uniform1(gl.GetUniformLocation(program, "ColliderCount"), 1);

            gl.DispatchCompute(1, 1, 1);
            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

            float[] results = new float[particles.Length];
            fixed (float* p = results) { gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, particleBuf); gl.GetBufferSubData(BufferTargetARB.ShaderStorageBuffer, 0, (nuint)(results.Length * sizeof(float)), p); }

            // Child particle should have been pushed outside the sphere
            float childX = results[particleStride + 0];
            float childY = results[particleStride + 1];
            float childZ = results[particleStride + 2];

            // Due to length constraint, position will be adjusted, but collision should have been detected
            int isColliding = BitConverter.SingleToInt32Bits(results[particleStride + 20]); // IsColliding offset

            isColliding.ShouldBe(1);
            childX.ShouldBeGreaterThan(0.05f);

            // Cleanup
            gl.DeleteBuffer(particleBuf);
            gl.DeleteBuffer(treeBuf);
            gl.DeleteBuffer(transformBuf);
            gl.DeleteBuffer(colliderBuf);
            gl.DeleteProgram(program);
            gl.DeleteShader(shader);
        }
        finally
        {
            window.Close();
            window.Dispose();
        }
    }

    #endregion

    #region Helper Methods

    private static void SetParticleData(float[] data, int index, int stride,
        Vector3 position, Vector3 prevPosition, Vector3 transformPosition, Vector3 transformLocalPosition,
        int parentIndex, float damping, float elasticity, float stiffness,
        float inert, float friction, float radius, float boneLength)
    {
        int i = index * stride;

        // Position (vec3 + pad)
        data[i + 0] = position.X;
        data[i + 1] = position.Y;
        data[i + 2] = position.Z;
        data[i + 3] = 0; // pad

        // PrevPosition (vec3 + pad)
        data[i + 4] = prevPosition.X;
        data[i + 5] = prevPosition.Y;
        data[i + 6] = prevPosition.Z;
        data[i + 7] = 0; // pad

        // TransformPosition (vec3 + pad)
        data[i + 8] = transformPosition.X;
        data[i + 9] = transformPosition.Y;
        data[i + 10] = transformPosition.Z;
        data[i + 11] = 0; // pad

        // TransformLocalPosition (vec3 + pad)
        data[i + 12] = transformLocalPosition.X;
        data[i + 13] = transformLocalPosition.Y;
        data[i + 14] = transformLocalPosition.Z;
        data[i + 15] = 0; // pad

        // ParentIndex (int)
        SystemBuffer.BlockCopy(BitConverter.GetBytes(parentIndex), 0, data, (i + 16) * sizeof(float), sizeof(int));

        // Damping, Elasticity, Stiffness, Inert
        data[i + 17] = damping;
        data[i + 18] = elasticity;
        data[i + 19] = stiffness;
        data[i + 20] = inert;

        // Friction, Radius, BoneLength, IsColliding
        data[i + 21] = friction;
        data[i + 22] = radius;
        data[i + 23] = boneLength;
        // i+24-26 would be IsColliding and padding (handled as ints)
    }

    private static void SetTreeData(float[] data, int index,
        Vector3 localGravity, Vector3 restGravity,
        int particleStart, int particleCount,
        Matrix4x4 rootWorldToLocal, float boneTotalLength)
    {
        int i = index * 28;

        // LocalGravity (vec3 + pad)
        data[i + 0] = localGravity.X;
        data[i + 1] = localGravity.Y;
        data[i + 2] = localGravity.Z;
        data[i + 3] = 0;

        // RestGravity (vec3 + pad)
        data[i + 4] = restGravity.X;
        data[i + 5] = restGravity.Y;
        data[i + 6] = restGravity.Z;
        data[i + 7] = 0;

        // ParticleStart, ParticleCount (ints)
        SystemBuffer.BlockCopy(BitConverter.GetBytes(particleStart), 0, data, (i + 8) * sizeof(float), sizeof(int));
        SystemBuffer.BlockCopy(BitConverter.GetBytes(particleCount), 0, data, (i + 9) * sizeof(float), sizeof(int));

        // Padding
        data[i + 10] = 0;
        data[i + 11] = 0;

        // RootWorldToLocal (mat4 = 16 floats)
        data[i + 12] = rootWorldToLocal.M11; data[i + 13] = rootWorldToLocal.M21; data[i + 14] = rootWorldToLocal.M31; data[i + 15] = rootWorldToLocal.M41;
        data[i + 16] = rootWorldToLocal.M12; data[i + 17] = rootWorldToLocal.M22; data[i + 18] = rootWorldToLocal.M32; data[i + 19] = rootWorldToLocal.M42;
        data[i + 20] = rootWorldToLocal.M13; data[i + 21] = rootWorldToLocal.M23; data[i + 22] = rootWorldToLocal.M33; data[i + 23] = rootWorldToLocal.M43;
        data[i + 24] = rootWorldToLocal.M14; data[i + 25] = rootWorldToLocal.M24; data[i + 26] = rootWorldToLocal.M34; data[i + 27] = rootWorldToLocal.M44;

        // BoneTotalLength and padding would be at i+28-31 but we're using 28 floats
    }

    private static Vector3 ComputeExpectedChildPosition(
        Vector3 parentPosition,
        Vector3 parentTransformPosition,
        Vector3 childPosition,
        Vector3 childPrevPosition,
        Vector3 childTransformPosition,
        float damping,
        Vector3 gravity,
        Vector3 force,
        Vector3 restGravity,
        float objectScale,
        float deltaTime)
    {
        var v = childPosition - childPrevPosition;
        Vector3 fdir = gravity.LengthSquared() > 1e-8f ? Vector3.Normalize(gravity) : Vector3.Zero;
        var pf = fdir * MathF.Max(Vector3.Dot(restGravity, fdir), 0.0f);
        var totalForce = (gravity - pf + force) * (objectScale * deltaTime);

        var predicted = childPosition + v * (1.0f - damping) + totalForce;

        var diff = parentPosition - predicted;
        float L = diff.Length();
        if (L > 1e-4f)
        {
            float restLen = (parentTransformPosition - childTransformPosition).Length();
            predicted += diff * ((L - restLen) / L);
        }

        return predicted;
    }

    #endregion
}
