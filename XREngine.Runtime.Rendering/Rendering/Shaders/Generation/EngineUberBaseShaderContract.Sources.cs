using System.Collections.Immutable;

namespace XREngine.Rendering.Shaders.Generation;

public static partial class EngineUberBaseShaderContract
{
    public static ImmutableArray<EngineLitMaterialShaderSource> RequiredSources { get; } =
    [
        new("UberBaseSurface.slang", "7c6b0cc044b7db2f55a7a14d8913aaa060e839e77192d4c2f940221225ac3c3c"),
        new("UberBaseRaster.slang", "d9ba7e170c829f23f126d2147a5794b2b2576dca4a6798d028d5c41b230daab1"),
        new("UberBaseFragmentSampling.slang", "36616e72ab608e965bc9599fb7a77106db127bf1b839b798cfe8dfbe88fe29fe"),
        new("UberBaseForward.slang", "09e941cadd9672be58c1be2f530896f5341558c53ee71122ac4e15a30a2aa1b3"),
        new("UberBaseForwardLighting.slang", "9ebab30338bb7c8d787b8238626206b6ae58adc7776ebeabe06fb0b6775e044a"),
        new("UberBaseDepth.slang", "4d49a23ef9059873fabe551fa3d1d2f154f35166ead1c7ca0c8220b011cbc682"),
        new("UberBaseLighting.slang", "0bd8d0736528661489de6c724ac3ed46dadc9e466c7357a02559d87652b1b83c"),
        new("UberBasePbrEnvironment.slang", "24961cb1d28f39520c00a149870c3aac080f8de812ecb5d94699bcd90e4e299b"),
        new("LocalShadowSampling.slang", "1d8aa4707225ae90e3a156369c6ac11c5e313de88892f00736857505fc9b32fc"),
        new("AuthoredOrderGate.slang", "7cbcd25fdeb93ecb8eb475d83aecaf231c68b8b71ceefe06a0c3374643c85f2c"),
    ];
    public static ImmutableArray<EngineLitMaterialShaderSource> RequiredDesktopSources { get; } =
    [
        new("Snippets/AmbientOcclusionSampling.glsl", "6dcc0b3ba5a7f19d97425a1888b1afce3da420473d0bafbcd9aff85f31c079ab"),
        new("Snippets/ExactTransparencyDepthPeel.glsl", "a2bad4809a51dc086c33444e22e9992f6bc3da2fa8d38ebb21205fbb37babdaa"),
        new("Snippets/ExactTransparencyPpll.glsl", "0a11e1086909cd3476a5f400c64b79d586ebc49b9bd324280bd0376192757f58"),
        new("Snippets/ForwardLighting.glsl", "94f86fe827191a35e177341da091e5cc9ed60cf1153c805cf785dda49b2f281a"),
        new("Snippets/ForwardShadowSampling.glsl", "08d0fa0becb05662315d51c95bd14fb7065305f6ac9cbdeb92a4f4a535dc0801"),
        new("Snippets/LightAttenuation.glsl", "dad7a438f800f40f7cadf28099c0d9559c0632314364eeed4e270d0d683287ba"),
        new("Snippets/LightStructs.glsl", "fb66908be5d31f18fa636b6a4dd53e4025cd149113c89f58edc3b85f83d9b954"),
        new("Snippets/NormalEncoding.glsl", "926f6f879f1cebbbdbe7eb911d0ccfe3e57a2353345cffb69508ddf7ab90e7e9"),
        new("Snippets/ScreenSpaceUtils.glsl", "a7a855fae8dfeddd22ead2ae94eaca8a41897459c9c933099c678497e1e6731b"),
        new("Snippets/ShadowFilterBodies.glsl", "97b0f087cb67c5a649f247134f0edfaef90ff7e439313169443184d667a390fc"),
        new("Snippets/ShadowMomentEncoding.glsl", "05ec8ee6984c20bf213c40e9308f44b762674ed4864eb3964a971140d3944f23"),
        new("Snippets/ShadowSampling.glsl", "469b17fc321f873fe6d934fe591af6af4ed8c66f726c117b7bcabebf95e07d9c"),
        new("Uber/UberShader.frag", "a7b15d070be30a5eaf6e48bf01ac4bfcc0e2c2096609648ba2b668f80cf45f49"),
        new("Uber/UberShader.vert", "e7e8e992c5d53ceae3f0e5b6277da12541c78f0e7e045c3cf3ae2827230c1f7b"),
        new("Uber/common.glsl", "039f6e0aaa8ae6e81afa2b9529327e4e76b948d6c9c3e57fbe60254a4e0d3ba2"),
        new("Uber/dissolve.glsl", "c39eea155bca10e345468ecf6d34f22a60b6eb3ae47a75f0975d0fe212680d21"),
        new("Uber/extended_effect_uniforms.glsl", "be9f40545c2a254a548158cb4fecb9b1b120b65b5efe7bec46262a94e315a21e"),
        new("Uber/extended_effects.glsl", "02ec7200b5a8fcb75a8dee22cf72624f92c5df7f1bd7c0405bdf30913387e7cb"),
        new("Uber/flipbook.glsl", "f68fe46ff678556d70cf263b0e35982564af495b39d180afbfac40a14d43ab2e"),
        new("Uber/glitter.glsl", "7972d07af60a4a46dde99c4a78123a1e488562848f1319cfb51e486486e04380"),
        new("Uber/parallax.glsl", "fb56cdaa7dc8ee796452275e412410d281bb6a77de32001fc1f9c5da3d933abf"),
        new("Uber/procedural_eye.glsl", "42992e818a6bd699bfed345bea76813310424b097525e7355aa7df3c823221cb"),
        new("Uber/source_dissolve.glsl", "7cc289de892eb719495c1baa49c6e3cae73eb635f555e3422e58251223696cf8"),
        new("Uber/source_flipbook.glsl", "9b651b9851da898871a922b2df6e51b6a0eb4aa9b3783d343f6767c606ac234f"),
        new("Uber/source_glitter.glsl", "c085818f0f16b0631980f441916b6a80ff9c018b1d5b46908f5ac18e5e63e881"),
        new("Uber/source_shading.glsl", "812556a0db0ae84ca43d00f3a09b4af164a11edd7ca29c39c8e991a660d3e5a3"),
        new("Uber/surface_extensions.glsl", "5755503e621b47c7358c6f8f556300f4b59182b2588d121c8e028716ade6ac79"),
        new("Uber/surface_extensions_uniforms.glsl", "227cf37ec746e396d969d53f8135b31fd6c7e619e1404b800941a9960ca9206d"),
        new("Uber/uniforms.glsl", "3b956d17300db492b5105f16ccdf60e1b00ece174f5e454a0d3776d8f928da4e"),
        new("Uber/vertex_effects.glsl", "75a6f32a379a9a243df005c04f590cd4699d130527c2498a7c41f90ed7c4f173"),
        new("Uber/vertex_manipulation.glsl", "673cddc426f27cb41600e77f582bc69cb597f21a222e19c126ac380b74cb9a55"),
    ];
}
