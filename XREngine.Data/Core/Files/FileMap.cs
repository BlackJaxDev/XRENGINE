using XREngine.Extensions;
using XREngine;
using XREngine.Data;

namespace System
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
                Diagnostics.Trace.TraceWarning($"File at {path} is in use; creating temporary copy at {tempPath}.");
                File.Copy(path, tempPath, true);
                stream = new FileStream(tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 8, options | FileOptions.DeleteOnClose);
            }
            try
            {
                map = FromStreamInternal(stream, prot, offset, length);
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
            FileStream stream = new(path = Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 8, FileOptions.RandomAccess | FileOptions.DeleteOnClose);
            try
            {
                return FromStreamInternal(stream, FileMapProtect.ReadWrite, 0, length);
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
            if (length == 0)
                length = stream.Length;
            else
                length = length.ClampMax(stream.Length);

            return new ProviderFileMap(FileMappingServices.Required.Map(stream, prot == FileMapProtect.ReadWrite, offset, length), stream, ownsStream: false);
        }

        public static FileMap FromStreamInternal(FileStream stream, FileMapProtect prot, long offset, long length)
        {
            if (length == 0)
                length = stream.Length;
            else
                length = length.ClampMax(stream.Length);

            length = length.ClampMin(stream.Length);

            return new ProviderFileMap(FileMappingServices.Required.Map(stream, prot == FileMapProtect.ReadWrite, offset, length), stream, ownsStream: true);
        }
    }

}
