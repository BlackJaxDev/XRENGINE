struct ViewUniforms {
    transform: mat4x4<f32>,
};

struct MaterialUniforms {
    tint: vec4f,
};

@group(0) @binding(0) var<uniform> view: ViewUniforms;
@group(1) @binding(0) var<uniform> material: MaterialUniforms;
@group(1) @binding(1) var colorTexture: texture_2d<f32>;
@group(1) @binding(2) var colorSampler: sampler;

struct VertexInput {
    @location(0) position: vec3f,
    @location(1) uv: vec2f,
};

struct VertexOutput {
    @builtin(position) position: vec4f,
    @location(0) uv: vec2f,
};

@vertex
fn vertexMain(input: VertexInput) -> VertexOutput {
    var output: VertexOutput;
    output.position = view.transform * vec4f(input.position, 1.0);
    output.uv = input.uv;
    return output;
}

@fragment
fn fragmentMain(input: VertexOutput) -> @location(0) vec4f {
    return textureSample(colorTexture, colorSampler, input.uv) * material.tint;
}
