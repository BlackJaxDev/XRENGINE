using System.Collections.Concurrent;
using Silk.NET.OpenGL;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using static XREngine.Rendering.OpenGL.OpenGLRenderer;

namespace XREngine.Rendering.OpenGL
{
    public abstract class GLTexture<T>(OpenGLRenderer renderer, T data) : GLObject<T>(renderer, data), IGLTexture where T : XRTexture
    {
        public override EGLObjectType Type => EGLObjectType.Texture;

        public XREvent<PreBindCallback>? PreBind;
        public XREvent<PrePushDataCallback>? PrePushData;
        public XREvent<GLTexture<T>>? PostPushData;

        private bool _isPushing = false;
        public bool IsPushing
        {
            get => _isPushing;
            protected set => SetField(ref _isPushing, value);
        }

        protected override void UnlinkData()
        {
            Data.AttachToFBORequested -= AttachToFBO;
            Data.DetachFromFBORequested -= DetachFromFBO;
            Data.PushDataRequested -= PushData;
            Data.BindRequested -= Bind;
            Data.UnbindRequested -= Unbind;
            Data.ClearRequested -= Clear;
            Data.GenerateMipmapsRequested -= GenerateMipmaps;
            Data.PropertyChanged -= DataPropertyChanged;
            Data.PropertyChanging -= DataPropertyChanging;
        }

        protected override void LinkData()
        {
            Data.AttachToFBORequested += AttachToFBO;
            Data.DetachFromFBORequested += DetachFromFBO;
            Data.PushDataRequested += PushData;
            Data.BindRequested += Bind;
            Data.UnbindRequested += Unbind;
            Data.ClearRequested += Clear;
            Data.GenerateMipmapsRequested += GenerateMipmaps;
            Data.PropertyChanged += DataPropertyChanged;
            Data.PropertyChanging += DataPropertyChanging;
        }

        protected virtual void DataPropertyChanging(object? sender, IXRPropertyChangingEventArgs e)
        {

        }

        protected virtual void DataPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            QueuePropertyUpdate(e.PropertyName);
        }

        [Flags]
        private enum TexturePropertyUpdateMask
        {
            None = 0,
            MinLOD = 1 << 0,
            MaxLOD = 1 << 1,
            LargestMipmapLevel = 1 << 2,
            SmallestAllowedMipmapLevel = 1 << 3,
            All = MinLOD | MaxLOD | LargestMipmapLevel | SmallestAllowedMipmapLevel
        }

        private static readonly ConcurrentQueue<GLTexture<T>> s_pendingPropertyUpdateTextures = new();
        private static int s_propertyUpdateBatchQueued;

        private int _pendingPropertyUpdates;
        private int _propertyUpdateEnqueued;

        private void QueuePropertyUpdate(string? propertyName)
        {
            TexturePropertyUpdateMask mask = propertyName switch
            {
                null => TexturePropertyUpdateMask.All,
                "" => TexturePropertyUpdateMask.All,
                nameof(XRTexture.MinLOD) => TexturePropertyUpdateMask.MinLOD,
                nameof(XRTexture.MaxLOD) => TexturePropertyUpdateMask.MaxLOD,
                nameof(XRTexture.LargestMipmapLevel) => TexturePropertyUpdateMask.LargestMipmapLevel,
                nameof(XRTexture.SmallestAllowedMipmapLevel) => TexturePropertyUpdateMask.SmallestAllowedMipmapLevel,
                _ => TexturePropertyUpdateMask.None
            };

            if (mask == TexturePropertyUpdateMask.None)
                return;

            Interlocked.Or(ref _pendingPropertyUpdates, (int)mask);

            if (Interlocked.Exchange(ref _propertyUpdateEnqueued, 1) == 0)
                s_pendingPropertyUpdateTextures.Enqueue(this);

            QueuePropertyUpdateBatchFlush();
        }

