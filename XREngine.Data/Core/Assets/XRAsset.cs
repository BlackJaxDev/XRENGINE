using Extensions;
using MemoryPack;
using System;
using System.ComponentModel;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Json.Serialization;
using XREngine.Data.Core;
using YamlDotNet.Serialization;

namespace XREngine.Core.Files
{
    /// <summary>
    /// Base class for all engine-formatted objects that are loaded from or saved to disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="XRAsset"/> provides the foundation for the engine's asset system, handling:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>File path management and tracking</description></item>
    ///   <item><description>Dirty state tracking for unsaved changes</description></item>
    ///   <item><description>Memory-mapped file streaming for large assets</description></item>
    ///   <item><description>3rd-party file import support</description></item>
    ///   <item><description>Embedded asset hierarchies</description></item>
    ///   <item><description>YAML/MemoryPack serialization</description></item>
    /// </list>
    /// <para>
    /// Assets can be standalone files or embedded within other assets. The <see cref="SourceAsset"/>
    /// property tracks the root asset that owns embedded sub-assets.
    /// </para>
    /// </remarks>
    [MemoryPackable(GenerateType.NoGenerate)]
    public abstract partial class XRAsset : XRObjectBase
    {
        #region Fields

        private EventList<XRAsset> _embeddedAssets = [];
        private string? _serializedAssetType;
        private string? _originalPath;
        private DateTime? _originalLastWriteTimeUtc;
        private string? _filePath;
        private XRAsset? _sourceAsset = null;
        private bool _isDirty = false;

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the <see cref="XRAsset"/> class.
        /// </summary>
        [MemoryPackConstructor]
        public XRAsset() { }

        /// <summary>
        /// Initializes a new instance of the <see cref="XRAsset"/> class with the specified name.
        /// </summary>
        /// <param name="name">The display name for this asset.</param>
        public XRAsset(string name) => Name = name;

        #endregion

        #region Events

        /// <summary>
        /// Occurs when the asset has been reloaded from disk.
        /// </summary>
        public event Action<XRAsset>? Reloaded;

        #endregion

        #region Properties - Serialization Metadata

        /// <summary>
        /// Gets the fully qualified type name of this asset for serialization purposes.
        /// </summary>
        /// <remarks>
        /// This property is automatically injected into YAML files as <c>__assetType</c>,
        /// allowing tools to determine the asset's concrete type without full deserialization.
        /// </remarks>
        [YamlMember(Alias = "__assetType", Order = -100)]
        [Browsable(false)]
        [MemoryPackIgnore]
        public string SerializedAssetType
        {
            get => GetType().FullName ?? GetType().Name;
            private set => SetField(ref _serializedAssetType, value);
        }

        #endregion

        #region Properties - File System

        /// <summary>
        /// Gets or sets the absolute file system path where this asset is stored.
        /// </summary>
        /// <remarks>
        /// For embedded assets, this returns the <see cref="FilePath"/> of the <see cref="SourceAsset"/>.
        /// When set, the path is automatically normalized to an absolute path if valid.
        /// </remarks>
        [YamlIgnore]
        public string? FilePath
        {
            get => _sourceAsset is null || _sourceAsset == this ? _filePath : _sourceAsset.FilePath;
            set => SetField(ref _filePath, (value?.IsValidPath() ?? false) ? Path.GetFullPath(value) : value);
        }

        /// <summary>
        /// Gets or sets the original file path of the source asset before it was imported.
        /// </summary>
        /// <remarks>
        /// This is used to track the original 3rd-party file (e.g., .fbx, .gltf) 
        /// that was converted to create this engine-native asset.
        /// </remarks>
        public string? OriginalPath
        {
            get => _originalPath;
            set => SetField(ref _originalPath, value);
        }

        /// <summary>
        /// Gets or sets the UTC timestamp of the source file when this asset was last imported.
        /// </summary>
        /// <remarks>
        /// Used to detect when the source file has been modified and the asset needs re-importing.
        /// </remarks>
        public DateTime? OriginalLastWriteTimeUtc
        {
            get => _originalLastWriteTimeUtc;
            set => SetField(ref _originalLastWriteTimeUtc, value);
        }

        #endregion

        #region Properties - Asset Hierarchy

