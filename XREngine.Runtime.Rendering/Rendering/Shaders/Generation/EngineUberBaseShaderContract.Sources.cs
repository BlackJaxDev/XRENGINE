using System.Collections.Immutable;

namespace XREngine.Rendering.Shaders.Generation;

public static partial class EngineUberBaseShaderContract
{
    public static ImmutableArray<EngineLitMaterialShaderSource> RequiredSources { get; } =
    [
        new("UberBaseSurface.slang", "3ee6c61d528f18899d30e0df0433f7484b6060a4464a171fbdb3de8a2df81689"),
        new("UberBaseRaster.slang", "1f18af644268baaef84c00927fa0d60e95850cc2bc5b8851e3b1c585866552bf"),
        new("UberBaseFragmentSampling.slang", "db0b2972629c1ff976e257855a727412daddcb9fb0ed7f6c4f523579680b6b78"),
        new("UberBaseForward.slang", "63eb87d5bb9b6a9c0e94c3182987e7fef638a42f7a6fcd2d1bc10ed388826603"),
        new("UberBaseForwardLighting.slang", "126e3c3434d1b588f326955ee33399968b63a6ebd5a90906ea21f77a005a4869"),
        new("UberBaseDepth.slang", "3ba5d11a7d80bc433c8a2f0f1a9baa081667b32d419e225012947ef2eacef3ba"),
        new("UberBaseLighting.slang", "0bd8d0736528661489de6c724ac3ed46dadc9e466c7357a02559d87652b1b83c"),
        new("UberBasePbrEnvironment.slang", "f2fa037c67c07084f0dd7fbfd147d48633724c77c9b8ed7d37c805b8682a8218"),
        new("LocalShadowSampling.slang", "1d8aa4707225ae90e3a156369c6ac11c5e313de88892f00736857505fc9b32fc"),
        new("AuthoredOrderGate.slang", "7cbcd25fdeb93ecb8eb475d83aecaf231c68b8b71ceefe06a0c3374643c85f2c"),
    ];
    public static ImmutableArray<EngineLitMaterialShaderSource> RequiredDesktopSources { get; } =
    [
        new("Snippets/AmbientOcclusionSampling.glsl", "6dcc0b3ba5a7f19d97425a1888b1afce3da420473d0bafbcd9aff85f31c079ab"),
        new("Snippets/ExactTransparencyDepthPeel.glsl", "a2bad4809a51dc086c33444e22e9992f6bc3da2fa8d38ebb21205fbb37babdaa"),
        new("Snippets/ExactTransparencyPpll.glsl", "0a11e1086909cd3476a5f400c64b79d586ebc49b9bd324280bd0376192757f58"),
        new("Snippets/ForwardLighting.glsl", "639636a250a335b0c960618783135d9c3404366c0f005426128dfabe28f741f4"),
        new("Snippets/LightAttenuation.glsl", "dad7a438f800f40f7cadf28099c0d9559c0632314364eeed4e270d0d683287ba"),
        new("Snippets/LightStructs.glsl", "fb66908be5d31f18fa636b6a4dd53e4025cd149113c89f58edc3b85f83d9b954"),
        new("Snippets/NormalEncoding.glsl", "926f6f879f1cebbbdbe7eb911d0ccfe3e57a2353345cffb69508ddf7ab90e7e9"),
        new("Snippets/ScreenSpaceUtils.glsl", "a7a855fae8dfeddd22ead2ae94eaca8a41897459c9c933099c678497e1e6731b"),
        new("Snippets/ShadowMomentEncoding.glsl", "b0ad566842e7e9ea654685bcadba629cf2000c88088251cffc5792a9ddf1b17b"),
        new("Snippets/ShadowSampling.glsl", "f404d3585da7d87332a019d9190c56b7c8aeb82d61679fa40d2aa742d876e726"),
        new("Uber/UberShader.frag", "ac53058b54e0f906861f24cdd48440b6d2a4d6222c8188b0ccb8d0d1eb807967"),
        new("Uber/UberShader.vert", "0a304c2b877f9011cd976081f51ae8bfaae03f325609889c1fbac14749af8ff8"),
        new("Uber/common.glsl", "98756dfd4a3370919006101de98c1adb99efabc6aac6c126a1476bd817281446"),
        new("Uber/dissolve.glsl", "dae8e92050de22e2be55e2b19b50000eb92fb8e259c0e4230bfbe9fd9d9b5d65"),
        new("Uber/extended_effect_uniforms.glsl", "3a96d7b2f142199d6425e4b4cbcb3a99025696a01c7f7ca6ac19622f2865031d"),
        new("Uber/extended_effects.glsl", "0abe0079db05c10c38a83b99cf991b1b0a673e73ed2205b3b1c9c97ea92307b4"),
        new("Uber/flipbook.glsl", "f68fe46ff678556d70cf263b0e35982564af495b39d180afbfac40a14d43ab2e"),
        new("Uber/glitter.glsl", "7972d07af60a4a46dde99c4a78123a1e488562848f1319cfb51e486486e04380"),
        new("Uber/parallax.glsl", "082cd0f043763c192995a66067bce797c46116578f6740ec748d9fbebd17d2dd"),
        new("Uber/surface_extensions.glsl", "f200b1689b41197f7d8648c9a9eb0691e60691da9cfe025f5807565773ac8e52"),
        new("Uber/surface_extensions_uniforms.glsl", "0b8b5f313c04f5572efb0a1c0f32745d755ef4ed988b5c4469a6c6460a0eb703"),
        new("Uber/uniforms.glsl", "6a40c01dac8ad25b4a3f446e94980ed494dc84f63d9d4cd4ec0d560c438cde96"),
        new("Uber/vertex_effects.glsl", "c35bed02889627b905c7d7328f455c7960268632bfb28b7f268c0548777a2a76"),
    ];
}
