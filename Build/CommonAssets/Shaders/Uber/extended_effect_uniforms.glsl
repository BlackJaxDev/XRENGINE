// Native effect, integration, and animation-facing uniforms.
// Defaults are deliberately neutral so a feature family may be prewarmed
// without changing rendering until an authored weight becomes non-zero.

//@category("Effects")
//@subcategory("Special Effects")
//@feature(id="extended-effects", name="Extended Effects", default=off, cost=high)
#ifndef XRENGINE_UBER_DISABLE_EXTENDED_EFFECTS
//@property(name="_UvTileDiscard", display="UV Tile Discard", mode=animated, range=[0,1], toggle=true)
uniform float _UvTileDiscard;
//@property(name="_UvTileDiscardGrid", display="UV Tile Grid", mode=static, default="vec2(1.0)")
uniform vec2 _UvTileDiscardGrid;
//@property(name="_UvTileDiscardRange", display="Visible Tile Range", mode=animated, default="vec2(0.0, 1.0)")
uniform vec2 _UvTileDiscardRange;
//@property(name="_FaceDiscard", display="Face Discard", mode=animated, enum="0:None|1:Front|2:Back")
uniform int _FaceDiscard;
//@property(name="_PathingStrength", display="Pathing", mode=animated, range=[0,1])
uniform float _PathingStrength;
uniform vec4 _PathingParams;
uniform vec4 _PathingColor;
//@property(name="_ProximityStrength", display="Proximity", mode=animated, range=[0,1])
uniform float _ProximityStrength;
uniform vec4 _ProximityParams;
uniform vec4 _ProximityColor;
//@property(name="_TouchGlowStrength", display="Depth/Touch Glow", mode=animated, range=[0,1])
uniform float _TouchGlowStrength;
uniform vec4 _TouchGlowParams;
uniform vec4 _TouchGlowColor;
//@property(name="_InternalParallaxStrength", display="Internal Parallax", mode=animated, range=[0,1])
uniform float _InternalParallaxStrength;
uniform vec4 _InternalParallaxParams;
//@property(name="_ProceduralMode", display="Procedural Mode", mode=animated, enum="0:Off|1:CRT|2:Gameboy|3:Voronoi|4:Truchet")
uniform int _ProceduralMode;
uniform vec4 _ProceduralParams;
uniform vec4 _ProceduralColor;
//@property(name="_VideoTexture", display="Video Texture", slot=texture)
uniform sampler2D _VideoTexture;
uniform vec4 _VideoTexture_ST;
//@property(name="_VideoBlend", display="Video Blend", mode=animated, range=[0,1])
uniform float _VideoBlend;
#endif

//@category("Integrations")
//@subcategory("AudioLink")
//@feature(id="audiolink", name="AudioLink Adapter", default=off, cost=medium)
#ifndef XRENGINE_UBER_DISABLE_AUDIOLINK
//@property(name="_AudioLinkTexture", display="AudioLink Data", slot=texture)
uniform sampler2D _AudioLinkTexture;
uniform vec4 _AudioLinkTextureSize;
uniform vec4 _AudioLinkTime;
//@property(name="_AudioLinkBand", display="Audio Band", mode=animated, range=[0,3])
uniform int _AudioLinkBand;
//@property(name="_AudioLinkStrength", display="Audio Strength", mode=animated, range=[0,8])
uniform float _AudioLinkStrength;
uniform vec4 _AudioLinkColor;
uniform vec4 _AudioLinkHistory;
#endif

//@category("Integrations")
//@subcategory("Environment")
//@feature(id="environment-lighting", name="LTCGI / Light Volumes", default=off, cost=medium)
#ifndef XRENGINE_UBER_DISABLE_ENVIRONMENT_LIGHTING
uniform vec4 _EnvironmentDiffuse;
uniform vec4 _EnvironmentSpecular;
uniform vec4 _EnvironmentBlacklight;
uniform int _EnvironmentFlags;
#endif

//@category("Integrations")
//@subcategory("View Context")
//@feature(id="view-context", name="Mirror / Camera View Context", default=off, cost=low)
#ifndef XRENGINE_UBER_DISABLE_VIEW_CONTEXT
uniform int _ViewFlags;
uniform int _ViewVisibilityMask;
uniform vec4 _ViewTint;
#endif

//@category("Geometry")
//@subcategory("Vertex Effects")
//@feature(id="vertex-effects", name="Vertex Effects", default=off, cost=medium)
#ifndef XRENGINE_UBER_DISABLE_VERTEX_EFFECTS
uniform float _VertexEffectsEnabled;
uniform vec3 _VertexManipulationLocalTranslation;
uniform vec3 _VertexManipulationLocalRotation;
uniform vec3 _VertexManipulationLocalRotationSpeed;
uniform vec3 _VertexManipulationLocalScale;
uniform vec3 _VertexManipulationWorldTranslation;
uniform float _VertexManipulationHeight;
uniform float _VertexRoundingEnabled;
uniform float _VertexRoundingDivision;
uniform float _VertexBarrelMode;
uniform float _VertexBarrelWidth;
uniform float _VertexBarrelAlpha;
uniform float _VertexBarrelHeight;
uniform float _VertexLookAtWeight;
uniform vec3 _VertexLookAtAxis;
uniform vec4 _VertexGlitch;
uniform vec4 _VertexWave;
uniform vec4 _VertexEquation;
uniform vec4 _VertexDepthBulge;
uniform vec4 _VertexColorPositionOffset;
uniform vec4 _VertexColorNormalOffset;
uniform vec4 _VertexConservativeBounds;
#endif
