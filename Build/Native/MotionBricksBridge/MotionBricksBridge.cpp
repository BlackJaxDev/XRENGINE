#include "MotionBricksBridge.h"

#include <algorithm>
#include <array>
#include <cctype>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <memory>
#include <mutex>
#include <sstream>
#include <string>
#include <string_view>
#include <vector>

#if defined(MOTIONBRICKS_BRIDGE_USE_TENSORRT)
#include <cuda_runtime_api.h>
#include <NvInfer.h>
#include <NvOnnxParser.h>
#endif

namespace
{
    constexpr int32_t kBatchSize = 1;
    constexpr int32_t kContextAndTargetFrames = 8;
    constexpr int32_t kGlobalRootValuesCount = kBatchSize * kContextAndTargetFrames * 5;
    constexpr int32_t kLocalRootValuesCount = kBatchSize * kContextAndTargetFrames * 4;
    constexpr int32_t kLocalPosesCount = kBatchSize * kContextAndTargetFrames * 267;
    constexpr int32_t kMaskCount = kBatchSize * kContextAndTargetFrames;
    constexpr int32_t kAllowedTokensCount = kBatchSize * 11;
    constexpr int32_t kPredGlobalMotionsCount = kBatchSize * 64 * 366;
    constexpr int32_t kPredNumTokensCount = kBatchSize;
    constexpr int32_t kMinTokenCount = 6;
    constexpr int32_t kMaxTokenCount = 16;

    MBBridgeResult CopyUtf8(const std::string& text, char* buffer, int32_t bufferCapacity, int32_t* outRequiredBytes)
    {
        int32_t requiredBytes = static_cast<int32_t>(text.size()) + 1;
        if (outRequiredBytes != nullptr)
            *outRequiredBytes = requiredBytes;

        if (buffer == nullptr || bufferCapacity <= 0)
            return MBBridgeResult_NoData;

        int32_t bytesToCopy = std::min(requiredBytes, bufferCapacity);
        std::memcpy(buffer, text.c_str(), static_cast<size_t>(bytesToCopy));
        if (bytesToCopy == bufferCapacity)
            buffer[bufferCapacity - 1] = '\0';
        return MBBridgeResult_Success;
    }

    bool IsNullOrWhiteSpace(const char* value)
    {
        if (value == nullptr)
            return true;

        while (*value != '\0')
        {
            if (!std::isspace(static_cast<unsigned char>(*value)))
                return false;
            ++value;
        }
        return true;
    }

    bool ValidateInputOutputViews(const MBSomaInputView* input, const MBSomaOutputView* output, std::string& error)
    {
        if (input == nullptr || output == nullptr)
        {
            error = "Input and output views are required.";
            return false;
        }

        if (input->GlobalRootValues == nullptr || input->GlobalRootValuesCount < kGlobalRootValuesCount)
        {
            error = "global_root_values must supply at least 40 float32 elements.";
            return false;
        }

        if (input->HasGlobalRootValues == nullptr || input->HasGlobalRootValuesCount < kMaskCount)
        {
            error = "has_global_root_values must supply at least 8 int32 elements.";
            return false;
        }

        if (input->LocalRootValues == nullptr || input->LocalRootValuesCount < kLocalRootValuesCount)
        {
            error = "local_root_values must supply at least 32 float32 elements.";
            return false;
        }

        if (input->HasLocalRootValues == nullptr || input->HasLocalRootValuesCount < kMaskCount)
        {
            error = "has_local_root_values must supply at least 8 int32 elements.";
            return false;
        }

        if (input->LocalPoses == nullptr || input->LocalPosesCount < kLocalPosesCount)
        {
            error = "local_poses must supply at least 2136 float32 elements.";
            return false;
        }

        if (input->HasLocalPoses == nullptr || input->HasLocalPosesCount < kMaskCount)
        {
            error = "has_local_poses must supply at least 8 int32 elements.";
            return false;
        }

        if (input->AllowedPredNumTokens == nullptr || input->AllowedPredNumTokensCount < kAllowedTokensCount)
        {
            error = "allowed_pred_num_tokens must supply at least 11 int32 elements.";
            return false;
        }

        if (output->PredGlobalMotions == nullptr || output->PredGlobalMotionsCount < kPredGlobalMotionsCount)
        {
            error = "pred_global_motions must supply at least 23424 float32 elements.";
            return false;
        }

        if (output->PredNumTokens == nullptr || output->PredNumTokensCount < kPredNumTokensCount)
        {
            error = "pred_num_tokens must supply at least 1 int32 element.";
            return false;
        }

        return true;
    }

#if defined(MOTIONBRICKS_BRIDGE_USE_TENSORRT)
    uint64_t HashBytesFnv1a64(uint64_t seed, const void* data, size_t size)
    {
        const auto* bytes = static_cast<const uint8_t*>(data);
        uint64_t hash = seed;
        for (size_t i = 0; i < size; ++i)
        {
            hash ^= bytes[i];
            hash *= 1099511628211ULL;
        }
        return hash;
    }

