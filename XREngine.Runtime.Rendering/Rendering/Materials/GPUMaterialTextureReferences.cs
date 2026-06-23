namespace XREngine.Rendering.Materials
{
    public readonly record struct GPUMaterialTextureReferences(
        GPUMaterialTextureReference Albedo,
        GPUMaterialTextureReference Normal,
        GPUMaterialTextureReference RM)
    {
        public static readonly GPUMaterialTextureReferences Empty = new();

        public static GPUMaterialTextureReferences FromOpenGLHandles(GPUMaterialTextureHandles handles)
            => new(
                GPUMaterialTextureReference.FromOpenGLBindlessHandle(handles.Albedo),
                GPUMaterialTextureReference.FromOpenGLBindlessHandle(handles.Normal),
                GPUMaterialTextureReference.FromOpenGLBindlessHandle(handles.RM));
    }
}
