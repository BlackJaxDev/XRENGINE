namespace XREngine.Data.Rendering
{
    //public class VertexAttribInfo(EBufferType type, uint index = 0)
    //{
    //    public EBufferType _type = type;
    //    public uint _index = index.Clamp(0, GetMaxBuffersForType(type) - 1);

    //    public EBufferType Type => _type;
    //    public uint Index => _index;

    //    public static uint GetMaxBuffersForType(EBufferType type)
    //        => type switch
    //        {
    //            EBufferType.Color => XRMeshDescriptor.MaxColors,
    //            EBufferType.TexCoord => XRMeshDescriptor.MaxTexCoords,
    //            EBufferType.Aux => XRMeshDescriptor.MaxOtherBuffers,
    //            _ => XRMeshDescriptor.MaxMorphs + 1,
    //        };

    //    public static string GetAttribName(EBufferType type, uint index)
    //        => type.ToString() + index.ToString();

    //    public static uint GetLocation(EBufferType type, uint index)
    //    {
    //        uint location = 0;
    //        for (EBufferType i = 0; i < type; ++i)
    //            location += GetMaxBuffersForType(i);
    //        return location + index;
    //    }

    //    public string GetAttribName()
    //        => GetAttribName(_type, _index);

    //    public uint GetLocation()
    //        => GetLocation(_type, _index);
    //}
}
