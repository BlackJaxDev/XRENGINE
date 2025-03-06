#version 330 core
in vec2 vUV;
out vec4 FragColor;

uniform samplerCube uCubemap;

// Converts a 2D UV coordinate (in our special layout) into a 3D direction.
// The layout is assumed to have two zones:
//   - Upper hemisphere (z >= 0) mapped into 4 inner triangles (touching near the texture center)
//   - Lower hemisphere (z < 0) mapped into 4 outer triangles (extending to the texture edges)
// Each set is divided into 4 radial quadrants.
// This function implements a custom mapping. Adjust parameters as needed.
vec3 ConvertUVToDirection(vec2 uv)
{
    // Center of texture
    vec2 center = vec2(0.5, 0.5);
    // Offset from center
    vec2 d = uv - center;
    // Polar coordinates (angle in [0,2pi))
    float theta = atan(d.y, d.x);
    if(theta < 0.0)
        theta += 2.0 * 3.14159265359;
    
    // Distance from center normalized to half texture size
    float r = length(d) / 0.5;  // r in [0, ~sqrt2] but our layout uses r < 1 for inner zone

    // Determine hemisphere based on radial distance.
    // We'll assume inner circle (r <= 1.0) is for upper hemisphere and outer ring (r > 1.0) for lower hemisphere.
    bool upperHemisphere = (r <= 1.0);

    // Determine quadrant index (0 to 3) for a full circle, each spanning pi/2.
    float quadrantSize = 3.14159265359 / 2.0;
    int quadrant = int(theta / quadrantSize);
    float baseAngle = float(quadrant) * quadrantSize;
    float localAngle = theta - baseAngle;  // in [0, pi/2]

    // Compress the local angle into the triangle coordinate.
    // For a right triangle with angle pi/2, use sine scaling.
    // The effective "x" within the triangle from 0 (apex) to 1 (base edge)
    float triFactor = localAngle / quadrantSize; // normalized within quadrant

    // Compute elevation angle.
    // Upper hemisphere: elevation from 0 (zenith) to pi/2
    // Lower hemisphere: elevation from pi/2 to pi (nadir)
    float elev;
    if(upperHemisphere)
    {
        // Map radial coordinate (r from 0 to 1) to elevation [0, pi/2]
        elev = mix(0.0, 3.14159265359/2.0, r);
    }
    else
    {
        // For lower hemisphere, r is in [1, sqrt2] since max distance is corner.
        // Normalize: subtract 1 and divide by (sqrt2 - 1)
        float rLower = (r - 1.0) / (1.41421356237 - 1.0);
        elev = mix(3.14159265359/2.0, 3.14159265359, clamp(rLower, 0.0, 1.0));
    }

    // The azimuth is given by baseAngle plus a refinement from the triangle compression.
    float azim = baseAngle + triFactor * quadrantSize;

    // Convert spherical coordinates to Cartesian direction.
    // Theta: azimuth, Elevation: angle from top (0) downward.
    float sinElev = sin(elev);
    vec3 dir;
    dir.x = sinElev * cos(azim);
    dir.y = sinElev * sin(azim);
    dir.z = cos(elev);

    return dir;
}

void main()
{
    vec3 sampleDir = ConvertUVToDirection(vUV);
    vec4 color = texture(uCubemap, sampleDir);
    FragColor = color;
}