    bool HashFileFnv1a64(const std::filesystem::path& path, uint64_t& outHash, uint64_t& outSize, std::string& error)
    {
        std::ifstream stream(path, std::ios::binary);
        if (!stream)
        {
            error = "Failed to open ONNX model file: " + path.string();
            return false;
        }

        std::array<char, 1 << 20> buffer{};
        uint64_t hash = 14695981039346656037ULL;
        uint64_t totalSize = 0;
        while (stream)
        {
            stream.read(buffer.data(), static_cast<std::streamsize>(buffer.size()));
            std::streamsize count = stream.gcount();
            if (count > 0)
            {
                hash = HashBytesFnv1a64(hash, buffer.data(), static_cast<size_t>(count));
                totalSize += static_cast<uint64_t>(count);
            }
        }

        if (!stream.eof() && stream.fail())
        {
            error = "Read error while hashing ONNX model file: " + path.string();
            return false;
        }

        outHash = hash;
        outSize = totalSize;
        return true;
    }

    class BridgeTensorRtLogger final : public nvinfer1::ILogger
    {
    public:
        void Clear()
        {
            std::lock_guard<std::mutex> lock(_sync);
            _lastDiagnostic.clear();
        }

        std::string GetLastDiagnostic() const
        {
            std::lock_guard<std::mutex> lock(_sync);
            return _lastDiagnostic;
        }

        void log(Severity severity, const char* msg) noexcept override
        {
            if (msg == nullptr)
                return;

            if (severity <= Severity::kWARNING)
            {
                std::lock_guard<std::mutex> lock(_sync);
                _lastDiagnostic = msg;
            }
        }

    private:
        mutable std::mutex _sync;
        std::string _lastDiagnostic;
    };

    struct ExpectedTensorSpec
    {
        const char* Name;
        nvinfer1::TensorIOMode Mode;
        nvinfer1::DataType DataType;
        int32_t NbDims;
        std::array<int64_t, 4> Dims;
        size_t ByteSize;
    };

