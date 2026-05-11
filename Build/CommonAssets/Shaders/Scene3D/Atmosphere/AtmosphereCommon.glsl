const float XRENGINE_ATMOSPHERE_PI = 3.14159265359f;
const int XRENGINE_ATMOSPHERE_SEGMENT_MISS = 0;
const int XRENGINE_ATMOSPHERE_SEGMENT_INSIDE = 1;
const int XRENGINE_ATMOSPHERE_SEGMENT_OUTSIDE = 2;
const int XRENGINE_ATMOSPHERE_SEGMENT_PLANET_OCCLUDED = 3;

struct AtmosphereStruct
{
  bool Enabled;
  bool RenderSky;
  bool AerialPerspective;
  int Quality;
  int ViewSamples;
  int OpticalDepthSamples;
  float MaxDistance;
  float JitterStrength;
  bool TemporalEnabled;
  int DebugMode;
  vec3 PlanetCenter;
  float GroundRadius;
  float AtmosphereHeight;
  float OuterRadius;
  vec3 SunDirection;
  float SunIntensity;
  vec3 SunColor;
  float RayleighScaleHeight;
  float MieScaleHeight;
  vec3 RayleighScattering;
  vec3 MieScattering;
  float MieAnisotropy;
  float ExposureScale;
  float GroundAlbedo;
};

uniform AtmosphereStruct Atmosphere;

float XRENGINE_Atmosphere_Saturate(float value)
{
  return clamp(value, 0.0f, 1.0f);
}

bool XRENGINE_Atmosphere_IntersectSphere(
  vec3 rayOrigin,
  vec3 rayDir,
  vec3 sphereCenter,
  float radius,
  out float tNear,
  out float tFar)
{
  vec3 localOrigin = rayOrigin - sphereCenter;
  float b = dot(localOrigin, rayDir);
  float c = dot(localOrigin, localOrigin) - radius * radius;
  float h = b * b - c;
  if (h < 0.0f)
  {
    tNear = 0.0f;
    tFar = 0.0f;
    return false;
  }

  h = sqrt(h);
  tNear = -b - h;
  tFar = -b + h;
  return tFar >= 0.0f;
}

bool XRENGINE_Atmosphere_IntersectPlanet(
  vec3 rayOrigin,
  vec3 rayDir,
  out float tPlanet)
{
  float tNear;
  float tFar;
  if (!XRENGINE_Atmosphere_IntersectSphere(rayOrigin, rayDir, Atmosphere.PlanetCenter, Atmosphere.GroundRadius, tNear, tFar))
  {
    tPlanet = 0.0f;
    return false;
  }

  tPlanet = tNear >= 0.0f ? tNear : tFar;
  return tPlanet >= 0.0f;
}

int XRENGINE_Atmosphere_ClassifySegment(
  vec3 rayOrigin,
  vec3 rayDir,
  float maxDistance,
  out float segmentStart,
  out float segmentEnd,
  out bool hitsPlanet)
{
  segmentStart = 0.0f;
  segmentEnd = 0.0f;
  hitsPlanet = false;

  float tAtmosphereNear;
  float tAtmosphereFar;
  if (!XRENGINE_Atmosphere_IntersectSphere(rayOrigin, rayDir, Atmosphere.PlanetCenter, Atmosphere.OuterRadius, tAtmosphereNear, tAtmosphereFar))
    return XRENGINE_ATMOSPHERE_SEGMENT_MISS;

  float finiteMaxDistance = max(maxDistance, 0.0f);
  segmentStart = max(tAtmosphereNear, 0.0f);
  segmentEnd = finiteMaxDistance > 0.0f ? min(tAtmosphereFar, finiteMaxDistance) : tAtmosphereFar;

  float tPlanet;
  if (XRENGINE_Atmosphere_IntersectPlanet(rayOrigin, rayDir, tPlanet))
  {
    if (tPlanet <= segmentStart)
      return XRENGINE_ATMOSPHERE_SEGMENT_PLANET_OCCLUDED;

    if (tPlanet < segmentEnd)
    {
      segmentEnd = tPlanet;
      hitsPlanet = true;
    }
  }

  if (segmentEnd <= segmentStart)
    return XRENGINE_ATMOSPHERE_SEGMENT_MISS;

  float distanceFromCenter = length(rayOrigin - Atmosphere.PlanetCenter);
  return distanceFromCenter <= Atmosphere.OuterRadius
    ? XRENGINE_ATMOSPHERE_SEGMENT_INSIDE
    : XRENGINE_ATMOSPHERE_SEGMENT_OUTSIDE;
}

float XRENGINE_Atmosphere_Altitude(vec3 worldPos)
{
  return max(length(worldPos - Atmosphere.PlanetCenter) - Atmosphere.GroundRadius, 0.0f);
}

float XRENGINE_Atmosphere_RayleighDensity(float altitude)
{
  return exp(-altitude / max(Atmosphere.RayleighScaleHeight, 1.0f));
}

