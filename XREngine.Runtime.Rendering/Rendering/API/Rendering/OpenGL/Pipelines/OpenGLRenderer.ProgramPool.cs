using System;
using System.Collections.Generic;
using XREngine.Rendering;

namespace XREngine.Rendering.OpenGL;

public partial class OpenGLRenderer
{
    private readonly object _combinedProgramPoolLock = new();
    private readonly Dictionary<XRRenderProgramDescriptor, CombinedProgramPoolEntry> _combinedProgramPool = [];

    internal sealed class CombinedProgramPoolLease : IDisposable
    {
        private OpenGLRenderer? _renderer;
        private CombinedProgramPoolEntry? _entry;

        internal CombinedProgramPoolLease(OpenGLRenderer renderer, CombinedProgramPoolEntry entry, bool isNew)
        {
            _renderer = renderer;
            _entry = entry;
            IsNew = isNew;
        }

        public GLRenderProgram Program => _entry?.Program ?? throw new ObjectDisposedException(nameof(CombinedProgramPoolLease));
        public XRRenderProgram Data => Program.Data;
        public XRRenderProgramDescriptor Descriptor => _entry?.Descriptor ?? XRRenderProgramDescriptor.Empty;
        public bool IsNew { get; }

        public void Dispose()
        {
            OpenGLRenderer? renderer = _renderer;
            CombinedProgramPoolEntry? entry = _entry;
            if (renderer is null || entry is null)
                return;

            _renderer = null;
            _entry = null;
            renderer.ReleaseCombinedProgram(entry);
        }
    }

    internal sealed class CombinedProgramPoolEntry
    {
        public required XRRenderProgramDescriptor Descriptor { get; init; }
        public required XRRenderProgram Data { get; init; }
        public required GLRenderProgram Program { get; init; }
        public int ReferenceCount { get; set; }
    }

    internal CombinedProgramPoolLease AcquireCombinedProgram(
        XRRenderProgramDescriptor descriptor,
        Func<XRRenderProgram> createProgramData)
    {
        ArgumentNullException.ThrowIfNull(createProgramData);
        if (descriptor.IsEmpty)
            throw new ArgumentException("Combined program descriptors must not be empty.", nameof(descriptor));

        lock (_combinedProgramPoolLock)
        {
            if (_combinedProgramPool.TryGetValue(descriptor, out CombinedProgramPoolEntry? entry) &&
                !entry.Data.IsDestroyed &&
                entry.Program.Data is not null)
            {
                entry.ReferenceCount++;
                ShaderProgramLifecycleDiagnostics.RecordCombinedProgramPoolHit();
                ShaderProgramLifecycleDiagnostics.RecordCombinedProgramPoolAcquire(entry.ReferenceCount);
                return new CombinedProgramPoolLease(this, entry, isNew: false);
            }

            if (entry is not null)
                _combinedProgramPool.Remove(descriptor);

            XRRenderProgram data = createProgramData();
            data.ProgramDescriptor = descriptor;
            GLRenderProgram program = GenericToAPI<GLRenderProgram>(data)!;
            entry = new CombinedProgramPoolEntry
            {
                Descriptor = descriptor,
                Data = data,
                Program = program,
                ReferenceCount = 1,
            };

            _combinedProgramPool[descriptor] = entry;
            ShaderProgramLifecycleDiagnostics.RecordCombinedProgramPoolMiss();
            ShaderProgramLifecycleDiagnostics.RecordCombinedProgramPoolAcquire(entry.ReferenceCount);
            return new CombinedProgramPoolLease(this, entry, isNew: true);
        }
    }

    private void ReleaseCombinedProgram(CombinedProgramPoolEntry entry)
    {
        bool evict = false;
        lock (_combinedProgramPoolLock)
        {
            if (entry.ReferenceCount > 0)
            {
                entry.ReferenceCount--;
                ShaderProgramLifecycleDiagnostics.RecordCombinedProgramPoolRelease();
            }

            if (entry.ReferenceCount == 0 &&
                _combinedProgramPool.TryGetValue(entry.Descriptor, out CombinedProgramPoolEntry? current) &&
                ReferenceEquals(current, entry))
            {
                _combinedProgramPool.Remove(entry.Descriptor);
                evict = true;
            }
        }

        if (!evict)
            return;

        ShaderProgramLifecycleDiagnostics.RecordCombinedProgramPoolEviction();
        if (!entry.Data.IsDestroyed)
            entry.Data.Destroy();
    }
}