    constexpr std::array<ExpectedTensorSpec, 9> kSomaTensorSpecs =
    {{
        { "global_root_values",      nvinfer1::TensorIOMode::kINPUT,  nvinfer1::DataType::kFLOAT, 3, { 1, 8, 5, 0 },    static_cast<size_t>(kGlobalRootValuesCount) * sizeof(float) },
        { "has_global_root_values",  nvinfer1::TensorIOMode::kINPUT,  nvinfer1::DataType::kINT32, 2, { 1, 8, 0, 0 },    static_cast<size_t>(kMaskCount) * sizeof(int32_t) },
        { "local_root_values",       nvinfer1::TensorIOMode::kINPUT,  nvinfer1::DataType::kFLOAT, 3, { 1, 8, 4, 0 },    static_cast<size_t>(kLocalRootValuesCount) * sizeof(float) },
        { "has_local_root_values",   nvinfer1::TensorIOMode::kINPUT,  nvinfer1::DataType::kINT32, 2, { 1, 8, 0, 0 },    static_cast<size_t>(kMaskCount) * sizeof(int32_t) },
        { "local_poses",             nvinfer1::TensorIOMode::kINPUT,  nvinfer1::DataType::kFLOAT, 3, { 1, 8, 267, 0 },  static_cast<size_t>(kLocalPosesCount) * sizeof(float) },
        { "has_local_poses",         nvinfer1::TensorIOMode::kINPUT,  nvinfer1::DataType::kINT32, 2, { 1, 8, 0, 0 },    static_cast<size_t>(kMaskCount) * sizeof(int32_t) },
        { "allowed_pred_num_tokens", nvinfer1::TensorIOMode::kINPUT,  nvinfer1::DataType::kINT32, 2, { 1, 11, 0, 0 },   static_cast<size_t>(kAllowedTokensCount) * sizeof(int32_t) },
        { "pred_global_motions",     nvinfer1::TensorIOMode::kOUTPUT, nvinfer1::DataType::kFLOAT, 3, { 1, 64, 366, 0 }, static_cast<size_t>(kPredGlobalMotionsCount) * sizeof(float) },
        { "pred_num_tokens",         nvinfer1::TensorIOMode::kOUTPUT, nvinfer1::DataType::kINT32, 1, { 1, 0, 0, 0 },    static_cast<size_t>(kPredNumTokensCount) * sizeof(int32_t) },
    }};

    bool FormatCudaError(cudaError_t status, const char* operation, std::string& error)
    {
        if (status == cudaSuccess)
            return true;

        std::ostringstream oss;
        oss << operation << " failed with CUDA error " << static_cast<int32_t>(status)
            << " (" << cudaGetErrorString(status) << ").";
        error = oss.str();
        return false;
    }

    bool SpecializeStaticBatch(nvinfer1::INetworkDefinition& network, std::string& error)
    {
        int32_t inputCount = network.getNbInputs();
        for (int32_t i = 0; i < inputCount; ++i)
        {
            nvinfer1::ITensor* tensor = network.getInput(i);
            nvinfer1::Dims dims = tensor->getDimensions();
            bool hasDynamic = false;
            for (int32_t d = 0; d < dims.nbDims; ++d)
                if (dims.d[d] < 0)
                    hasDynamic = true;

            if (!hasDynamic)
                continue;

            if (dims.nbDims <= 0 || dims.d[0] != -1)
            {
                error = std::string("Only the leading batch dimension may be dynamic for tensor '") + tensor->getName() + "'.";
                return false;
            }

            for (int32_t d = 1; d < dims.nbDims; ++d)
            {
                if (dims.d[d] <= 0)
                {
                    error = std::string("Unexpected dynamic non-batch dimension on tensor '") + tensor->getName() + "'.";
                    return false;
                }
            }

            dims.d[0] = kBatchSize;
            tensor->setDimensions(dims);
        }

        return true;
    }

