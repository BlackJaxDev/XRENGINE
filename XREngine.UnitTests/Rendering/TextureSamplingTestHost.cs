using System.Reflection;
using XREngine.Core.Files;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

/// <summary>Provides texture bytes and asset loads without a renderer.</summary>
public class TextureSamplingTestHost : DispatchProxy
{
    public IRuntimeRenderingHostServices Inner { get; set; } = null!;
    public XRTexture2D? LoadedTexture { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        switch (targetMethod.Name)
        {
            case "get_SupportsSynchronousTextureSourceWork":
                return true;
            case "get_IsRendererActive":
            case "get_IsRenderThread":
                return false;
            case "get_AssetFileExtension":
                return "asset";
            case nameof(IRuntimeRenderingHostServices.LoadAsset):
                return LoadedTexture;
            case nameof(IRuntimeRenderingHostServices.ReadAllBytesOwned):
                byte[] bytes = File.ReadAllBytes((string)args![0]!);
                using (CookedPayloadLease lease = CookedPayloadBufferPool.Rent(bytes.Length))
                {
                    CookedPayloadOwner owner = lease.TransferToOwner();
                    bytes.CopyTo(owner.GetSpan());
                    return owner;
                }
            default:
                return targetMethod.Invoke(Inner, args);
        }
    }
}
