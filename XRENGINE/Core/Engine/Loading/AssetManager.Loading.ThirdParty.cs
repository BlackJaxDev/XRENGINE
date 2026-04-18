using XREngine.Extensions;
using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Core;
using XRAsset = XREngine.Core.Files.XRAsset;

namespace XREngine
{
    public partial class AssetManager
    {
        private T? Load3rdPartyAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(string filePath, string ext) where T : XRAsset, new()
        {
            var exts = typeof(T).GetCustomAttribute<XR3rdPartyExtensionsAttribute>()?.Extensions;
            var match = exts?.FirstOrDefault(e => e.ext == ext);
            if (match is not null)
            {
                if (match.Value.staticLoad)
                {
                    var method = typeof(T).GetMethod("Load3rdPartyStatic", BindingFlags.Public | BindingFlags.Static);
                    if (method is not null)
                    {
                        var asset = method.Invoke(null, [filePath]) as T;
                        if (asset is not null)
                        {
                            asset.OriginalPath = filePath;
                            return asset;
                        }
                        else
                        {
                            Debug.LogWarning($"The asset type '{typeof(T).Name}' has a 3rd party extension '{ext}' but the static loader method does not return the correct type or returned null.");
                            return null;
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"The asset type '{typeof(T).Name}' has a 3rd party extension '{ext}' but does not have a static load method.");
                        return null;
                    }
                }
                else
                {
                    var asset = new T
                    {
                        OriginalPath = filePath
                    };
                    var context = CreateImportContext(filePath);
                    if (asset.Load3rdParty(filePath, context))
                        return asset;
                    else
                    {
                        Debug.LogWarning($"Failed to load 3rd party asset '{filePath}' as type '{typeof(T).Name}'.");
                        return null;
                    }
                }
            }
            else
            {
                Debug.LogWarning($"The file extension '{ext}' is not supported by the asset type '{typeof(T).Name}'.");
                return null;
            }
        }

        private XRAsset? Load3rdPartyAsset(string filePath, string ext, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type type)
        {
            var exts = type.GetCustomAttribute<XR3rdPartyExtensionsAttribute>()?.Extensions;
            var match = exts?.FirstOrDefault(e => e.ext == ext);
            if (match is not null)
            {
                if (match.Value.staticLoad)
                {
                    var method = type.GetMethod("Load3rdPartyStatic", BindingFlags.Public | BindingFlags.Static);
                    if (method is not null)
                    {
                        var asset = method.Invoke(null, [filePath]) as XRAsset;
                        if (asset is not null)
                        {
                            asset.OriginalPath = filePath;
                            return asset;
                        }
                        else
                        {
                            Debug.LogWarning($"The asset type '{type.Name}' has a 3rd party extension '{ext}' but the static loader method does not return the correct type or returned null.");
                            return null;
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"The asset type '{type.Name}' has a 3rd party extension '{ext}' but does not have a static load method.");
                        return null;
                    }
                }
                else
                {
                    if (type.CreateInstance() is not XRAsset asset)
                    {
                        Debug.LogWarning($"Failed to construct 3rd party asset '{filePath}' as type '{type.Name}'.");
                        return null;
                    }

                    asset.OriginalPath = filePath;
                    var context = CreateImportContext(filePath);
                    if (asset.Load3rdParty(filePath, context))
                        return asset;
                    else
                    {
                        Debug.LogWarning($"Failed to load 3rd party asset '{filePath}' as type '{type.Name}'.");
                        return null;
                    }
                }
            }
            else
            {
                Debug.LogWarning($"The file extension '{ext}' is not supported by the asset type '{type.Name}'.");
                return null;
            }
        }

        private async Task<T?> Load3rdPartyAssetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(string filePath, string ext) where T : XRAsset, new()
        {
            var exts = typeof(T).GetCustomAttribute<XR3rdPartyExtensionsAttribute>()?.Extensions;
            var match = exts?.FirstOrDefault(e => e.ext == ext);
            if (match is not null)
            {
                if (match.Value.staticLoad)
                {
                    var method = typeof(T).GetMethod("Load3rdPartyStaticAsync", BindingFlags.Public | BindingFlags.Static);
                    if (method is not null)
                    {
                        var assetTask = method.Invoke(null, [filePath]) as Task<T?>;
                        if (assetTask is not null)
                        {
                            var asset = await assetTask.ConfigureAwait(false);
                            if (asset is not null)
                            {
                                asset.OriginalPath = filePath;
                                return asset;
                            }
                            else
                            {
                                Debug.LogWarning($"The asset type '{typeof(T).Name}' has a 3rd party extension '{ext}' but the static loader method returned null.");
                                return null;
                            }
                        }
                        else
                        {
                            Debug.LogWarning($"The asset type '{typeof(T).Name}' has a 3rd party extension '{ext}' but the static loader method does not return the correct type.");
                            return null;
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"The asset type '{typeof(T).Name}' has a 3rd party extension '{ext}' but does not have a static loader method.");
                        return null;
                    }
                }
                else
                {
                    var asset = new T
                    {
                        OriginalPath = filePath
                    };
                    var context = CreateImportContext(filePath);
                    if (await asset.Load3rdPartyAsync(filePath, context).ConfigureAwait(false))
                        return asset;
                    else
                    {
                        Debug.LogWarning($"Failed to load 3rd party asset '{filePath}' as type '{typeof(T).Name}'.");
                        return null;
                    }
                }
            }
            else
            {
                Debug.LogWarning($"The file extension '{ext}' is not supported by the asset type '{typeof(T).Name}'.");
                return null;
            }
        }
    }
}