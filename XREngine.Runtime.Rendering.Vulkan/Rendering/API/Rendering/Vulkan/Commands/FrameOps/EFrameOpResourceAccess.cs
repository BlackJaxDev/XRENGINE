namespace XREngine.Rendering.Vulkan;

[Flags]
internal enum EFrameOpResourceAccess : byte
{
    None = 0,
    Read = 1,
    Write = 2,
    Imported = 4,
}
