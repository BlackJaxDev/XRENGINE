//using Extensions;
//using ImageMagick;
//using XREngine.Data.Core;
//using XREngine.Data.Rendering;
//using XREngine.Rendering.Models.Materials.Textures;

//namespace XREngine.Rendering
//{
//    public class CubeMipmap : XRBase
//    {
//        public bool IsCrossMap => Sides.Length == 1;

//        public CubeSide[] Sides { get; private set; }
        
//        public CubeMipmap(MagickImage crossMap, EPixelInternalFormat internalFormat)
//            => Sides = [new CubeSideTextured(crossMap, internalFormat)];

//        public CubeMipmap(
//            CubeSide posX, CubeSide negX,
//            CubeSide posY, CubeSide negY,
//            CubeSide posZ, CubeSide negZ)
//            => Sides = [posX, negX, posY, negY, posZ, negZ];

//        public CubeMipmap(uint width, uint height, EPixelInternalFormat internalFormat, EPixelFormat format, EPixelType type)
//        {
//            Sides = new CubeSide[6];
//            Sides.Fill(i => new CubeSideEmpty(width, height, internalFormat, format, type));
//        }

//        public void FillRenderMap(TextureCubeMipmap mip)
//        {
//            if (mip is null)
//                return;

//            if (IsCrossMap)
//            {
//                CubeSideTextured crossTex = ((CubeSideTextured)Sides[0]);
//                //var tex = crossTex.Map;
//                //MagickImage[]? maps = [tex];//x.Result.Bitmaps;
//                //if (maps is null)
//                //    return;

//                MagickImage bmp = crossTex.Map;
//                ////If the mip index is greater than the amount of mipmaps, resize the last mip to the desired size.
//                //if (mipIndex >= maps.Length)
//                //{
//                //    bmp = maps[^1];
//                //    uint w = bmp.Width;
//                //    uint h = bmp.Height;
//                //    for (int i = maps.Length; i <= mipIndex && w >= 1 && h >= 1; ++i)
//                //    {
//                //        w /= 2;
//                //        h /= 2;
//                //    }
//                //    bmp.InterpolativeResize(w, h, PixelInterpolateMethod.Bilinear); //TODO: use NearestNeighbor instead?
//                //}
//                //else
//                //    bmp = maps[mipIndex];

//                if (!mip.SetCrossCubeMap(bmp, crossTex.InternalFormat))
//                    Debug.Out("Cubemap cross dimensions are invalid; width/height be a 4:3 or 3:4 ratio.");
//            }
//            else
//            {
//                mip.SetSides(
//                    Sides[0],
//                    Sides[1],
//                    Sides[2],
//                    Sides[3],
//                    Sides[4],
//                    Sides[5]);
//            }
//        }
//        public TextureCubeMipmap AsRenderMipmap()
//        {
//            TextureCubeMipmap mip = new();
//            FillRenderMap(mip);
//            return mip;
//        }
//    }
//}