        private static void QueuePropertyUpdateBatchFlush()
        {
            if (Interlocked.Exchange(ref s_propertyUpdateBatchQueued, 1) == 1)
                return;

            // Batch texture parameter updates into one render-thread job so large
            // streaming bursts do not flood the queue with one invoke per texture.
            Engine.EnqueueMainThreadTask(FlushQueuedPropertyUpdates, "GLTexture.UpdateProperty");
        }

        private static void FlushQueuedPropertyUpdates()
        {
            Interlocked.Exchange(ref s_propertyUpdateBatchQueued, 0);

            while (s_pendingPropertyUpdateTextures.TryDequeue(out GLTexture<T>? texture))
            {
                Interlocked.Exchange(ref texture._propertyUpdateEnqueued, 0);
                texture.FlushPropertyUpdates();
            }

            if (!s_pendingPropertyUpdateTextures.IsEmpty)
                QueuePropertyUpdateBatchFlush();
        }

        private void FlushPropertyUpdates()
        {
            TexturePropertyUpdateMask mask = (TexturePropertyUpdateMask)Interlocked.Exchange(ref _pendingPropertyUpdates, 0);

            if (mask == TexturePropertyUpdateMask.None)
                return;

            // Check if the texture has been generated and is a valid texture object.
            // Property changes can be queued before the texture is created, in which case
            // we skip the GL calls here - the parameters will be set when the texture is bound/pushed.
            if (!TryGetBindingId(out uint id) || id == InvalidBindingId || !Api.IsTexture(id))
                return;

            if (mask.HasFlag(TexturePropertyUpdateMask.MinLOD))
            {
                int param = Data.MinLOD;
                Api.TextureParameterI(id, TextureParameterName.TextureMinLod, ref param);
            }

            if (mask.HasFlag(TexturePropertyUpdateMask.MaxLOD))
            {
                int param = Data.MaxLOD;
                Api.TextureParameterI(id, TextureParameterName.TextureMaxLod, ref param);
            }

            if (mask.HasFlag(TexturePropertyUpdateMask.LargestMipmapLevel))
            {
                int param = Data.LargestMipmapLevel;
                Api.TextureParameterI(id, TextureParameterName.TextureBaseLevel, ref param);
            }

            if (mask.HasFlag(TexturePropertyUpdateMask.SmallestAllowedMipmapLevel))
            {
                int param = Data.SmallestAllowedMipmapLevel;
                Api.TextureParameterI(id, TextureParameterName.TextureMaxLevel, ref param);
            }
        }

        public bool IsMultisampleTarget =>
            TextureTarget == ETextureTarget.Texture2DMultisample ||
            TextureTarget == ETextureTarget.Texture2DMultisampleArray ||
            TextureTarget == ETextureTarget.ProxyTexture2DMultisample ||
            TextureTarget == ETextureTarget.ProxyTexture2DMultisampleArray;

        public Vector3 WidthHeightDepth
            => Data.WidthHeightDepth;

        protected virtual void SetParameters()
            => Engine.InvokeOnMainThread(SetParametersInternal, "GLTexture.SetParameters", true);

        private void SetParametersInternal()
        {
            Interlocked.Exchange(ref _pendingPropertyUpdates, 0);

            int param;
            if (!IsMultisampleTarget)
            {
                param = Data.MinLOD;
                Api.TextureParameterI(BindingId, TextureParameterName.TextureMinLod, ref param);

                param = Data.MaxLOD;
                Api.TextureParameterI(BindingId, TextureParameterName.TextureMaxLod, ref param);
            }

            param = Data.LargestMipmapLevel;
            Api.TextureParameterI(BindingId, TextureParameterName.TextureBaseLevel, ref param);

            param = Data.SmallestAllowedMipmapLevel;
            Api.TextureParameterI(BindingId, TextureParameterName.TextureMaxLevel, ref param);
        }

        protected virtual bool OnPreBind()
        {
            if (ReferenceEquals(Renderer.GetBoundTexture(TextureTarget), this))
                return false;

            PreBindCallback callback = new();
            PreBind?.Invoke(callback);
            return callback.ShouldBind;
        }

