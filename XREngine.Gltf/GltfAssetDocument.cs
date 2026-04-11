using System.Numerics;

namespace XREngine.Gltf;

public sealed class GltfAssetDocument : IDisposable
{
    private readonly FastGltfNativeAssetHandle _nativeHandle;

    private GltfAssetDocument(string sourceFilePath, GltfRoot root, FastGltfNativeAssetHandle nativeHandle)
    {
        SourceFilePath = sourceFilePath;
        Root = root;
        _nativeHandle = nativeHandle;
    }

    public string SourceFilePath { get; }

    public GltfRoot Root { get; }

    public static GltfAssetDocument Open(string sourceFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);

        GltfRoot root = GltfJsonLoader.Load(sourceFilePath);
        ValidateLocalResourceUris(sourceFilePath, root);
        FastGltfNativeAssetHandle nativeHandle = FastGltfNative.OpenAsset(sourceFilePath);
        return new GltfAssetDocument(sourceFilePath, root, nativeHandle);
    }

    public float[] ReadFloatScalarAccessor(int accessorIndex)
    {
        int count = GetAccessor(accessorIndex).Count;
        float[] values = new float[count];
        FastGltfNative.CopyAccessor(_nativeHandle, accessorIndex, FastGltfCopyFormat.Float32Scalar, values);
        return values;
    }

    public Vector2[] ReadVector2Accessor(int accessorIndex)
    {
        int count = GetAccessor(accessorIndex).Count;
        Vector2[] values = new Vector2[count];
        FastGltfNative.CopyAccessor(_nativeHandle, accessorIndex, FastGltfCopyFormat.Float32Vec2, values);
        return values;
    }

    public Vector3[] ReadVector3Accessor(int accessorIndex)
    {
        int count = GetAccessor(accessorIndex).Count;
        Vector3[] values = new Vector3[count];
        FastGltfNative.CopyAccessor(_nativeHandle, accessorIndex, FastGltfCopyFormat.Float32Vec3, values);
        return values;
    }

    public Vector4[] ReadVector4Accessor(int accessorIndex)
    {
        int count = GetAccessor(accessorIndex).Count;
        Vector4[] values = new Vector4[count];
        FastGltfNative.CopyAccessor(_nativeHandle, accessorIndex, FastGltfCopyFormat.Float32Vec4, values);
        return values;
    }

    public uint[] ReadUIntScalarAccessor(int accessorIndex)
    {
        int count = GetAccessor(accessorIndex).Count;
        uint[] values = new uint[count];
        FastGltfNative.CopyAccessor(_nativeHandle, accessorIndex, FastGltfCopyFormat.UInt32Scalar, values);
        return values;
    }

    public UInt4[] ReadUInt4Accessor(int accessorIndex)
    {
        int count = GetAccessor(accessorIndex).Count;
        UInt4[] values = new UInt4[count];
        FastGltfNative.CopyAccessor(_nativeHandle, accessorIndex, FastGltfCopyFormat.UInt32Vec4, values);
        return values;
    }

    public Matrix4x4[] ReadMatrix4Accessor(int accessorIndex)
    {
        int count = GetAccessor(accessorIndex).Count;
        Matrix4x4[] values = new Matrix4x4[count];
        FastGltfNative.CopyAccessor(_nativeHandle, accessorIndex, FastGltfCopyFormat.Float32Mat4, values);

        for (int index = 0; index < values.Length; index++)
            values[index] = Matrix4x4.Transpose(values[index]);

        return values;
    }

    public byte[] ReadBufferViewBytes(int bufferViewIndex)
        => FastGltfNative.CopyBufferView(_nativeHandle, bufferViewIndex);

    public void Dispose()
        => _nativeHandle.Dispose();

    private GltfAccessor GetAccessor(int accessorIndex)
    {
        if (accessorIndex < 0 || accessorIndex >= Root.Accessors.Count)
            throw new ArgumentOutOfRangeException(nameof(accessorIndex));
        return Root.Accessors[accessorIndex];
    }

    private static void ValidateLocalResourceUris(string sourceFilePath, GltfRoot root)
    {
        foreach (GltfBuffer buffer in root.Buffers)
        {
            if (!string.IsNullOrWhiteSpace(buffer.Uri))
                ValidateLocalResourceUri(sourceFilePath, buffer.Uri, "buffer");
        }

        foreach (GltfImage image in root.Images)
        {
            if (!string.IsNullOrWhiteSpace(image.Uri))
                ValidateLocalResourceUri(sourceFilePath, image.Uri, "image");
        }
    }

    private static void ValidateLocalResourceUri(string sourceFilePath, string uri, string resourceKind)
    {
        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return;

        if (Uri.TryCreate(uri, UriKind.Absolute, out Uri? absoluteUri) && !absoluteUri.IsFile)
            throw new NotSupportedException($"glTF {resourceKind} uri '{uri}' in '{sourceFilePath}' is not a local file path. Network and remote resource fetches are intentionally disabled for deterministic imports.");
    }
}