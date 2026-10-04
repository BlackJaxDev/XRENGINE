using XREngine.Extensions;
using System.Diagnostics;
using XREngine.Data;

namespace XREngine
{
    public abstract class FileMap : IDisposable
    {
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
            FileStream stream;
            FileMap map;
            try
            {
                if (!File.Exists(path))
                    stream = File.Create(path, 8, options);
                else
                    stream = new FileStream(path, FileMode.Open, (prot == FileMapProtect.ReadWrite) ? FileAccess.ReadWrite : FileAccess.Read, FileShare.Read, 8, options);
            }
            catch //File is currently in use, but we can copy it to a temp location and read that
            {
                string tempPath = Path.GetTempFileName();
                Trace.WriteLine($"File at {path} is in use; creating temporary copy at {tempPath}.");
                File.Copy(path, tempPath, true);
                stream = new FileStream(tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 8, options | FileOptions.DeleteOnClose);
            }
            try
            {
                map = FromStreamInternal(stream, prot, offset, length, backend);
            }
            catch (Exception)
            {
                stream.Dispose();
                throw;
            }
            map._path = path; //In case we're using a temp file
            return map;
        }
        public static FileMap? FromTempFile(long length)
            => FromTempFile(length, out _);
        public static FileMap? FromTempFile(long length, out string path)
        {
            RuntimeAssetReadServices.EnsureHostFileAccess("Temporary file mapping");
            IFileMappingBackend backend = FileMappingServices.Required;
            FileStream stream = new FileStream(path = Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 8, FileOptions.RandomAccess | FileOptions.DeleteOnClose);
            try
            {
                return FromStreamInternal(stream, FileMapProtect.ReadWrite, 0, length, backend);
            }
            catch (Exception ex)
            {
                stream.Dispose();
                Trace.WriteLine(ex);
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
            //FileStream newStream = new FileStream(stream.Name, FileMode.Open, prot == FileMapProtect.Read ? FileAccess.Read : FileAccess.ReadWrite, FileShare.Read, 8, FileOptions.RandomAccess);
            //try { return FromStreamInternal(newStream, prot, offset, length); }
            //catch (Exception x) { newStream.Dispose(); throw x; }

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