        protected virtual void OnPrePushData(out bool shouldPush, out bool allowPostPushCallback)
        {
            PrePushDataCallback callback = new();
            PrePushData?.Invoke(callback);
            shouldPush = callback.ShouldPush;
            allowPostPushCallback = callback.AllowPostPushCallback;
        }

        protected virtual void OnPostPushData()
            => PostPushData?.Invoke(this);

        public abstract ETextureTarget TextureTarget { get; }

        /// <summary>
        /// If true, this texture's data has been updated and needs to be pushed to the GPU.
        /// </summary>
        /// <returns></returns>
        public bool IsInvalidated { get; protected set; } = true;
        /// <summary>
        /// Informs the renderer that this texture's data has been updated and needs to be pushed to the GPU.
        /// </summary>
        /// <returns></returns>
        public void Invalidate() => IsInvalidated = true;

        public virtual void Bind()
        {
            uint id = BindingId;
            if (id == InvalidBindingId)
            {
                // CRITICAL: do NOT silently return — the caller (e.g. GLRenderProgram.Sampler) has
                // already issued glActiveTexture(unit) for the sampler unit we are about to populate.
                // If we return without binding anything, the unit keeps whatever texture was bound on
                // this target by the previous draw, causing cross-material texture bleed (e.g. the
                // lion diffuse rendering on the sponza roof after a DataResized/immutable-recreate
                // race dropped this wrapper's GL name just before a draw).
                //
                // Explicitly zero-bind the active unit's target so the stale residue cannot be
                // sampled, and emit a loud diagnostic identifying the affected unit + wrapper.
                Api.BindTexture(ToGLEnum(TextureTarget), 0);
                Renderer.SetBoundTexture(TextureTarget, null);
                Debug.OpenGLWarning(
                    $"[GLTexture.Bind] Binding SKIPPED (id=InvalidBindingId) for '{Data?.Name ?? GetType().Name}' " +
                    $"on active unit {Renderer.ActiveTextureUnit}, target={TextureTarget}. " +
                    $"Unit cleared to 0 to prevent stale-texture bleed.");
                return;
            }

            // Even if our engine-side tracking believes this texture is already bound, perform the GL bind.
            // This prevents state drift (or new texture names from `glGenTextures`) from leaving the object
            // non-existent/uninitialized for DSA calls (e.g. `glTextureStorage2D`, `glTextureView`, FBO attach).
            // Note: GetBoundTexture is tracked per-target globally, not per-unit, so this check is only a
            // "best effort" fast path for back-to-back state queries — we still always issue glBindTexture
            // below because the caller may have switched the active unit since the last bind of this wrapper.
            bool alreadyTrackedBound = ReferenceEquals(Renderer.GetBoundTexture(TextureTarget), this);
            if (!alreadyTrackedBound)
            {
                if (!OnPreBind())
                {
                    // Pre-bind callback vetoed the bind. The active unit currently targets whatever
                    // the previous draw left there — leaving it bound is unsafe (see above). Unbind
                    // the target on the current unit and warn.
                    Api.BindTexture(ToGLEnum(TextureTarget), 0);
                    Renderer.SetBoundTexture(TextureTarget, null);
                    Debug.OpenGLWarning(
                        $"[GLTexture.Bind] Binding VETOED by OnPreBind for '{Data?.Name ?? GetType().Name}' " +
                        $"on active unit {Renderer.ActiveTextureUnit}, target={TextureTarget}. " +
                        $"Unit cleared to 0 to prevent stale-texture bleed.");
                    return;
                }
            }

            Api.BindTexture(ToGLEnum(TextureTarget), id);
            Renderer.SetBoundTexture(TextureTarget, this, Data.Name);
            VerifySettings();
        }

        private void VerifySettings()
        {
            if (!IsInvalidated || IsPushing)
            {
                SetParameters();
                return;
            }

            using var sample = Engine.Profiler.Start("GLTexture.VerifySettings");
            using (Engine.Profiler.Start("GLTexture.VerifySettings.SetParameters"))
                SetParameters();

            IsInvalidated = false;
            using (Engine.Profiler.Start("GLTexture.VerifySettings.PushData"))
                PushData();
        }

