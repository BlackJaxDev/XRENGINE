using XREngine.Data.Core;

namespace XREngine.Rendering.OpenGL
{
    public partial class GLTexture2D
    {
        public class MipmapInfo : XRBase
        {
            private bool _needsFullPush = true;
            private readonly Mipmap2D _mipmap;
            private readonly GLTexture2D _texture;

            public Mipmap2D Mipmap => _mipmap;

            public MipmapInfo(GLTexture2D texture, Mipmap2D mipmap)
            {
                _texture = texture;
                _mipmap = mipmap;
                _mipmap.PropertyChanged += MipmapPropertyChanged;
                _mipmap.Invalidated += MipmapInvalidated;
            }

            ~MipmapInfo()
            {
                _mipmap.PropertyChanged -= MipmapPropertyChanged;
                _mipmap.Invalidated -= MipmapInvalidated;
            }

            private void MipmapInvalidated()
            {
                _texture.Invalidate();
            }

            private void MipmapPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
            {
                switch (e.PropertyName)
                {
                    case nameof(Mipmap.StreamingPBO):
                    case nameof(Mipmap.Data):
                        _texture.Invalidate();
                        break;
                    case nameof(Mipmap.Width):
                    case nameof(Mipmap.Height):
                    case nameof(Mipmap.InternalFormat):
                    case nameof(Mipmap.PixelFormat):
                    case nameof(Mipmap.PixelType):
                        NeedsFullPush = true;
                        _texture.Invalidate();
                        break;
                }
            }

            /// <summary>
            /// Whether the texture needs to be fully pushed to the GPU instead of using a sub-push.
            /// </summary>
            public bool NeedsFullPush
            {
                get => _needsFullPush;
                set => SetField(ref _needsFullPush, value);
            } 
        }
    }
}