    bool ValidateEngineContract(const nvinfer1::ICudaEngine& engine, std::string& error)
    {
        int32_t ioCount = engine.getNbIOTensors();
        if (ioCount != static_cast<int32_t>(kSomaTensorSpecs.size()))
        {
            std::ostringstream oss;
            oss << "SOMA TensorRT engine tensor count mismatch. Expected " << kSomaTensorSpecs.size()
                << ", got " << ioCount << ".";
            error = oss.str();
            return false;
        }

        for (const ExpectedTensorSpec& expected : kSomaTensorSpecs)
        {
            nvinfer1::TensorIOMode mode = engine.getTensorIOMode(expected.Name);
            if (mode != expected.Mode)
            {
                error = std::string("Tensor '") + expected.Name + "' has an unexpected I/O mode or is missing.";
                return false;
            }

            nvinfer1::DataType dtype = engine.getTensorDataType(expected.Name);
            if (dtype != expected.DataType)
            {
                error = std::string("Tensor '") + expected.Name + "' has an unexpected data type.";
                return false;
            }

            nvinfer1::Dims dims = engine.getTensorShape(expected.Name);
            if (dims.nbDims != expected.NbDims)
            {
                error = std::string("Tensor '") + expected.Name + "' has an unexpected rank.";
                return false;
            }

            for (int32_t d = 0; d < expected.NbDims; ++d)
            {
                if (dims.d[d] != expected.Dims[static_cast<size_t>(d)])
                {
                    error = std::string("Tensor '") + expected.Name + "' has an unexpected dimension size.";
                    return false;
                }
            }
        }

        return true;
    }
#endif
}

struct MBBridgeSession
{
    std::mutex Sync;
    std::string LastError;
    bool IsConfigured = false;
    std::string OnnxModelPath;
    std::string PlanCacheDir;
    bool EnableFp16 = true;
    bool EnableTf32 = true;
    int32_t BuilderOptimizationLevel = 3;
    bool ForceRebuild = false;
    int64_t WorkspaceSizeBytes = 0;

#if defined(MOTIONBRICKS_BRIDGE_USE_TENSORRT)
    BridgeTensorRtLogger Logger;
    std::unique_ptr<nvinfer1::IRuntime> Runtime;
    std::unique_ptr<nvinfer1::ICudaEngine> Engine;
    std::unique_ptr<nvinfer1::IExecutionContext> Context;
    cudaStream_t Stream = nullptr;
    std::array<void*, 9> DeviceBuffers{};
    bool EngineReady = false;

    void ReleaseEngineResources()
    {
        EngineReady = false;
        Context.reset();
        Engine.reset();
        Runtime.reset();

        for (void*& ptr : DeviceBuffers)
        {
            if (ptr != nullptr)
            {
                cudaFree(ptr);
                ptr = nullptr;
            }
        }

        if (Stream != nullptr)
        {
            cudaStreamDestroy(Stream);
            Stream = nullptr;
        }
    }

    ~MBBridgeSession()
    {
        ReleaseEngineResources();
    }
#endif
};

MOTIONBRICKS_BRIDGE_API int32_t MBBridge_IsBackendAvailable()
{
#if defined(MOTIONBRICKS_BRIDGE_USE_TENSORRT)
    return 1;
#else
    return 0;
#endif
}

MOTIONBRICKS_BRIDGE_API int32_t MBBridge_GetTensorRtVersion()
{
#if defined(MOTIONBRICKS_BRIDGE_USE_TENSORRT)
    return static_cast<int32_t>(getInferLibVersion());
#else
    return 0;
#endif
}

MOTIONBRICKS_BRIDGE_API MBBridgeResult MBBridge_CreateSession(void** outSession)
{
    if (outSession == nullptr)
        return MBBridgeResult_InvalidArgument;

    *outSession = nullptr;
    try
    {
        auto* session = new MBBridgeSession();
        *outSession = session;
        return MBBridgeResult_Success;
    }
    catch (...)
    {
        return MBBridgeResult_InternalError;
    }
}

MOTIONBRICKS_BRIDGE_API void MBBridge_DestroySession(void* sessionHandle)
{
    auto* session = static_cast<MBBridgeSession*>(sessionHandle);
    delete session;
}

