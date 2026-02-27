#version 450
layout (location = 0) out vec4 OutColor;
layout (location = 0) flat in vec4 MatColor;
layout (location = 1) in vec2 FragUV;
void main()
{
    //Create a circle by discarding fragments outside a radius of 1.0
    //Using the length of the UV vector (from -1 to 1, center is (0,0))
    if (length(FragUV) > 1.0)
        discard;
    
    float alpha = smoothstep(1.0, 0.95, length(FragUV));
    OutColor = vec4(MatColor.rgb, MatColor.a * alpha);
}