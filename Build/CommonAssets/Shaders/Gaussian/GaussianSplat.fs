#version 450 core

in VS_OUT
{
    vec4 color;
    mat2 invEllipseBasis;
    float pointRadiusPixels;
} fsIn;

layout(location = 0) out vec4 FragColor;

float EvaluateGaussian(vec2 uv)
{
    // Gaussian falloff with standard deviation of 0.5 in UV space.
    float r2 = dot(uv, uv);
    return exp(-r2 * 2.0);
}

void main()
{
    vec2 centered = gl_PointCoord * 2.0 - 1.0;
    vec2 offsetPixels = centered * fsIn.pointRadiusPixels;
    vec2 uv = fsIn.invEllipseBasis * offsetPixels;

    float density = EvaluateGaussian(uv);
    if (density < 0.001)
        discard;

    vec4 baseColor = fsIn.color;
    float alpha = baseColor.a * density;
    vec3 rgb = baseColor.rgb;
    FragColor = vec4(rgb, alpha);
}
