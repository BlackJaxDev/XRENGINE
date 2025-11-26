//using Silk.NET.OpenGL;

//namespace XREngine.Rendering.OpenGL
//{
//    public class GLTextureCube(OpenGLRenderer renderer, XRTextureCube data) : GLTexture<XRTextureCube>(renderer, data)
//    {
//        public override XRTextureCube Data
//        {
//            get => base.Data;
//            protected set
//            {
//                base.Data = value;
//                if (value != null)
//                {
//                    Bind();
//                    Api.BindTexture(GLEnum.TextureCubeMap, BindingId);
//                    for (int x = 0; x < value.Mipmaps.Length; ++x)
//                    {
//                        for (int i = 0; i < 6; i++)
//                        {
//                            Api.TexImage2D(GLEnum.TextureCubeMapPositiveX + i, 0, value.InternalFormat, value.Width, value.Height, 0, value.Format, value.Type, IntPtr.Zero);
//                        }
//                    }
//                    SetParameters();
//                    Unbind();
//                }
//            }
//        }

//        private void SetParameters()
//        {
//            Api.TexParameter(GLEnum.TextureCubeMap, GLEnum.TextureMinFilter, (int)Data.MinFilter);
//            Api.TexParameter(GLEnum.TextureCubeMap, GLEnum.TextureMagFilter, (int)Data.MagFilter);
//            Api.TexParameter(GLEnum.TextureCubeMap, GLEnum.TextureWrapS, (int)Data.UWrap);
//            Api.TexParameter(GLEnum.TextureCubeMap, GLEnum.TextureWrapT, (int)Data.VWrap);
//            Api.TexParameter(GLEnum.TextureCubeMap, GLEnum.TextureWrapR, (int)Data.WWrap);
//            Api.TexParameter(GLEnum.TextureCubeMap, GLEnum.TextureLodBias, Data.LodBias);
//        }
//    }
//}