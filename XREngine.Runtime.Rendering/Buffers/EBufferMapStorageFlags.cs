namespace XREngine.Rendering
{
    public enum EBufferMapStorageFlags
    {
        Read = 0x0001,
        Write = 0x0002,
        ReadWrite = Read | Write,
        /// <summary>
        /// The client may request that the server read from or write to the buffer while it is mapped. 
        /// The client's pointer to the data store remains valid so long as the data store is mapped, even during execution of drawing or dispatch commands.
        /// If flags contains GL_MAP_PERSISTENT_BIT, it must also contain at least one of GL_MAP_READ_BIT or GL_MAP_WRITE_BIT.
        /// </summary>
        Persistent = 0x0004,
        /// <summary>
        /// Shared access to buffers that are simultaneously mapped for client access and are used by the server will be coherent, so long as that mapping is performed using glMapBufferRange. 
        /// That is, data written to the store by either the client or server will be immediately visible to the other with no further action taken by the application.
        /// In particular,
        /// If not set and the client performs a write followed by a call to the glMemoryBarrier command with the GL_CLIENT_MAPPED_BUFFER_BARRIER_BIT set, then in subsequent commands the server will see the writes.
        /// If set and the client performs a write, then in subsequent commands the server will see the writes.
        /// If not set and the server performs a write, the application must call glMemoryBarrier with the GL_CLIENT_MAPPED_BUFFER_BARRIER_BIT set and then call glFenceSync with GL_SYNC_GPU_COMMANDS_COMPLETE (or glFinish). Then the CPU will see the writes after the sync is complete.
        /// If set and the server does a write, the app must call glFenceSync with GL_SYNC_GPU_COMMANDS_COMPLETE(or glFinish). Then the CPU will see the writes after the sync is complete.
        /// If flags contains GL_MAP_COHERENT_BIT, it must also contain GL_MAP_PERSISTENT_BIT.
        /// </summary>
        Coherent = 0x0005,
        /// <summary>
        /// When all other criteria for the buffer storage allocation are met, 
        /// this bit may be used by an implementation to determine whether 
        /// to use storage that is local to the server 
        /// or to the client to serve as the backing store for the buffer.
        /// </summary>
        ClientStorage = 0x0008,
        /// <summary>
        /// The contents of the data store may be updated after creation through calls to glBufferSubData.
        /// If this bit is not set, the buffer content may not be directly updated by the client.
        /// The data argument may be used to specify the initial content of the buffer's data store regardless of the presence of the GL_DYNAMIC_STORAGE_BIT.
        /// Regardless of the presence of this bit, buffers may always be updated with server-side calls such as glCopyBufferSubData and glClearBufferSubData. 
        /// </summary>
        DynamicStorage = 0x0010,
    }
}