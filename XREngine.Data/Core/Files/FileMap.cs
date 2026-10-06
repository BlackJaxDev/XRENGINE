using XREngine.Extensions;
using XREngine;
using XREngine.Data;

namespace System
{
    public abstract class FileMap : IDisposable
    {
        private static readonly Action<string, string> ReportFallback =
            static (path, tempPath) => Diagnostics.Trace.TraceWarning($"File at {path} is in use; creating temporary copy at {tempPath}.");

        protected VoidPtr _addr;
        protected long _length;
        protected string? _path;
        protected FileStream? _baseStream;

        public VoidPtr Address => _addr;
        public long Length { get => _length; set => _length = value; }
        public string? FilePath => _path;
        public FileStream? BaseStream => _baseStream;

        ~FileMap() { Dispose(); }
        public virtual void Dispose()
        {
            if (_baseStream != null)
            {
                _baseStream.Close();
                _baseStream.Dispose();
                _baseStream = null;
            }
            GC.SuppressFinalize(this);
        }

        public static FileMap FromFile(string path)
            => FromFile(path, FileMapProtect.ReadWrite, 0, 0);
        public static FileMap FromFile(string path, FileMapProtect prot) 
            => FromFile(path, prot, 0, 0);
        public static FileMap FromFile(string path, FileMapProtect prot, long offset, long length)
            => FromFile(path, prot, offset, length, FileOptions.RandomAccess);
        public static FileMap FromFile(string path, FileMapProtect prot, long offset, long length, FileOptions options)
        {
            RuntimeAssetReadServices.EnsureHostFileAccess("File mapping");
            IFileMappingBackend backend = FileMappingServices.Required;
            FileStream stream = backend.OpenFile(path, prot == FileMapProtect.ReadWrite, options, ReportFallback);
            FileMap map;
            try
            {
                map = FromStreamInternal(stream, prot, offset, length, backend);
            }
            catch (Exception)
            {
                stream.Dispose();
                throw;
            }
            map._path = path;
            return map;
        }
        public static FileMap? FromTempFile(long length)
            => FromTempFile(length, out _);
        public static FileMap? FromTempFile(long length, out string path)
        {
            RuntimeAssetReadServices.EnsureHostFileAccess("Temporary file mapping");
            IFileMappingBackend backend = FileMappingServices.Required;
            FileStream stream = backend.OpenTemporaryFile(out path);
            try
            {
                return FromStreamInternal(stream, FileMapProtect.ReadWrite, 0, length, backend);
            }
            catch (Exception ex)
            {
                stream.Dispose();
                Diagnostics.Trace.TraceError(ex.ToString());
            }
            return null;
        }

        public static FileMap FromStream(FileStream stream) 
            => FromStream(stream, FileMapProtect.ReadWrite, 0, 0);
        public static FileMap FromStream(FileStream stream, FileMapProtect prot)
            => FromStream(stream, prot, 0, 0);
        public static FileMap FromStream(FileStream stream, FileMapProtect prot, long offset, long length)
        {
            RuntimeAssetReadServices.EnsureHostFileAccess("File stream mapping");
            IFileMappingBackend backend = FileMappingServices.Required;
            if (length == 0)
                length = stream.Length;
            else
                length = length.ClampMax(stream.Length);

            return new ProviderFileMap(backend.Map(stream, prot == FileMapProtect.ReadWrite, offset, length), stream, ownsStream: false);
        }

        public static FileMap FromStreamInternal(FileStream stream, FileMapProtect prot, long offset, long length)
        {
            RuntimeAssetReadServices.EnsureHostFileAccess("File stream mapping");
            return FromStreamInternal(stream, prot, offset, length, FileMappingServices.Required);
        }

        private static FileMap FromStreamInternal(FileStream stream, FileMapProtect prot, long offset, long length, IFileMappingBackend backend)
        {
            if (length == 0)
                length = stream.Length;
            else
                length = length.ClampMax(stream.Length);

            length = length.ClampMin(stream.Length);

            return new ProviderFileMap(backend.Map(stream, prot == FileMapProtect.ReadWrite, offset, length), stream, ownsStream: true);
        }
    }

}
