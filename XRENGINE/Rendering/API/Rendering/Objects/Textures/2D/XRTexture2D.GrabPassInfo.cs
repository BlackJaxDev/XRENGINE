using MemoryPack;
using XREngine.Data.Core;
using XREngine.Data.Rendering;

namespace XREngine.Rendering
{
    public partial class XRTexture2D
    {
        [MemoryPackable]
        public partial class GrabPassInfo : XRBase
        {
            private EReadBufferMode _readBuffer;
            private bool _colorBit;
            private bool _depthBit;
            private bool _stencilBit;
            private bool _linearFilter;
            private bool _resizeToFit;
            private float _resizeScale;
            [MemoryPackIgnore]
            private XRTexture2D? _resultTexture;

            public EReadBufferMode ReadBuffer
            {
                get => _readBuffer;
                set => SetField(ref _readBuffer, value);
            }
            public bool ColorBit
            {
                get => _colorBit;
                set => SetField(ref _colorBit, value);
            }
            public bool DepthBit
            {
                get => _depthBit;
                set => SetField(ref _depthBit, value);
            }
            public bool StencilBit
            {
                get => _stencilBit;
                set => SetField(ref _stencilBit, value);
            }
            public bool LinearFilter
            {
                get => _linearFilter;
                set => SetField(ref _linearFilter, value);
            }
            public bool ResizeToFit
            {
                get => _resizeToFit;
                set => SetField(ref _resizeToFit, value);
            }
            public float ResizeScale
            {
                get => _resizeScale;
                set => SetField(ref _resizeScale, value);
            }
            [MemoryPackIgnore]
            public XRTexture2D? ResultTexture
            {
                get => _resultTexture;
                set => SetField(ref _resultTexture, value);
            }

            [MemoryPackIgnore]
            private XRFrameBuffer? _resultFBO = null;
            [MemoryPackIgnore]
            public XRFrameBuffer? ResultFBO => _resultFBO ??= (ResultTexture is null ? null : new((ResultTexture, EFrameBufferAttachment.ColorAttachment0, 0, -1)));

            public GrabPassInfo(XRTexture2D? resultTexture, EReadBufferMode readBuffer, bool colorBit, bool depthBit, bool stencilBit, bool linearFilter, bool resizeToFit, float resizeScale)
            {
                _readBuffer = readBuffer;
                _colorBit = colorBit;
                _depthBit = depthBit;
                _stencilBit = stencilBit;
                _linearFilter = linearFilter;
                _resizeToFit = resizeToFit;
                _resizeScale = resizeScale;
                _resultTexture = resultTexture;
            }

            [MemoryPackConstructor]
            public GrabPassInfo() { }

            protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
            {
                base.OnPropertyChanged(propName, prev, field);
                if (propName == nameof(ResultTexture))
                {
                    _resultFBO?.Destroy();
                    _resultFBO = null;
                }
            }

            public void Grab(XRFrameBuffer? inFBO, XRViewport? viewport)
            {
                if (Engine.Rendering.State.IsShadowPass || Engine.Rendering.State.IsSceneCapturePass)
                    return;

                var rend = AbstractRenderer.Current;
                if (rend is null)
                    return;

                var fbo = ResultFBO;
                if (fbo is null)
                    return;

                uint w, h;
                if (inFBO is not null)
                {
                    if (_readBuffer < EReadBufferMode.ColorAttachment0)
                        return;
                    
                    w = inFBO.Width;
                    h = inFBO.Height;
                    Resize(w, h);
                    rend.BlitFBOToFBO(inFBO, fbo, _readBuffer, _colorBit, _depthBit, _stencilBit, _linearFilter);
                }
                else if (viewport is not null)
                {
                    w = (uint)viewport.Width;
                    h = (uint)viewport.Height;
                    Resize(w, h);
                    rend.BlitViewportToFBO(viewport, fbo, _readBuffer, _colorBit, _depthBit, _stencilBit, _linearFilter);
                }
            }

            private void Resize(uint w, uint h)
            {
                var fbo = ResultFBO;
                if (fbo is null)
                    return;

                if (!_resizeToFit || w == fbo.Width && h == fbo.Height)
                    return;

                fbo.Resize((uint)(w * _resizeScale), (uint)(h * _resizeScale));
            }
        }
    }
}
