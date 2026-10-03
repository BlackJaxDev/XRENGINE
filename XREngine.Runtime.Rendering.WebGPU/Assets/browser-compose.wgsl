struct Frame { shadow:mat4x4<f32>,light:vec4<f32>,ambient:vec4<f32>,skyTop:vec4<f32>,skyBottom:vec4<f32>,output:vec4<f32>,options:vec4<f32> };
@group(0) @binding(0) var<uniform> frame: Frame;
@group(1) @binding(0) var color: texture_2d<f32>;
@group(1) @binding(1) var colorSampler: sampler;
struct VertexOutput { @builtin(position) position:vec4<f32>,@location(0) uv:vec2<f32>,@location(1) tint:vec4<f32> };
@vertex fn screenVertex(@builtin(vertex_index) index:u32)->VertexOutput {
    let uv=vec2<f32>(f32((index<<1u)&2u),f32(index&2u));
    var o:VertexOutput; o.position=vec4<f32>(uv*vec2<f32>(2.0,-2.0)+vec2<f32>(-1.0,1.0),0.0,1.0);o.uv=uv;o.tint=vec4<f32>(1.0);return o;
}
fn encodeSrgb(value:vec3<f32>)->vec3<f32> {
    let c=max(value,vec3<f32>(0.0));
    return select(1.055*pow(c,vec3<f32>(1.0/2.4))-0.055,c*12.92,c<=vec3<f32>(0.0031308));
}
@fragment fn presentFragment(v:VertexOutput)->@location(0) vec4<f32> {
    var c=textureSample(color,colorSampler,v.uv).rgb*frame.ambient.w;
    if (frame.options.y>0.0) { c=c/(vec3<f32>(1.0)+c); }
    return vec4<f32>(clamp(c,vec3<f32>(0.0),vec3<f32>(1.0)),1.0);
}
@vertex fn uiVertex(@builtin(vertex_index) index:u32,@location(0) rectangle:vec4<f32>,@location(1) uvRectangle:vec4<f32>,@location(2) tint:vec4<f32>)->VertexOutput {
    let corners=array<vec2<f32>,6>(vec2<f32>(0,0),vec2<f32>(1,0),vec2<f32>(0,1),vec2<f32>(0,1),vec2<f32>(1,0),vec2<f32>(1,1));
    let corner=corners[index];let point=rectangle.xy+rectangle.zw*corner;
    var o:VertexOutput;o.position=vec4<f32>(point/frame.output.xy*vec2<f32>(2,-2)+vec2<f32>(-1,1),0,1);
    o.uv=uvRectangle.xy+uvRectangle.zw*corner;o.tint=tint;return o;
}
@fragment fn uiFragment(v:VertexOutput)->@location(0) vec4<f32> {
    let c=textureSample(color,colorSampler,v.uv)*v.tint;
    return c;
}
@fragment fn encodeFragment(v:VertexOutput)->@location(0) vec4<f32> {
    return vec4<f32>(encodeSrgb(textureSample(color,colorSampler,v.uv).rgb),1.0);
}
