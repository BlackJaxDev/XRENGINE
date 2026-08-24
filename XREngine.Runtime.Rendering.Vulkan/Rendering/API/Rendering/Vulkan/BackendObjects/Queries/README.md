# Vulkan Query Backend Objects

Owns Vulkan wrappers for engine query-like resources, including render queries
and transform feedback. Pool allocation, native pool retirement, capability
state, result-completion checks, and provider registration are owned by the
generation-local <code>VulkanQueryAuthority</code>. Wrappers access native
command tracking only through its narrow command service.