float XRENGINE_Atmosphere_MieDensity(float altitude)
{
  return exp(-altitude / max(Atmosphere.MieScaleHeight, 1.0f));
}

float XRENGINE_Atmosphere_PhaseRayleigh(float cosTheta)
{
  return (3.0f / (16.0f * XRENGINE_ATMOSPHERE_PI)) * (1.0f + cosTheta * cosTheta);
}

float XRENGINE_Atmosphere_PhaseMie(float cosTheta, float anisotropy)
{
  float g = clamp(anisotropy, -0.99f, 0.99f);
  float g2 = g * g;
  float denom = pow(max(1.0f + g2 - 2.0f * g * cosTheta, 0.001f), 1.5f);
  return (1.0f - g2) / (4.0f * XRENGINE_ATMOSPHERE_PI * denom);
}

float XRENGINE_Atmosphere_OpticalDepthScaleApproximation(float cosZenith)
{
  float x = 1.0f - clamp(cosZenith, 0.0f, 1.0f);
  return exp(-0.00287f + x * (0.459f + x * (3.83f + x * (-6.80f + x * 5.25f))));
}

vec2 XRENGINE_Atmosphere_ReferenceOpticalDepth(vec3 origin, vec3 sunDir, int sampleCount)
{
  float tNear;
  float tFar;
  if (!XRENGINE_Atmosphere_IntersectSphere(origin, sunDir, Atmosphere.PlanetCenter, Atmosphere.OuterRadius, tNear, tFar))
    return vec2(0.0f);

  float tPlanet;
  if (XRENGINE_Atmosphere_IntersectPlanet(origin, sunDir, tPlanet) && tPlanet > 0.0f && tPlanet < tFar)
    return vec2(1.0e8f);

  int steps = clamp(sampleCount, 1, 32);
  float startT = max(tNear, 0.0f);
  float segmentLength = max(tFar - startT, 0.0f);
  float stepLength = segmentLength / float(steps);
  vec2 opticalDepth = vec2(0.0f);

  for (int i = 0; i < 32; ++i)
  {
    if (i >= steps)
      break;

    vec3 samplePos = origin + sunDir * (startT + (float(i) + 0.5f) * stepLength);
    float altitude = XRENGINE_Atmosphere_Altitude(samplePos);
    opticalDepth += vec2(
      XRENGINE_Atmosphere_RayleighDensity(altitude),
      XRENGINE_Atmosphere_MieDensity(altitude)) * stepLength;
  }

  return opticalDepth;
}

vec2 XRENGINE_Atmosphere_SunOpticalDepth(vec3 samplePos, vec3 sunDir, float altitude)
{
  if (Atmosphere.OpticalDepthSamples > 0)
    return XRENGINE_Atmosphere_ReferenceOpticalDepth(samplePos, sunDir, Atmosphere.OpticalDepthSamples);

  vec3 up = normalize(samplePos - Atmosphere.PlanetCenter);
  float cosZenith = dot(up, sunDir);
  float scale = XRENGINE_Atmosphere_OpticalDepthScaleApproximation(cosZenith);
  return vec2(
    XRENGINE_Atmosphere_RayleighDensity(altitude) * Atmosphere.RayleighScaleHeight,
    XRENGINE_Atmosphere_MieDensity(altitude) * Atmosphere.MieScaleHeight) * scale;
}

int XRENGINE_Atmosphere_ResolveSampleCount()
{
  int settingsSamples = clamp(Atmosphere.ViewSamples, 1, 64);
  if (Atmosphere.Quality == 0)
    return min(settingsSamples, 6);
  if (Atmosphere.Quality == 2)
    return max(settingsSamples, 16);
  if (Atmosphere.Quality == 3)
    return max(settingsSamples, 32);
  return settingsSamples;
}

