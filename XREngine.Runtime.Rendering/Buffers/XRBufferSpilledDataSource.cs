using System.IO.MemoryMappedFiles;
using XREngine.Data;

namespace XREngine.Rendering
{
    /// <summary>
    /// A buffer's client bytes backed by a copy-on-write view of a session spill
    /// file. The pages are file-backed, so the OS can drop and reread them; a write
    /// through <see cref="DataSource.Address"/> copies only the touched pages into
    /// private memory. Disposing unmaps the view and closes the delete-on-close file.
    /// </summary>
    internal sealed unsafe class XRBufferSpilledDataSource : DataSource
    {
        private MemoryMappedFile? _mapping;
        private MemoryMappedViewAccessor? _view;

        private XRBufferSpilledDataSource(byte* address, uint length, MemoryMappedFile mapping, MemoryMappedViewAccessor view)
            : base((void*)address, length, copyInternal: false)
        {
            _mapping = mapping;
            _view = view;
        }

        /// <summary>
        /// Maps <paramref name="file"/> copy-on-write and takes ownership of it.
        /// </summary>
        public static XRBufferSpilledDataSource Map(FileStream file, uint length)
        {
            MemoryMappedFile mapping = MemoryMappedFile.CreateFromFile(
                file,
                mapName: null,
                capacity: 0L,
                MemoryMappedFileAccess.CopyOnWrite,
                HandleInheritability.None,
                leaveOpen: false);
            MemoryMappedViewAccessor? view = null;
            try
            {
                view = mapping.CreateViewAccessor(0L, length, MemoryMappedFileAccess.CopyOnWrite);
                byte* pointer = null;
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
                return new XRBufferSpilledDataSource(pointer + view.PointerOffset, length, mapping, view);
            }
            catch
            {
                view?.Dispose();
                mapping.Dispose();
                throw;
            }
        }

        /// <summary>
        /// The mapping belongs to this source alone, so a clone gets its own private
        /// copy. The base class would return a second view of the mapping, which dies
        /// with this source.
        /// </summary>
        public override DataSource Clone()
        {
            DataSource clone = new(Length);
            Memory.Move(clone.Address, Address, Length);
            return clone;
        }

        protected override void Dispose(bool disposing)
        {
            MemoryMappedViewAccessor? view = Interlocked.Exchange(ref _view, null);
            MemoryMappedFile? mapping = Interlocked.Exchange(ref _mapping, null);
            if (view is not null)
            {
                XRBufferClientSpill.RecordReleased(Length);
                // Readers test Address for CPU data; an unmapped view must not look valid.
                Address = null;
                Length = 0;
                // Also valid from the finalizer: SafeHandles finalize after ordinary
                // finalizers, so the view handle is still alive here. Releasing the
                // acquired pointer lets that handle unmap the view and the file
                // handle close (deleting the spill file) even for buffers that were
                // dropped without being disposed.
                view.SafeMemoryMappedViewHandle.ReleasePointer();
                if (disposing)
                    view.Dispose();
            }
            if (disposing)
                mapping?.Dispose();
            base.Dispose(disposing);
        }
    }
}
