# Vulkan Image Resources

Owns engine-allocated image records and copied allocation diagnostics. Image
views and sampler caches are separate responsibilities, and imported/external
images must not be treated as engine-owned without an explicit ownership
transfer.
