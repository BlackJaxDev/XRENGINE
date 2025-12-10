namespace XREngine.Rendering
{
    public enum EBufferMapRangeFlags
    {
        /// <summary>
        /// GL_MAP_READ_BIT indicates that the returned pointer may be used to read buffer object data.
        /// No GL error is generated if the pointer is used to query a mapping which excludes this flag,
        /// but the result is undefined and system errors (possibly including program termination) may occur.
        /// </summary>
        Read = 0x0001,
        /// <summary>
        /// GL_MAP_WRITE_BIT indicates that the returned pointer may be used to modify buffer object data.
        /// No GL error is generated if the pointer is used to modify a mapping which excludes this flag,
        /// but the result is undefined and system errors (possibly including program termination) may occur. 
        /// </summary>
        Write = 0x0002,
        ReadWrite = Read | Write,
        /// <summary>
        /// GL_MAP_PERSISTENT_BIT indicates that the mapping is to be made in a persistent fashion and that the client intends to hold and use the returned pointer during subsequent GL operation.
        /// It is not an error to call drawing commands (render) while buffers are mapped using this flag.
        /// It is an error to specify this flag if the buffer's data store was not allocated through a call to the glBufferStorage command in which the GL_MAP_PERSISTENT_BIT was also set. 
        /// </summary>
        Persistent = 0x0040,
        /// <summary>
        /// GL_MAP_COHERENT_BIT indicates that a persistent mapping is also to be coherent.
        /// Coherent maps guarantee that the effect of writes to a buffer's data store by
        /// either the client or server will eventually become visible to the other without further intervention from the application.
        /// In the absence of this bit, persistent mappings are not coherent and modified ranges of the buffer store must be explicitly communicated to the GL,
        /// either by unmapping the buffer, or through a call to glFlushMappedBufferRange or glMemoryBarrier.
        /// </summary>
        Coherent = 0x0041,
        /// <summary>
        /// GL_MAP_INVALIDATE_RANGE_BIT indicates that the previous contents of the specified range may be discarded.
        /// Data within this range are undefined with the exception of subsequently written data.
        /// No GL error is generated if subsequent GL operations access unwritten data, but the result is undefined and system errors (possibly including program termination) may occur.
        /// This flag may not be used in combination with GL_MAP_READ_BIT.
        /// </summary>
        InvalidateRange = 0x0004,
        /// <summary>
        /// GL_MAP_INVALIDATE_BUFFER_BIT indicates that the previous contents of the entire buffer may be discarded.
        /// Data within the entire buffer are undefined with the exception of subsequently written data.
        /// No GL error is generated if subsequent GL operations access unwritten data, but the result is undefined and system errors (possibly including program termination) may occur.
        /// This flag may not be used in combination with GL_MAP_READ_BIT.
        /// </summary>
        InvalidateBuffer = 0x0008,
        /// <summary>
        /// GL_MAP_FLUSH_EXPLICIT_BIT indicates that one or more discrete subranges of the mapping may be modified.
        /// When this flag is set, modifications to each subrange must be explicitly flushed by calling glFlushMappedBufferRange.
        /// No GL error is set if a subrange of the mapping is modified and not flushed, but data within the corresponding subrange of the buffer are undefined.
        /// This flag may only be used in conjunction with GL_MAP_WRITE_BIT.
        /// When this option is selected, flushing is strictly limited to regions that are explicitly indicated with calls to glFlushMappedBufferRange prior to unmap;
        /// if this option is not selected glUnmapBuffer will automatically flush the entire mapped range when called.
        /// </summary>
        FlushExplicit = 0x0010,
        /// <summary>
        /// GL_MAP_UNSYNCHRONIZED_BIT indicates that the GL should not attempt to synchronize pending operations on the buffer prior to returning from glMapBufferRange or glMapNamedBufferRange.
        /// No GL error is generated if pending operations which source or modify the buffer overlap the mapped region, but the result of such previous and any subsequent operations is undefined. 
        /// </summary>
        Unsynchronized = 0x0020,
    }
}