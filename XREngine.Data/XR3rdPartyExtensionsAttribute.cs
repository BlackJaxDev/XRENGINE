namespace XREngine.Data
{
    /// <summary>
    /// These are the extensions that will be recognized by the asset manager as 3rd-party loadable for this asset.
    /// No need to include the dot in the extension.
    /// </summary>
    /// <remarks>
    /// The extensions that will be recognized by the asset manager as 3rd-party loadable for this asset.
    /// Sets staticLoad to false for all extensions.
    /// </remarks>
    /// <param name="extensions"></param>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public class XR3rdPartyExtensionsAttribute : Attribute
    {
        public XR3rdPartyExtensionsAttribute(params string[] extensions)
        {
            Extensions = extensions.Select(ext => ext.EndsWith(":static") ? (ext[..^7], true) : (ext, false)).ToArray();
        }

        public XR3rdPartyExtensionsAttribute(Type importOptionsType, params string[] extensions)
            : this(extensions)
        {
            ImportOptionsType = importOptionsType;
        }

        /// <summary>
        /// These are the 3rd-party file extensions that this asset type can load.
        /// staticLoad is used to determine if the asset should be loaded statically with T? Load3rdPartyStatic(string filePath) or async Task{T?} Load3rdPartyStaticAsync(string filePath), or after the instance is created for you.
        /// </summary>
        public (string ext, bool staticLoad)[] Extensions { get; }

        /// <summary>
        /// Optional import-options type used by the editor/import pipeline for these extensions.
        /// The object is serialized as YAML in the project's cache directory.
        /// </summary>
        public Type? ImportOptionsType { get; }
    }
}
