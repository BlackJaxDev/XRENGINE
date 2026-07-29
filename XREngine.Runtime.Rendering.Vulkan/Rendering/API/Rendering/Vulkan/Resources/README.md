# Vulkan Resources

Owns backend resource allocation, physical image/buffer lifetime, staging and
upload services, swapchain image/framebuffer resources, and resource
registration helpers. Engine resource wrappers live under `BackendObjects/`.

- `Buffers/` owns renderer-level allocation, mapping, upload, destruction, and
  buffer allocation registries. It must not contain engine-resource wrappers.
- `Images/` owns engine-allocated image records and allocation diagnostics.
  Imported/external image ownership remains explicit.
- `Lifetime/` owns resource generations, use publication, completion
  observations, and retirement readiness.
- `Retirement/` owns deferred-destruction queues and cross-frame-slot handle
  deduplication.
- `Uploads/` separates upload contracts, preparation/staging, transfer
  submission, publication, and queue policy.
- The files at this folder root each declare one allocator contract, allocation
  value, alias group, create template, or physical resource group.

Native destruction must follow the exactly-once contract documented in
`docs/architecture/rendering/vulkan-resource-lifetime-and-retirement.md`.