MOTIONBRICKS_BRIDGE_API MBBridgeResult MBBridge_ConfigureSession(void* sessionHandle, const MBBridgeSessionConfig* config)
{
    auto* session = static_cast<MBBridgeSession*>(sessionHandle);
    if (session == nullptr || config == nullptr)
        return MBBridgeResult_InvalidArgument;

    std::lock_guard<std::mutex> lock(session->Sync);
    session->LastError.clear();

    if (IsNullOrWhiteSpace(config->OnnxModelPath))
    {
        session->LastError = "OnnxModelPath is required.";
        return MBBridgeResult_InvalidArgument;
    }

    if (config->BuilderOptimizationLevel < 0 || config->BuilderOptimizationLevel > 5)
    {
        session->LastError = "BuilderOptimizationLevel must be between 0 and 5.";
        return MBBridgeResult_InvalidArgument;
    }

    if (config->WorkspaceSizeBytes < 0)
    {
        session->LastError = "WorkspaceSizeBytes cannot be negative.";
        return MBBridgeResult_InvalidArgument;
    }

    session->OnnxModelPath = config->OnnxModelPath;
    session->PlanCacheDir = IsNullOrWhiteSpace(config->PlanCacheDir) ? std::string() : std::string(config->PlanCacheDir);
    session->EnableFp16 = config->EnableFp16 != 0;
    session->EnableTf32 = config->EnableTf32 != 0;
    session->BuilderOptimizationLevel = config->BuilderOptimizationLevel;
    session->ForceRebuild = config->ForceRebuild != 0;
    session->WorkspaceSizeBytes = config->WorkspaceSizeBytes;
    session->IsConfigured = true;

#if defined(MOTIONBRICKS_BRIDGE_USE_TENSORRT)
    session->ReleaseEngineResources();
#endif

    return MBBridgeResult_Success;
}

