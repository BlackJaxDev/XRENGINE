using System.Drawing;
using System.Drawing.Imaging;
using XREngine.Data;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Models.Materials.Textures
{
    /// <summary>
    /// Binary data representing one image.
    /// </summary>
    public class TextureData : IDisposable
    {
        DataSource _data;
        EPixelFormat _pixelFormat;

        public VoidPtr Scan0 => _data.Address;

        public TextureData(Bitmap bmp)
        {
            switch (bmp.PixelFormat)
            {
                case PixelFormat.Format32bppArgb:
                    _pixelFormat = EPixelFormat.Bgra;
                    break;
                case PixelFormat.Format24bppRgb:
                    _pixelFormat = EPixelFormat.Bgr;
                    break;
            }
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, bmp.PixelFormat);
            uint length = (uint)data.Stride * (uint)data.Height;
            _data = new DataSource(length);
            Memory.Move(_data.Address, data.Scan0, (uint)length);
            bmp.UnlockBits(data);
        }
        //public TextureData(FreeImageBitmap bmp) : this(bmp.ToBitmap()) { }
        public TextureData(VoidPtr data, uint length, EPixelFormat pixelFormat, EPixelType pixelType)
        {
            _data = new DataSource(data, length, true);
            _pixelFormat = pixelFormat;
        }
        public TextureData(VoidPtr data, uint width, uint height, EPixelFormat pixelFormat, EPixelType pixelType)
        {
            _data = new DataSource(data, GetLength(width, height, pixelFormat, pixelType), true);
            _pixelFormat = pixelFormat;
        }

        public static uint GetLength(uint width, uint height, EPixelFormat pixelFormat, EPixelType pixelType)
        {
            return 0;
        }

        #region IDisposable Support
        private bool disposedValue = false;
        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    
                }
                _data.Dispose();
                disposedValue = true;
            }
        }
        ~TextureData()
        {
            Dispose(false);
        }
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion
    }
}
