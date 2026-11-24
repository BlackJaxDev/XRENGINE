// Engine-authored compatibility include for translated Unity programs.
// Translated programs draw without Unity instancing and with one view per draw.
// The macros keep instancing and stereo code compiling with single-instance meaning.
#ifndef UNITY_INSTANCING_INCLUDED
#define UNITY_INSTANCING_INCLUDED

#define UNITY_VERTEX_INPUT_INSTANCE_ID
#define UNITY_VERTEX_OUTPUT_STEREO
#define UNITY_SETUP_INSTANCE_ID(input)
#define UNITY_TRANSFER_INSTANCE_ID(input, output)
#define UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output)
#define UNITY_TRANSFER_VERTEX_OUTPUT_STEREO(input, output)
#define UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input)
#define UNITY_GET_INSTANCE_ID(input) 0
#define UNITY_INSTANCING_BUFFER_START(name)
#define UNITY_INSTANCING_BUFFER_END(name)
#define UNITY_INSTANCING_CBUFFER_START(name)
#define UNITY_INSTANCING_CBUFFER_END
#define UNITY_DEFINE_INSTANCED_PROP(type, name) type name;
#define UNITY_ACCESS_INSTANCED_PROP(buffer, name) name
#define UNITY_ACCESS_INSTANCED_PROP_ARRAY_ELEMENT(buffer, name, index) name[index]
#define DEFAULT_UNITY_VERTEX_INPUT_INSTANCE_ID
#define DEFAULT_UNITY_SETUP_INSTANCE_ID(input)
#define DEFAULT_UNITY_TRANSFER_INSTANCE_ID(input, output)
#define DEFAULT_UNITY_VERTEX_OUTPUT_STEREO
#define DEFAULT_UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output)
#define DEFAULT_UNITY_TRANSFER_VERTEX_OUTPUT_STEREO(input, output)
#define DEFAULT_UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input)
#define UNITY_DOTS_INSTANCING_START(name)
#define UNITY_DOTS_INSTANCING_END(name)

#endif // UNITY_INSTANCING_INCLUDED