vec4 XRENGINE_Atmosphere_ComputeScattering(
  vec3 rayOrigin,
  vec3 rayDir,
  float segmentEndDistance,
  bool shadeSurface)
{
  if (!Atmosphere.Enabled || Atmosphere.SunIntensity <= 0.0f || Atmosphere.ExposureScale <= 0.0f)
    return vec4(0.0f, 0.0f, 0.0f, 1.0f);

  float segmentStart;
  float segmentEnd;
  bool hitsPlanet;
  int segmentClass = XRENGINE_Atmosphere_ClassifySegment(
    rayOrigin,
    rayDir,
    segmentEndDistance,
    segmentStart,
    segmentEnd,
    hitsPlanet);

  if (segmentClass == XRENGINE_ATMOSPHERE_SEGMENT_MISS
    || segmentClass == XRENGINE_ATMOSPHERE_SEGMENT_PLANET_OCCLUDED)
  {
    return vec4(0.0f, 0.0f, 0.0f, 1.0f);
  }

  int steps = XRENGINE_Atmosphere_ResolveSampleCount();
  float segmentLength = max(segmentEnd - segmentStart, 0.0f);
  float stepLength = segmentLength / float(steps);
  vec3 sunDir = normalize(Atmosphere.SunDirection);
  float cosTheta = dot(rayDir, sunDir);
  float phaseR = XRENGINE_Atmosphere_PhaseRayleigh(cosTheta);
  float phaseM = XRENGINE_Atmosphere_PhaseMie(cosTheta, Atmosphere.MieAnisotropy);
  vec3 betaR = max(Atmosphere.RayleighScattering, vec3(0.0f));
  vec3 betaM = max(Atmosphere.MieScattering, vec3(0.0f));
  vec2 viewOpticalDepth = vec2(0.0f);
  vec3 scatter = vec3(0.0f);

  for (int i = 0; i < 64; ++i)
  {
    if (i >= steps)
      break;

    float sampleT = segmentStart + (float(i) + 0.5f) * stepLength;
    vec3 samplePos = rayOrigin + rayDir * sampleT;
    float altitude = XRENGINE_Atmosphere_Altitude(samplePos);
    float densityR = XRENGINE_Atmosphere_RayleighDensity(altitude);
    float densityM = XRENGINE_Atmosphere_MieDensity(altitude);
    viewOpticalDepth += vec2(densityR, densityM) * stepLength;

    vec2 sunOpticalDepth = XRENGINE_Atmosphere_SunOpticalDepth(samplePos, sunDir, altitude);
    vec3 attenuation = exp(-(betaR * (viewOpticalDepth.x + sunOpticalDepth.x)
      + betaM * (viewOpticalDepth.y + sunOpticalDepth.y)));
    vec3 inscatter = densityR * betaR * phaseR + densityM * betaM * phaseM;
    scatter += inscatter * attenuation * stepLength;
  }

  vec3 sunEnergy = max(Atmosphere.SunColor, vec3(0.0f)) * Atmosphere.SunIntensity;
  scatter *= sunEnergy * Atmosphere.ExposureScale;

  vec3 transmittance = exp(-(betaR * viewOpticalDepth.x + betaM * viewOpticalDepth.y));
  float scalarTransmittance = clamp(dot(transmittance, vec3(0.2126f, 0.7152f, 0.0722f)), 0.0f, 1.0f);

  if (shadeSurface && hitsPlanet)
    scatter += sunEnergy * Atmosphere.GroundAlbedo * 0.02f * scalarTransmittance;

  return vec4(scatter, scalarTransmittance);
}

vec4 XRENGINE_Atmosphere_DebugOutput(vec3 rayOrigin, vec3 rayDir, float segmentEndDistance, vec4 normalOutput)
{
  if (Atmosphere.DebugMode == 0)
    return normalOutput;

  float segmentStart;
  float segmentEnd;
  bool hitsPlanet;
  int segmentClass = XRENGINE_Atmosphere_ClassifySegment(
    rayOrigin,
    rayDir,
    segmentEndDistance,
    segmentStart,
    segmentEnd,
    hitsPlanet);

  if (Atmosphere.DebugMode == 1)
    return vec4(Atmosphere.Enabled ? vec3(0.0f, 1.0f, 0.0f) : vec3(1.0f, 0.0f, 0.0f), 0.0f);
  if (Atmosphere.DebugMode == 2)
    return vec4(segmentClass == XRENGINE_ATMOSPHERE_SEGMENT_MISS ? vec3(1.0f, 0.0f, 0.0f) : vec3(0.0f, 1.0f, 0.0f), 0.0f);
  if (Atmosphere.DebugMode == 3)
  {
    float altitude = XRENGINE_Atmosphere_Altitude(rayOrigin);
    return vec4(vec3(clamp(altitude / max(Atmosphere.AtmosphereHeight, 1.0f), 0.0f, 1.0f)), 0.0f);
  }
  if (Atmosphere.DebugMode == 4)
    return vec4(vec3(1.0f - normalOutput.a), 0.0f);
  if (Atmosphere.DebugMode == 5)
    return vec4(vec3(normalOutput.a), 0.0f);
  if (Atmosphere.DebugMode == 6)
    return vec4(normalOutput.rgb * vec3(1.0f, 0.35f, 0.15f), 0.0f);
  if (Atmosphere.DebugMode == 7)
    return vec4(normalOutput.rgb * vec3(0.35f, 0.45f, 1.0f), 0.0f);
  if (Atmosphere.DebugMode == 8)
    return vec4(vec3(max(dot(normalize(Atmosphere.SunDirection), normalize(rayDir)), 0.0f)), 0.0f);
  if (Atmosphere.DebugMode == 9)
    return vec4(segmentClass == XRENGINE_ATMOSPHERE_SEGMENT_INSIDE ? vec3(0.0f, 0.6f, 1.0f) : vec3(1.0f, 0.6f, 0.0f), 0.0f);

  return normalOutput;
}