        /// <summary>
        /// Gets or sets the collection of sub-assets embedded within this asset.
        /// </summary>
        /// <remarks>
        /// This metadata is automatically reconstructed from the asset graph after loading.
        /// Embedded assets share the same file path as their source asset.
        /// </remarks>
        [YamlIgnore]
        public EventList<XRAsset> EmbeddedAssets
        {
            get => _embeddedAssets;
            internal set => SetField(ref _embeddedAssets, value);
        }

        /// <summary>
        /// Gets or sets the root asset that contains this asset.
        /// </summary>
        /// <remarks>
        /// <para>
        /// For top-level assets, this returns the asset itself.
        /// For embedded assets, this returns the parent asset that is actually persisted to disk.
        /// </para>
        /// <para>
        /// The source asset is used to determine the file path for saving and to manage
        /// the asset graph hierarchy.
        /// </para>
        /// </remarks>
        [YamlIgnore]
        [Browsable(false)]
        public XRAsset SourceAsset
        {
            get => _sourceAsset ?? this;
            set => SetField(ref _sourceAsset, value);
        }

        #endregion

        #region Properties - State

        /// <summary>
        /// Gets a value indicating whether this asset has unsaved changes.
        /// </summary>
        /// <remarks>
        /// Use <see cref="MarkDirty"/> to flag the asset as modified and 
        /// <see cref="ClearDirty"/> after saving.
        /// </remarks>
        [Browsable(false)]
        [MemoryPackIgnore]
        public bool IsDirty
        {
            get => _isDirty;
            private set => SetField(ref _isDirty, value);
        }

        #endregion

        #region Properties - Memory Mapped File Access

        /// <summary>
        /// Gets the memory-mapped file handle for direct memory access to the asset file.
        /// </summary>
        /// <remarks>
        /// This is used for unsafe pointer access to large asset data.
        /// Call <see cref="OpenForStreaming"/> to initialize.
        /// </remarks>
        [JsonIgnore]
        [YamlIgnore]
        [Browsable(false)]
        private MemoryMappedFile? FileMap { get; set; }

        /// <summary>
        /// Gets the stream for sequential reading and writing of the memory-mapped file.
        /// </summary>
        /// <remarks>
        /// Available after calling <see cref="OpenForStreaming"/>.
        /// Disposed when calling <see cref="CloseStreaming"/>.
        /// </remarks>
        [JsonIgnore]
        [YamlIgnore]
        [Browsable(false)]
        [MemoryPackIgnore]
        public MemoryMappedViewStream? FileMapStream { get; private set; }

        #endregion

        #region Methods - Dirty State Management

        /// <summary>
        /// Marks this asset as having unsaved changes.
        /// </summary>
        public void MarkDirty()
            => IsDirty = true;

        /// <summary>
        /// Clears the dirty flag, typically called after saving the asset.
        /// </summary>
        public void ClearDirty()
            => IsDirty = false;

        #endregion

        #region Methods - Memory Mapped File Streaming

        /// <summary>
        /// Opens the asset file for memory-mapped streaming access.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when <see cref="FilePath"/> is <see langword="null"/>.
        /// </exception>
        /// <remarks>
        /// This enables efficient random access to large asset files.
        /// Call <see cref="CloseStreaming"/> when finished to release resources.
        /// </remarks>
        public void OpenForStreaming()
        {
            if (FilePath is null)
                throw new InvalidOperationException("Cannot open a file for streaming without a file path.");

            CloseStreaming();

            FileMap = MemoryMappedFile.CreateFromFile(FilePath);
            FileMapStream = FileMap.CreateViewStream();
        }

        /// <summary>
        /// Closes the memory-mapped file stream and releases associated resources.
        /// </summary>
        public void CloseStreaming()
        {
            FileMapStream?.Dispose();
            FileMapStream = null;

            FileMap?.Dispose();
            FileMap = null;
        }

        #endregion

        #region Methods - 3rd Party Import

        /// <summary>
        /// Loads asset data from a 3rd-party file format.
        /// </summary>
        /// <param name="filePath">The path to the 3rd-party file to load.</param>
        /// <returns>
        /// <see langword="true"/> if the file was successfully loaded; otherwise, <see langword="false"/>.
        /// </returns>
        /// <remarks>
        /// Override this method in derived classes to implement loading from specific 
        /// 3rd-party formats (e.g., .fbx, .gltf, .png). The file extension should be 
        /// registered in the <c>XR3rdPartyExtensionsAttribute</c> list.
        /// </remarks>
        public virtual bool Load3rdParty(string filePath)
            => false;

