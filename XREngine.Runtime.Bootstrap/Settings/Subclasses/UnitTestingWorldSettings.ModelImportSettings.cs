using Assimp;
using Newtonsoft.Json;

namespace XREngine.Runtime.Bootstrap;

public partial class UnitTestingWorldSettings
{
    public class ModelImportSettings
    {
        public bool Enabled { get; set; } = true;
        public UnitTestModelImportKind Kind { get; set; } = UnitTestModelImportKind.Static;
        public ModelImportMaterialMode MaterialMode { get; set; } = ModelImportMaterialMode.Deferred;
        /// <summary>
        /// When true and <see cref="MaterialMode"/> is <see cref="ModelImportMaterialMode.Deferred"/>,
        /// materials whose textures have alpha channels will use forward (lit) shaders
        /// instead of deferred shaders, giving proper transparency blending for those meshes.
        /// Materials without alpha stay in the deferred pipeline.
        /// </summary>
        public bool UseForwardForTransparent { get; set; } = false;
        /// <summary>
        /// Selects how this model import chooses between native format-specific importers
        /// and Assimp fallback. PreferNativeThenAssimp uses a native importer when the
        /// format has one available and falls back to Assimp otherwise. Today the native
        /// path exists for FBX and glTF.
        /// </summary>
        public ModelImportBackendPreference ImporterBackend { get; set; } = ModelImportBackendPreference.PreferNativeThenAssimp;
        public string Path { get; set; } = string.Empty;
        public PostProcessSteps ImportFlags { get; set; } = PostProcessSteps.None;
        public float Scale { get; set; } = 1.0f;
        public bool ZUp { get; set; } = false;

        /// <summary>
        /// Number of independent scene instances to create from the imported
        /// hierarchy. The source asset is imported once and additional
        /// instances are cloned after import.
        /// </summary>
        public int InstanceCount { get; set; } = 1;

        /// <summary>
        /// Additional post-import actions to apply after the source model has been loaded.
        /// </summary>
        public ModelPostImportFlags PostImportFlags { get; set; } = ModelPostImportFlags.None;

        [JsonProperty("GenerateCoacdCollidersPerSubmesh")]
        private bool LegacyGenerateCoacdCollidersPerSubmesh
        {
            set => SetLegacyPostImportFlag(ModelPostImportFlags.GenerateCoacdCollidersPerSubmesh, value);
        }

        [JsonProperty("SplitSubmeshesIntoSeparateModelComponents")]
        private bool LegacySplitSubmeshesIntoSeparateModelComponents
        {
            set => SetLegacyPostImportFlag(ModelPostImportFlags.SplitSubmeshesIntoSeparateModelComponents, value);
        }

        [JsonProperty("SeparateMeshIslands")]
        private bool LegacySeparateMeshIslands
        {
            set => SetLegacyPostImportFlag(ModelPostImportFlags.SeparateMeshIslands, value);
        }

        private void SetLegacyPostImportFlag(ModelPostImportFlags flag, bool enabled)
        {
            if (enabled)
                PostImportFlags |= flag;
        }

        public YawPitchRollDegrees? YawPitchRoll { get; set; }
        public TranslationXYZ? Translation { get; set; }
    }
}
