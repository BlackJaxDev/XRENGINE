//using XREngine.Core.Files;
//using XREngine.Data;
//using XREngine.Data.Colors;
//using XREngine.Data.Rendering;

//namespace XREngine.Rendering.Models.Materials
//{
//    public class Bitmap3D : XRAsset, IDisposable
//    {
//        public Bitmap3D() : this(1, 1, 1) { }
//        public Bitmap3D(int width, int height, int depth)
//        {
//            Format = EPixelType.Basic;
//            _pixelSize = GetPixelSize();
//            _wh = width * height;
//            Width = width;
//            Height = height;
//            Depth = depth;
//            _data = new DataSource(_wh * Depth * _pixelSize);
//        }
//        public Bitmap3D(int width, int height, int depth, ETPixelType format)
//        {
//            Format = format;
//            _pixelSize = GetPixelSize();
//            _wh = width * height;
//            Width = width;
//            Height = height;
//            Depth = depth;
//            _data = new DataSource(_wh * Depth * _pixelSize);
//        }
//        public Bitmap3D(int width, int height, int depth, EPixelType format, IntPtr scan0)
//        {
//            Format = format;
//            _pixelSize = GetPixelSize();
//            _wh = width * height;
//            Width = width;
//            Height = height;
//            Depth = depth;
//            _data = new DataSource(_wh * Depth * _pixelSize);
//            Memory.Move(_data.Address, scan0, (uint)_data.Length);
//        }

//        private DataSource _data;
//        private int _wh, _pixelSize;

//        public VoidPtr Scan0 => _data.Address;
//        public EPixelType Format { get; }

//        public int Width { get; }

//        public int Height { get; }

//        public int Depth { get; }

//        public int GetPixelSize()
//        {
//            return Format switch
//            {
//                EPixelFormat.Format8bIntensity => 1,
//                EPixelFormat.Format16bIntensityi => 2,
//                EPixelFormat.Format16bIntensityf => 2,
//                EPixelFormat.Format32bIntensityi => 4,
//                EPixelFormat.Format32bIntensityf => 4,
//                EPixelFormat.Format8bppIntensityAlpha => 2,
//                EPixelFormat.Format16bppIntensityAlphai => 4,
//                EPixelFormat.Format16bppIntensityAlphaf => 4,
//                EPixelFormat.Format32bppIntensityAlphai => 8,
//                EPixelFormat.Format32bppIntensityAlphaf => 8,
//                EPixelFormat.Format8bppRGB => 3,
//                EPixelFormat.Format8bppRGBA => 4,
//                EPixelFormat.Format16bppRGBi => 6,
//                EPixelFormat.Format16bppRGBAi => 8,
//                EPixelFormat.Format16bppRGBf => 6,
//                EPixelFormat.Format16bppRGBAf => 8,
//                EPixelFormat.FormatRGB32ui => 12,
//                EPixelFormat.FormatRGBA32ui => 16,
//                EPixelFormat.RGB32f => 12,
//                EPixelFormat.RGBA32f => 16,
//                _ => 0,
//            };
//        }
//        public ColorF4 GetPixel(int x, int y, int z)
//        {
//            int index = x * (y * Width) * (z * _wh);
//            return GetPixel(_data.Address[index, _pixelSize]);
//        }
//        public void SetPixel(int x, int y, int z, ColorF4 color)
//        {
//            int index = x * (y * Width) * (z * _wh);
//            SetPixel(_data.Address[index, _pixelSize], color);
//        }

//        internal Bitmap3D Resized(int width, int height, int depth)
//        {
//            throw new NotImplementedException();
//        }

//        private unsafe void SetPixel(VoidPtr addr, ColorF4 color)
//        {
//            //switch (_format)
//            //{
//            //    case TPixelFormat.Format8bIntensity:
//            //        *(byte*)addr = color.R.ToByte();
//            //        break;
//            //    case TPixelFormat.Format16bIntensityi:
//            //        *(ushort*)addr = color.R.ToUShort();
//            //        break;
//            //    case TPixelFormat.Format16bIntensityf:
//            //        *(Half*)addr = (Half)color.R;
//            //        break;
//            //    case TPixelFormat.Format32bIntensityi:
//            //        *(uint*)addr = color.R.ToUInt();
//            //        break;
//            //    case TPixelFormat.Format32bIntensityf:
//            //        *(float*)addr = color.R;
//            //        break;

