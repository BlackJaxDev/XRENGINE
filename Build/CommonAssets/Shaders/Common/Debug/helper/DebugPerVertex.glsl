// Intentionally empty.
//
// Debug primitive geometry shaders only need the default gl_in[].gl_Position
// input and gl_Position output. Explicitly redeclaring gl_PerVertex here caused
// Vulkan/glslang to expose a different built-in block between VS and GS
// (notably CullDistance), which invalidates graphics pipeline creation.
