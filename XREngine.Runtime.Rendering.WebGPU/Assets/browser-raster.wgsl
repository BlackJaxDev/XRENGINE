struct Frame {
    shadow: mat4x4<f32>,
    light: vec4<f32>,
    ambient: vec4<f32>,
    skyTop: vec4<f32>,
    skyBottom: vec4<f32>,
    output: vec4<f32>,
    options: vec4<f32>,
};
struct Material { cutoff: f32, mode: f32, lit: f32, receiveShadow: f32 };
@group(0) @binding(0) var<uniform> frame: Frame;
@group(0) @binding(1) var shadowMap: texture_depth_2d;
@group(0) @binding(2) var shadowSampler: sampler_comparison;
@group(1) @binding(0) var<uniform> tint: vec4<f32>;
@group(1) @binding(1) var albedo: texture_2d<f32>;
@group(1) @binding(2) var colorSampler: sampler;
@group(2) @binding(0) var<uniform> material: Material;
struct VertexInput {
    @location(0) position: vec3<f32>, @location(1) uv: vec2<f32>,
    @location(2) world0: vec4<f32>, @location(3) world1: vec4<f32>,
    @location(4) world2: vec4<f32>, @location(5) world3: vec4<f32>,
    @location(6) mvp0: vec4<f32>, @location(7) mvp1: vec4<f32>,
    @location(8) mvp2: vec4<f32>, @location(9) mvp3: vec4<f32>,
};
struct VertexOutput {
    @builtin(position) position: vec4<f32>, @location(0) uv: vec2<f32>,
    @location(1) world: vec3<f32>, @location(2) shadow: vec4<f32>,
};
fn transformVertex(v: VertexInput) -> VertexOutput {
    let world = mat4x4<f32>(v.world0,v.world1,v.world2,v.world3) * vec4<f32>(v.position,1.0);
    var o: VertexOutput;
    o.position = mat4x4<f32>(v.mvp0,v.mvp1,v.mvp2,v.mvp3) * vec4<f32>(v.position,1.0);
    o.uv=v.uv; o.world=world.xyz; o.shadow=frame.shadow*world;
    return o;
}
@vertex fn sceneVertex(v: VertexInput) -> VertexOutput { return transformVertex(v); }
@vertex fn shadowVertex(v: VertexInput) -> VertexOutput {
    var o=transformVertex(v); o.position=o.shadow; return o;
}
@fragment fn shadowFragment(v: VertexOutput) {
    let alpha=textureSampleLevel(albedo,colorSampler,v.uv,0.0).a*tint.a;
    if (material.mode == 1.0 && alpha < material.cutoff) { discard; }
}
@fragment fn sceneFragment(v: VertexOutput, @builtin(front_facing) front: bool) -> @location(0) vec4<f32> {
    let texel=textureSample(albedo,colorSampler,v.uv)*tint;
    // The admitted position/UV mesh profile deliberately uses flat world normals.
    let derivative=cross(dpdy(v.world),dpdx(v.world));
    let normal=normalize(select(-derivative,derivative,front)+vec3<f32>(0.0000001));
    let coord=v.shadow.xyz / v.shadow.w;
    let shadowUv=vec2<f32>(coord.x*0.5+0.5,0.5-coord.y*0.5);
    let sampled=textureSampleCompareLevel(shadowMap,shadowSampler,shadowUv,coord.z-0.001);
    let inside=all(shadowUv>=vec2<f32>(0.0)) && all(shadowUv<=vec2<f32>(1.0)) && coord.z>=0.0 && coord.z<=1.0 && v.shadow.w>0.0;
    let visibility=select(1.0,sampled,inside && material.receiveShadow>0.0 && frame.options.x>0.0);
    let lightLength=max(length(frame.light.xyz),0.000001);
    let diffuse=max(dot(normal,frame.light.xyz/lightLength),0.0)*frame.light.w*visibility;
    let illumination=select(vec3<f32>(1.0),frame.ambient.rgb+vec3<f32>(diffuse),material.lit>0.0);
    if (material.mode == 1.0 && texel.a < material.cutoff) { discard; }
    return vec4<f32>(texel.rgb*illumination,select(1.0,texel.a,material.mode==2.0));
}
struct ScreenVertex { @builtin(position) position: vec4<f32>, @location(0) uv: vec2<f32> };
@vertex fn screenVertex(@builtin(vertex_index) index: u32) -> ScreenVertex {
    let uv=vec2<f32>(f32((index<<1u)&2u),f32(index&2u));
    var o: ScreenVertex; o.position=vec4<f32>(uv*vec2<f32>(2.0,-2.0)+vec2<f32>(-1.0,1.0),0.0,1.0); o.uv=uv; return o;
}
@fragment fn skyFragment(v: ScreenVertex) -> @location(0) vec4<f32> {
    return vec4<f32>(mix(frame.skyTop.rgb,frame.skyBottom.rgb,clamp(v.uv.y,0.0,1.0)),1.0);
}