//            //    case TPixelFormat.Format8bppIntensityAlpha:
//            //        *(byte*)addr = color.R.ToByte();
//            //        *(byte*)(addr + 1) = color.A.ToByte();
//            //        break;
//            //    case TPixelFormat.Format16bppIntensityAlphai:
//            //        *(ushort*)addr = color.R.ToUShort();
//            //        *(ushort*)(addr + 2) = color.A.ToUShort();
//            //        break;
//            //    case TPixelFormat.Format16bppIntensityAlphaf:
//            //        *(Half*)addr = (Half)color.R;
//            //        *(Half*)(addr + 2) = (Half)color.A;
//            //        break;
//            //    case TPixelFormat.Format32bppIntensityAlphai:
//            //        *(uint*)addr = color.R.ToUInt();
//            //        *(uint*)(addr + 4) = color.A.ToUInt();
//            //        break;
//            //    case TPixelFormat.Format32bppIntensityAlphaf:
//            //        *(float*)addr = color.R;
//            //        *(float*)(addr + 4) = color.A;
//            //        break;

//            //    case TPixelFormat.Format8bppRGB:
//            //        *(byte*)addr = color.R.ToByte();
//            //        *(byte*)(addr + 1) = color.G.ToByte();
//            //        *(byte*)(addr + 2) = color.B.ToByte();
//            //        break;
//            //    case TPixelFormat.Format8bppRGBA:
//            //        *(byte*)addr = color.R.ToByte();
//            //        *(byte*)(addr + 1) = color.G.ToByte();
//            //        *(byte*)(addr + 2) = color.B.ToByte();
//            //        *(byte*)(addr + 3) = color.A.ToByte();
//            //        break;

//            //    case TPixelFormat.Format16bppRGBi:
//            //        *(ushort*)addr = color.R.ToUShort();
//            //        *(ushort*)(addr + 2) = color.G.ToUShort();
//            //        *(ushort*)(addr + 4) = color.B.ToUShort();
//            //        break;
//            //    case TPixelFormat.Format16bppRGBAi:
//            //        *(ushort*)addr = color.R.ToUShort();
//            //        *(ushort*)(addr + 2) = color.G.ToUShort();
//            //        *(ushort*)(addr + 4) = color.B.ToUShort();
//            //        *(ushort*)(addr + 6) = color.A.ToUShort();
//            //        break;

//            //    case TPixelFormat.Format16bppRGBf:
//            //        *(Half*)addr = (Half)color.R;
//            //        *(Half*)(addr + 2) = (Half)color.G;
//            //        *(Half*)(addr + 4) = (Half)color.B;
//            //        break;
//            //    case TPixelFormat.Format16bppRGBAf:
//            //        *(Half*)addr = (Half)color.R;
//            //        *(Half*)(addr + 2) = (Half)color.G;
//            //        *(Half*)(addr + 4) = (Half)color.B;
//            //        *(Half*)(addr + 6) = (Half)color.A;
//            //        break;

//            //    case TPixelFormat.FormatRGB32ui:
//            //        *(uint*)addr = color.R.ToUInt();
//            //        *(uint*)(addr + 4) = color.G.ToUInt();
//            //        *(uint*)(addr + 8) = color.B.ToUInt();
//            //        break;
//            //    case TPixelFormat.FormatRGBA32ui:
//            //        *(uint*)addr = color.R.ToUInt();
//            //        *(uint*)(addr + 4) = color.G.ToUInt();
//            //        *(uint*)(addr + 8) = color.B.ToUInt();
//            //        *(uint*)(addr + 12) = color.A.ToUInt();
//            //        break;

//            //    case TPixelFormat.RGB32f:
//            //        *(float*)addr = color.R;
//            //        *(float*)(addr + 4) = color.G;
//            //        *(float*)(addr + 8) = color.B;
//            //        break;
//            //    case TPixelFormat.RGBA32f:
//            //        *(float*)addr = color.R;
//            //        *(float*)(addr + 4) = color.G;
//            //        *(float*)(addr + 8) = color.B;
//            //        *(float*)(addr + 12) = color.A;
//            //        break;
//            //}
//        }
//        private unsafe ColorF4 GetPixel(VoidPtr addr)
//        {
//            //switch (_format)
//            //{
//            //    case TPixelFormat.Format8bIntensity: return new ColorF4(*(byte*)addr / (float)byte.MaxValue);
//            //    case TPixelFormat.Format16bIntensityi: return new ColorF4(*(ushort*)addr / (float)ushort.MaxValue);
//            //    case TPixelFormat.Format16bIntensityf: return new ColorF4(*(Half*)addr);
//            //    case TPixelFormat.Format32bIntensityi: return new ColorF4(*(uint*)addr / (float)uint.MaxValue);
//            //    case TPixelFormat.Format32bIntensityf: return new ColorF4(*(float*)addr);
                
