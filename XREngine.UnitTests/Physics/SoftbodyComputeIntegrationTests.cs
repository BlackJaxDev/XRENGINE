using System.Numerics;
using NUnit.Framework;
using Shouldly;
using Silk.NET.OpenGL;
using XREngine.Data.Vectors;
using XREngine.Rendering.Compute;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class SoftbodyComputeIntegrationTests : GpuTestBase
{
    [Test]
    public unsafe void Integrate_ProducesExpectedParticleMotion()
    {
        RunWithGLContext(gl =>
        {
            AssertHardwareComputeOrInconclusive(gl);

            uint shader = CompileComputeShader(gl, LoadShaderSource("Compute/Softbody/Integrate.comp"));
            uint program = CreateComputeProgram(gl, shader);

            try
            {
                GPUSoftbodyParticleData[] particles =
                [
                    new GPUSoftbodyParticleData
                    {
                        CurrentPosition = Vector3.Zero,
                        PreviousPosition = Vector3.Zero,
                        RestPosition = Vector3.Zero,
                        InverseMass = 1.0f,
                        Radius = 0.05f,
                        InstanceIndex = 0,
                    }
                ];

                GPUSoftbodyDispatchData[] dispatches =
                [
                    new GPUSoftbodyDispatchData
                    {
                        ParticleConstraintRanges = new IVector4(0, 1, 0, 0),
                        ClusterRanges = new IVector4(0, 0, 0, 0),
                        ColliderBindingRanges = new IVector4(0, 0, 0, 0),
                        SimulationScalars = new Vector4(1.0f, 0.0f, 0.0f, 0.0f),
                        GravitySubsteps = new Vector4(1.0f, 0.0f, 0.0f, 1.0f),
                        ForceIterations = new Vector4(Vector3.Zero, 1.0f),
                    }
                ];

                uint particleBuffer = CreateShaderStorageBuffer(gl, particles, BufferUsageARB.DynamicDraw);
                uint dispatchBuffer = CreateShaderStorageBuffer(gl, dispatches, BufferUsageARB.DynamicDraw);

                try
                {
                    gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0, particleBuffer);
                    gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 6, dispatchBuffer);

                    gl.UseProgram(program);
                    gl.Uniform1(gl.GetUniformLocation(program, "particleCount"), particles.Length);
                    gl.Uniform1(gl.GetUniformLocation(program, "instanceCount"), dispatches.Length);
                    gl.Uniform1(gl.GetUniformLocation(program, "currentSubstep"), 0);

                    gl.DispatchCompute(1, 1, 1);
                    gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

                    GPUSoftbodyParticleData[] results = ReadShaderStorageBuffer<GPUSoftbodyParticleData>(gl, particleBuffer, particles.Length);
                    results[0].CurrentPosition.X.ShouldBe(1.0f, 0.001f);
                    results[0].CurrentPosition.Y.ShouldBe(0.0f, 0.001f);
                    results[0].CurrentPosition.Z.ShouldBe(0.0f, 0.001f);
                }
                finally
                {
                    gl.DeleteBuffer(particleBuffer);
                    gl.DeleteBuffer(dispatchBuffer);
                }
            }
            finally
            {
                gl.DeleteProgram(program);
                gl.DeleteShader(shader);
            }
        });
    }

    [Test]
    public unsafe void CapsuleCollision_PushesParticleOutOfPenetration()
    {
        RunWithGLContext(gl =>
        {
            AssertHardwareComputeOrInconclusive(gl);

            uint shader = CompileComputeShader(gl, LoadShaderSource("Compute/Softbody/CollideCapsules.comp"));
            uint program = CreateComputeProgram(gl, shader);

            try
            {
                GPUSoftbodyParticleData[] particles =
                [
                    new GPUSoftbodyParticleData
                    {
                        CurrentPosition = Vector3.Zero,
                        PreviousPosition = Vector3.Zero,
                        RestPosition = Vector3.Zero,
                        InverseMass = 1.0f,
                        Radius = 0.2f,
                        InstanceIndex = 0,
                    }
                ];

                GPUSoftbodyColliderData[] colliders =
                [
                    new GPUSoftbodyColliderData
                    {
                        SegmentStartRadius = new Vector4(0.0f, -1.0f, 0.0f, 0.5f),
                        SegmentEndFriction = new Vector4(0.0f, 1.0f, 0.0f, 0.0f),
                        VelocityAndDrag = new Vector4(0.0f, 0.0f, 0.0f, 1.0f),
                        Type = (int)GPUSoftbodyColliderType.Capsule,
                        InstanceIndex = 0,
                        Margin = 0.1f,
                    }
                ];

                GPUSoftbodyDispatchData[] dispatches =
                [
                    new GPUSoftbodyDispatchData
                    {
                        ParticleConstraintRanges = new IVector4(0, 1, 0, 0),
                        ClusterRanges = new IVector4(0, 0, 0, 0),
                        ColliderBindingRanges = new IVector4(0, 1, 0, 0),
                        SimulationScalars = new Vector4(1.0f, 0.0f, 0.1f, 0.0f),
                        GravitySubsteps = new Vector4(Vector3.Zero, 1.0f),
                        ForceIterations = new Vector4(Vector3.Zero, 1.0f),
                    }
                ];

                uint particleBuffer = CreateShaderStorageBuffer(gl, particles, BufferUsageARB.DynamicDraw);
                uint colliderBuffer = CreateShaderStorageBuffer(gl, colliders, BufferUsageARB.DynamicDraw);
                uint dispatchBuffer = CreateShaderStorageBuffer(gl, dispatches, BufferUsageARB.DynamicDraw);

                try
                {
                    gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0, particleBuffer);
                    gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 4, colliderBuffer);
                    gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 6, dispatchBuffer);

                    gl.UseProgram(program);
                    gl.Uniform1(gl.GetUniformLocation(program, "particleCount"), particles.Length);
                    gl.Uniform1(gl.GetUniformLocation(program, "colliderCount"), colliders.Length);
                    gl.Uniform1(gl.GetUniformLocation(program, "currentSubstep"), 0);

                    gl.DispatchCompute(1, 1, 1);
                    gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

                    GPUSoftbodyParticleData[] results = ReadShaderStorageBuffer<GPUSoftbodyParticleData>(gl, particleBuffer, particles.Length);
                    float distance = Vector3.Distance(results[0].CurrentPosition, Vector3.Zero);
                    distance.ShouldBe(0.8f, 0.01f);
                }
                finally
                {
                    gl.DeleteBuffer(particleBuffer);
                    gl.DeleteBuffer(colliderBuffer);
                    gl.DeleteBuffer(dispatchBuffer);
                }
            }
            finally
            {
                gl.DeleteProgram(program);
                gl.DeleteShader(shader);
            }
        });
    }

    [Test]
    public unsafe void DistanceSolve_RemainsStableAcrossSeveralFrames()
    {
        RunWithGLContext(gl =>
        {
            AssertHardwareComputeOrInconclusive(gl);

            uint integrateShader = CompileComputeShader(gl, LoadShaderSource("Compute/Softbody/Integrate.comp"));
            uint solveShader = CompileComputeShader(gl, LoadShaderSource("Compute/Softbody/SolveDistance.comp"));
            uint integrateProgram = CreateComputeProgram(gl, integrateShader);
            uint solveProgram = CreateComputeProgram(gl, solveShader);

            try
            {
                GPUSoftbodyParticleData[] particles =
                [
                    new GPUSoftbodyParticleData
                    {
                        CurrentPosition = Vector3.Zero,
                        PreviousPosition = Vector3.Zero,
                        RestPosition = Vector3.Zero,
                        InverseMass = 0.0f,
                        Radius = 0.05f,
                        InstanceIndex = 0,
                    },
                    new GPUSoftbodyParticleData
                    {
                        CurrentPosition = new Vector3(0.0f, -1.0f, 0.0f),
                        PreviousPosition = new Vector3(0.0f, -1.0f, 0.0f),
                        RestPosition = new Vector3(0.0f, -1.0f, 0.0f),
                        InverseMass = 1.0f,
                        Radius = 0.05f,
                        InstanceIndex = 0,
                    }
                ];

                GPUSoftbodyDistanceConstraintData[] constraints =
                [
                    new GPUSoftbodyDistanceConstraintData
                    {
                        ParticleA = 0,
                        ParticleB = 1,
                        RestLength = 1.0f,
                        Compliance = 0.0f,
                        Stiffness = 1.0f,
                        InstanceIndex = 0,
                    }
                ];

                GPUSoftbodyDispatchData[] dispatches =
                [
                    new GPUSoftbodyDispatchData
                    {
                        ParticleConstraintRanges = new IVector4(0, particles.Length, 0, constraints.Length),
                        ClusterRanges = new IVector4(0, 0, 0, 0),
                        ColliderBindingRanges = new IVector4(0, 0, 0, 0),
                        SimulationScalars = new Vector4(1.0f / 60.0f, 0.02f, 0.0f, 0.0f),
                        GravitySubsteps = new Vector4(0.0f, -9.8f, 0.0f, 2.0f),
                        ForceIterations = new Vector4(Vector3.Zero, 6.0f),
                    }
                ];

                uint currentParticleBuffer = CreateShaderStorageBuffer(gl, particles, BufferUsageARB.DynamicDraw);
                uint scratchParticleBuffer = CreateShaderStorageBuffer(gl, particles, BufferUsageARB.DynamicDraw);
                uint constraintBuffer = CreateShaderStorageBuffer(gl, constraints, BufferUsageARB.DynamicDraw);
                uint dispatchBuffer = CreateShaderStorageBuffer(gl, dispatches, BufferUsageARB.DynamicDraw);

                try
                {
                    const int frameCount = 60;
                    const int substeps = 2;
                    const int solverIterations = 6;

                    for (int frame = 0; frame < frameCount; frame++)
                    {
                        for (int currentSubstep = 0; currentSubstep < substeps; currentSubstep++)
                        {
                            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0, currentParticleBuffer);
                            gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 6, dispatchBuffer);

                            gl.UseProgram(integrateProgram);
                            gl.Uniform1(gl.GetUniformLocation(integrateProgram, "particleCount"), particles.Length);
                            gl.Uniform1(gl.GetUniformLocation(integrateProgram, "instanceCount"), dispatches.Length);
                            gl.Uniform1(gl.GetUniformLocation(integrateProgram, "currentSubstep"), currentSubstep);
                            gl.DispatchCompute(1, 1, 1);
                            gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

                            for (int currentSolverIteration = 0; currentSolverIteration < solverIterations; currentSolverIteration++)
                            {
                                gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 0, currentParticleBuffer);
                                gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 1, constraintBuffer);
                                gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 6, dispatchBuffer);
                                gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, 7, scratchParticleBuffer);

                                gl.UseProgram(solveProgram);
                                gl.Uniform1(gl.GetUniformLocation(solveProgram, "particleCount"), particles.Length);
                                gl.Uniform1(gl.GetUniformLocation(solveProgram, "constraintCount"), constraints.Length);
                                gl.Uniform1(gl.GetUniformLocation(solveProgram, "currentSubstep"), currentSubstep);
                                gl.Uniform1(gl.GetUniformLocation(solveProgram, "currentSolverIteration"), currentSolverIteration);
                                gl.DispatchCompute(1, 1, 1);
                                gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit);

                                (currentParticleBuffer, scratchParticleBuffer) = (scratchParticleBuffer, currentParticleBuffer);
                            }
                        }
                    }

                    GPUSoftbodyParticleData[] results = ReadShaderStorageBuffer<GPUSoftbodyParticleData>(gl, currentParticleBuffer, particles.Length);
                    Vector3 rootPosition = results[0].CurrentPosition;
                    Vector3 childPosition = results[1].CurrentPosition;
                    float distance = Vector3.Distance(rootPosition, childPosition);

                    float.IsFinite(childPosition.X).ShouldBeTrue();
                    float.IsFinite(childPosition.Y).ShouldBeTrue();
                    float.IsFinite(childPosition.Z).ShouldBeTrue();
                    distance.ShouldBeGreaterThan(0.85f);
                    distance.ShouldBeLessThan(1.15f);
                }
                finally
                {
                    gl.DeleteBuffer(currentParticleBuffer);
                    gl.DeleteBuffer(scratchParticleBuffer);
                    gl.DeleteBuffer(constraintBuffer);
                    gl.DeleteBuffer(dispatchBuffer);
                }
            }
            finally
            {
                gl.DeleteProgram(integrateProgram);
                gl.DeleteProgram(solveProgram);
                gl.DeleteShader(integrateShader);
                gl.DeleteShader(solveShader);
            }
        });
    }

    private static unsafe uint CreateShaderStorageBuffer<T>(GL gl, T[] data, BufferUsageARB usage) where T : unmanaged
    {
        uint buffer = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);
        fixed (T* ptr = data)
            gl.BufferData(BufferTargetARB.ShaderStorageBuffer, (nuint)(data.Length * sizeof(T)), ptr, usage);
        return buffer;
    }

    private static unsafe T[] ReadShaderStorageBuffer<T>(GL gl, uint buffer, int count) where T : unmanaged
    {
        T[] results = new T[count];
        gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, buffer);
        fixed (T* ptr = results)
            gl.GetBufferSubData(BufferTargetARB.ShaderStorageBuffer, 0, (nuint)(results.Length * sizeof(T)), ptr);
        return results;
    }
}