        /// <summary>
        /// Loads asset data from a 3rd-party file format with an import context.
        /// The context provides cache-aware paths for auxiliary outputs (atlas textures, etc.)
        /// so that derived types never need to resolve cache paths themselves.
        /// </summary>
        /// <param name="filePath">The path to the 3rd-party file to load.</param>
        /// <param name="context">Import context supplied by the engine's asset pipeline.</param>
        /// <returns>
        /// <see langword="true"/> if the file was successfully loaded; otherwise, <see langword="false"/>.
        /// </returns>
        /// <remarks>
        /// The default implementation delegates to <see cref="Load3rdParty(string)"/>.
        /// Override this method when your asset type generates auxiliary files during import
        /// (e.g., font atlas textures) — use <see cref="AssetImportContext.ResolveAuxiliaryPath"/>
        /// to determine where to write them.
        /// </remarks>
        public virtual bool Load3rdParty(string filePath, AssetImportContext context)
            => Load3rdParty(filePath);

        /// <summary>
        /// Loads asset data from a 3rd-party file format asynchronously.
        /// </summary>
        /// <param name="filePath">The path to the 3rd-party file to load.</param>
        /// <returns>
        /// A task that represents the asynchronous operation, containing 
        /// <see langword="true"/> if successful; otherwise, <see langword="false"/>.
        /// </returns>
        /// <remarks>
        /// The default implementation runs <see cref="Load3rdParty(string)"/> on a background thread.
        /// Override for true async implementations.
        /// </remarks>
        public virtual async Task<bool> Load3rdPartyAsync(string filePath)
            => await Task.Run(() => Load3rdParty(filePath));

        /// <summary>
        /// Loads asset data from a 3rd-party file format asynchronously with an import context.
        /// </summary>
        /// <param name="filePath">The path to the 3rd-party file to load.</param>
        /// <param name="context">Import context supplied by the engine's asset pipeline.</param>
        /// <returns>
        /// A task that represents the asynchronous operation, containing 
        /// <see langword="true"/> if successful; otherwise, <see langword="false"/>.
        /// </returns>
        /// <remarks>
        /// The default implementation runs <see cref="Load3rdParty(string, AssetImportContext)"/> on a background thread.
        /// Override for true async implementations.
        /// </remarks>
        public virtual async Task<bool> Load3rdPartyAsync(string filePath, AssetImportContext context)
            => await Task.Run(() => Load3rdParty(filePath, context));

        /// <summary>
        /// Imports a 3rd-party file into this asset with optional import settings.
        /// </summary>
        /// <param name="filePath">The path to the 3rd-party file to import.</param>
        /// <param name="importOptions">
        /// Optional import options specific to the file type. May be <see langword="null"/>.
        /// </param>
        /// <returns>
        /// <see langword="true"/> if the import was successful; otherwise, <see langword="false"/>.
        /// </returns>
        /// <remarks>
        /// The default implementation ignores <paramref name="importOptions"/> and delegates to 
        /// <see cref="Load3rdParty(string)"/>. Override to handle custom import options.
        /// </remarks>
        public virtual bool Import3rdParty(string filePath, object? importOptions)
            => Load3rdParty(filePath);

        /// <summary>
        /// Imports a 3rd-party file into this asset asynchronously with optional import settings.
        /// </summary>
        /// <param name="filePath">The path to the 3rd-party file to import.</param>
        /// <param name="importOptions">
        /// Optional import options specific to the file type. May be <see langword="null"/>.
        /// </param>
        /// <returns>
        /// A task that represents the asynchronous operation, containing 
        /// <see langword="true"/> if successful; otherwise, <see langword="false"/>.
        /// </returns>
        public virtual async Task<bool> Import3rdPartyAsync(string filePath, object? importOptions)
            => await Task.Run(() => Import3rdParty(filePath, importOptions));

        #endregion

        #region Methods - Reload

