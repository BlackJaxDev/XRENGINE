# Vulkan Resource Lifetime

Owns resource generations, recorded/queued/submitted pins, completion
watermarks, external-ownership state, and retirement readiness. Renderer
partials may delegate native operations to this owner but must not maintain
parallel lifetime registries.
