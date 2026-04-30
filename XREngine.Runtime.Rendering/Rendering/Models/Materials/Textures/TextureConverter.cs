using System.Drawing;

namespace XREngine.Rendering.Models.Materials
{
    /// <summary>
    /// Central location for reading and writing texture files.
    /// </summary>
    public static class TextureConverter
    {
        public static Bitmap[] Decode(string path)
        {
            if (!File.Exists(path))
                return [];

            //FIBITMAP dib = FreeImage.LoadEx(path);
            //if (dib.IsNull)
            //    return new Bitmap[0];

            try
            {
                //Bitmap bitmap = FreeImage.GetBitmap(dib);
                //FreeImage.UnloadEx(ref dib);
#pragma warning disable CA1416 // Validate platform compatibility
                Bitmap bitmap = new(path);
#pragma warning restore CA1416 // Validate platform compatibility
                return [bitmap];
            }
            catch
            {
                return [];
            }
        }
    }
}