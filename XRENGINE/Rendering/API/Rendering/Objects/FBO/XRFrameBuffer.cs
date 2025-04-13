using Extensions;
using XREngine.Data.Rendering;

namespace XREngine.Rendering
{
    public class XRFrameBuffer : GenericRenderObject
    {
        public XRFrameBuffer() { }
        public XRFrameBuffer(params (IFrameBufferAttachement Target, EFrameBufferAttachment Attachment, int MipLevel, int LayerIndex)[]? targets)
            => SetRenderTargets(targets);

        private EDrawBuffersAttachment[]? _drawBuffers;
        private EFrameBufferTextureTypeFlags _textureTypes = EFrameBufferTextureTypeFlags.None;
        private (IFrameBufferAttachement Target, EFrameBufferAttachment Attachment, int MipLevel, int LayerIndex)[]? _targets;

        private static readonly Stack<XRFrameBuffer> _readStack = new();
        private static readonly Stack<XRFrameBuffer> _writeStack = new();
        private static readonly Stack<XRFrameBuffer> _bindStack = new();

        /// <summary>
        /// The currently bound framebuffer for reading.
        /// </summary>
        public static XRFrameBuffer? BoundForReading => _readStack.Count > 0 ? _readStack.Peek() : null;
        /// <summary>
        /// The currently bound framebuffer for writing.
        /// </summary>
        public static XRFrameBuffer? BoundForWriting => _writeStack.Count > 0 ? _writeStack.Peek() : null;
        /// <summary>
        /// The currently bound framebuffer for general use.
        /// </summary>
        public static XRFrameBuffer? CurrentlyBound => _bindStack.Count > 0 ? _bindStack.Peek() : null;

        public uint Width => Targets?.FirstOrDefault().Target?.Width ?? 0u;
        public uint Height => Targets?.FirstOrDefault().Target?.Height ?? 0u;

        public EFrameBufferTextureTypeFlags TextureTypes
        {
            get => _textureTypes;
            set => SetField(ref _textureTypes, value);
        }
        public (IFrameBufferAttachement Target, EFrameBufferAttachment Attachment, int MipLevel, int LayerIndex)[]? Targets
        {
            get => _targets;
            private set => SetField(ref _targets, value);
        }
        public EDrawBuffersAttachment[]? DrawBuffers
        {
            get => _drawBuffers;
            private set => SetField(ref _drawBuffers, value);
        }

        public event Action? Resized;

        public virtual void Resize(uint width, uint height)
        {
            if (Targets is null)
                return;

            foreach (var (texture, _, _, _) in Targets)
                if (texture is XRTexture2D texture2D)
                    texture2D.Resize(width, height);

            Resized?.Invoke();
        }

        public event Action? PreSetRenderTargets;
        public event Action? PostSetRenderTargets;

        public void SetRenderTargets(XRMaterial? material)
            => SetRenderTargets(material?.Textures.
                Where(x => x?.FrameBufferAttachment != null).
                Select(x => ((IFrameBufferAttachement)x!, x!.FrameBufferAttachment!.Value, 0, -1)).
                ToArray());

