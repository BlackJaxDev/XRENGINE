//using MemoryPack;
//using XREngine.Core.Files;

//namespace XREngine.Rendering
//{
//    /// <summary>
//    /// Meant to wrap generic render objects as files, but GenericRenderObject classes derive directly from XRAsset now anyways.
//    /// </summary>
//    /// <typeparam name="T"></typeparam>
//    /// <param name="data"></param>
//    /// <param name="name"></param>
//    [MemoryPackable(GenerateType.NoGenerate)]
//    public partial class XRRenderAsset<T>(T data, string name) : XRAsset(name) where T : GenericRenderObject
//    {
//        public T Data { get; set; } = data;
//    }
//}
