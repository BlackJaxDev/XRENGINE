using System;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using Shouldly;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using XREngine.Rendering.OpenGL;
using static XREngine.Rendering.OpenGL.OpenGLRenderer;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Tests for the async shader pipeline: <see cref="GLSharedContext"/>,
/// <see cref="GLProgramBinaryUploadQueue"/>, and <see cref="GLProgramCompileLinkQueue"/>.
/// <para>
/// GPU integration tests create real GLFW windows with shared contexts.
/// Headless CI environments (no GPU) automatically skip via <c>Assert.Inconclusive</c>.
/// </para>
/// </summary>
[TestFixture]
public sealed class AsyncShaderPipelineTests : GpuTestBase
{
    // ──────────────── Trivial shader sources for testing ────────────────

    private const string TrivialVertexSource = @"#version 460 core
layout(location = 0) in vec3 aPos;
void main() { gl_Position = vec4(aPos, 1.0); }";

    private const string TrivialFragmentSource = @"#version 460 core
out vec4 FragColor;
void main() { FragColor = vec4(1.0, 0.0, 0.0, 1.0); }";

    private const string InvalidShaderSource = @"#version 460 core
THIS IS NOT VALID GLSL;";

    // ─────────── Shared context helper for tests ────────────────────────

    /// <summary>
    /// Creates a 1×1 hidden GLFW window that shares the GL context of the given primary window.
    /// Returns null if creation fails.
    /// </summary>
    private static IWindow? CreateSharedWindow(IWindow primary)
    {
        try
        {
            var opts = WindowOptions.Default;
            opts.Size = new Vector2D<int>(1, 1);
            opts.IsVisible = false;
            opts.API = primary.API;
            opts.SharedContext = primary.GLContext;
            opts.ShouldSwapAutomatically = false;

            Window.PrioritizeGlfw();
            var shared = Window.Create(opts);
            shared.Initialize();
            return shared;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Creates a <see cref="GLSharedContext"/> initialized with a pre-created shared window.
    /// Returns (context, sharedWindow) or (null, null) on failure.
    /// </summary>
    private static (GLSharedContext? ctx, IWindow? sharedWindow) CreateTestSharedContext(IWindow primary)
    {
        var sharedWindow = CreateSharedWindow(primary);
        if (sharedWindow is null)
            return (null, null);

        // Restore primary context on this thread.
        primary.MakeCurrent();

        var ctx = new GLSharedContext();
        if (!ctx.Initialize(sharedWindow))
        {
            ctx.Dispose();
            DisposeContext(sharedWindow);
            return (null, null);
        }

        return (ctx, sharedWindow);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  1. GLSharedContext Tests
    // ═══════════════════════════════════════════════════════════════════

    #region GLSharedContext

    [Test]
    public void SharedContext_IsNotRunning_BeforeInitialize()
    {
        var ctx = new GLSharedContext();
        ctx.IsRunning.ShouldBeFalse();
        ctx.Dispose();
    }

    [Test]
    public void SharedContext_Dispose_IsIdempotent()
    {
        var ctx = new GLSharedContext();
        // Should not throw even when never initialized.
        ctx.Dispose();
        ctx.Dispose();
    }

    [Test]
    public void SharedContext_IsRunning_AfterInitialize()
    {
        RunWithGLContext((gl, window) =>
        {
            var (ctx, sharedWindow) = CreateTestSharedContext(window);
            if (ctx is null)
            {
                Assert.Inconclusive("Failed to create shared context.");
                return;
            }

            ctx.IsRunning.ShouldBeTrue();

            ctx.Dispose();
        });
    }

    [Test]
    public void SharedContext_IsNotRunning_AfterDispose()
    {
        RunWithGLContext((gl, window) =>
        {
            var (ctx, sharedWindow) = CreateTestSharedContext(window);
            if (ctx is null)
            {
                Assert.Inconclusive("Failed to create shared context.");
                return;
            }

            ctx.Dispose();
            ctx.IsRunning.ShouldBeFalse();
        });
    }

    [Test]
    public void SharedContext_ExecutesJobOnBackgroundThread()
    {
        RunWithGLContext((gl, window) =>
        {
            var (ctx, sharedWindow) = CreateTestSharedContext(window);
            if (ctx is null)
            {
                Assert.Inconclusive("Failed to create shared context.");
                return;
            }

            int mainThreadId = Environment.CurrentManagedThreadId;
            int jobThreadId = -1;
            using var done = new ManualResetEventSlim(false);

            ctx.Enqueue(_ =>
            {
                jobThreadId = Environment.CurrentManagedThreadId;
                done.Set();
            });

            done.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue("Background job did not complete in time.");
            jobThreadId.ShouldNotBe(mainThreadId, "Job should run on a different thread.");

            ctx.Dispose();
        });
    }

    [Test]
    public void SharedContext_GLObjectsAreShared_BetweenContexts()
    {
        RunWithGLContext((gl, window) =>
        {
            // Create a program on the primary context.
            uint program = gl.CreateProgram();
            program.ShouldBeGreaterThan(0u);

            var (ctx, sharedWindow) = CreateTestSharedContext(window);
            if (ctx is null)
            {
                gl.DeleteProgram(program);
                Assert.Inconclusive("Failed to create shared context.");
                return;
            }

            bool isProgram = false;
            using var verified = new ManualResetEventSlim(false);

            ctx.Enqueue(sharedGl =>
            {
                isProgram = sharedGl.IsProgram(program);
                verified.Set();
            });

            verified.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
            isProgram.ShouldBeTrue("GL program created on primary context should be visible on shared context.");

            gl.DeleteProgram(program);
            ctx.Dispose();
        });
    }

    [Test]
    public void SharedContext_MultipleJobsExecuteInOrder()
    {
        RunWithGLContext((gl, window) =>
        {
            var (ctx, sharedWindow) = CreateTestSharedContext(window);
            if (ctx is null)
            {
                Assert.Inconclusive("Failed to create shared context.");
                return;
            }

            int counter = 0;
            int[] order = new int[3];
            using var done = new ManualResetEventSlim(false);

            ctx.Enqueue(_ => order[Interlocked.Increment(ref counter) - 1] = 1);
            ctx.Enqueue(_ => order[Interlocked.Increment(ref counter) - 1] = 2);
            ctx.Enqueue(_ =>
            {
                order[Interlocked.Increment(ref counter) - 1] = 3;
                done.Set();
            });

            done.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
            order.ShouldBe(new[] { 1, 2, 3 }, "Jobs should execute in FIFO order.");

            ctx.Dispose();
        });
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════════
    //  2. GLProgramBinaryUploadQueue Tests
    // ═══════════════════════════════════════════════════════════════════

    #region GLProgramBinaryUploadQueue

    [Test]
    public void BinaryUpload_SucceedsForValidBinary()
    {
        RunWithGLContext((gl, window) =>
        {
            // First, compile and link a program to get a valid binary.
            uint vs = CompileShader(gl, ShaderType.VertexShader, TrivialVertexSource);
            uint fs = CompileShader(gl, ShaderType.FragmentShader, TrivialFragmentSource);
            uint srcProgram = LinkProgram(gl, vs, fs);
            gl.DeleteShader(vs);
            gl.DeleteShader(fs);

            // Extract binary.
            gl.GetProgram(srcProgram, GLEnum.ProgramBinaryLength, out int len);
            if (len <= 0)
            {
                gl.DeleteProgram(srcProgram);
                Assert.Inconclusive("Driver does not support program binary retrieval.");
                return;
            }

            byte[] binary = new byte[len];
            uint binaryLength;
            GLEnum format;
            unsafe
            {
                fixed (byte* ptr = binary)
                    gl.GetProgramBinary(srcProgram, (uint)len, &binaryLength, &format, ptr);
            }

            gl.DeleteProgram(srcProgram);

            // Create shared context and queue.
            var (ctx, sharedWindow) = CreateTestSharedContext(window);
            if (ctx is null)
            {
                Assert.Inconclusive("Failed to create shared context.");
                return;
            }

            var uploadQueue = new GLProgramBinaryUploadQueue(ctx);
            uploadQueue.IsAvailable.ShouldBeTrue();
            uploadQueue.CanEnqueue.ShouldBeTrue();
            uploadQueue.InFlightCount.ShouldBe(0);

            // Create a new empty program and upload the binary.
            uint targetProgram = gl.CreateProgram();
            uploadQueue.EnqueueUpload(targetProgram, binary, format, binaryLength, 12345UL);
            uploadQueue.InFlightCount.ShouldBe(1);

            // Poll until complete.
            GLProgramBinaryUploadQueue.UploadResult result = default;
            SpinWait.SpinUntil(() => uploadQueue.TryGetResult(targetProgram, out result),
                TimeSpan.FromSeconds(10));

            result.Status.ShouldBe(GLProgramBinaryUploadQueue.UploadStatus.Success);
            result.Hash.ShouldBe(12345UL);
            uploadQueue.InFlightCount.ShouldBe(0);

            // Verify the program is linked on the primary context.
            gl.GetProgram(targetProgram, GLEnum.LinkStatus, out int linkStatus);
            linkStatus.ShouldNotBe(0, "Program should be linked after successful binary upload.");

            gl.DeleteProgram(targetProgram);
            ctx.Dispose();
        });
    }

    [Test]
    public void BinaryUpload_FailsForCorruptBinary()
    {
        RunWithGLContext((gl, window) =>
        {
            var (ctx, sharedWindow) = CreateTestSharedContext(window);
            if (ctx is null)
            {
                Assert.Inconclusive("Failed to create shared context.");
                return;
            }

            var uploadQueue = new GLProgramBinaryUploadQueue(ctx);
            uint targetProgram = gl.CreateProgram();

            // Upload garbage bytes.
            byte[] garbage = new byte[64];
            Array.Fill(garbage, (byte)0xDE);
            uploadQueue.EnqueueUpload(targetProgram, garbage, (GLEnum)0x1234, 64, 99UL);

            GLProgramBinaryUploadQueue.UploadResult result = default;
            SpinWait.SpinUntil(() => uploadQueue.TryGetResult(targetProgram, out result),
                TimeSpan.FromSeconds(10));

            result.Status.ShouldBe(GLProgramBinaryUploadQueue.UploadStatus.Failed);
            uploadQueue.InFlightCount.ShouldBe(0);

            gl.DeleteProgram(targetProgram);
            ctx.Dispose();
        });
    }

    [Test]
    public void BinaryUpload_InFlightCount_TracksCorrectly()
    {
        RunWithGLContext((gl, window) =>
        {
            var (ctx, sharedWindow) = CreateTestSharedContext(window);
            if (ctx is null)
            {
                Assert.Inconclusive("Failed to create shared context.");
                return;
            }

            var uploadQueue = new GLProgramBinaryUploadQueue(ctx);

            // Stall the shared context so uploads queue up.
            using var blocker = new ManualResetEventSlim(false);
            ctx.Enqueue(_ => blocker.Wait(TimeSpan.FromSeconds(10)));

            byte[] dummy = new byte[4];
            uint p1 = gl.CreateProgram();
            uint p2 = gl.CreateProgram();
            uint p3 = gl.CreateProgram();

            uploadQueue.EnqueueUpload(p1, dummy, GLEnum.None, 4, 1);
            uploadQueue.EnqueueUpload(p2, dummy, GLEnum.None, 4, 2);
            uploadQueue.EnqueueUpload(p3, dummy, GLEnum.None, 4, 3);

            uploadQueue.InFlightCount.ShouldBe(3);
            uploadQueue.CanEnqueue.ShouldBeTrue(); // 3 < MaxInFlight (8)

            // Release the blocker and let them process.
            blocker.Set();

            SpinWait.SpinUntil(() =>
            {
                uploadQueue.TryGetResult(p1, out _);
                uploadQueue.TryGetResult(p2, out _);
                uploadQueue.TryGetResult(p3, out _);
                return uploadQueue.InFlightCount == 0;
            }, TimeSpan.FromSeconds(10));

            uploadQueue.InFlightCount.ShouldBe(0);

            gl.DeleteProgram(p1);
            gl.DeleteProgram(p2);
            gl.DeleteProgram(p3);
            ctx.Dispose();
        });
    }

    [Test]
    public void BinaryUpload_CanEnqueue_ReturnsFalse_WhenAtCapacity()
    {
        RunWithGLContext((gl, window) =>
        {
            var (ctx, sharedWindow) = CreateTestSharedContext(window);
            if (ctx is null)
            {
                Assert.Inconclusive("Failed to create shared context.");
                return;
            }

            var uploadQueue = new GLProgramBinaryUploadQueue(ctx);

            // Block the processor thread.
            using var blocker = new ManualResetEventSlim(false);
            ctx.Enqueue(_ => blocker.Wait(TimeSpan.FromSeconds(10)));

            // Fill to capacity.
            byte[] dummy = new byte[4];
            uint[] programs = new uint[GLProgramBinaryUploadQueue.MaxInFlight];
            for (int i = 0; i < programs.Length; i++)
            {
                programs[i] = gl.CreateProgram();
                uploadQueue.EnqueueUpload(programs[i], dummy, GLEnum.None, 4, (ulong)i);
            }

            uploadQueue.InFlightCount.ShouldBe(GLProgramBinaryUploadQueue.MaxInFlight);
            uploadQueue.CanEnqueue.ShouldBeFalse("Should be at capacity.");

            // Unblock and clean up.
            blocker.Set();
            SpinWait.SpinUntil(() =>
            {
                foreach (uint p in programs)
                    uploadQueue.TryGetResult(p, out _);
                return uploadQueue.InFlightCount == 0;
            }, TimeSpan.FromSeconds(10));

            foreach (uint p in programs)
                gl.DeleteProgram(p);

            ctx.Dispose();
        });
    }

    [Test]
    public void BinaryUpload_Enqueue_DoesNotBlockMainThread_WhenWorkerIsBusy()
    {
        RunWithGLContext((gl, window) =>
        {
            var (ctx, sharedWindow) = CreateTestSharedContext(window);
            if (ctx is null)
            {
                Assert.Inconclusive("Failed to create shared context.");
                return;
            }

            var uploadQueue = new GLProgramBinaryUploadQueue(ctx);

            using var blocker = new ManualResetEventSlim(false);
            ctx.Enqueue(_ => blocker.Wait(TimeSpan.FromSeconds(10)));

            uint program = gl.CreateProgram();
            byte[] dummy = new byte[4];

            var stopwatch = Stopwatch.StartNew();
            uploadQueue.EnqueueUpload(program, dummy, GLEnum.None, 4, 7UL);
            stopwatch.Stop();

            stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(100),
                "Enqueue should stay fast even when the worker thread is stalled.");
            uploadQueue.TryGetResult(program, out _).ShouldBeFalse(
                "Result should remain pending while the worker thread is blocked.");

            blocker.Set();
            SpinWait.SpinUntil(() => uploadQueue.TryGetResult(program, out _), TimeSpan.FromSeconds(10))
                .ShouldBeTrue("Upload should complete after the worker thread resumes.");

            gl.DeleteProgram(program);
            ctx.Dispose();
        });
    }

    [Test]
    public void BinaryUpload_TryGetResult_ReturnsFalse_WhenNotComplete()
    {
        RunWithGLContext((gl, window) =>
        {
            var (ctx, sharedWindow) = CreateTestSharedContext(window);
            if (ctx is null)
            {
                Assert.Inconclusive("Failed to create shared context.");
                return;
            }

            var uploadQueue = new GLProgramBinaryUploadQueue(ctx);

            // Block the processor.
            using var blocker = new ManualResetEventSlim(false);
            ctx.Enqueue(_ => blocker.Wait(TimeSpan.FromSeconds(10)));

            uint p = gl.CreateProgram();
            uploadQueue.EnqueueUpload(p, new byte[4], GLEnum.None, 4, 1);

            // Should not have a result yet.
            uploadQueue.TryGetResult(p, out _).ShouldBeFalse();

            blocker.Set();
            SpinWait.SpinUntil(() => uploadQueue.TryGetResult(p, out _), TimeSpan.FromSeconds(10));

            gl.DeleteProgram(p);
            ctx.Dispose();
        });
    }

    [Test]
    public void BinaryUpload_TryGetResult_ReturnsFalse_ForUnknownProgramId()
    {
        RunWithGLContext((gl, window) =>
        {
            var (ctx, sharedWindow) = CreateTestSharedContext(window);
            if (ctx is null)
            {
                Assert.Inconclusive("Failed to create shared context.");
                return;
            }

            var uploadQueue = new GLProgramBinaryUploadQueue(ctx);

            // Querying a program ID that was never uploaded.
            uploadQueue.TryGetResult(999999, out _).ShouldBeFalse();

            ctx.Dispose();
        });
    }

    [Test]
    public void BinaryUpload_ResultIsConsumedOnce()
    {
        RunWithGLContext((gl, window) =>
        {
            var (ctx, sharedWindow) = CreateTestSharedContext(window);
            if (ctx is null)
            {
                Assert.Inconclusive("Failed to create shared context.");
                return;
            }

            var uploadQueue = new GLProgramBinaryUploadQueue(ctx);

            uint p = gl.CreateProgram();
            uploadQueue.EnqueueUpload(p, new byte[4], GLEnum.None, 4, 1);

            SpinWait.SpinUntil(() => uploadQueue.TryGetResult(p, out _),
                TimeSpan.FromSeconds(10));

            // Second retrieval should fail — result was consumed.
            uploadQueue.TryGetResult(p, out _).ShouldBeFalse(
                "Result should be consumed on first retrieval.");

            gl.DeleteProgram(p);
            ctx.Dispose();
        });
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════════
    //  3. GLProgramCompileLinkQueue Tests
    // ═══════════════════════════════════════════════════════════════════

    #region GLProgramCompileLinkQueue

    [Test]
    public void CompileLink_SucceedsForValidShaders()
    {
        RunWithGLContext((gl, window) =>
        {
            var (ctx, sharedWindow) = CreateTestSharedContext(window);
            if (ctx is null)
            {
                Assert.Inconclusive("Failed to create shared context.");
                return;
            }

            uint program = gl.CreateProgram();
            var compileQueue = new GLProgramCompileLinkQueue(ctx);

            compileQueue.IsAvailable.ShouldBeTrue();
            compileQueue.CanEnqueue.ShouldBeTrue();

            var inputs = new GLProgramCompileLinkQueue.ShaderInput[]
            {
                new(TrivialVertexSource, ShaderType.VertexShader),
                new(TrivialFragmentSource, ShaderType.FragmentShader),
            };

            compileQueue.EnqueueCompileAndLink(program, inputs);
            compileQueue.InFlightCount.ShouldBe(1);

            GLProgramCompileLinkQueue.CompileResult result = default;
            SpinWait.SpinUntil(() => compileQueue.TryGetResult(program, out result),
                TimeSpan.FromSeconds(15));

            result.Status.ShouldBe(GLProgramCompileLinkQueue.CompileStatus.Success);
            result.ErrorLog.ShouldBeNull();
            compileQueue.InFlightCount.ShouldBe(0);

            // Verify the program is usable on the primary context.
            gl.GetProgram(program, GLEnum.LinkStatus, out int linkStatus);
            linkStatus.ShouldNotBe(0, "Program should be linked after async compile+link.");

            gl.GetError().ShouldBe(GLEnum.NoError);

            gl.DeleteProgram(program);
            ctx.Dispose();
        });
    }

    [Test]
    public void CompileLink_ReportsCompileFailure_ForInvalidShader()
    {
        RunWithGLContext((gl, window) =>
        {
            var (ctx, sharedWindow) = CreateTestSharedContext(window);
            if (ctx is null)
            {
                Assert.Inconclusive("Failed to create shared context.");
                return;
            }

            uint program = gl.CreateProgram();
            var compileQueue = new GLProgramCompileLinkQueue(ctx);

            var inputs = new GLProgramCompileLinkQueue.ShaderInput[]
            {
                new(InvalidShaderSource, ShaderType.VertexShader),
                new(TrivialFragmentSource, ShaderType.FragmentShader),
            };

            compileQueue.EnqueueCompileAndLink(program, inputs);

            GLProgramCompileLinkQueue.CompileResult result = default;
            SpinWait.SpinUntil(() => compileQueue.TryGetResult(program, out result),
                TimeSpan.FromSeconds(15));

            result.Status.ShouldBe(GLProgramCompileLinkQueue.CompileStatus.CompileFailed);
            result.ErrorLog.ShouldNotBeNullOrEmpty("Should contain a shader compile error message.");
            compileQueue.InFlightCount.ShouldBe(0);

            gl.DeleteProgram(program);
            ctx.Dispose();
        });
    }

    [Test]
    public void CompileLink_ReportsLinkFailure_ForMismatchedShaders()
    {
        RunWithGLContext((gl, window) =>
        {
            var (ctx, sharedWindow) = CreateTestSharedContext(window);
            if (ctx is null)
            {
                Assert.Inconclusive("Failed to create shared context.");
                return;
            }

            uint program = gl.CreateProgram();
            var compileQueue = new GLProgramCompileLinkQueue(ctx);

            // Two vertex shaders, no fragment = link failure on most drivers.
            var inputs = new GLProgramCompileLinkQueue.ShaderInput[]
            {
                new(TrivialVertexSource, ShaderType.VertexShader),
                new(TrivialVertexSource, ShaderType.VertexShader),
            };

            compileQueue.EnqueueCompileAndLink(program, inputs);

            GLProgramCompileLinkQueue.CompileResult result = default;
            SpinWait.SpinUntil(() => compileQueue.TryGetResult(program, out result),
                TimeSpan.FromSeconds(15));

            // Some drivers may tolerate this; the important thing is it should NOT succeed.
            result.Status.ShouldNotBe(GLProgramCompileLinkQueue.CompileStatus.Success,
                "Linking two vertex shaders with no fragment should not succeed.");

            gl.DeleteProgram(program);
            ctx.Dispose();
        });
    }

    [Test]
    public void CompileLink_SecondShaderFailure_CleansUpFirstShader()
    {
        RunWithGLContext((gl, window) =>
        {
            var (ctx, sharedWindow) = CreateTestSharedContext(window);
            if (ctx is null)
            {
                Assert.Inconclusive("Failed to create shared context.");
                return;
            }

            uint program = gl.CreateProgram();
            var compileQueue = new GLProgramCompileLinkQueue(ctx);

            // First shader valid, second is garbage — should fail on second compile
            // and clean up the first shader object.
            var inputs = new GLProgramCompileLinkQueue.ShaderInput[]
            {
                new(TrivialVertexSource, ShaderType.VertexShader),
                new(InvalidShaderSource, ShaderType.FragmentShader),
            };

            compileQueue.EnqueueCompileAndLink(program, inputs);

            GLProgramCompileLinkQueue.CompileResult result = default;
            SpinWait.SpinUntil(() => compileQueue.TryGetResult(program, out result),
                TimeSpan.FromSeconds(15));

            result.Status.ShouldBe(GLProgramCompileLinkQueue.CompileStatus.CompileFailed);
            compileQueue.InFlightCount.ShouldBe(0);

            gl.DeleteProgram(program);
            ctx.Dispose();
        });
    }

    [Test]
    public void CompileLink_EmptyShaderArray_ReportsLinkFailure()
    {
        RunWithGLContext((gl, window) =>
        {
            var (ctx, sharedWindow) = CreateTestSharedContext(window);
            if (ctx is null)
            {
                Assert.Inconclusive("Failed to create shared context.");
                return;
            }

            uint program = gl.CreateProgram();
            var compileQueue = new GLProgramCompileLinkQueue(ctx);

            // Empty shader array — link should fail (no shaders attached).
            compileQueue.EnqueueCompileAndLink(program, []);

            GLProgramCompileLinkQueue.CompileResult result = default;
            SpinWait.SpinUntil(() => compileQueue.TryGetResult(program, out result),
                TimeSpan.FromSeconds(15));

            result.Status.ShouldBe(GLProgramCompileLinkQueue.CompileStatus.LinkFailed);

            gl.DeleteProgram(program);
            ctx.Dispose();
        });
    }

    [Test]
    public void CompileLink_InFlightCount_TracksCorrectly()
    {
        RunWithGLContext((gl, window) =>
        {
            var (ctx, sharedWindow) = CreateTestSharedContext(window);
            if (ctx is null)
            {
                Assert.Inconclusive("Failed to create shared context.");
                return;
            }

            var compileQueue = new GLProgramCompileLinkQueue(ctx);

            // Block the processor.
            using var blocker = new ManualResetEventSlim(false);
            ctx.Enqueue(_ => blocker.Wait(TimeSpan.FromSeconds(10)));

            var inputs = new GLProgramCompileLinkQueue.ShaderInput[]
            {
                new(TrivialVertexSource, ShaderType.VertexShader),
                new(TrivialFragmentSource, ShaderType.FragmentShader),
            };

            uint p1 = gl.CreateProgram();
            uint p2 = gl.CreateProgram();

            compileQueue.EnqueueCompileAndLink(p1, inputs);
            compileQueue.EnqueueCompileAndLink(p2, inputs);

            compileQueue.InFlightCount.ShouldBe(2);

            // Unblock and wait for completion.
            blocker.Set();

            SpinWait.SpinUntil(() =>
            {
                compileQueue.TryGetResult(p1, out _);
                compileQueue.TryGetResult(p2, out _);
                return compileQueue.InFlightCount == 0;
            }, TimeSpan.FromSeconds(15));

            compileQueue.InFlightCount.ShouldBe(0);

            gl.DeleteProgram(p1);
            gl.DeleteProgram(p2);
            ctx.Dispose();
        });
    }

    [Test]
    public void CompileLink_CanEnqueue_ReturnsFalse_WhenAtCapacity()
    {
        RunWithGLContext((gl, window) =>
        {
            var (ctx, sharedWindow) = CreateTestSharedContext(window);
            if (ctx is null)
            {
                Assert.Inconclusive("Failed to create shared context.");
                return;
            }

            var compileQueue = new GLProgramCompileLinkQueue(ctx);

            // Block the processor.
            using var blocker = new ManualResetEventSlim(false);
            ctx.Enqueue(_ => blocker.Wait(TimeSpan.FromSeconds(10)));

            var inputs = new GLProgramCompileLinkQueue.ShaderInput[]
            {
                new(TrivialVertexSource, ShaderType.VertexShader),
                new(TrivialFragmentSource, ShaderType.FragmentShader),
            };

            // Fill to capacity (MaxInFlight = 4).
            uint[] programs = new uint[GLProgramCompileLinkQueue.MaxInFlight];
            for (int i = 0; i < programs.Length; i++)
            {
                programs[i] = gl.CreateProgram();
                compileQueue.EnqueueCompileAndLink(programs[i], inputs);
            }

            compileQueue.InFlightCount.ShouldBe(GLProgramCompileLinkQueue.MaxInFlight);
            compileQueue.CanEnqueue.ShouldBeFalse();

            // Unblock and clean up.
            blocker.Set();
            SpinWait.SpinUntil(() =>
            {
                foreach (uint p in programs)
                    compileQueue.TryGetResult(p, out _);
                return compileQueue.InFlightCount == 0;
            }, TimeSpan.FromSeconds(15));

            foreach (uint p in programs)
                gl.DeleteProgram(p);

            ctx.Dispose();
        });
    }

    [Test]
    public void CompileLink_Enqueue_DoesNotBlockMainThread_WhenWorkerIsBusy()
    {
        RunWithGLContext((gl, window) =>
        {
            var (ctx, sharedWindow) = CreateTestSharedContext(window);
            if (ctx is null)
            {
                Assert.Inconclusive("Failed to create shared context.");
                return;
            }

            var compileQueue = new GLProgramCompileLinkQueue(ctx);

            using var blocker = new ManualResetEventSlim(false);
            ctx.Enqueue(_ => blocker.Wait(TimeSpan.FromSeconds(10)));

            var inputs = new GLProgramCompileLinkQueue.ShaderInput[]
            {
                new(TrivialVertexSource, ShaderType.VertexShader),
                new(TrivialFragmentSource, ShaderType.FragmentShader),
            };

            uint program = gl.CreateProgram();

            var stopwatch = Stopwatch.StartNew();
            compileQueue.EnqueueCompileAndLink(program, inputs);
            stopwatch.Stop();

            stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(100),
                "Enqueue should stay fast even when the worker thread is stalled.");
            compileQueue.TryGetResult(program, out _).ShouldBeFalse(
                "Result should remain pending while the worker thread is blocked.");

            blocker.Set();
            SpinWait.SpinUntil(() => compileQueue.TryGetResult(program, out _), TimeSpan.FromSeconds(15))
                .ShouldBeTrue("Compile/link should complete after the worker thread resumes.");

            gl.DeleteProgram(program);
            ctx.Dispose();
        });
    }

    [Test]
    public void CompileLink_SingleComputeShader_CompilesAndLinks()
    {
        const string computeSource = @"#version 460 core
layout(local_size_x = 1) in;
void main() { }";

        RunWithGLContext((gl, window) =>
        {
            var (ctx, sharedWindow) = CreateTestSharedContext(window);
            if (ctx is null)
            {
                Assert.Inconclusive("Failed to create shared context.");
                return;
            }

            uint program = gl.CreateProgram();
            var compileQueue = new GLProgramCompileLinkQueue(ctx);

            var inputs = new GLProgramCompileLinkQueue.ShaderInput[]
            {
                new(computeSource, ShaderType.ComputeShader),
            };

            compileQueue.EnqueueCompileAndLink(program, inputs);

            GLProgramCompileLinkQueue.CompileResult result = default;
            SpinWait.SpinUntil(() => compileQueue.TryGetResult(program, out result),
                TimeSpan.FromSeconds(15));

            result.Status.ShouldBe(GLProgramCompileLinkQueue.CompileStatus.Success);

            gl.GetProgram(program, GLEnum.LinkStatus, out int linkStatus);
            linkStatus.ShouldNotBe(0);

            gl.DeleteProgram(program);
            ctx.Dispose();
        });
    }

    [Test]
    public void CompileLink_ResultIsConsumedOnce()
    {
        RunWithGLContext((gl, window) =>
        {
            var (ctx, sharedWindow) = CreateTestSharedContext(window);
            if (ctx is null)
            {
                Assert.Inconclusive("Failed to create shared context.");
                return;
            }

            var compileQueue = new GLProgramCompileLinkQueue(ctx);
            uint p = gl.CreateProgram();
            compileQueue.EnqueueCompileAndLink(p, new GLProgramCompileLinkQueue.ShaderInput[]
            {
                new(TrivialVertexSource, ShaderType.VertexShader),
                new(TrivialFragmentSource, ShaderType.FragmentShader),
            });

            SpinWait.SpinUntil(() => compileQueue.TryGetResult(p, out _),
                TimeSpan.FromSeconds(15));

            compileQueue.TryGetResult(p, out _).ShouldBeFalse(
                "Result should be consumed on first retrieval.");

            gl.DeleteProgram(p);
            ctx.Dispose();
        });
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════════
    //  4. Binary Round-Trip: Compile → Extract → Upload
    // ═══════════════════════════════════════════════════════════════════

    #region Binary Round-Trip

    [Test]
    public void BinaryRoundTrip_CompileOnShared_ExtractOnMain_UploadOnShared()
    {
        RunWithGLContext((gl, window) =>
        {
            var (ctx, sharedWindow) = CreateTestSharedContext(window);
            if (ctx is null)
            {
                Assert.Inconclusive("Failed to create shared context.");
                return;
            }

            // Step 1: Compile+link on shared context.
            uint srcProgram = gl.CreateProgram();
            var compileQueue = new GLProgramCompileLinkQueue(ctx);

            var inputs = new GLProgramCompileLinkQueue.ShaderInput[]
            {
                new(TrivialVertexSource, ShaderType.VertexShader),
                new(TrivialFragmentSource, ShaderType.FragmentShader),
            };

            compileQueue.EnqueueCompileAndLink(srcProgram, inputs);

            GLProgramCompileLinkQueue.CompileResult compResult = default;
            SpinWait.SpinUntil(() => compileQueue.TryGetResult(srcProgram, out compResult),
                TimeSpan.FromSeconds(15));
            compResult.Status.ShouldBe(GLProgramCompileLinkQueue.CompileStatus.Success);

            // Step 2: Extract binary on main context.
            gl.GetProgram(srcProgram, GLEnum.ProgramBinaryLength, out int len);
            if (len <= 0)
            {
                gl.DeleteProgram(srcProgram);
                Assert.Inconclusive("Driver does not support binary extraction.");
                return;
            }

            byte[] binary = new byte[len];
            uint binaryLength;
            GLEnum format;
            unsafe
            {
                fixed (byte* ptr = binary)
                    gl.GetProgramBinary(srcProgram, (uint)len, &binaryLength, &format, ptr);
            }

            gl.DeleteProgram(srcProgram);

            // Step 3: Upload binary on shared context.
            uint targetProgram = gl.CreateProgram();
            var uploadQueue = new GLProgramBinaryUploadQueue(ctx);

            uploadQueue.EnqueueUpload(targetProgram, binary, format, binaryLength, 42UL);

            GLProgramBinaryUploadQueue.UploadResult upResult = default;
            SpinWait.SpinUntil(() => uploadQueue.TryGetResult(targetProgram, out upResult),
                TimeSpan.FromSeconds(10));

            upResult.Status.ShouldBe(GLProgramBinaryUploadQueue.UploadStatus.Success);

            // Verify the program is linked on the main context.
            gl.GetProgram(targetProgram, GLEnum.LinkStatus, out int linkStatus);
            linkStatus.ShouldNotBe(0);

            gl.DeleteProgram(targetProgram);
            ctx.Dispose();
        });
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════════
    //  5. Shader Type Mapping Tests
    // ═══════════════════════════════════════════════════════════════════

    #region ShaderTypeMapping

    [TestCase(EShaderType.Vertex, ShaderType.VertexShader)]
    [TestCase(EShaderType.Fragment, ShaderType.FragmentShader)]
    [TestCase(EShaderType.Geometry, ShaderType.GeometryShader)]
    [TestCase(EShaderType.TessControl, ShaderType.TessControlShader)]
    [TestCase(EShaderType.TessEvaluation, ShaderType.TessEvaluationShader)]
    [TestCase(EShaderType.Compute, ShaderType.ComputeShader)]
    public void ToGLShaderType_MapsCorrectly(
        EShaderType input,
        ShaderType expected)
    {
        GLProgramCompileLinkQueue.ToGLShaderType(input).ShouldBe(expected);
    }

    [Test]
    public void ToGLShaderType_Task_MapsToExpectedValue()
    {
        GLProgramCompileLinkQueue.ToGLShaderType(
            EShaderType.Task).ShouldBe((ShaderType)0x955A);
    }

    [Test]
    public void ToGLShaderType_Mesh_MapsToExpectedValue()
    {
        GLProgramCompileLinkQueue.ToGLShaderType(
            EShaderType.Mesh).ShouldBe((ShaderType)0x9559);
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════════
    //  6. Concurrent Access Tests
    // ═══════════════════════════════════════════════════════════════════

    #region Concurrent Access

    [Test]
    public void CompileLink_ConcurrentEnqueueFromMultipleThreads()
    {
        RunWithGLContext((gl, window) =>
        {
            var (ctx, sharedWindow) = CreateTestSharedContext(window);
            if (ctx is null)
            {
                Assert.Inconclusive("Failed to create shared context.");
                return;
            }

            var compileQueue = new GLProgramCompileLinkQueue(ctx);

            const int threadCount = 4;
            uint[] programs = new uint[threadCount];
            for (int i = 0; i < threadCount; i++)
                programs[i] = gl.CreateProgram();

            var barrier = new Barrier(threadCount);
            Thread[] threads = new Thread[threadCount];

            for (int i = 0; i < threadCount; i++)
            {
                int index = i;
                threads[i] = new Thread(() =>
                {
                    barrier.SignalAndWait(); // Synchronize start.
                    var inputs = new GLProgramCompileLinkQueue.ShaderInput[]
                    {
                        new(TrivialVertexSource, ShaderType.VertexShader),
                        new(TrivialFragmentSource, ShaderType.FragmentShader),
                    };
                    compileQueue.EnqueueCompileAndLink(programs[index], inputs);
                })
                {
                    IsBackground = true
                };
                threads[i].Start();
            }

            foreach (var t in threads)
                t.Join(TimeSpan.FromSeconds(5));

            // All should eventually complete.
            var results = new GLProgramCompileLinkQueue.CompileResult[threadCount];

            SpinWait.SpinUntil(() =>
            {
                bool allDone = true;
                for (int i = 0; i < threadCount; i++)
                {
                    if (results[i].Status == default && programs[i] != 0)
                    {
                        if (!compileQueue.TryGetResult(programs[i], out results[i]))
                            allDone = false;
                    }
                }
                return allDone;
            }, TimeSpan.FromSeconds(30));

            for (int i = 0; i < threadCount; i++)
            {
                results[i].Status.ShouldBe(GLProgramCompileLinkQueue.CompileStatus.Success,
                    $"Program {i} should compile+link successfully.");
                gl.DeleteProgram(programs[i]);
            }

            compileQueue.InFlightCount.ShouldBe(0);
            ctx.Dispose();
        }, timeoutMs: 60_000);
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════════
    //  7. Queue Interleaving Tests
    // ═══════════════════════════════════════════════════════════════════

    #region Queue Interleaving

    [Test]
    public void BothQueues_CanShareSameSharedContext()
    {
        RunWithGLContext((gl, window) =>
        {
            var (ctx, sharedWindow) = CreateTestSharedContext(window);
            if (ctx is null)
            {
                Assert.Inconclusive("Failed to create shared context.");
                return;
            }

            var uploadQueue = new GLProgramBinaryUploadQueue(ctx);
            var compileQueue = new GLProgramCompileLinkQueue(ctx);

            // Compile a program to get a binary.
            uint srcProgram = gl.CreateProgram();
            compileQueue.EnqueueCompileAndLink(srcProgram, new GLProgramCompileLinkQueue.ShaderInput[]
            {
                new(TrivialVertexSource, ShaderType.VertexShader),
                new(TrivialFragmentSource, ShaderType.FragmentShader),
            });

            GLProgramCompileLinkQueue.CompileResult compResult = default;
            SpinWait.SpinUntil(() => compileQueue.TryGetResult(srcProgram, out compResult),
                TimeSpan.FromSeconds(15));
            compResult.Status.ShouldBe(GLProgramCompileLinkQueue.CompileStatus.Success);

            // Extract binary.
            gl.GetProgram(srcProgram, GLEnum.ProgramBinaryLength, out int len);
            if (len <= 0)
            {
                gl.DeleteProgram(srcProgram);
                Assert.Inconclusive("Driver does not support binary extraction.");
                return;
            }

            byte[] binary = new byte[len];
            uint binaryLength;
            GLEnum format;
            unsafe
            {
                fixed (byte* ptr = binary)
                    gl.GetProgramBinary(srcProgram, (uint)len, &binaryLength, &format, ptr);
            }

            gl.DeleteProgram(srcProgram);

            // Interleave: compile + binary upload at the same time.
            uint compProgram = gl.CreateProgram();
            uint uploadProgram = gl.CreateProgram();

            compileQueue.EnqueueCompileAndLink(compProgram, new GLProgramCompileLinkQueue.ShaderInput[]
            {
                new(TrivialVertexSource, ShaderType.VertexShader),
                new(TrivialFragmentSource, ShaderType.FragmentShader),
            });
            uploadQueue.EnqueueUpload(uploadProgram, binary, format, binaryLength, 77UL);

            GLProgramCompileLinkQueue.CompileResult compResult2 = default;
            GLProgramBinaryUploadQueue.UploadResult upResult = default;

            SpinWait.SpinUntil(() =>
            {
                if (compResult2.Status == default)
                    compileQueue.TryGetResult(compProgram, out compResult2);
                if (upResult.Status == default)
                    uploadQueue.TryGetResult(uploadProgram, out upResult);
                return compResult2.Status != default && upResult.Status != default;
            }, TimeSpan.FromSeconds(15));

            compResult2.Status.ShouldBe(GLProgramCompileLinkQueue.CompileStatus.Success);
            upResult.Status.ShouldBe(GLProgramBinaryUploadQueue.UploadStatus.Success);

            gl.DeleteProgram(compProgram);
            gl.DeleteProgram(uploadProgram);
            ctx.Dispose();
        });
    }

    #endregion
}
