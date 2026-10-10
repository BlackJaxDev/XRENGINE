using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine;

public static partial class RuntimeEngine
{
    public static partial class Rendering
    {
        private static readonly object PipelineFactoryInstallationLock = new();
        private static RenderPipelineFactoryInstallation? _pipelineFactoryInstallation;

        /// <summary>
        /// Installs an output-aware pipeline factory above the desktop selection policy
        /// for one host lifetime. Disposal exposes the newest installation still alive.
        /// </summary>
        /// <remarks>
        /// Cold factory invocation and installation lifetime changes are serialized;
        /// the ownership lock permits same-thread reentrancy and never covers frame submission.
        /// </remarks>
        public static IDisposable InstallRenderPipelineFactory(
            Func<RenderPipelineRequest, RenderPipeline> factory)
        {
            ArgumentNullException.ThrowIfNull(factory);
            lock (PipelineFactoryInstallationLock)
            {
                RenderPipelineFactoryInstallation installed = new(factory,
                    PruneDisposedPipelineFactoryInstallations());
                Volatile.Write(ref _pipelineFactoryInstallation, installed);
                return installed;
            }
        }

        private static bool TryCreateScopedRenderPipeline(
            RenderPipelineRequest request,
            out RenderPipeline pipeline)
        {
            if (Volatile.Read(ref _pipelineFactoryInstallation) is null)
            {
                pipeline = null!;
                return false;
            }

            // Pipeline creation is cold host work. Keep invocation inside the ownership
            // lock so teardown cannot dispose a selected callback before it is entered.
            lock (PipelineFactoryInstallationLock)
            {
                if (PruneDisposedPipelineFactoryInstallations()?.Factory is not { } factory)
                {
                    pipeline = null!;
                    return false;
                }

                pipeline = factory(request) ?? throw new InvalidOperationException(
                    "The installed render-pipeline factory did not provide a pipeline.");
                return true;
            }
        }

        private static RenderPipelineFactoryInstallation? PruneDisposedPipelineFactoryInstallations()
        {
            RenderPipelineFactoryInstallation? installed = _pipelineFactoryInstallation;
            while (installed is not null && installed.Factory is null)
                installed = installed.Previous;
            Volatile.Write(ref _pipelineFactoryInstallation, installed);
            return installed;
        }

        private sealed class RenderPipelineFactoryInstallation(
            Func<RenderPipelineRequest, RenderPipeline> factory,
            RenderPipelineFactoryInstallation? previous) : IDisposable
        {
            internal Func<RenderPipelineRequest, RenderPipeline>? Factory { get; private set; } = factory;
            internal RenderPipelineFactoryInstallation? Previous { get; } = previous;

            public void Dispose()
            {
                lock (PipelineFactoryInstallationLock)
                {
                    if (Factory is null)
                        return;
                    Factory = null;
                    PruneDisposedPipelineFactoryInstallations();
                }
            }
        }
    }
}