        public void SetRenderTargets(params (IFrameBufferAttachement Target, EFrameBufferAttachment Attachment, int MipLevel, int LayerIndex)[]? targets)
        {
            PreSetRenderTargets?.Invoke();

            Targets = targets;
            TextureTypes = EFrameBufferTextureTypeFlags.None;

            List<EDrawBuffersAttachment> fboAttachments = [];
            if (Targets is not null)
            {
                foreach (var (_, Attachment, _, _) in Targets)
                {
                    switch (Attachment)
                    {
                        //case EFrameBufferAttachment.ColorAttachment0:
                        //case EFrameBufferAttachment.ColorAttachment1:
                        //case EFrameBufferAttachment.ColorAttachment2:
                        //case EFrameBufferAttachment.ColorAttachment3:
                        //case EFrameBufferAttachment.ColorAttachment4:
                        //case EFrameBufferAttachment.ColorAttachment5:
                        //case EFrameBufferAttachment.ColorAttachment6:
                        //case EFrameBufferAttachment.ColorAttachment7:
                        //case EFrameBufferAttachment.ColorAttachment8:
                        //case EFrameBufferAttachment.ColorAttachment9:
                        //case EFrameBufferAttachment.ColorAttachment10:
                        //case EFrameBufferAttachment.ColorAttachment11:
                        //case EFrameBufferAttachment.ColorAttachment12:
                        //case EFrameBufferAttachment.ColorAttachment13:
                        //case EFrameBufferAttachment.ColorAttachment14:
                        //case EFrameBufferAttachment.ColorAttachment15:
                        //case EFrameBufferAttachment.ColorAttachment16:
                        //case EFrameBufferAttachment.ColorAttachment17:
                        //case EFrameBufferAttachment.ColorAttachment18:
                        //case EFrameBufferAttachment.ColorAttachment19:
                        //case EFrameBufferAttachment.ColorAttachment20:
                        //case EFrameBufferAttachment.ColorAttachment21:
                        //case EFrameBufferAttachment.ColorAttachment22:
                        //case EFrameBufferAttachment.ColorAttachment23:
                        //case EFrameBufferAttachment.ColorAttachment24:
                        //case EFrameBufferAttachment.ColorAttachment25:
                        //case EFrameBufferAttachment.ColorAttachment26:
                        //case EFrameBufferAttachment.ColorAttachment27:
                        //case EFrameBufferAttachment.ColorAttachment28:
                        //case EFrameBufferAttachment.ColorAttachment29:
                        //case EFrameBufferAttachment.ColorAttachment30:
                        //case EFrameBufferAttachment.ColorAttachment31:
                        //case EFrameBufferAttachment.FrontLeft:
                        //case EFrameBufferAttachment.FrontRight:
                        //case EFrameBufferAttachment.BackLeft:
                        //case EFrameBufferAttachment.BackRight:
                        //case EFrameBufferAttachment.Back:
                        //case EFrameBufferAttachment.Front:
                        //case EFrameBufferAttachment.Left:
                        //case EFrameBufferAttachment.Right:
                        //    TextureTypes |= EFrameBufferTextureTypeFlags.Color;
                        //    continue;
                        case EFrameBufferAttachment.DepthAttachment:
                            TextureTypes |= EFrameBufferTextureTypeFlags.Depth;
                            continue;
                        case EFrameBufferAttachment.DepthStencilAttachment:
                            TextureTypes |= EFrameBufferTextureTypeFlags.Depth | EFrameBufferTextureTypeFlags.Stencil;
                            continue;
                        case EFrameBufferAttachment.StencilAttachment:
                            TextureTypes |= EFrameBufferTextureTypeFlags.Stencil;
                            continue;
                    }
                    fboAttachments.Add(ToDrawBuffer(Attachment));
                    TextureTypes |= EFrameBufferTextureTypeFlags.Color;
                }
            }

            DrawBuffers = [.. fboAttachments];

            PostSetRenderTargets?.Invoke();
        }

