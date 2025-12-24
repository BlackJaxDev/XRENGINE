#version 450

layout (location = 0) out vec4 OutColor;

uniform vec4 FillColor;
uniform float StippleScale;
uniform float StippleThickness;

void main()
{
    // Get screen-space coordinates for stippling
    vec2 screenPos = gl_FragCoord.xy;
    
    // Diagonal stipple pattern (screen-space) at 45 degrees
    // The diagonal line equation: x + y = constant
    float diagonal = screenPos.x + screenPos.y;
    
    // Create repeating pattern along diagonal
    float pattern = mod(diagonal, StippleScale);
    
    // Create dotted pattern - half on, half off with smooth edges
    float halfScale = StippleScale * 0.5;
    float dist = abs(pattern - halfScale);
    
    // Smooth step for anti-aliased stippling
    float stipple = smoothstep(StippleThickness - 1.0, StippleThickness + 1.0, dist);
    
    // Discard fully transparent pixels
    float alpha = FillColor.a * (1.0 - stipple * 0.85);
    if (alpha < 0.01)
        discard;
    
    OutColor = vec4(FillColor.rgb, alpha);
}
