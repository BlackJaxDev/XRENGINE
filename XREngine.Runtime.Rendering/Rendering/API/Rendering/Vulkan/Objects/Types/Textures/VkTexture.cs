using Silk.NET.Vulkan;
using System.Threading;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Vulkan;
public unsafe partial class VulkanRenderer
{
    public class VulkanPrePushDataCallback
    {
        public bool ShouldPush { get; set; } = true;
        public bool AllowPostPushCallback { get; set; } = true;
    }

    public abstract class VkTexture<T>(VulkanRenderer api, T data) : VkObject<T>(api, data) where T : XRTexture
    {
        public XREvent<VulkanPrePushDataCallback>? PrePushData;
        public XREvent<VkTexture<T>>? PostPushData;

        public override VkObjectType Type => VkObjectType.Image;

        /// <summary>
        /// Tracks CPU-side data invalidation separately from Vulkan handle generation.
        /// </summary>
        public bool IsInvalidated { get; protected set; } = true;

        /// <summary>
        /// True after this wrapper has completed an upload or otherwise marked its backing
        /// resource as ready for descriptor use. This is intentionally separate from
        /// <see cref="IsGenerated"/>, which only answers whether backend handles exist.
        /// </summary>
        public bool HasUploadedData { get; protected set; }

        /// <summary>
        /// Set when sampler/view-affecting state changes and descriptor users should
        /// refresh their cached image or texel-buffer info.
        /// </summary>
        public bool IsDescriptorDirty { get; protected set; } = true;
        private long _descriptorGeneration;

        public ulong DescriptorGeneration
            => unchecked((ulong)Volatile.Read(ref _descriptorGeneration));

        /// <summary>
        /// Generic Vulkan texture readiness for descriptor use. Attachment/pass readiness
        /// is still owned by the render pass and framebuffer planner.
        /// </summary>
        public virtual bool IsDescriptorReady => IsGenerated && !IsDescriptorDirty;

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
            LinkTextureData();
        }

        protected override void UnlinkData()
        {
            UnlinkTextureData();
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

        protected virtual void LinkTextureData()
        {
        }

        protected virtual void UnlinkTextureData()
        {
        }

        protected virtual void DataPropertyChanging(object? sender, IXRPropertyChangingEventArgs e)
        {
        }

        protected virtual void DataPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
        {
            if (IsSamplerAffectingProperty(e.PropertyName))
                MarkDescriptorDirty();

            if (IsStorageAffectingProperty(e.PropertyName))
                InvalidateTextureData();
        }

        protected virtual bool IsSamplerAffectingProperty(string? propertyName)
            => propertyName is null
                or ""
                or nameof(XRTexture.MinLOD)
                or nameof(XRTexture.MaxLOD)
                or nameof(XRTexture.LargestMipmapLevel)
                or nameof(XRTexture.SmallestAllowedMipmapLevel)
                or nameof(XRTexture.AutoGenerateMipmaps);

        protected virtual bool IsStorageAffectingProperty(string? propertyName)
            => propertyName is null
                or ""
                or nameof(XRTexture.RequiresStorageUsage)
                or nameof(XRTexture.FrameBufferAttachment);

        protected void InvalidateTextureData()
        {
            IsInvalidated = true;
            HasUploadedData = false;
            MarkDescriptorDirty();
        }

        protected void MarkDescriptorDirty()
        {
            IsDescriptorDirty = true;
            IncrementDescriptorGeneration();
        }

        protected void MarkDescriptorClean()
            => IsDescriptorDirty = false;

        protected void MarkDescriptorPublished()
        {
            IncrementDescriptorGeneration();
            MarkDescriptorClean();
        }

        protected void MarkUploaded()
        {
            HasUploadedData = true;
            IsInvalidated = false;
            MarkDescriptorPublished();
        }

        private void IncrementDescriptorGeneration()
            => Interlocked.Increment(ref _descriptorGeneration);

        protected virtual void OnPrePushData(out bool shouldPush, out bool allowPostPushCallback)
        {
            VulkanPrePushDataCallback callback = new();
            PrePushData?.Invoke(callback);
            shouldPush = callback.ShouldPush;
            allowPostPushCallback = callback.AllowPostPushCallback;
        }

        protected virtual void OnPostPushData()
            => PostPushData?.Invoke(this);

        protected bool TryBeginPushData(out bool allowPostPushCallback)
        {
            OnPrePushData(out bool shouldPush, out allowPostPushCallback);
            if (shouldPush)
                return true;

            if (allowPostPushCallback)
                OnPostPushData();
            return false;
        }

        protected void CompletePushData(bool allowPostPushCallback)
        {
            if (allowPostPushCallback)
                OnPostPushData();
        }

        public virtual void PushData()
        {
            if (!TryBeginPushData(out bool allowPostPushCallback))
                return;

            Generate();
            if (IsGenerated)
                MarkUploaded();

            CompletePushData(allowPostPushCallback);
        }

        public virtual void Bind()
        {
            EnsureDescriptorReadyForVulkanUse("BindRequested");
        }

        public virtual void Unbind()
        {
            Debug.VulkanEvery(
                $"Vulkan.Texture.Unbind.{Data.GetHashCode()}",
                TimeSpan.FromSeconds(5),
                "[Vulkan Compat] Texture UnbindRequested for '{0}' is a no-op; Vulkan texture state is descriptor/pass owned.",
                Data.Name ?? Data.GetDescribingName());
        }

        public virtual void Clear(ColorF4 color, int level = 0)
            => Debug.VulkanWarningEvery(
                $"Vulkan.Texture.ClearUnsupported.{Data.GetHashCode()}",
                TimeSpan.FromSeconds(5),
                "[Vulkan Compat] ClearRequested for texture '{0}' has no image-backed clear path in wrapper '{1}'. Preferred Vulkan path: issue an explicit clear/copy command through the owning render pass or command context.",
                Data.Name ?? Data.GetDescribingName(),
                GetType().Name);

        public virtual void GenerateMipmaps()
            => Debug.VulkanWarningEvery(
                $"Vulkan.Texture.MipmapUnsupported.{Data.GetHashCode()}",
                TimeSpan.FromSeconds(5),
                "[Vulkan Compat] GenerateMipmapsRequested for texture '{0}' is unsupported by wrapper '{1}'. Preferred Vulkan path: generate mips through the image-backed texture upload/blit path.",
                Data.Name ?? Data.GetDescribingName(),
                GetType().Name);

        public virtual void AttachToFBO(XRFrameBuffer fbo, EFrameBufferAttachment attachment, int mipLevel = 0)
        {
            Generate();
            if (!IsGenerated)
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.Texture.AttachNotGenerated.{Data.GetHashCode()}",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan Compat] AttachToFBORequested could not generate texture '{0}' for framebuffer '{1}' attachment={2} mip={3}. Preferred Vulkan path: declare attachments through XRFrameBuffer.SetRenderTargets before pass construction.",
                    Data.Name ?? Data.GetDescribingName(),
                    fbo.Name ?? fbo.GetDescribingName(),
                    attachment,
                    mipLevel);
                return;
            }

