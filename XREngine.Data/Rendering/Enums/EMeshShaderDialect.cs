namespace XREngine.Data.Rendering
{
    /// <summary>
    /// Identifies the task/mesh shader dialect exposed by the active renderer.
    /// </summary>
    public enum EMeshShaderDialect
    {
        /// <summary>
        /// No task/mesh shader dialect is available.
        /// </summary>
        None = 0,

        /// <summary>
        /// OpenGL GL_NV_mesh_shader dialect.
        /// </summary>
        OpenGLNV = 1,

        /// <summary>
        /// OpenGL GL_EXT_mesh_shader dialect.
        /// </summary>
        OpenGLEXT = 2,

        /// <summary>
        /// Vulkan VK_EXT_mesh_shader dialect.
        /// </summary>
        VulkanEXT = 3,
    }
}
