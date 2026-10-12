using XREngine.Data;

namespace XREngine.Rendering
{
    /// <summary>
    /// A buffer's client bytes backed by a host-owned copy-on-write spill mapping.
    /// </summary>
    internal sealed unsafe class XRBufferSpilledDataSource : DataSource
    {
        private IXRBufferSpillLease? _lease;

        private XRBufferSpilledDataSource(IXRBufferSpillLease lease)
            : base(lease.Address, lease.Length, copyInternal: false)
        {
            _lease = lease;
        }

        internal static XRBufferSpilledDataSource FromLease(IXRBufferSpillLease lease)
        {
            try
            {
                return new XRBufferSpilledDataSource(lease);
            }
            catch
            {
                lease.Release(disposing: true);
                throw;
            }
        }

        /// <summary>
        /// Maps <paramref name="file"/> copy-on-write and takes ownership of it.
        /// </summary>
        public static XRBufferSpilledDataSource Map(FileStream file, uint length)
            => FromLease(XRBufferSpillStorageServices.Required.Map(file, length));

        /// <summary>
        /// A clone owns a private copy because the mapping belongs to this source.
        /// </summary>
        public override DataSource Clone()
        {
            DataSource clone = new(Length);
            Memory.Move(clone.Address, Address, Length);
            return clone;
        }

        protected override void Dispose(bool disposing)
        {
            IXRBufferSpillLease? lease = Interlocked.Exchange(ref _lease, null);
            if (lease is not null)
            {
                XRBufferClientSpill.RecordReleased(Length);
                // Readers test Address for CPU data. Clear it before release.
                Address = null;
                Length = 0;
                lease.Release(disposing);
            }
            base.Dispose(disposing);
        }
    }
}