            if (Renderer.GetOrCreateAPIRenderObject(fbo, generateNow: true) is not VkFrameBuffer vkFrameBuffer || !vkFrameBuffer.IsGenerated)
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.Texture.AttachFboUnavailable.{fbo.GetHashCode()}",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan Compat] AttachToFBORequested for texture '{0}' could not resolve Vulkan framebuffer '{1}'. Vulkan framebuffer attachments are rebuilt from XRFrameBuffer targets; prefer XRFrameBuffer.SetRenderTargets.",
                    Data.Name ?? Data.GetDescribingName(),
                    fbo.Name ?? fbo.GetDescribingName());
            }
        }

        public virtual void DetachFromFBO(XRFrameBuffer fbo, EFrameBufferAttachment attachment, int mipLevel = 0)
            => Debug.VulkanEvery(
                $"Vulkan.Texture.Detach.{Data.GetHashCode()}",
                TimeSpan.FromSeconds(5),
                "[Vulkan Compat] DetachFromFBORequested for texture '{0}' framebuffer '{1}' attachment={2} mip={3}; Vulkan framebuffer attachments are immutable and rebuilt from XRFrameBuffer targets. Preferred Vulkan path: update XRFrameBuffer.SetRenderTargets.",
                Data.Name ?? Data.GetDescribingName(),
                fbo.Name ?? fbo.GetDescribingName(),
                attachment,
                mipLevel);

        protected void EnsureDescriptorReadyForVulkanUse(string reason)
        {
            Generate();

            if (IsInvalidated)
                PushData();

            if (!IsGenerated)
            {
                Debug.VulkanWarningEvery(
                    $"Vulkan.Texture.DescriptorNotGenerated.{Data.GetHashCode()}",
                    TimeSpan.FromSeconds(2),
                    "[Vulkan] Texture descriptor readiness failed for '{0}' ({1}): wrapper={2} generated=false uploaded={3} descriptorDirty={4}.",
                    Data.Name ?? Data.GetDescribingName(),
                    reason,
                    GetType().Name,
                    HasUploadedData,
                    IsDescriptorDirty);
                return;
            }

            MarkDescriptorClean();
        }

        protected virtual string? ResolveLogicalResourceName()
        {
            string? name = Data.Name;
            if (!string.IsNullOrWhiteSpace(name))
                return name;

            string describing = Data.GetDescribingName();
            return string.IsNullOrWhiteSpace(describing) ? null : describing;
        }

        internal VulkanPhysicalImageGroup? TryResolvePhysicalGroup(bool ensureAllocated = true)
        {
            string? resourceName = ResolveLogicalResourceName();
            if (string.IsNullOrWhiteSpace(resourceName))
                return null;

            if (!Renderer.ResourceAllocator.TryGetPhysicalGroupForResource(resourceName, out VulkanPhysicalImageGroup? group) || group is null)
                return null;

            if (ensureAllocated)
                group.EnsureAllocated(Renderer);

            return group;
        }

        protected bool TryResolvePhysicalImage(out Image image)
        {
            if (TryResolvePhysicalGroup(out VulkanPhysicalImageGroup? group) && group is not null)
            {
                image = group.Image;
                return image.Handle != 0;
            }

            image = default;
            return false;
        }

        internal bool TryResolvePhysicalGroup(out VulkanPhysicalImageGroup? group)
        {
            group = TryResolvePhysicalGroup(true);
            return group is not null;
        }
    }
}
