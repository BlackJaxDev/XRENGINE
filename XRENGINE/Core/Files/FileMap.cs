using Extensions;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
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
                Debug.LogWarning($"File at {path} is in use; creating temporary copy at {tempPath}.");
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
                Debug.LogException(ex);
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

            return Environment.OSVersion.Platform switch
            {
                PlatformID.Win32NT => new WFileMap(stream.SafeFileHandle.DangerousGetHandle(), prot, offset, length) { _path = stream.Name },
                _ => new CFileMap(stream, prot, offset, length) { _path = stream.Name },
            };
        }

        public static FileMap FromStreamInternal(FileStream stream, FileMapProtect prot, long offset, long length)
        {
            if (length == 0)
                length = stream.Length;
            else
                length = length.ClampMax(stream.Length);

            length = length.ClampMin(stream.Length);

            return Environment.OSVersion.Platform switch
            {
                PlatformID.Win32NT => new WFileMap(stream.SafeFileHandle.DangerousGetHandle(), prot, offset, length) { _baseStream = stream, _path = stream.Name },
                _ => new CFileMap(stream, prot, offset, length) { _baseStream = stream, _path = stream.Name },
            };
        }
    }

    public enum FileMapProtect : uint
    {
        Read = 0x01,
        ReadWrite = 0x02
    }

    public class WFileMap : FileMap
    {
        internal WFileMap(VoidPtr hFile, FileMapProtect protect, long offset, long length)
        {
            long maxSize = offset + length;
            uint maxHigh = (uint)(maxSize >> 32);
            uint maxLow = (uint)maxSize;
            Win32._FileMapProtect mProtect;
            Win32._FileMapAccess mAccess;
            if (protect == FileMapProtect.ReadWrite)
            {
                mProtect = Win32._FileMapProtect.ReadWrite;
                mAccess = Win32._FileMapAccess.Write;
            }
            else
            {
                mProtect = Win32._FileMapProtect.ReadOnly;
                mAccess = Win32._FileMapAccess.Read;
            }

            using Win32.SafeHandle h = Win32.CreateFileMapping(hFile, null, mProtect, maxHigh, maxLow, string.Empty);
            h.ErrorCheck();
            _addr = Win32.MapViewOfFile(h.Handle, mAccess, (uint)(offset >> 32), (uint)offset, (nuint)length);
            if (!_addr)
                Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
            _length = length;
        }

        public override void Dispose()
        {
            if (_addr)
            {
                Win32.FlushViewOfFile(_addr, 0);
                Win32.UnmapViewOfFile(_addr);
                _addr = null;
            }
            GC.SuppressFinalize(this);
            base.Dispose();
        }
    }
    public unsafe class CFileMap : FileMap
    {
        protected MemoryMappedFile _mappedFile;
        protected MemoryMappedViewAccessor _mappedFileAccessor;

        public CFileMap(FileStream stream, FileMapProtect protect, long offset, long length)
        {
            MemoryMappedFileAccess cProtect = (protect == FileMapProtect.ReadWrite) ? MemoryMappedFileAccess.ReadWrite : MemoryMappedFileAccess.Read;
            _length = length;
            _mappedFile = MemoryMappedFile.CreateFromFile(stream, stream.Name, _length, cProtect, HandleInheritability.None, true);
            _mappedFileAccessor = _mappedFile.CreateViewAccessor(offset, _length, cProtect);
            _addr = _mappedFileAccessor.SafeMemoryMappedViewHandle.DangerousGetHandle();
        }

        public override void Dispose()
        {
            _mappedFile?.Dispose();
            _mappedFileAccessor?.Dispose();
            GC.SuppressFinalize(this);
            base.Dispose();
        }
    }
}
