// Ported from Alegruz/Screen-Space-ReSTIR-GI (github.com)

#include <GL/glew.h>

#ifndef GLEW_NV_ray_tracing
#define GLEW_NV_ray_tracing 0
#endif

static PFNGLCREATERAYTRACINGPIPELINESNVPROC g_glCreateRayTracingPipelinesNV = nullptr;
static PFNGLBINDRAYTRACINGPIPELINENVPROC g_glBindRayTracingPipelineNV = nullptr;
static PFNGLTRACERAYSNVPROC g_glTraceRaysNV = nullptr;
static bool g_restirRayTracingInitialized = false;

static void ResetRayTracingPointers()
{
	g_glCreateRayTracingPipelinesNV = nullptr;
	g_glBindRayTracingPipelineNV = nullptr;
	g_glTraceRaysNV = nullptr;
	g_restirRayTracingInitialized = false;
}

extern "C" __declspec(dllexport) bool InitReSTIRRayTracingNV()
{
	if (g_restirRayTracingInitialized)
		return true;

	if (!GLEW_NV_ray_tracing)
		return false;

	g_glCreateRayTracingPipelinesNV = reinterpret_cast<PFNGLCREATERAYTRACINGPIPELINESNVPROC>(glewGetProcAddress("glCreateRayTracingPipelinesNV"));
	g_glBindRayTracingPipelineNV = reinterpret_cast<PFNGLBINDRAYTRACINGPIPELINENVPROC>(glewGetProcAddress("glBindRayTracingPipelineNV"));
	g_glTraceRaysNV = reinterpret_cast<PFNGLTRACERAYSNVPROC>(glewGetProcAddress("glTraceRaysNV"));

	if (!g_glCreateRayTracingPipelinesNV || !g_glBindRayTracingPipelineNV || !g_glTraceRaysNV)
	{
		ResetRayTracingPointers();
		return false;
	}

	g_restirRayTracingInitialized = true;
	return true;
}

extern "C" __declspec(dllexport) bool BindReSTIRPipelineNV(GLuint pipeline)
{
	if (!g_restirRayTracingInitialized || g_glBindRayTracingPipelineNV == nullptr)
		return false;

	g_glBindRayTracingPipelineNV(pipeline);
	return true;
}

extern "C" __declspec(dllexport) bool TraceRaysNVWrapper(
	GLuint raygenSbtBuffer, GLuint raygenSbtOffset, GLuint raygenSbtStride,
	GLuint missSbtBuffer, GLuint missSbtOffset, GLuint missSbtStride,
	GLuint hitGroupSbtBuffer, GLuint hitGroupSbtOffset, GLuint hitGroupSbtStride,
	GLuint callableSbtBuffer, GLuint callableSbtOffset, GLuint callableSbtStride,
	GLuint width, GLuint height, GLuint depth)
{
	if (!g_restirRayTracingInitialized || g_glTraceRaysNV == nullptr)
		return false;

	g_glTraceRaysNV(
		raygenSbtBuffer, raygenSbtOffset, raygenSbtStride,
		missSbtBuffer, missSbtOffset, missSbtStride,
		hitGroupSbtBuffer, hitGroupSbtOffset, hitGroupSbtStride,
		callableSbtBuffer, callableSbtOffset, callableSbtStride,
		width, height, depth);

	return true;
}