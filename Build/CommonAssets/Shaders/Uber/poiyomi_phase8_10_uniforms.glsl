// Poiyomi phase 8-10 effect, adapter, and animation-facing uniforms.
// Defaults are deliberately neutral so a feature family may be prewarmed
// without changing rendering until an authored weight becomes non-zero.

//@category("Poiyomi")
//@subcategory("Special Effects")
//@feature(id="poiyomi-special-effects", name="Poiyomi Special Effects", default=off, cost=high)
#ifndef XRENGINE_UBER_DISABLE_POIYOMI_SPECIAL_EFFECTS
//@property(name="_PoiUvDiscard", display="UV Tile Discard", mode=animated, range=[0,1], toggle=true)
uniform float _PoiUvDiscard;
//@property(name="_PoiUvDiscardGrid", display="UV Tile Grid", mode=static, default="vec2(1.0)")
uniform vec2 _PoiUvDiscardGrid;
//@property(name="_PoiUvDiscardRange", display="Visible Tile Range", mode=animated, default="vec2(0.0, 1.0)")
uniform vec2 _PoiUvDiscardRange;
//@property(name="_PoiFaceDiscard", display="Face Discard", mode=animated, enum="0:None|1:Front|2:Back")
uniform int _PoiFaceDiscard;
//@property(name="_PoiPathing", display="Pathing", mode=animated, range=[0,1])
uniform float _PoiPathing;
uniform vec4 _PoiPathingParams;
uniform vec4 _PoiPathingColor;
//@property(name="_PoiProximity", display="Proximity", mode=animated, range=[0,1])
uniform float _PoiProximity;
uniform vec4 _PoiProximityParams;
uniform vec4 _PoiProximityColor;
//@property(name="_PoiTouchGlow", display="Depth/Touch Glow", mode=animated, range=[0,1])
uniform float _PoiTouchGlow;
uniform vec4 _PoiTouchGlowParams;
uniform vec4 _PoiTouchGlowColor;
//@property(name="_PoiInternalParallax", display="Internal Parallax", mode=animated, range=[0,1])
uniform float _PoiInternalParallax;
uniform vec4 _PoiInternalParallaxParams;
//@property(name="_PoiProceduralMode", display="Procedural Mode", mode=animated, enum="0:Off|1:CRT|2:Gameboy|3:Voronoi|4:Truchet")
uniform int _PoiProceduralMode;
uniform vec4 _PoiProceduralParams;
uniform vec4 _PoiProceduralColor;
//@property(name="_PoiVideoTexture", display="Video Texture", slot=texture)
uniform sampler2D _PoiVideoTexture;
uniform vec4 _PoiVideoTexture_ST;
//@property(name="_PoiVideoBlend", display="Video Blend", mode=animated, range=[0,1])
uniform float _PoiVideoBlend;
#endif

//@category("Poiyomi")
//@subcategory("AudioLink")
//@feature(id="poiyomi-audiolink", name="AudioLink Adapter", default=off, cost=medium)
#ifndef XRENGINE_UBER_DISABLE_POIYOMI_AUDIOLINK
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

//@category("Poiyomi")
//@subcategory("Environment")
//@feature(id="poiyomi-environment-adapters", name="LTCGI / Light Volumes", default=off, cost=medium)
#ifndef XRENGINE_UBER_DISABLE_POIYOMI_ENVIRONMENT_ADAPTERS
uniform vec4 _PoiEnvironmentLight;
uniform vec4 _PoiEnvironmentSpecular;
uniform vec4 _PoiBlacklight;
uniform int _PoiEnvironmentFlags;
#endif

//@category("Poiyomi")
//@subcategory("View Context")
//@feature(id="poiyomi-view-context", name="Mirror / Camera View Context", default=off, cost=low)
#ifndef XRENGINE_UBER_DISABLE_POIYOMI_VIEW_CONTEXT
uniform int _PoiViewFlags;
uniform int _PoiViewVisibilityMask;
uniform vec4 _PoiViewTint;
#endif

//@category("Poiyomi")
//@subcategory("Vertex Effects")
//@feature(id="poiyomi-vertex-effects", name="Poiyomi Vertex Effects", default=off, cost=medium)
#ifndef XRENGINE_UBER_DISABLE_POIYOMI_VERTEX_EFFECTS
uniform float _PoiVertexEffectsEnabled;
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
uniform float _PoiLookAtWeight;
uniform vec3 _PoiLookAtAxis;
uniform vec4 _PoiVertexGlitch;
uniform vec4 _PoiUzumore;
uniform vec4 _PoiNaturalEquation;
uniform vec4 _PoiDepthBulge;
uniform vec4 _PoiVertexColorPosition;
uniform vec4 _PoiVertexColorNormal;
uniform vec4 _PoiConservativeBounds;
#endif

