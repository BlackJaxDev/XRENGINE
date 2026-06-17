# Vulkan Pipeline Compilation

The Vulkan backend avoids render-thread stalls from cold `vkCreateGraphicsPipelines`
calls by using three layers:

- SPIR-V shader artifacts are compiled asynchronously by `VkShader` and cached under
  `Build/Cache/Vulkan/ShaderArtifacts/`.
- Graphics pipeline libraries are cached at the renderer/device level so matching
  vertex-input, pre-rasterization, fragment-shader, and fragment-output subsets can
  be reused across mesh renderers.
- Cold graphics pipelines are queued to background workers when
  `RuntimeEngine.Rendering.Settings.AsyncProgramCompilation` is enabled. Command
  recording skips that draw while the pipeline is still compiling, then marks command
  buffers dirty when the worker finishes so the next recording can bind the completed
  pipeline.

Background pipeline workers create pipelines without the shared persistent
`VkPipelineCache`. Vulkan requires host access to a `VkPipelineCache` to be externally
synchronized, so using no cache on workers allows parallel `vkCreateGraphicsPipelines`
calls. The synchronous render-thread fallback still uses the active persistent cache.

Set `XRE_VK_PIPELINE_COMPILE_WORKERS` to override the worker count. Valid values are
`1` through `16`; when unset, the engine uses up to four workers based on CPU count.