        /// <summary>
        /// Reloads the asset from its current <see cref="FilePath"/>.
        /// </summary>
        /// <remarks>
        /// Does nothing if <see cref="FilePath"/> is <see langword="null"/>.
        /// Raises the <see cref="Reloaded"/> event after successful reload.
        /// </remarks>
        public void Reload()
        {
            if (FilePath is null)
                return;

            Reload(FilePath);
            Reloaded?.Invoke(this);
        }

        /// <summary>
        /// Reloads the asset from the specified file path.
        /// </summary>
        /// <param name="path">The file path to reload from.</param>
        /// <remarks>
        /// Override in derived classes to implement type-specific reload logic.
        /// The base implementation does nothing.
        /// </remarks>
        public virtual void Reload(string path)
        {
        }

        /// <summary>
        /// Reloads the asset from its current <see cref="FilePath"/> asynchronously.
        /// </summary>
        /// <returns>A task that represents the asynchronous reload operation.</returns>
        /// <remarks>
        /// Does nothing if <see cref="FilePath"/> is <see langword="null"/>.
        /// Raises the <see cref="Reloaded"/> event after successful reload.
        /// </remarks>
        public async Task ReloadAsync()
        {
            if (FilePath is null)
                return;

            await ReloadAsync(FilePath);
            Reloaded?.Invoke(this);
        }

        /// <summary>
        /// Reloads the asset from the specified file path asynchronously.
        /// </summary>
        /// <param name="path">The file path to reload from.</param>
        /// <returns>A task that represents the asynchronous reload operation.</returns>
        /// <remarks>
        /// The default implementation runs <see cref="Reload(string)"/> on a background thread.
        /// Override for true async implementations.
        /// </remarks>
        public virtual async Task ReloadAsync(string path)
            => await Task.Run(() => Reload(path));

        #endregion

        #region Methods - Serialization

        /// <summary>
        /// Serializes this asset to a file using the provided YAML serializer.
        /// </summary>
        /// <param name="filePath">The destination file path.</param>
        /// <param name="defaultSerializer">The YAML serializer to use.</param>
        /// <remarks>
        /// <para>
        /// The directory structure is created if it doesn't exist.
        /// The asset is serialized using its concrete runtime type to ensure all 
        /// derived members are included.
        /// </para>
        /// <para>
        /// Override to customize serialization behavior for specific asset types.
        /// </para>
        /// </remarks>
        public virtual void SerializeTo(string filePath, ISerializer defaultSerializer)
        {
            EnsureDirectoryExists(filePath);
            using var writer = new StreamWriter(filePath, append: false, Encoding.UTF8);
            defaultSerializer.Serialize(writer, this, GetType());
        }

        /// <summary>
        /// Serializes this asset to a file asynchronously using the provided YAML serializer.
        /// </summary>
        /// <param name="filePath">The destination file path.</param>
        /// <param name="defaultSerializer">The YAML serializer to use.</param>
        /// <returns>A task that represents the asynchronous serialization operation.</returns>
        /// <remarks>
        /// The serialization itself is synchronous, but the file write is asynchronous.
        /// The directory structure is created if it doesn't exist.
        /// </remarks>
        public virtual async Task SerializeToAsync(string filePath, ISerializer defaultSerializer)
        {
            EnsureDirectoryExists(filePath);
            string yaml = defaultSerializer.Serialize(this, GetType());
            await File.WriteAllTextAsync(filePath, yaml, Encoding.UTF8).ConfigureAwait(false);
        }

        #endregion

        #region Methods - Protected Overrides

        /// <inheritdoc/>
        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);

            // Skip asset graph refresh for hierarchy-related properties to avoid infinite recursion
            if (propName is nameof(SourceAsset) or nameof(EmbeddedAssets))
                return;

            if (XRAssetGraphUtility.ShouldRefreshForPropertyChange(prev, field))
                XRAssetGraphUtility.RefreshAssetGraph(SourceAsset);
        }

        #endregion

        #region Methods - Private Helpers

        /// <summary>
        /// Ensures that the directory for the specified file path exists, creating it if necessary.
        /// </summary>
        /// <param name="filePath">The file path whose directory should be created.</param>
        private static void EnsureDirectoryExists(string filePath)
        {
            string? directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
        }

        #endregion
    }
}
