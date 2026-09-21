#ifndef XR_DDGI_BINDINGS_GLSL
#define XR_DDGI_BINDINGS_GLSL

// OpenGL has separate binding namespaces for storage buffers, samplers, and
// images. Vulkan does not, so DDGI assigns each class to a descriptor set.
#ifdef XRENGINE_VULKAN
#define XR_DDGI_STORAGE_BINDING(slot) set = 0, binding = slot
#define XR_DDGI_UNIFORM_BINDING(slot) set = 0, binding = slot
#define XR_DDGI_SAMPLER_BINDING(slot) set = 1, binding = slot
#define XR_DDGI_IMAGE_BINDING(slot) set = 2, binding = slot
#else
#define XR_DDGI_STORAGE_BINDING(slot) binding = slot
#define XR_DDGI_UNIFORM_BINDING(slot) binding = slot
#define XR_DDGI_SAMPLER_BINDING(slot) binding = slot
#define XR_DDGI_IMAGE_BINDING(slot) binding = slot
#endif

#endif
