namespace XREngine.Rendering.Materials
{

public partial class GPUMaterialTable
    {
        private struct GPUTextureHandleEntryWords
        {
            public uint HandleLo;
            public uint HandleHi;
            public uint Flags;
            public uint Padding0;
        }
    }
}
