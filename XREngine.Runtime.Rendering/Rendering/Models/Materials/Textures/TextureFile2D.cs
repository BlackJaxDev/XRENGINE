//using ImageMagick;
//using System.Drawing.Imaging;
//using XREngine.Core.Files;
//using XREngine.Rendering.Models.Materials;

//namespace XREngine.Rendering.Textures
//{
//    public class TextureFile2D(uint width, uint height, PixelFormat format = PixelFormat.Format32bppArgb) : XRAsset
//    {
//#pragma warning disable CA1416 // Validate platform compatibility
//        public MagickImage[] Bitmaps { get; set; } = [new MagickImage(width, height)];
//#pragma warning restore CA1416 // Validate platform compatibility

//        public MagickImage? GetBitmap(int index = 0) => 
//            Bitmaps != null && 
//            index < Bitmaps.Length && 
//            index >= 0 
//                ? Bitmaps[0]
//                : null;

//        public TextureFile2D()
//            : this(1, 1) { }

//        public void LoadAsync(string path, Action<TextureFile2D> onFinishedAsync)
//        {
//            void OnLoaded(Task<MagickImage[]> t) 
//            {
//                Bitmaps = t.Result; 
//                onFinishedAsync?.Invoke(this);
//            }
//            Task.Run(() => TextureConverter.Decode(path)).ContinueWith(OnLoaded);
//        }
//    }
//}
