using System.Runtime.InteropServices;

namespace XREngine.Data
{
    public class WFileMap : FileMap
    {
        internal WFileMap(VoidPtr hFile, FileMapProtect protect, long offset, uint length)
        {
            long maxSize = offset + length;
            uint maxHigh = (uint)(maxSize >> 32);
            uint maxLow = (uint)maxSize;
            Win32.FileMapProtect mProtect;
            Win32.FileMapAccess mAccess;
            if (protect == FileMapProtect.ReadWrite)
            {
                mProtect = Win32.FileMapProtect.ReadWrite;
                mAccess = Win32.FileMapAccess.Write;
            }
            else
            {
                mProtect = Win32.FileMapProtect.ReadOnly;
                mAccess = Win32.FileMapAccess.Read;
            }

            using (Win32.SafeHandle h = Win32.CreateFileMapping(hFile, null, mProtect, maxHigh, maxLow, string.Empty))
            {
                h.ErrorCheck();
                _addr = Win32.MapViewOfFile(h.Handle, mAccess, (uint)(offset >> 32), (uint)offset, length);
                if (!_addr)
                    Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
                _length = (int)length;
            }
        }

        public override void Dispose()
        {
            if (_addr)
            {
                Win32.FlushViewOfFile(_addr, 0);
                Win32.UnmapViewOfFile(_addr);
                _addr = null;
            }
            base.Dispose();
        }
    }
}
