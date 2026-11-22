using System.Collections.Immutable;

namespace XREngine.Rendering.Shaders.Generation;

public static partial class EngineUberBaseShaderContract
{
    public static ImmutableArray<EngineLitMaterialShaderSource> RequiredSources { get; } =
    [
        new("UberBaseSurface.slang", "049d8b7173dbd462def62dd718f60b1b9515c14a30732c4df0ea5286642f840a"),
        new("UberBaseRaster.slang", "d9ba7e170c829f23f126d2147a5794b2b2576dca4a6798d028d5c41b230daab1"),
        new("UberBaseFragmentSampling.slang", "36616e72ab608e965bc9599fb7a77106db127bf1b839b798cfe8dfbe88fe29fe"),
        new("UberBaseForward.slang", "09e941cadd9672be58c1be2f530896f5341558c53ee71122ac4e15a30a2aa1b3"),
        new("UberBaseForwardLighting.slang", "2fd98325e7e6097d4365e7b4b5aedb296e98c09a0673bf05c0edf913264d719e"),
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
        new("Uber/UberShader.frag", "b5cd48ef5474f1752cddfcf620a9ef3a3388a1dbbdf683251b8f1a8313b64a0a"),
        new("Uber/UberShader.vert", "e7e8e992c5d53ceae3f0e5b6277da12541c78f0e7e045c3cf3ae2827230c1f7b"),
        new("Uber/common.glsl", "98756dfd4a3370919006101de98c1adb99efabc6aac6c126a1476bd817281446"),
        new("Uber/dissolve.glsl", "aee16f86a38bad4dbe4b555b3b3c8ef3b670319521b8ee51082f0818580e8394"),
        new("Uber/extended_effect_uniforms.glsl", "5cc4d8151fbd72b87b013cfc640592584d300ae6e9ae0cb46c5b4329cb3c05e1"),
        new("Uber/extended_effects.glsl", "08af279fa5313cb78bf2cdb6c7480a273a4818d8408c8bb7ffb5fb0b88720b01"),
        new("Uber/flipbook.glsl", "f68fe46ff678556d70cf263b0e35982564af495b39d180afbfac40a14d43ab2e"),
        new("Uber/glitter.glsl", "7972d07af60a4a46dde99c4a78123a1e488562848f1319cfb51e486486e04380"),
        new("Uber/parallax.glsl", "fb56cdaa7dc8ee796452275e412410d281bb6a77de32001fc1f9c5da3d933abf"),
        new("Uber/procedural_eye.glsl", "42992e818a6bd699bfed345bea76813310424b097525e7355aa7df3c823221cb"),
        new("Uber/source_dissolve.glsl", "7cc289de892eb719495c1baa49c6e3cae73eb635f555e3422e58251223696cf8"),
        new("Uber/source_flipbook.glsl", "b283a308738fb555550db767daf4f9f5eb8b7457bf1122a35f7c4760d4438b06"),
        new("Uber/source_glitter.glsl", "59dc797e5259e83bc89714c1f3dd05293508434f7e8112a256c52a5c6b847799"),
        new("Uber/source_shading.glsl", "bf2dd18b6a72694132f340a4cfb7db443389bce475c3611187e7ff74224fb251"),
        new("Uber/surface_extensions.glsl", "fe454d4ef65050ca90e03e7573f2d8395407d82f98ad5850452ae830c5ae7c1b"),
        new("Uber/surface_extensions_uniforms.glsl", "8a83ae29127436a0262cdf3eddb35cd5bd12583f1d417386ac36236e2ec4b9de"),
        new("Uber/uniforms.glsl", "370ce76029c98410f815e06b7007eba1ff3d89875cee31b8e2ae270bdbf73aab"),
        new("Uber/vertex_effects.glsl", "4d615ebe2ff7234d50f1fffa73c0e3fcf496d26b0fb8c6d59094be73743bdd87"),
        new("Uber/vertex_manipulation.glsl", "673cddc426f27cb41600e77f582bc69cb597f21a222e19c126ac380b74cb9a55"),
    ];
}
