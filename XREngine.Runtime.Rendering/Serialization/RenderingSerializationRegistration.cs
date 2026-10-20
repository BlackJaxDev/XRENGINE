using System.Diagnostics.CodeAnalysis;
using XREngine.Core.Files.Caching;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Serialization;
using YamlDotNet.Serialization;

namespace XREngine;

/// <summary>Installs rendering-owned serializers, cache codecs, type hints, and importers.</summary>
public static class RenderingSerializationRegistration
{
    public static IDisposable Install()
        => RegistrationLeaseGroup.Create(static leases =>
        {
            RenderingPublishedCookedAssetRegistration.RegisterBrowserRuntimeFactories();
            leases.Add(global::XREngine.Generated.GeneratedRuntimeContracts_XREngine_Runtime_Rendering.Install());
            leases.Add(RuntimeCookedBinarySerializer.RegisterRuntimeFactory<XRShader>(static () => new XRShader()));
            leases.Add(ThirdPartyCacheCodecRegistry.Install(new TextureStreamingCacheCodec()));
            leases.Add(ThirdPartyAssetTypeRegistry.Install("XREngine.Runtime.Rendering", typeof(XRTexture2D)));
            // .cs also names C# scripts; only an explicitly typed shader load may claim it.
            leases.Add(ThirdPartyAssetTypeRegistry.Install(
                "XREngine.Runtime.Rendering",
                typeof(XRShader),
                typeof(XRShaderImportOptions),
                ["glsl", "shader", "slang", "frag", "vert", "geom", "tesc", "tese",
                    "comp", "task", "mesh", "fs", "vs", "gs", "tcs", "tes", "ts", "ms"]));
            leases.Add(ThirdPartyAssetTypeRegistry.Install("XREngine.Runtime.Rendering", typeof(FontGlyphSet)));
#if !XRE_PUBLISHED
            leases.Add(YamlSerializationContributions.Install(new RenderingYamlContribution()));
            leases.Add(AssetTypeHintProviders.Install(new RenderingAssetTypeHintProvider()));
            leases.Add(YamlEnumAliasRegistry.Install(
                "XREngine.Runtime.Rendering",
                EMeshSubmissionStrategyExtensions.LegacyGpuMeshletName,
                EMeshSubmissionStrategy.GpuMeshletZeroReadback));
            leases.Add(RenderingPolymorphicYamlFallbacks.Install());
#endif
        });

#if !XRE_PUBLISHED
    private sealed class RenderingYamlContribution : IYamlSerializationContribution
    {
        private const string TexturedAlphaTag = "!xre-textured-alpha-v1";
        private const string AuthoredTexturedTag = "!xre-authored-textured-v1";
        public string OwnerName => "XREngine.Runtime.Rendering";

        public void ConfigureSerializer(SerializerBuilder builder)
            => builder.WithTagMapping(TexturedAlphaTag, typeof(AuthoredTexturedAlphaMaterial))
                .WithTagMapping(AuthoredTexturedTag, typeof(AuthoredTexturedMaterial));

        public IEnumerable<IYamlTypeConverter> CreateTypeConverters()
            =>
            [
                new XRTextureYamlTypeConverter(),
                new XRTexture2DYamlTypeConverter(),
                new XRShaderCollectionYamlTypeConverter(),
                new XRMeshYamlTypeConverter(),
                new XRMeshBufferCollectionYamlTypeConverter(),
                new ModelYamlTypeConverter(),
                new XRMaterialYamlTypeConverter(),
                new SubMeshYamlTypeConverter(),
                new ShaderVarYamlTypeConverter(),
                new ViewportRenderCommandContainerYamlTypeConverter(),
            ];

        public void ConfigureDeserializer(DeserializerBuilder builder)
        {
            // The new explicit schema must survive concrete XRMaterial slots;
            // existing untagged material mappings retain their original reader.
            builder.WithTagMapping(TexturedAlphaTag, typeof(AuthoredTexturedAlphaMaterial));
            builder.WithTagMapping(AuthoredTexturedTag, typeof(AuthoredTexturedMaterial));
            builder.WithNodeDeserializer(
                new ViewportRenderCommandContainerYamlNodeDeserializer(),
                registration => registration.OnTop());
            builder.WithNodeDeserializer(
                new XRShaderScalarYamlNodeDeserializer(),
                registration => registration.OnTop());
            builder.WithNodeDeserializer(
                new XRShaderCollectionYamlNodeDeserializer(),
                registration => registration.OnTop());
        }
    }

    private sealed class RenderingAssetTypeHintProvider : IAssetTypeHintProvider
    {
        public bool TryResolveLegacyRootKey(
            string rootKey,
            Type expectedType,
            [NotNullWhen(true)] out Type? assetType)
        {
            Type? candidate = rootKey switch
            {
                nameof(Model.Meshes) => typeof(Model),
                nameof(SubMesh.LODs) => typeof(SubMesh),
                nameof(XRMaterial.Shaders) => typeof(XRMaterial),
                _ => null,
            };
            assetType = candidate;
            return candidate is not null && expectedType.IsAssignableFrom(candidate);
        }
    }
#endif

}