MOTIONBRICKS_BRIDGE_API MBBridgeResult MBBridge_BuildOrLoadEngine(void* sessionHandle, int32_t* outLoadedFromCache)
{
    auto* session = static_cast<MBBridgeSession*>(sessionHandle);
    if (session == nullptr)
        return MBBridgeResult_InvalidArgument;

    if (outLoadedFromCache != nullptr)
        *outLoadedFromCache = 0;

    std::lock_guard<std::mutex> lock(session->Sync);
    session->LastError.clear();

    if (!session->IsConfigured)
    {
        session->LastError = "Session must be configured before building or loading a TensorRT engine.";
        return MBBridgeResult_InvalidArgument;
    }

#if !defined(MOTIONBRICKS_BRIDGE_USE_TENSORRT)
    session->LastError =
        "MotionBricksBridge.Native was built without the NVIDIA TensorRT and CUDA SDKs. "
        "Run Tools/Dependencies/Get-TensorRT.ps1, install the CUDA Toolkit, and rebuild Build/Native/MotionBricksBridge/MotionBricksBridge.vcxproj.";
    return MBBridgeResult_BackendUnavailable;
#else
    session->ReleaseEngineResources();
    session->Logger.Clear();

    int32_t trtVersion = static_cast<int32_t>(getInferLibVersion());
    if (trtVersion < 100000)
    {
        session->LastError = "MotionBricks requires NVIDIA TensorRT 10.0 or newer.";
        return MBBridgeResult_BackendUnavailable;
    }

    int deviceCount = 0;
    if (!FormatCudaError(cudaGetDeviceCount(&deviceCount), "cudaGetDeviceCount", session->LastError))
        return MBBridgeResult_BackendUnavailable;

    if (deviceCount <= 0)
    {
        session->LastError = "No CUDA-capable NVIDIA GPU was detected.";
        return MBBridgeResult_BackendUnavailable;
    }

    int deviceIndex = 0;
    if (!FormatCudaError(cudaGetDevice(&deviceIndex), "cudaGetDevice", session->LastError))
        return MBBridgeResult_BackendUnavailable;

    cudaDeviceProp deviceProp{};
    if (!FormatCudaError(cudaGetDeviceProperties(&deviceProp, deviceIndex), "cudaGetDeviceProperties", session->LastError))
        return MBBridgeResult_BackendUnavailable;

    std::filesystem::path onnxPath(session->OnnxModelPath);
    if (!std::filesystem::exists(onnxPath))
    {
        session->LastError = "ONNX model file does not exist: " + onnxPath.string();
        return MBBridgeResult_InvalidArgument;
    }

    uint64_t onnxHash = 0;
    uint64_t onnxSize = 0;
    if (!HashFileFnv1a64(onnxPath, onnxHash, onnxSize, session->LastError))
        return MBBridgeResult_EngineBuildFailed;

    std::filesystem::path planPath;
    if (!session->PlanCacheDir.empty())
    {
        std::ostringstream keyBuilder;
        keyBuilder << "soma_b" << kBatchSize
                   << "_trt" << trtVersion
                   << "_sm" << deviceProp.major << deviceProp.minor
                   << "_fp16-" << (session->EnableFp16 ? 1 : 0)
                   << "_tf32-" << (session->EnableTf32 ? 1 : 0)
                   << "_opt" << session->BuilderOptimizationLevel
                   << "_" << std::hex << std::setfill('0') << std::setw(16) << onnxHash
                   << "_" << std::dec << onnxSize
                   << ".plan";
        planPath = std::filesystem::path(session->PlanCacheDir) / keyBuilder.str();
    }

    std::vector<char> serializedPlan;
    bool loadedFromCache = false;
    if (!planPath.empty() && !session->ForceRebuild && std::filesystem::exists(planPath))
    {
        std::ifstream planStream(planPath, std::ios::binary | std::ios::ate);
        if (planStream)
        {
            std::streamsize size = planStream.tellg();
            if (size > 0)
            {
                serializedPlan.resize(static_cast<size_t>(size));
                planStream.seekg(0, std::ios::beg);
                if (planStream.read(serializedPlan.data(), size))
                    loadedFromCache = true;
                else
                    serializedPlan.clear();
            }
        }
    }

    if (!loadedFromCache)
    {
        std::unique_ptr<nvinfer1::IBuilder> builder(nvinfer1::createInferBuilder(session->Logger));
        if (!builder)
        {
            session->LastError = "Failed to create TensorRT IBuilder. " + session->Logger.GetLastDiagnostic();
            return MBBridgeResult_EngineBuildFailed;
        }

        std::unique_ptr<nvinfer1::INetworkDefinition> network(builder->createNetworkV2(0U));
        if (!network)
        {
            session->LastError = "Failed to create TensorRT INetworkDefinition. " + session->Logger.GetLastDiagnostic();
            return MBBridgeResult_EngineBuildFailed;
        }

        std::unique_ptr<nvonnxparser::IParser> parser(nvonnxparser::createParser(*network, session->Logger));
        if (!parser)
        {
            session->LastError = "Failed to create TensorRT ONNX parser. " + session->Logger.GetLastDiagnostic();
            return MBBridgeResult_EngineBuildFailed;
        }

        if (!parser->parseFromFile(onnxPath.string().c_str(), static_cast<int32_t>(nvinfer1::ILogger::Severity::kWARNING)))
        {
            std::ostringstream oss;
            oss << "TensorRT ONNX parser failed for '" << onnxPath.string() << "'.";
            if (parser->getNbErrors() > 0 && parser->getError(0) != nullptr)
                oss << " " << parser->getError(0)->desc();
            session->LastError = oss.str();
            return MBBridgeResult_EngineBuildFailed;
        }

        if (!SpecializeStaticBatch(*network, session->LastError))
            return MBBridgeResult_ContractMismatch;

        std::unique_ptr<nvinfer1::IBuilderConfig> builderConfig(builder->createBuilderConfig());
        if (!builderConfig)
        {
            session->LastError = "Failed to create TensorRT IBuilderConfig.";
            return MBBridgeResult_EngineBuildFailed;
        }

        if (session->WorkspaceSizeBytes > 0)
            builderConfig->setMemoryPoolLimit(nvinfer1::MemoryPoolType::kWORKSPACE, static_cast<size_t>(session->WorkspaceSizeBytes));

        builderConfig->setBuilderOptimizationLevel(session->BuilderOptimizationLevel);
        if (session->EnableFp16)
            builderConfig->setFlag(nvinfer1::BuilderFlag::kFP16);
        if (!session->EnableTf32)
            builderConfig->clearFlag(nvinfer1::BuilderFlag::kTF32);

        std::unique_ptr<nvinfer1::IHostMemory> builtPlan(builder->buildSerializedNetwork(*network, *builderConfig));
        if (!builtPlan || builtPlan->size() == 0)
        {
            session->LastError = "TensorRT failed to build serialized network plan. " + session->Logger.GetLastDiagnostic();
            return MBBridgeResult_EngineBuildFailed;
        }

        serializedPlan.resize(builtPlan->size());
        std::memcpy(serializedPlan.data(), builtPlan->data(), builtPlan->size());

        if (!planPath.empty())
        {
            std::error_code ec;
            std::filesystem::create_directories(planPath.parent_path(), ec);
            std::filesystem::path tempPath = planPath;
            tempPath += ".tmp";
            std::ofstream outStream(tempPath, std::ios::binary | std::ios::trunc);
            if (outStream)
            {
                outStream.write(serializedPlan.data(), static_cast<std::streamsize>(serializedPlan.size()));
                outStream.close();
                if (outStream.good())
                    std::filesystem::rename(tempPath, planPath, ec);
            }
        }
    }

    session->Runtime.reset(nvinfer1::createInferRuntime(session->Logger));
    if (!session->Runtime)
    {
        session->LastError = "Failed to create TensorRT IRuntime. " + session->Logger.GetLastDiagnostic();
        return MBBridgeResult_EngineBuildFailed;
    }

    session->Engine.reset(session->Runtime->deserializeCudaEngine(serializedPlan.data(), serializedPlan.size()));
    if (!session->Engine)
    {
        session->LastError = "Failed to deserialize TensorRT ICudaEngine. " + session->Logger.GetLastDiagnostic();
        session->ReleaseEngineResources();
        return MBBridgeResult_EngineBuildFailed;
    }

    if (!ValidateEngineContract(*session->Engine, session->LastError))
    {
        session->ReleaseEngineResources();
        return MBBridgeResult_ContractMismatch;
    }

    session->Context.reset(session->Engine->createExecutionContext());
    if (!session->Context)
    {
        session->LastError = "Failed to create TensorRT IExecutionContext.";
        session->ReleaseEngineResources();
        return MBBridgeResult_EngineBuildFailed;
    }

    if (!FormatCudaError(cudaStreamCreateWithFlags(&session->Stream, cudaStreamNonBlocking), "cudaStreamCreateWithFlags", session->LastError))
    {
        session->ReleaseEngineResources();
        return MBBridgeResult_EngineBuildFailed;
    }

    for (size_t i = 0; i < kSomaTensorSpecs.size(); ++i)
    {
        if (!FormatCudaError(cudaMalloc(&session->DeviceBuffers[i], kSomaTensorSpecs[i].ByteSize), "cudaMalloc", session->LastError))
        {
            session->ReleaseEngineResources();
            return MBBridgeResult_EngineBuildFailed;
        }

        if (!session->Context->setTensorAddress(kSomaTensorSpecs[i].Name, session->DeviceBuffers[i]))
        {
            session->LastError = std::string("Failed to bind TensorRT tensor address for '") + kSomaTensorSpecs[i].Name + "'.";
            session->ReleaseEngineResources();
            return MBBridgeResult_ContractMismatch;
        }
    }

    session->EngineReady = true;
    if (outLoadedFromCache != nullptr)
        *outLoadedFromCache = loadedFromCache ? 1 : 0;

    return MBBridgeResult_Success;
#endif
}

