#pragma once

#include <cstdint>

#if defined(_WIN32)
#if defined(MOTIONBRICKSBRIDGE_EXPORTS)
#define MOTIONBRICKS_BRIDGE_API extern "C" __declspec(dllexport)
#else
#define MOTIONBRICKS_BRIDGE_API extern "C" __declspec(dllimport)
#endif
#else
#define MOTIONBRICKS_BRIDGE_API extern "C"
#endif

enum MBBridgeResult : int32_t
{
    MBBridgeResult_Success = 0,
    MBBridgeResult_NoData = 1,
    MBBridgeResult_InvalidArgument = 2,
    MBBridgeResult_BackendUnavailable = 3,
    MBBridgeResult_ContractMismatch = 4,
    MBBridgeResult_EngineBuildFailed = 5,
    MBBridgeResult_InferenceFailed = 6,
    MBBridgeResult_InternalError = 7,
};

struct MBBridgeSessionConfig
{
    const char* OnnxModelPath;
    const char* PlanCacheDir;
    int32_t EnableFp16;
    int32_t EnableTf32;
    int32_t BuilderOptimizationLevel;
    int32_t ForceRebuild;
    int64_t WorkspaceSizeBytes;
};

struct MBSomaInputView
{
    const float* GlobalRootValues;
    int32_t GlobalRootValuesCount;
    const int32_t* HasGlobalRootValues;
    int32_t HasGlobalRootValuesCount;
    const float* LocalRootValues;
    int32_t LocalRootValuesCount;
    const int32_t* HasLocalRootValues;
    int32_t HasLocalRootValuesCount;
    const float* LocalPoses;
    int32_t LocalPosesCount;
    const int32_t* HasLocalPoses;
    int32_t HasLocalPosesCount;
    const int32_t* AllowedPredNumTokens;
    int32_t AllowedPredNumTokensCount;
};

struct MBSomaOutputView
{
    float* PredGlobalMotions;
    int32_t PredGlobalMotionsCount;
    int32_t* PredNumTokens;
    int32_t PredNumTokensCount;
};

MOTIONBRICKS_BRIDGE_API int32_t MBBridge_IsBackendAvailable();
MOTIONBRICKS_BRIDGE_API int32_t MBBridge_GetTensorRtVersion();
MOTIONBRICKS_BRIDGE_API MBBridgeResult MBBridge_CreateSession(void** outSession);
MOTIONBRICKS_BRIDGE_API void MBBridge_DestroySession(void* session);
MOTIONBRICKS_BRIDGE_API MBBridgeResult MBBridge_ConfigureSession(void* session, const MBBridgeSessionConfig* config);
MOTIONBRICKS_BRIDGE_API MBBridgeResult MBBridge_BuildOrLoadEngine(void* session, int32_t* outLoadedFromCache);
MOTIONBRICKS_BRIDGE_API MBBridgeResult MBBridge_RunSomaInference(void* session, const MBSomaInputView* input, MBSomaOutputView* output);
MOTIONBRICKS_BRIDGE_API MBBridgeResult MBBridge_GetLastError(void* session, char* utf8Buffer, int32_t bufferCapacity, int32_t* outRequiredBytes);
