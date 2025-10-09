// Ported from Alegruz/Screen-Space-ReSTIR-GI (github.com)

/* Native/GLRayTracer.h */
#pragma once
#include <GL/glew.h>
extern "C" 
{
	void InitReSTIRRayTracingNV();
	void BindReSTIRPipelineNV(GLuint pipeline);
	void TraceRaysNVWrapper(GLuint sbtBuffer, GLuint sbtOffset, GLuint sbtStride, GLuint width, GLuint height);
}

/* Native/GLRayTracer.cpp */
#include "GLRayTracer.h"
#include <GL/glew.h>

PFNGLCREATERAYTRACINGPIPELINESNVPROC glCreateRayTracingPipelinesNV = nullptr;
PFNGLBINDRAYTRACINGPIPELINENVPROC glBindRayTracingPipelineNV = nullptr;
PFNGLTRACERAYSNVPROC glTraceRaysNV = nullptr;

void InitReSTIRRayTracingNV()
{
	glCreateRayTracingPipelinesNV = (PFNGLCREATERAYTRACINGPIPELINESNVPROC)glewGetProcAddress("glCreateRayTracingPipelinesNV");
	glBindRayTracingPipelineNV = (PFNGLBINDRAYTRACINGPIPELINENVPROC)glewGetProcAddress("glBindRayTracingPipelineNV");
	glTraceRaysNV = (PFNGLTRACERAYSNVPROC)glewGetProcAddress("glTraceRaysNV");
}

void BindReSTIRPipelineNV(GLuint pipeline)
{
	glBindRayTracingPipelineNV(pipeline);
}

void TraceRaysNVWrapper(GLuint sbtBuffer, GLuint sbtOffset, GLuint sbtStride, GLuint width, GLuint height)
{
	// Use same SBT for raygen, miss, hit groups
	glTraceRaysNV(
		sbtBuffer, sbtOffset, sbtStride,
		sbtBuffer, sbtOffset, sbtStride,
		sbtBuffer, sbtOffset, sbtStride,
		width, height, 1);
}