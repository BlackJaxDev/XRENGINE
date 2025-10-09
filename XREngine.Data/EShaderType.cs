namespace XREngine
{
    public enum EShaderType
    {
        Fragment,
        Vertex,
        Geometry,
        TessEvaluation,
        TessControl,
        Compute,
        /// <summary>
        /// Requires GL_NV_mesh_shader extension
        /// </summary>
        Task,
        /// <summary>
        /// Requires GL_NV_mesh_shader extension
        /// </summary>
        Mesh,
    }
}
