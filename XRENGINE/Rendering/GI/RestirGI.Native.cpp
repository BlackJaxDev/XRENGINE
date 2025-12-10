// Ported from Alegruz/Screen-Space-ReSTIR-GI (github.com)

#include <Windows.h>
#include <GL/glew.h>

extern "C" void* glewGetProcAddress(const char* name);

#ifndef GLEW_NV_ray_tracing
#define GLEW_NV_ray_tracing 0
#endif

// Fallback signatures for GL_NV_ray_tracing function pointers in case the headers lack them.
using PFNGLBINDRAYTRACINGPIPELINENVPROC = void(__stdcall*)(unsigned int pipeline);
using PFNGLTRACERAYSNVPROC = void(__stdcall*)(
	unsigned int raygenSbtBuffer, unsigned int raygenSbtOffset, unsigned int raygenSbtStride,
	unsigned int missSbtBuffer, unsigned int missSbtOffset, unsigned int missSbtStride,
	unsigned int hitGroupSbtBuffer, unsigned int hitGroupSbtOffset, unsigned int hitGroupSbtStride,
	unsigned int callableSbtBuffer, unsigned int callableSbtOffset, unsigned int callableSbtStride,
	unsigned int width, unsigned int height, unsigned int depth);

static PFNGLBINDRAYTRACINGPIPELINENVPROC g_glBindRayTracingPipelineNV = nullptr;
static PFNGLTRACERAYSNVPROC g_glTraceRaysNV = nullptr;
static bool g_restirRayTracingInitialized = false;

extern "C" __declspec(dllexport) bool IsReSTIRRayTracingSupportedNV()
{
#if defined(GLEW_NV_ray_tracing)
	return GLEW_NV_ray_tracing != 0;
#else
	return false;
#endif
}

static void ResetRayTracingPointers()
{
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

	g_glBindRayTracingPipelineNV = reinterpret_cast<PFNGLBINDRAYTRACINGPIPELINENVPROC>(glewGetProcAddress("glBindRayTracingPipelineNV"));
	g_glTraceRaysNV = reinterpret_cast<PFNGLTRACERAYSNVPROC>(glewGetProcAddress("glTraceRaysNV"));

	if (!g_glBindRayTracingPipelineNV || !g_glTraceRaysNV)
	{
		ResetRayTracingPointers();
		return false;
	}

	g_restirRayTracingInitialized = true;
	return true;
}

extern "C" __declspec(dllexport) bool BindReSTIRPipelineNV(unsigned int pipeline)
{
	if (!g_restirRayTracingInitialized || g_glBindRayTracingPipelineNV == nullptr)
		return false;

	g_glBindRayTracingPipelineNV(pipeline);
	return true;
}

extern "C" __declspec(dllexport) bool TraceRaysNVWrapper(
	unsigned int raygenSbtBuffer, unsigned int raygenSbtOffset, unsigned int raygenSbtStride,
	unsigned int missSbtBuffer, unsigned int missSbtOffset, unsigned int missSbtStride,
	unsigned int hitGroupSbtBuffer, unsigned int hitGroupSbtOffset, unsigned int hitGroupSbtStride,
	unsigned int callableSbtBuffer, unsigned int callableSbtOffset, unsigned int callableSbtStride,
	unsigned int width, unsigned int height, unsigned int depth)
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