        public void Unbind()
        {
            if (!ReferenceEquals(Renderer.GetBoundTexture(TextureTarget), this))
                return;
            
            Renderer.SetBoundTexture(TextureTarget, null);
            Api.BindTexture(ToGLEnum(TextureTarget), 0);
        }

        public void Clear(ColorF4 color, int level = 0)
        {
            // Ensure the texture exists and has storage before clearing.
            // Some passes clear render-target textures before they've ever been bound/pushed.
            var previous = Renderer.BoundTexture;
            Bind();

            var id = BindingId;
            if (id != InvalidBindingId && Api.IsTexture(id))
                Renderer.ClearTexImage(id, level, color);

            if (previous is null || ReferenceEquals(previous, this))
                Unbind();
            else
                previous.Bind();
        }

        public virtual void GenerateMipmaps()
        {
            if (IsMultisampleTarget)
                return;

            // `glGenerateTextureMipmap` requires a valid texture object. With `glGenTextures`, the name
            // may not become a real texture object until it has been bound at least once.
            // Bloom (and other passes) can request mipmaps before the first bind/push in some paths (VR).
            // Bind here to ensure initialization, then restore prior binding.
            var previous = Renderer.BoundTexture;
            Bind();

            var id = BindingId;
            if (id != InvalidBindingId)
                Api.GenerateTextureMipmap(id);

            if (previous is null || ReferenceEquals(previous, this))
                Unbind();
            else
                previous.Bind();
        }

        protected override uint CreateObject()
            => Api.GenTexture();

        protected internal override void PostGenerated()
            => Invalidate();

        public virtual void AttachToFBO(XRFrameBuffer fbo, EFrameBufferAttachment attachment, int mipLevel = 0)
        {
            // Ensure the texture exists and has storage before attaching.
            // Some render targets are attached before first bind/push (e.g. shadow maps).
            var previous = Renderer.BoundTexture;
            Bind();

            Api.NamedFramebufferTexture(Renderer.GenericToAPI<GLFrameBuffer>(fbo)!.BindingId, ToGLEnum(attachment), BindingId, mipLevel);

            if (previous is null || ReferenceEquals(previous, this))
                Unbind();
            else
                previous.Bind();
        }
        public virtual void DetachFromFBO(XRFrameBuffer fbo, EFrameBufferAttachment attachment, int mipLevel = 0)
            => Api.NamedFramebufferTexture(Renderer.GenericToAPI<GLFrameBuffer>(fbo)!.BindingId, ToGLEnum(attachment), 0, mipLevel);

        public void AttachToFBO_OVRMultiView(XRFrameBuffer fbo, EFrameBufferAttachment attachment, int mipLevel, int offset, uint numViews)
        {
            // Ensure the texture exists and has storage before attaching.
            var previous = Renderer.BoundTexture;
            Bind();

            Renderer.OVRMultiView?.NamedFramebufferTextureMultiview(
                Renderer.GenericToAPI<GLFrameBuffer>(fbo)!.BindingId,
                ToFrameBufferAttachement(attachment),
                BindingId,
                mipLevel,
                offset,
                numViews);

            if (previous is null || ReferenceEquals(previous, this))
                Unbind();
            else
                previous.Bind();
        }
        public void DetachFromFBO_OVRMultiView(XRFrameBuffer fbo, EFrameBufferAttachment attachment, int mipLevel, int offset, uint numViews)
            => Renderer.OVRMultiView?.NamedFramebufferTextureMultiview(
                Renderer.GenericToAPI<GLFrameBuffer>(fbo)!.BindingId,
                ToFrameBufferAttachement(attachment),
                0,
                mipLevel,
                offset,
                numViews);

        public abstract void PushData();
        public string ResolveSamplerName(int textureIndex, string? samplerNameOverride)
            => Data.ResolveSamplerName(textureIndex, samplerNameOverride);

        public virtual void PostSampling()
        {

        }

        public virtual void PreSampling()
        {

        }
    }
}
