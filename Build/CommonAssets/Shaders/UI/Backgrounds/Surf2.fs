#version 450

layout (location = 0) out vec4 OutColor;

layout(location = 0) in vec3 FragPos;

uniform float RenderTime;
uniform float ScreenWidth;
uniform float ScreenHeight;
uniform vec4 UIXYWH; // UI coordinates (x, y, width, height)

//"Surf 2" by @XorDev
//https://www.shadertoy.com/view/3fKSzc

void main()
{
    // initialize ray-march variables
    float z = 0.0;
    float d = 0.0;
    vec4 col = vec4(0.0);
    float t = RenderTime;
    vec2 I = (FragPos.xy * 2.0 + 1.0) * vec2(ScreenWidth, ScreenHeight); // pixel coordinates in screen space
    float w = ScreenWidth; // width of the screen
    float h = ScreenHeight; // height of the screen

    // ray-march loop
    for (int iter = 1; iter <= 100; ++iter)
    {
        float i = float(iter);

        // sample along ray
        vec3 p = z * normalize(vec3(I * 2.0, 0.0) - vec3(w, h, w));

        // polar coordinates warp
        p = vec3(
            atan(p.y, p.x) * 2.0,
            p.z / 3.0,
            length(p.xy) - 6.0);

        // turbulence
        for (d = 1.0; d < 9.0; d += 1.0)
            p += sin(p.yzx * d - t + 0.2 * i) / d;

        // distance estimate
        d = 0.2 * length(vec4(
            p.z,
            0.1 * cos(p.x * 3.0) - 0.1,
            0.1 * cos(p.y * 3.0) - 0.1,
            0.1 * cos(p.z * 3.0) - 0.1));
        z += d;

        // accumulate color
        col += (1.0 + cos(i * 0.7 + vec4(6.0, 1.0, 2.0, 0.0))) / d / i;
    }

    // tanh tonemap (GLSL 330 has no built-in tanh)
    vec4 tmp = col * col / 900.0;
    OutColor = (exp(tmp * 2.0) - 1.0) / (exp(tmp * 2.0) + 1.0);
}
