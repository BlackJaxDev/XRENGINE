using Silk.NET.Maths;
using Silk.NET.Windowing;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading;
using XREngine.Rendering;

namespace XREngine
{
    public static partial class Engine
    {
        public static partial class Rendering
        {
            /// <summary>
            /// Background compute context intended for secondary GPUs or shared-context offloading.
            /// </summary>
            public sealed class SecondaryGpuContext : IDisposable
            {
                private readonly ConcurrentQueue<Action<AbstractRenderer>> _jobs = new();
                private readonly AutoResetEvent _jobSignal = new(false);
                private CancellationTokenSource? _cts;
                private Thread? _thread;
                private XRWindow? _headlessWindow;
                private AbstractRenderer? _renderer;

                public bool IsRunning => _thread is not null && _thread.IsAlive;
                public bool HasRenderer => _renderer is not null;

                public void InitializeIfSupported(XRWindow? templateWindow)
                {
                    if (IsRunning || templateWindow is null)
                        return;

                    if (!Engine.Rendering.Settings.EnableSecondaryGpuCompute)
                        return;

                    if (!HasMultipleGpus() && !Engine.Rendering.Settings.AllowSecondaryContextSharingFallback)
                        return;

                    _cts = new CancellationTokenSource();
                    _thread = new Thread(() => RunContext(templateWindow, _cts.Token))
                    {
                        IsBackground = true,
                        Name = "XR Secondary Render Context"
                    };
                    _thread.Start();
                }

                public bool EnqueueJob(Action<AbstractRenderer> job, bool allowFallbackToMainThread = true)
                {
                    if (job is null)
                        return false;

                    if (IsRunning)
                    {
                        _jobs.Enqueue(job);
                        _jobSignal.Set();
                        return true;
                    }

                    if (!allowFallbackToMainThread)
                        return false;

                    // fallback executes on render thread to preserve correctness
                    Engine.EnqueueMainThreadTask(() =>
                    {
                        var renderer = AbstractRenderer.Current ?? Engine.Windows.FirstOrDefault()?.Renderer;
                        if (renderer is null)
                            return;
                        job(renderer);
                    });
                    return true;
                }

                public void Dispose()
                {
                    try
                    {
                        _cts?.Cancel();
                        _jobSignal.Set();
                        _thread?.Join(TimeSpan.FromSeconds(1));
                        _cts?.Dispose();
                    }
                    catch
                    {
                        // ignored - best effort shutdown
                    }

                    try
                    {
                        _headlessWindow?.Renderer.CleanUp();
                        _headlessWindow?.Window.Dispose();
                    }
                    catch
                    {
                    }

                    _headlessWindow = null;
                    _renderer = null;
                    _thread = null;
                    _cts = null;
                }

                private void RunContext(XRWindow templateWindow, CancellationToken token)
                {
                    try
                    {
                        CreateHeadlessWindow(templateWindow);
                        var renderer = _renderer;
                        if (renderer is null || _headlessWindow is null)
                            return;

                        while (!token.IsCancellationRequested)
                        {
                            if (!_jobs.TryDequeue(out var job))
                            {
                                _jobSignal.WaitOne(TimeSpan.FromMilliseconds(2));
                                continue;
                            }

                            try
                            {
                                _headlessWindow.Window.DoEvents();
                                _headlessWindow.Window.MakeCurrent();
                                renderer.Active = true;
                                AbstractRenderer.Current = renderer;
                                job(renderer);
                            }
                            finally
                            {
                                renderer.Active = false;
                                AbstractRenderer.Current = null;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"Secondary render context terminated: {ex.Message}\n{ex.StackTrace}");
                    }
                }

                private void CreateHeadlessWindow(XRWindow templateWindow)
                {
                    var options = WindowOptions.Default;
                    options.Size = new Vector2D<int>(Math.Max(64, templateWindow.Window.Size.X / 8), Math.Max(64, templateWindow.Window.Size.Y / 8));
                    options.Title = "XR Secondary GPU Context";
                    options.PreferredStencilBufferBits = templateWindow.Window.StencilBits;
                    options.PreferredDepthBufferBits = templateWindow.Window.DepthBits;
                    options.API = templateWindow.Window.API;
                    options.PreferredBitDepth = templateWindow.Window.VideoMode.BitsPerPixel;
                    options.IsVisible = false;
                    options.PreferredRefreshRate = templateWindow.Window.VideoMode.RefreshRate;
                    options.SharedContext = Engine.Rendering.Settings.AllowSecondaryContextSharingFallback
                        ? templateWindow.Window
                        : null;

                    var window = new XRWindow(options, templateWindow.UseNativeTitleBar);
                    _headlessWindow = window;
                    window.Renderer.Initialize();
                    _renderer = window.Renderer;
                }

                private static bool HasMultipleGpus()
                {
                    try
                    {
                        if (!OperatingSystem.IsWindows())
                            return false;

                        using var searcher = new ManagementObjectSearcher("select Name from Win32_VideoController where Status='OK'");
                        using var results = searcher.Get();
                        int count = 0;
                        foreach (var _ in results)
                            count++;
                        return count > 1;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"Unable to query GPU inventory: {ex.Message}");
                        return false;
                    }
                }
            }

            public static SecondaryGpuContext SecondaryContext { get; } = new();

            public static IReadOnlyList<string> RecommendedSecondaryGpuTasks { get; } = new List<string>
            {
                "CPU-visible readback of GPU counters and visibility buffers to avoid stalling the main swap chain",
                "Async skinning and bounds expansion for skinned meshes",
                "Mesh signed-distance-field (SDF) generation and voxelization jobs",
                "Building Hi-Z/occlusion data for next-frame culling",
                "Light probe or irradiance volume updates when not bound to the main frame budget"
            };
        }
    }
}