        private static EDrawBuffersAttachment ToDrawBuffer(EFrameBufferAttachment attachment)
            => attachment switch
            {
                EFrameBufferAttachment.ColorAttachment0 => EDrawBuffersAttachment.ColorAttachment0,
                EFrameBufferAttachment.ColorAttachment1 => EDrawBuffersAttachment.ColorAttachment1,
                EFrameBufferAttachment.ColorAttachment2 => EDrawBuffersAttachment.ColorAttachment2,
                EFrameBufferAttachment.ColorAttachment3 => EDrawBuffersAttachment.ColorAttachment3,
                EFrameBufferAttachment.ColorAttachment4 => EDrawBuffersAttachment.ColorAttachment4,
                EFrameBufferAttachment.ColorAttachment5 => EDrawBuffersAttachment.ColorAttachment5,
                EFrameBufferAttachment.ColorAttachment6 => EDrawBuffersAttachment.ColorAttachment6,
                EFrameBufferAttachment.ColorAttachment7 => EDrawBuffersAttachment.ColorAttachment7,
                EFrameBufferAttachment.ColorAttachment8 => EDrawBuffersAttachment.ColorAttachment8,
                EFrameBufferAttachment.ColorAttachment9 => EDrawBuffersAttachment.ColorAttachment9,
                EFrameBufferAttachment.ColorAttachment10 => EDrawBuffersAttachment.ColorAttachment10,
                EFrameBufferAttachment.ColorAttachment11 => EDrawBuffersAttachment.ColorAttachment11,
                EFrameBufferAttachment.ColorAttachment12 => EDrawBuffersAttachment.ColorAttachment12,
                EFrameBufferAttachment.ColorAttachment13 => EDrawBuffersAttachment.ColorAttachment13,
                EFrameBufferAttachment.ColorAttachment14 => EDrawBuffersAttachment.ColorAttachment14,
                EFrameBufferAttachment.ColorAttachment15 => EDrawBuffersAttachment.ColorAttachment15,
                EFrameBufferAttachment.ColorAttachment16 => EDrawBuffersAttachment.ColorAttachment16,
                EFrameBufferAttachment.ColorAttachment17 => EDrawBuffersAttachment.ColorAttachment17,
                EFrameBufferAttachment.ColorAttachment18 => EDrawBuffersAttachment.ColorAttachment18,
                EFrameBufferAttachment.ColorAttachment19 => EDrawBuffersAttachment.ColorAttachment19,
                EFrameBufferAttachment.ColorAttachment20 => EDrawBuffersAttachment.ColorAttachment20,
                EFrameBufferAttachment.ColorAttachment21 => EDrawBuffersAttachment.ColorAttachment21,
                EFrameBufferAttachment.ColorAttachment22 => EDrawBuffersAttachment.ColorAttachment22,
                EFrameBufferAttachment.ColorAttachment23 => EDrawBuffersAttachment.ColorAttachment23,
                EFrameBufferAttachment.ColorAttachment24 => EDrawBuffersAttachment.ColorAttachment24,
                EFrameBufferAttachment.ColorAttachment25 => EDrawBuffersAttachment.ColorAttachment25,
                EFrameBufferAttachment.ColorAttachment26 => EDrawBuffersAttachment.ColorAttachment26,
                EFrameBufferAttachment.ColorAttachment27 => EDrawBuffersAttachment.ColorAttachment27,
                EFrameBufferAttachment.ColorAttachment28 => EDrawBuffersAttachment.ColorAttachment28,
                EFrameBufferAttachment.ColorAttachment29 => EDrawBuffersAttachment.ColorAttachment29,
                EFrameBufferAttachment.ColorAttachment30 => EDrawBuffersAttachment.ColorAttachment30,
                EFrameBufferAttachment.ColorAttachment31 => EDrawBuffersAttachment.ColorAttachment31,
                EFrameBufferAttachment.FrontLeft => EDrawBuffersAttachment.FrontLeft,
                EFrameBufferAttachment.FrontRight => EDrawBuffersAttachment.FrontRight,
                EFrameBufferAttachment.BackLeft => EDrawBuffersAttachment.BackLeft,
                EFrameBufferAttachment.BackRight => EDrawBuffersAttachment.BackRight,
                _ => EDrawBuffersAttachment.ColorAttachment0,
            };

        public event Action? BindForReadRequested;
        public event Action? BindForWriteRequested;
        public event Action? BindRequested;

        public event Action? UnbindFromReadRequested;
        public event Action? UnbindFromWriteRequested;
        public event Action? UnbindRequested;

        public void BindForReading()
        {
            _readStack.Push(this);
            OnBindForRead();
        }

        private void OnBindForRead()
        {
            BindForReadRequested?.Invoke();
        }

        public StateObject BindForReadingState()
        {
            BindForReading();
            return StateObject.New(UnbindFromReading);
        }
        public void UnbindFromReading()
        {
            if (BoundForReading != this)
                return;

            if (_readStack.Count > 0)
            {
                _readStack.Pop();
                if (_readStack.TryPeek(out var fbo))
                    fbo.OnBindForRead();
                else
                    UnbindFromReadRequested?.Invoke();
            }
            else
                UnbindFromReadRequested?.Invoke();
        }
        public void BindForWriting()
        {
            _writeStack.Push(this);
            OnBindForWrite();
        }

        private void OnBindForWrite()
        {
            BindForWriteRequested?.Invoke();
        }

        public StateObject BindForWritingState()
        {
            BindForWriting();
            return StateObject.New(UnbindFromWriting);
        }
        public void UnbindFromWriting()
        {
            if (BoundForWriting != this)
                return;

            if (_writeStack.Count > 0)
            {
                _writeStack.Pop();
                if (_writeStack.TryPeek(out var fbo))
                    fbo.OnBindForWrite();
                else
                    UnbindFromWriteRequested?.Invoke();
            }
            else
                UnbindFromWriteRequested?.Invoke();
        }

        public void Bind()
        {
            _bindStack.Push(this);
            OnBind();
        }

        private void OnBind()
        {
            BindRequested?.Invoke();
        }

