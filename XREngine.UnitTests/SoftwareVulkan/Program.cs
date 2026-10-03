using System.Text.Json;
using Silk.NET.Vulkan;
using XREngine;
using XREngine.Execution;
using XREngine.Rendering;
using XREngine.Rendering.Profiling;
using XREngine.Rendering.Vulkan;
using XREngine.RenderBench;

namespace XREngine.UnitTests.SoftwareVulkan;

/// <summary>Small correctness-only suite using the production presentationless Vulkan host.</summary>
internal static unsafe class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "--icd" || !File.Exists(args[1]))
        {
            Console.Error.WriteLine("Usage: XREngine.SoftwareVulkanValidation --icd <software-ICD.json>\nExit codes: 0 all checks passed; 1 failure; 2 invalid input; 3 unsupported/skipped checks.");
            return 2;
        }

        // Set before loading Vulkan. Never silently fall back to a hardware adapter.
        string icd = Path.GetFullPath(args[1]);
        var checks = new List<ValidationCheck>();
        int exitCode = 0;
        bool schedulerOwned = false;
        IRendererNativeCallbackEntryPoints? previousCallbacks = RendererNativeCallbackBridge.EntryPoints;
        try
        {
            RendererNativeCallbackBridge.EntryPoints = new SoftwareVulkanCallbackEntryPoints();
            SoftwareVulkanEnvironment.SelectDriver(icd);
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.VulkanValidation, "1");
            Environment.SetEnvironmentVariable(XREngineEnvironmentVariables.VulkanSynchronizationValidation, "1");
            if (RuntimeWorkScheduler.Scheduler is null)
            {
                RuntimeWorkScheduler.Configure(EngineExecutionTopology.Resolve(new EngineExecutionTopologyRequest
                {
                    EffectiveProcessorCount = Environment.ProcessorCount,
                    GeneralWorkerThreadCount = 1,
                    GeneralWorkerThreadCap = 1,
                    RenderWorkerThreadCount = 1,
                    RenderWorkerThreadCap = 1,
                    DedicatedBackgroundThreadCount = 0,
                    AllowCpuOversubscription = false,
                    RenderWorkerQos = XREngine.Data.Rendering.ERenderWorkerQos.OsDefault,
                    ReservedForegroundThreadCount = 1,
                }), null, null);
                schedulerOwned = true;
            }
            Run("presentationless-composition", PresentationlessCompositionCheck.Run);
            using IDisposable presentation = RuntimeRenderingHostServices.InstallPresentationless();
            using var catalog = new RendererBackendCatalog();
            using IDisposable backend = VulkanRendererBackendModule.Register(catalog);
            // Explicitly exercise the existing managed Vulkan allocator. This removes
            // the Windows-only VMA bridge requirement, not Vulkan or shader execution.
            RuntimeEngine.Rendering.Settings.VulkanRobustnessSettings.AllocatorBackend = EVulkanAllocatorBackend.Managed;
            using var host = new VulkanExplicitTargetRendererHost(new PresentationlessRenderTarget(16, 16, FrameSlotCount: 2));
            host.Api.GetPhysicalDeviceProperties(host.PhysicalDevice, out PhysicalDeviceProperties properties);
            host.Api.GetPhysicalDeviceFeatures(host.PhysicalDevice, out PhysicalDeviceFeatures features);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                adapter = host.AdapterName, vendorId = properties.VendorID, deviceId = properties.DeviceID,
                deviceType = properties.DeviceType.ToString(), apiVersion = properties.ApiVersion,
                driverVersion = properties.DriverVersion, allocator = "Managed", host.SupportsDynamicRendering,
                shaderInt64 = (bool)features.ShaderInt64, shaderFloat64 = (bool)features.ShaderFloat64,
                enabledExtensions = host.EnabledDeviceExtensions,
            }));
            if (properties.DeviceType != PhysicalDeviceType.Cpu)
                throw new InvalidOperationException("Selected adapter is not a CPU software Vulkan device; refusing to validate another device.");

            Run("clear-readback-and-frame-slot-reuse", () =>
            {
                for (int frame = 0; frame < 5; frame++)
                {
                    bool alternate = frame % 2 != 0;
                    host.SubmitFrame((api, command, target) => RecordClear(api, command, target, alternate));
                    RequireColor(host.ReadbackLastSubmittedColor(16 * 16 * 4), alternate ? [255, 0, 0, 255] : [0, 255, 0, 255], 0);
                }
            });
            if (host.SupportsDynamicRendering)
            {
                Run("fullscreen-shader-image", () =>
                {
                    var pipeline = new RenderBenchFullscreenPipeline(host, new RenderProfileRecipe
                    {
                        ColorFormat = "Rgba8", SampleCount = 1, LabelPolicy = RenderProfileLabelPolicy.Disabled,
                    }, "software-validation", 1, false);
                    try
                    {
                        // Seed=0, pass=0, draw=0 produces (.35, .175, .0875, 1).
                        for (int frame = 0; frame < 3; frame++)
                        {
                            host.SubmitFrame((api, command, target) => pipeline.Record(api, command, target, 1, 1, 0));
                            RequireColor(host.ReadbackLastSubmittedColor(16 * 16 * 4), [89, 45, 22, 255], 1);
                        }
                    }
                    finally
                    {
                        // A failed readback may follow an accepted submission. Never retire in-flight objects.
                        Result settled = host.Api.DeviceWaitIdle(host.Device);
                        if (settled is Result.Success or Result.ErrorDeviceLost)
                            pipeline.Dispose();
                        else
                            throw new InvalidOperationException($"Cannot safely retire graphics fixture: {settled}.");
                    }
                });
            }
            else
                Skip("fullscreen-shader-image", "Dynamic rendering is unavailable on the selected logical device.");

            uint familyCount = 0;
            host.Api.GetPhysicalDeviceQueueFamilyProperties(host.PhysicalDevice, ref familyCount, null);
            var families = new QueueFamilyProperties[familyCount];
            fixed (QueueFamilyProperties* pointer = families)
                host.Api.GetPhysicalDeviceQueueFamilyProperties(host.PhysicalDevice, ref familyCount, pointer);
            if ((families[host.GraphicsQueueFamilyIndex].QueueFlags & QueueFlags.ComputeBit) != 0)
                Run("compute-storage-buffer", () => SoftwareComputeCheck.Run(host));
            else
                Skip("compute-storage-buffer", "The production submission queue does not support compute.");

            VulkanValidationDiagnosticSnapshot validation = host.CaptureValidationDiagnostics();
            using (System.Diagnostics.Process process = System.Diagnostics.Process.GetCurrentProcess())
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    shaderCompilerLibraries = process.Modules.Cast<System.Diagnostics.ProcessModule>()
                        .Where(module => module.FileName.Contains("shaderc", StringComparison.OrdinalIgnoreCase))
                        .Select(module => module.FileName).ToArray(),
                }));
            Console.WriteLine(JsonSerializer.Serialize(new { validation }));
            if (validation.ErrorCount != 0 || validation.OverflowCount != 0)
                throw new InvalidOperationException("Vulkan validation reported errors or diagnostic overflow.");
            if (!validation.StandardValidationEnabled || !validation.SynchronizationValidationEnabled || !validation.DebugMessengerActive)
                Skip("native-validation-layer", "Standard/synchronization validation or debug messenger unavailable; output checks remain independent.");
            else
                checks.Add(new("native-validation-layer", "passed", "No validation errors."));
        }
        catch (DllNotFoundException exception)
        {
            Skip("native-runtime", exception.Message);
        }
        catch (Exception exception)
        {
            checks.Add(new("suite", "failed", exception.ToString()));
            exitCode = 1;
        }
        finally
        {
            RendererNativeCallbackBridge.EntryPoints = previousCallbacks;
            if (schedulerOwned && !RuntimeWorkScheduler.Shutdown(waitForWorkers: true))
            {
                checks.Add(new("scheduler-shutdown", "failed", "Workers did not quiesce."));
                exitCode = 1;
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new { checks, exitCode }));
        return exitCode;

        void Run(string name, Action check)
        {
            try { check(); checks.Add(new(name, "passed", "")); }
            catch (Exception exception) { checks.Add(new(name, "failed", exception.ToString())); exitCode = 1; }
        }
        void Skip(string name, string reason)
        {
            checks.Add(new(name, "skipped", reason));
            if (exitCode == 0) exitCode = 3;
        }
    }

    private static void RequireColor(byte[] pixels, byte[] expected, int tolerance)
    {
        if (pixels.Length != 16 * 16 * 4)
            throw new InvalidOperationException($"Unexpected readback size {pixels.Length}.");
        for (int index = 0; index < pixels.Length; index++)
            if (Math.Abs(pixels[index] - expected[index % 4]) > tolerance)
                throw new InvalidOperationException($"Pixel byte {index}: expected {expected[index % 4]} +/- {tolerance}, got {pixels[index]}.");
    }

    private static void RecordClear(Vk api, CommandBuffer command, VulkanRenderFrameTarget target, bool alternate)
    {
        ImageSubresourceRange range = new(ImageAspectFlags.ColorBit, 0, 1, 0, target.Layers);
        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier, Image = target.ColorImage, SubresourceRange = range,
            OldLayout = target.InitialColorLayout, NewLayout = ImageLayout.TransferDstOptimal,
            SrcAccessMask = target.InitialColorLayout == ImageLayout.Undefined ? 0 : AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
            DstAccessMask = AccessFlags.TransferWriteBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
        };
        api.CmdPipelineBarrier(command, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, in barrier);
        ClearColorValue color = alternate ? new(1f, 0f, 0f, 1f) : new(0f, 1f, 0f, 1f);
        api.CmdClearColorImage(command, target.ColorImage, ImageLayout.TransferDstOptimal, in color, 1, in range);
        barrier.OldLayout = ImageLayout.TransferDstOptimal;
        barrier.NewLayout = target.RequiredFinalColorLayout;
        barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
        barrier.DstAccessMask = AccessFlags.TransferReadBit;
        api.CmdPipelineBarrier(command, PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, in barrier);
    }
}
