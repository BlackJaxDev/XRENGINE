using System.Globalization;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    public int CreateTexture(BrowserTextureData texture)
    {
        RequireReady();
        ArgumentNullException.ThrowIfNull(texture);
        byte[] bytes = texture.CopyPackedBytes();
        // Procedural single-level assets retain the original update/copy-compatible route.
        if (texture.Format == "rgba8unorm-srgb" && texture.MipCount == 1 && texture.NormalConvention == "none")
            return Track(WebGpuImports.CreateTexture(_session, texture.Width, texture.Height, bytes));
        string description = string.Create(CultureInfo.InvariantCulture,
            $"{{\"width\":{texture.Width},\"height\":{texture.Height},\"format\":\"{texture.Format}\",\"mipCount\":{texture.MipCount},\"normalConvention\":\"{texture.NormalConvention}\",\"alphaMode\":\"{texture.AlphaMode}\"}}");
        return Track(WebGpuImports.CreateCookedTexture(_session, description, bytes));
    }
}