//            //    case TPixelFormat.Format8bppIntensityAlpha: return new ColorF4(*(byte*)addr / (float)byte.MaxValue, *(byte*)(addr + 1) / (float)byte.MaxValue);
//            //    case TPixelFormat.Format16bppIntensityAlphai: return new ColorF4(*(ushort*)addr / (float)ushort.MaxValue, *(ushort*)(addr + 2) / (float)ushort.MaxValue);
//            //    case TPixelFormat.Format16bppIntensityAlphaf: return new ColorF4(*(Half*)addr, *(Half*)(addr + 2));
//            //    case TPixelFormat.Format32bppIntensityAlphai: return new ColorF4(*(uint*)addr / (float)uint.MaxValue, *(uint*)(addr + 4) / (float)uint.MaxValue);
//            //    case TPixelFormat.Format32bppIntensityAlphaf: return new ColorF4(*(float*)addr, *(float*)(addr + 4));
                
//            //    case TPixelFormat.Format8bppRGB: return new ColorF4(*(byte*)(addr + 0) / (float)byte.MaxValue, *(byte*)(addr + 1) / (float)byte.MaxValue, *(byte*)(addr + 2) / (float)byte.MaxValue);
//            //    case TPixelFormat.Format8bppRGBA: return new ColorF4(*(byte*)(addr + 0) / (float)byte.MaxValue, *(byte*)(addr + 1) / (float)byte.MaxValue, *(byte*)(addr + 2) / (float)byte.MaxValue, *(byte*)(addr + 3) / (float)byte.MaxValue);

//            //    case TPixelFormat.Format16bppRGBi: return new ColorF4(*(ushort*)(addr + 0) / (float)ushort.MaxValue, *(ushort*)(addr + 2) / (float)ushort.MaxValue, *(ushort*)(addr + 4) / (float)ushort.MaxValue);
//            //    case TPixelFormat.Format16bppRGBAi: return new ColorF4(*(ushort*)(addr + 0) / (float)ushort.MaxValue, *(ushort*)(addr + 2) / (float)ushort.MaxValue, *(ushort*)(addr + 4) / (float)ushort.MaxValue, *(ushort*)(addr + 6) / (float)ushort.MaxValue);

//            //    case TPixelFormat.Format16bppRGBf: return new ColorF4(*(Half*)(addr + 0), *(Half*)(addr + 2), *(Half*)(addr + 4));
//            //    case TPixelFormat.Format16bppRGBAf: return new ColorF4(*(Half*)(addr + 0), *(Half*)(addr + 2), *(Half*)(addr + 4), *(Half*)(addr + 6));

//            //    case TPixelFormat.FormatRGB32ui: return new ColorF4(*(uint*)(addr + 0) / (float)uint.MaxValue, *(uint*)(addr + 4) / (float)uint.MaxValue, *(uint*)(addr + 8) / (float)uint.MaxValue);
//            //    case TPixelFormat.FormatRGBA32ui: return new ColorF4(*(uint*)(addr + 0) / (float)uint.MaxValue, *(uint*)(addr + 4) / (float)uint.MaxValue, *(uint*)(addr + 8) / (float)uint.MaxValue, *(uint*)(addr + 12) / (float)uint.MaxValue);

//            //    case TPixelFormat.RGB32f: return new ColorF4(*(float*)(addr + 0), *(float*)(addr + 4), *(float*)(addr + 8));
//            //    case TPixelFormat.RGBA32f: return new ColorF4(*(float*)(addr + 0), *(float*)(addr + 4), *(float*)(addr + 8), *(float*)(addr + 12));
//            //}
//            return new ColorF4();
//        }

//        public void Resize(int width, int height, int depth)
//        {
//            throw new NotImplementedException();
//        }

//        #region IDisposable Support
//        private bool disposedValue = false; // To detect redundant calls

//        protected virtual void Dispose(bool disposing)
//        {
//            if (!disposedValue)
//            {
//                if (disposing)
//                {

//                }

//                _data?.Dispose();
//                disposedValue = true;
//            }
//        }
        
//         ~Bitmap3D()
//        {
//            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
//            Dispose(false);
//        }

//        // This code added to correctly implement the disposable pattern.
//        public void Dispose()
//        {
//            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
//            Dispose(true);
//            GC.SuppressFinalize(this);
//        }
//        #endregion
//    }
//}
