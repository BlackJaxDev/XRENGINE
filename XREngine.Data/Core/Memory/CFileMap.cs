using System.IO.MemoryMappedFiles;

namespace XREngine
{
    public unsafe class CFileMap : FileMap
    {
        protected MemoryMappedFile _mappedFile;
        protected MemoryMappedViewAccessor _mappedFileAccessor;

        public CFileMap(FileStream stream, FileMapProtect protect, long offset, long length)
        {
            MemoryMappedFileAccess cProtect = (protect == FileMapProtect.ReadWrite) 
                ? MemoryMappedFileAccess.ReadWrite 
                : MemoryMappedFileAccess.Read;

            _length = length;
            _mappedFile = MemoryMappedFile.CreateFromFile(stream, stream.Name, _length, cProtect, HandleInheritability.None, true);
            _mappedFileAccessor = _mappedFile.CreateViewAccessor(offset, _length, cProtect);
            _addr = _mappedFileAccessor.SafeMemoryMappedViewHandle.DangerousGetHandle();
        }

        public override void Dispose()
        {
            _mappedFile?.Dispose();
            _mappedFileAccessor?.Dispose();
            base.Dispose();
        }
    }
}