MOTIONBRICKS_BRIDGE_API MBBridgeResult MBBridge_RunSomaInference(void* sessionHandle, const MBSomaInputView* input, MBSomaOutputView* output)
{
    auto* session = static_cast<MBBridgeSession*>(sessionHandle);
    if (session == nullptr)
        return MBBridgeResult_InvalidArgument;

    std::lock_guard<std::mutex> lock(session->Sync);
    session->LastError.clear();

    if (!ValidateInputOutputViews(input, output, session->LastError))
        return MBBridgeResult_InvalidArgument;

#if !defined(MOTIONBRICKS_BRIDGE_USE_TENSORRT)
    session->LastError =
        "MotionBricksBridge.Native was built without the NVIDIA TensorRT and CUDA SDKs. "
        "Rebuild Build/Native/MotionBricksBridge/MotionBricksBridge.vcxproj with TensorRT 10.x and CUDA installed.";
    return MBBridgeResult_BackendUnavailable;
#else
    if (!session->EngineReady || !session->Context || session->Stream == nullptr)
    {
        session->LastError = "TensorRT engine is not ready. Call MBBridge_BuildOrLoadEngine first.";
        return MBBridgeResult_InferenceFailed;
    }

    const std::array<const void*, 7> hostInputs =
    {{
        input->GlobalRootValues,
        input->HasGlobalRootValues,
        input->LocalRootValues,
        input->HasLocalRootValues,
        input->LocalPoses,
        input->HasLocalPoses,
        input->AllowedPredNumTokens,
    }};

    for (size_t i = 0; i < hostInputs.size(); ++i)
    {
        if (!FormatCudaError(
            cudaMemcpyAsync(session->DeviceBuffers[i], hostInputs[i], kSomaTensorSpecs[i].ByteSize, cudaMemcpyHostToDevice, session->Stream),
            "cudaMemcpyAsync(H2D)",
            session->LastError))
        {
            return MBBridgeResult_InferenceFailed;
        }
    }

    if (!session->Context->enqueueV3(session->Stream))
    {
        session->LastError = "TensorRT enqueueV3 failed. " + session->Logger.GetLastDiagnostic();
        return MBBridgeResult_InferenceFailed;
    }

    if (!FormatCudaError(
        cudaMemcpyAsync(output->PredGlobalMotions, session->DeviceBuffers[7], kSomaTensorSpecs[7].ByteSize, cudaMemcpyDeviceToHost, session->Stream),
        "cudaMemcpyAsync(D2H pred_global_motions)",
        session->LastError))
    {
        return MBBridgeResult_InferenceFailed;
    }

    if (!FormatCudaError(
        cudaMemcpyAsync(output->PredNumTokens, session->DeviceBuffers[8], kSomaTensorSpecs[8].ByteSize, cudaMemcpyDeviceToHost, session->Stream),
        "cudaMemcpyAsync(D2H pred_num_tokens)",
        session->LastError))
    {
        return MBBridgeResult_InferenceFailed;
    }

    if (!FormatCudaError(cudaStreamSynchronize(session->Stream), "cudaStreamSynchronize", session->LastError))
        return MBBridgeResult_InferenceFailed;

    int32_t tokenCount = output->PredNumTokens[0];
    if (tokenCount < kMinTokenCount || tokenCount > kMaxTokenCount)
    {
        std::ostringstream oss;
        oss << "Model predicted an out-of-range token count: " << tokenCount << " (expected 6..16).";
        session->LastError = oss.str();
        return MBBridgeResult_ContractMismatch;
    }

    return MBBridgeResult_Success;
#endif
}

MOTIONBRICKS_BRIDGE_API MBBridgeResult MBBridge_GetLastError(void* sessionHandle, char* utf8Buffer, int32_t bufferCapacity, int32_t* outRequiredBytes)
{
    auto* session = static_cast<MBBridgeSession*>(sessionHandle);
    if (session == nullptr)
        return MBBridgeResult_InvalidArgument;

    std::lock_guard<std::mutex> lock(session->Sync);
    return CopyUtf8(session->LastError, utf8Buffer, bufferCapacity, outRequiredBytes);
}
