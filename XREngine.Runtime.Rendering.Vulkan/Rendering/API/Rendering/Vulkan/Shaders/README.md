# Vulkan Shaders

Owns GLSL-to-SPIR-V compile helpers, source fixups, auto-uniform rewriting, transform-feedback rewriting, reflection, and shader artifact caches.

`VulkanShaderCompileScheduler` runs shader CPU artifact builds on 2 to 6 BelowNormal threads, not on the .NET thread pool. This keeps startup shader builds from filling every core and making the rest of the PC slow.