        public StateObject BindState()
        {
            Bind();
            return StateObject.New(Unbind);
        }

        public void Unbind()
        {
            if (CurrentlyBound != this)
                return;

            if (_bindStack.Count > 0)
            {
                _bindStack.Pop();
                if (_bindStack.TryPeek(out var fbo))
                    fbo.OnBind();
                else
                    UnbindRequested?.Invoke();
            }
            else
                UnbindRequested?.Invoke();
        }

        public event Action<int>? PreSetRenderTarget;
        public event Action<int>? PostSetRenderTarget;
        public event Action? SetDrawBuffersRequested;

        private unsafe void SetDrawBuffers()
            => SetDrawBuffersRequested?.Invoke();

        public void SetRenderTarget(int i, (IFrameBufferAttachement Target, EFrameBufferAttachment Attachment, int MipLevel, int LayerIndex) target)
        {
            if (Targets is null || !Targets.IndexInRangeArrayT(i))
            {
                Debug.Out($"Index {i} is out of range for the number of targets in the framebuffer.");
                return;
            }

            PreSetRenderTarget?.Invoke(i);
            Targets[i] = target;
            PostSetRenderTarget?.Invoke(i);
        }

        public unsafe void AttachAll()
        {
            using var t = BindState();
            if (Targets != null)
                for (int i = 0; i < Targets.Length; ++i)
                    Attach(i);
            SetDrawBuffers();
            //AbstractRenderer.Current?.SetReadBuffer(EReadBufferMode.None);
        }

        public void DetachAll()
        {
            using var t = BindState();
            if (Targets != null)
                for (int i = 0; i < Targets.Length; ++i)
                    Detach(i);
        }

        public void Attach(int i)
        {
            var targets = Targets;
            if (targets is null)
                return;

            var (Target, Attachment, MipLevel, LayerIndex) = targets[i];
            switch (Target)
            {
                default:
                    {
                        if (Target is not XRTexture texture)
                            break;
                        texture.Bind();
                        texture.AttachToFBO(this, Attachment, MipLevel);
                        break;
                    }
                case XRRenderBuffer buf:
                    buf.Bind();
                    buf.AttachToFBO(this, Attachment);
                    break;
                case XRTexture2DArray arrayRef when LayerIndex >= 0:
                    arrayRef.Bind();
                    arrayRef.AttachImageToFBO(this, Attachment, LayerIndex, MipLevel);
                    break;
                case XRTextureCube cuberef when LayerIndex >= 0 && LayerIndex < 6:
                    cuberef.Bind();
                    cuberef.AttachFaceToFBO(this, Attachment, ECubemapFace.PosX + LayerIndex, MipLevel);
                    break;
                case XRTexture2DArray arrayref when arrayref.OVRMultiViewParameters is XRTexture2DArray.OVRMultiView ovr && !Engine.Rendering.State.IsNVIDIA:
                    arrayref.Bind();
                    arrayref.AttachToFBO_OVRMultiView(this, Attachment, MipLevel, ovr.Offset, ovr.NumViews);
                    break;
            }
        }

        public void Detach(int i)
        {
            if (Targets is null)
                return;

            var (Target, Attachment, MipLevel, LayerIndex) = Targets[i];
            switch (Target)
            {
                default:
                    {
                        if (Target is not XRTexture texture)
                            break;
                        texture.Bind();
                        texture.DetachFromFBO(this, Attachment, MipLevel);
                        break;
                    }
                case XRRenderBuffer buf:
                    buf.Bind();
                    buf.DetachFromFBO(this, Attachment);
                    break;
                case XRTexture2DArray arrayRef when LayerIndex >= 0:
                    arrayRef.Bind();
                    arrayRef.DetachImageFromFBO(this, Attachment, LayerIndex, MipLevel);
                    break;
                case XRTextureCube cuberef when LayerIndex >= 0 && LayerIndex < 6:
                    cuberef.Bind();
                    cuberef.DetachFaceFromFBO(this, Attachment, ECubemapFace.PosX + LayerIndex, MipLevel);
                    break;
                case XRTexture2DArray arrayref when arrayref.OVRMultiViewParameters is XRTexture2DArray.OVRMultiView ovr:
                    arrayref.Bind();
                    arrayref.DetachFromFBO_OVRMultiView(this, Attachment, MipLevel, ovr.Offset, ovr.NumViews);
                    break;
            }
        }
    }
}