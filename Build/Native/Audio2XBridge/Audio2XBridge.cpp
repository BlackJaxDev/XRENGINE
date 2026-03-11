#include <algorithm>
#include <array>
#include <cctype>
#include <cstdint>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <string_view>
#include <system_error>
#include <vector>

#ifndef AUDIO2X_BRIDGE_API
#define AUDIO2X_BRIDGE_API extern "C" __declspec(dllexport)
#endif

#if defined(AUDIO2X_BRIDGE_USE_SDK)
#include "audio2face/audio2face.h"
#include "audio2emotion/audio2emotion.h"
#include "audio2x/cuda_utils.h"
#endif

enum A2XBridgeResult : int32_t
{
    A2XBridgeResult_Success = 0,
    A2XBridgeResult_NoData = 1,
    A2XBridgeResult_InvalidArgument = 2,
    A2XBridgeResult_BackendUnavailable = 3,
    A2XBridgeResult_InternalError = 4,
};

struct A2XBridgeSessionConfig
{
    int32_t InputSampleRate;
    bool EnableEmotion;
    const char* FaceModelPath;
    const char* EmotionModelPath;
};

namespace
{
    constexpr int kRequiredInputSampleRate = 16000;

    int CopyUtf8(const std::string& text, char* buffer, int bufferBytes, int* outRequiredBytes)
    {
        int requiredBytes = static_cast<int>(text.size()) + 1;
        if (outRequiredBytes != nullptr)
            *outRequiredBytes = requiredBytes;

        if (buffer == nullptr || bufferBytes <= 0)
            return A2XBridgeResult_NoData;

        int bytesToCopy = requiredBytes;
        if (bytesToCopy > bufferBytes)
            bytesToCopy = bufferBytes;

        std::memcpy(buffer, text.c_str(), static_cast<size_t>(bytesToCopy));
        if (bytesToCopy == bufferBytes)
            buffer[bufferBytes - 1] = '\0';
        return A2XBridgeResult_Success;
    }

    std::string JoinNames(const std::vector<std::string>& names)
    {
        std::string result;
        for (size_t index = 0; index < names.size(); ++index)
        {
            if (index > 0)
                result.push_back(';');

            result += names[index];
        }

        return result;
    }

    std::string ToLowerAscii(std::string_view value)
    {
        std::string lowered;
        lowered.reserve(value.size());
        for (char ch : value)
            lowered.push_back(static_cast<char>(std::tolower(static_cast<unsigned char>(ch))));
        return lowered;
    }

#if defined(AUDIO2X_BRIDGE_USE_SDK)
    template<typename TObject>
    struct SdkDestroyer
    {
        void operator()(TObject* value) const
        {
            if (value != nullptr)
                value->Destroy();
        }
    };

    template<typename TObject>
    using UniqueSdkPtr = std::unique_ptr<TObject, SdkDestroyer<TObject>>;

    constexpr std::array<const char*, 6> kSupportedEmotionNames =
    {
        "angry",
        "disgust",
        "fear",
        "happy",
        "neutral",
        "sad",
    };

    int ResolveSupportedEmotionIndex(std::string_view sourceName)
    {
        std::string lowered = ToLowerAscii(sourceName);
        if (lowered == "angry" || lowered == "anger")
            return 0;
        if (lowered == "disgust")
            return 1;
        if (lowered == "fear")
            return 2;
        if (lowered == "happy" || lowered == "joy")
            return 3;
        if (lowered == "neutral")
            return 4;
        if (lowered == "sad" || lowered == "sadness" || lowered == "grief")
            return 5;
        return -1;
    }

    bool IsNullOrWhiteSpace(const char* text)
    {
        if (text == nullptr)
            return true;

        while (*text != '\0')
        {
            if (!std::isspace(static_cast<unsigned char>(*text)))
                return false;
            ++text;
        }

        return true;
    }
#endif
}

struct A2XBridgeSession
{
    std::mutex Sync;
    std::string LastError;
    bool IsConfigured = false;
    bool EmotionEnabled = false;
    std::vector<std::string> BlendshapeNames;
    std::string BlendshapeLayout;
    std::vector<float> LatestBlendshapeWeights;
    bool HasBlendshapeFrame = false;
    std::vector<std::string> EmotionNames;
    std::string EmotionLayout;
    std::vector<float> LatestEmotionWeights;
    bool HasEmotionFrame = false;

#if defined(AUDIO2X_BRIDGE_USE_SDK)
    UniqueSdkPtr<nva2f::IBlendshapeExecutorBundle> FaceBundle;
    UniqueSdkPtr<nva2e::IClassifierModel::IEmotionModelInfo> EmotionModelInfo;
    UniqueSdkPtr<nva2e::IEmotionExecutor> EmotionExecutor;
    std::vector<int> EmotionSourceToTarget;
    std::vector<float> AudioScratch;

    void ResetRuntimeState()
    {
        EmotionExecutor.reset();
        EmotionModelInfo.reset();
        FaceBundle.reset();
        EmotionSourceToTarget.clear();
        AudioScratch.clear();
    }
#endif
};

#if defined(AUDIO2X_BRIDGE_USE_SDK)
namespace
{
    void SetLastError(A2XBridgeSession& session, std::string message)
    {
        std::lock_guard lock(session.Sync);
        session.LastError = std::move(message);
    }

    void ClearFrames(A2XBridgeSession& session)
    {
        std::lock_guard lock(session.Sync);
        session.HasBlendshapeFrame = false;
        session.HasEmotionFrame = false;
        std::fill(session.LatestBlendshapeWeights.begin(), session.LatestBlendshapeWeights.end(), 0.0f);
        std::fill(session.LatestEmotionWeights.begin(), session.LatestEmotionWeights.end(), 0.0f);
    }

    bool StoreBlendshapeLayout(A2XBridgeSession& session)
    {
        auto& executor = session.FaceBundle->GetExecutor();
        std::vector<std::string> names;
        names.reserve(executor.GetWeightCount());

        nva2f::IBlendshapeSolver* skinSolver = nullptr;
        nva2f::IBlendshapeSolver* tongueSolver = nullptr;
        std::error_code skinError = nva2f::GetExecutorSkinSolver(executor, 0, &skinSolver);
        std::error_code tongueError = nva2f::GetExecutorTongueSolver(executor, 0, &tongueSolver);

        if (!skinError && skinSolver != nullptr)
        {
            for (int index = 0; index < skinSolver->NumBlendshapePoses(); ++index)
                names.emplace_back(skinSolver->GetPoseName(static_cast<size_t>(index)));
        }

        if (!tongueError && tongueSolver != nullptr)
        {
            for (int index = 0; index < tongueSolver->NumBlendshapePoses(); ++index)
                names.emplace_back(tongueSolver->GetPoseName(static_cast<size_t>(index)));
        }

        if (names.size() != executor.GetWeightCount())
        {
            names.clear();
            names.reserve(executor.GetWeightCount());
            for (size_t index = 0; index < executor.GetWeightCount(); ++index)
                names.emplace_back("weight_" + std::to_string(index));
        }

        std::lock_guard lock(session.Sync);
        session.BlendshapeNames = std::move(names);
        session.BlendshapeLayout = JoinNames(session.BlendshapeNames);
        session.LatestBlendshapeWeights.assign(session.BlendshapeNames.size(), 0.0f);
        return true;
    }

    void StoreSupportedEmotionLayout(A2XBridgeSession& session)
    {
        std::vector<std::string> names;
        names.reserve(kSupportedEmotionNames.size());
        for (const char* name : kSupportedEmotionNames)
            names.emplace_back(name);

        std::lock_guard lock(session.Sync);
        session.EmotionNames = std::move(names);
        session.EmotionLayout = JoinNames(session.EmotionNames);
        session.LatestEmotionWeights.assign(session.EmotionNames.size(), 0.0f);
    }

    void ClearEmotionLayout(A2XBridgeSession& session)
    {
        std::lock_guard lock(session.Sync);
        session.EmotionNames.clear();
        session.EmotionLayout.clear();
        session.LatestEmotionWeights.clear();
        session.HasEmotionFrame = false;
    }

    void BlendshapeResultsCallback(void* userdata, const nva2f::IBlendshapeExecutor::HostResults& results, std::error_code errorCode)
    {
        A2XBridgeSession& session = *static_cast<A2XBridgeSession*>(userdata);
        if (errorCode)
        {
            SetLastError(session, "Audio2Face host solve failed: " + errorCode.message());
            return;
        }

        std::lock_guard lock(session.Sync);
        session.LatestBlendshapeWeights.assign(results.weights.Data(), results.weights.Data() + results.weights.Size());
        session.HasBlendshapeFrame = true;
        session.LastError.clear();
    }

    bool EmotionResultsCallback(void* userdata, const nva2e::IEmotionExecutor::Results& results)
    {
        A2XBridgeSession& session = *static_cast<A2XBridgeSession*>(userdata);
        auto& emotionAccumulator = session.FaceBundle->GetEmotionAccumulator(results.trackIndex);
        if (std::error_code accumulateError = emotionAccumulator.Accumulate(results.timeStampCurrentFrame, results.emotions, results.cudaStream))
        {
            SetLastError(session, "Audio2Emotion accumulation failed: " + accumulateError.message());
            return false;
        }

        std::vector<float> sourceWeights(results.emotions.Size(), 0.0f);
        if (std::error_code copyError = nva2x::CopyDeviceToHost(
            nva2x::HostTensorFloatView(sourceWeights.data(), sourceWeights.size()),
            results.emotions,
            results.cudaStream))
        {
            SetLastError(session, "Audio2Emotion readback failed: " + copyError.message());
            return false;
        }

        std::vector<float> mappedWeights(kSupportedEmotionNames.size(), 0.0f);
        for (size_t index = 0; index < session.EmotionSourceToTarget.size() && index < sourceWeights.size(); ++index)
        {
            int targetIndex = session.EmotionSourceToTarget[index];
            if (targetIndex < 0)
                continue;

            mappedWeights[static_cast<size_t>(targetIndex)] = std::max(
                mappedWeights[static_cast<size_t>(targetIndex)],
                std::clamp(sourceWeights[index], 0.0f, 1.0f));
        }

        std::lock_guard lock(session.Sync);
        session.LatestEmotionWeights = std::move(mappedWeights);
        session.HasEmotionFrame = true;
        session.LastError.clear();
        return true;
    }

    bool TryCreateFaceBundle(A2XBridgeSession& session, const char* modelPath)
    {
        auto regressionBundle = UniqueSdkPtr<nva2f::IBlendshapeExecutorBundle>(
            nva2f::ReadRegressionBlendshapeSolveExecutorBundle(
                1,
                modelPath,
                nva2f::IGeometryExecutor::ExecutionOption::SkinTongue,
                false,
                60,
                1,
                nullptr,
                nullptr));
        if (regressionBundle)
        {
            session.FaceBundle = std::move(regressionBundle);
            return true;
        }

        auto diffusionBundle = UniqueSdkPtr<nva2f::IBlendshapeExecutorBundle>(
            nva2f::ReadDiffusionBlendshapeSolveExecutorBundle(
                1,
                modelPath,
                nva2f::IGeometryExecutor::ExecutionOption::SkinTongue,
                false,
                0,
                false,
                nullptr,
                nullptr));
        if (diffusionBundle)
        {
            session.FaceBundle = std::move(diffusionBundle);
            return true;
        }

        return false;
    }

    bool TryCreateEmotionExecutor(A2XBridgeSession& session, const char* modelPath)
    {
        session.EmotionModelInfo = UniqueSdkPtr<nva2e::IClassifierModel::IEmotionModelInfo>(
            nva2e::ReadClassifierModelInfo(modelPath));
        if (!session.EmotionModelInfo)
            return false;

        nva2e::EmotionExecutorCreationParameters params;
        params.cudaStream = session.FaceBundle->GetCudaStream().Data();
        params.nbTracks = 1;
        const nva2x::IAudioAccumulator* sharedAudioAccumulator = &session.FaceBundle->GetAudioAccumulator(0);
        params.sharedAudioAccumulators = &sharedAudioAccumulator;

        auto classifierParams = session.EmotionModelInfo->GetExecutorCreationParameters(60000, 30, 1, 30);
        session.EmotionExecutor = UniqueSdkPtr<nva2e::IEmotionExecutor>(
            nva2e::CreateClassifierEmotionExecutor(params, classifierParams));
        if (!session.EmotionExecutor)
        {
            session.EmotionModelInfo.reset();
            return false;
        }

        session.EmotionSourceToTarget.clear();
        size_t emotionCount = session.EmotionModelInfo->GetNetworkInfo().GetEmotionsCount();
        session.EmotionSourceToTarget.reserve(emotionCount);
        for (size_t index = 0; index < emotionCount; ++index)
        {
            session.EmotionSourceToTarget.push_back(
                ResolveSupportedEmotionIndex(session.EmotionModelInfo->GetNetworkInfo().GetEmotionName(index)));
        }

        return true;
    }

    bool SeedNeutralEmotion(A2XBridgeSession& session)
    {
        auto& emotionAccumulator = session.FaceBundle->GetEmotionAccumulator(0);
        std::vector<float> zeroEmotion(emotionAccumulator.GetEmotionSize(), 0.0f);
        std::error_code accumulateError = emotionAccumulator.Accumulate(
            0,
            nva2x::HostTensorFloatConstView(zeroEmotion.data(), zeroEmotion.size()),
            session.FaceBundle->GetCudaStream().Data());
        if (accumulateError)
        {
            SetLastError(session, "Failed to seed default emotion state: " + accumulateError.message());
            return false;
        }

        std::error_code closeError = emotionAccumulator.Close();
        if (closeError)
        {
            SetLastError(session, "Failed to close default emotion accumulator: " + closeError.message());
            return false;
        }

        return true;
    }

    bool TryConfigureSdkSession(A2XBridgeSession& session, const A2XBridgeSessionConfig& config)
    {
        if (config.InputSampleRate != kRequiredInputSampleRate)
        {
            session.LastError = "Audio2XBridge requires 16 kHz mono PCM input.";
            return false;
        }

        if (IsNullOrWhiteSpace(config.FaceModelPath))
        {
            session.LastError = "FaceModelPath must point to an Audio2Face model.json file.";
            return false;
        }

        session.ResetRuntimeState();
        ClearFrames(session);
        ClearEmotionLayout(session);
        {
            std::lock_guard lock(session.Sync);
            session.BlendshapeNames.clear();
            session.BlendshapeLayout.clear();
            session.LatestBlendshapeWeights.clear();
            session.IsConfigured = false;
            session.EmotionEnabled = config.EnableEmotion;
        }

        if (std::error_code cudaError = nva2x::SetCudaDeviceIfNeeded(0))
        {
            session.LastError = "Failed to select CUDA device 0: " + cudaError.message();
            return false;
        }

        if (!TryCreateFaceBundle(session, config.FaceModelPath))
        {
            session.LastError = "Failed to create an Audio2Face blendshape executor bundle from the provided model path.";
            return false;
        }

        auto& blendshapeExecutor = session.FaceBundle->GetExecutor();
        if (blendshapeExecutor.GetResultType() != nva2f::IBlendshapeExecutor::ResultsType::HOST)
        {
            session.LastError = "Audio2XBridge expects a host-result blendshape executor.";
            return false;
        }

        if (std::error_code callbackError = blendshapeExecutor.SetResultsCallback(BlendshapeResultsCallback, &session))
        {
            session.LastError = "Failed to attach the Audio2Face results callback: " + callbackError.message();
            return false;
        }

        StoreBlendshapeLayout(session);

        if (config.EnableEmotion)
        {
            if (IsNullOrWhiteSpace(config.EmotionModelPath))
            {
                session.LastError = "EmotionModelPath must point to an Audio2Emotion model.json file when emotion is enabled.";
                return false;
            }

            if (!TryCreateEmotionExecutor(session, config.EmotionModelPath))
            {
                session.LastError = "Failed to create an Audio2Emotion executor from the provided model path.";
                return false;
            }

            if (std::error_code callbackError = session.EmotionExecutor->SetResultsCallback(EmotionResultsCallback, &session))
            {
                session.LastError = "Failed to attach the Audio2Emotion results callback: " + callbackError.message();
                return false;
            }

            StoreSupportedEmotionLayout(session);
        }
        else if (!SeedNeutralEmotion(session))
        {
            return false;
        }

        {
            std::lock_guard lock(session.Sync);
            session.IsConfigured = true;
            session.LastError.clear();
        }

        return true;
    }

    bool TryDropConsumedData(A2XBridgeSession& session)
    {
        auto& faceExecutor = session.FaceBundle->GetExecutor();
        auto& audioAccumulator = session.FaceBundle->GetAudioAccumulator(0);

        size_t audioDropStart = faceExecutor.GetNextAudioSampleToRead(0);
        if (session.EmotionExecutor)
            audioDropStart = std::min(audioDropStart, session.EmotionExecutor->GetNextAudioSampleToRead(0));

        if (std::error_code dropError = audioAccumulator.DropSamplesBefore(audioDropStart))
        {
            SetLastError(session, "Failed to drop consumed audio samples: " + dropError.message());
            return false;
        }

        if (session.EmotionExecutor)
        {
            auto& emotionAccumulator = session.FaceBundle->GetEmotionAccumulator(0);
            if (!emotionAccumulator.IsEmpty())
            {
                nva2x::IEmotionAccumulator::timestamp_t timestampToRead = faceExecutor.GetNextEmotionTimestampToRead(0);
                nva2x::IEmotionAccumulator::timestamp_t timestampToDrop = std::min(timestampToRead, emotionAccumulator.LastAccumulatedTimestamp());
                if (std::error_code dropError = emotionAccumulator.DropEmotionsBefore(timestampToDrop))
                {
                    SetLastError(session, "Failed to drop consumed emotion frames: " + dropError.message());
                    return false;
                }
            }
        }

        return true;
    }

    bool TryProcessAvailableData(A2XBridgeSession& session)
    {
        while (true)
        {
            auto& faceExecutor = session.FaceBundle->GetExecutor();
            size_t readyBlendshapeTracks = nva2x::GetNbReadyTracks(faceExecutor);
            if (readyBlendshapeTracks > 0)
            {
                if (std::error_code executeError = faceExecutor.Execute(nullptr))
                {
                    SetLastError(session, "Audio2Face execution failed: " + executeError.message());
                    return false;
                }

                if (std::error_code waitError = faceExecutor.Wait(0))
                {
                    SetLastError(session, "Audio2Face host solve wait failed: " + waitError.message());
                    return false;
                }

                if (!TryDropConsumedData(session))
                    return false;

                continue;
            }

            if (session.EmotionExecutor)
            {
                size_t readyEmotionTracks = nva2x::GetNbReadyTracks(*session.EmotionExecutor);
                if (readyEmotionTracks > 0)
                {
                    if (std::error_code executeError = session.EmotionExecutor->Execute(nullptr))
                    {
                        SetLastError(session, "Audio2Emotion execution failed: " + executeError.message());
                        return false;
                    }

                    if (!TryDropConsumedData(session))
                        return false;

                    continue;
                }
            }

            break;
        }

        return true;
    }
}
#endif

AUDIO2X_BRIDGE_API int A2XBridge_IsBackendAvailable()
{
#if defined(AUDIO2X_BRIDGE_USE_SDK)
    return 1;
#else
    return 0;
#endif
}

AUDIO2X_BRIDGE_API int A2XBridge_GetRequiredInputSampleRate()
{
    return kRequiredInputSampleRate;
}

AUDIO2X_BRIDGE_API int A2XBridge_CreateSession(void** outSession)
{
    if (outSession == nullptr)
        return A2XBridgeResult_InvalidArgument;

    auto session = std::make_unique<A2XBridgeSession>();
#if defined(AUDIO2X_BRIDGE_USE_SDK)
    session->LastError.clear();
#else
    session->LastError = "Audio2XBridge.Native was built without the NVIDIA Audio2Face-3D SDK. Build the upstream SDK first, then rebuild this bridge project to enable live inference.";
#endif
    *outSession = session.release();
    return A2XBridgeResult_Success;
}

AUDIO2X_BRIDGE_API void A2XBridge_DestroySession(void* session)
{
    delete static_cast<A2XBridgeSession*>(session);
}

AUDIO2X_BRIDGE_API int A2XBridge_ConfigureSession(void* session, const A2XBridgeSessionConfig* config)
{
    if (session == nullptr || config == nullptr)
        return A2XBridgeResult_InvalidArgument;

    A2XBridgeSession& typedSession = *static_cast<A2XBridgeSession*>(session);

#if defined(AUDIO2X_BRIDGE_USE_SDK)
    return TryConfigureSdkSession(typedSession, *config)
        ? A2XBridgeResult_Success
        : A2XBridgeResult_BackendUnavailable;
#else
    typedSession.LastError = "Audio2XBridge.Native stub detected. Build Build/Dependencies/Audio2Face-3D-SDK with TensorRT/CUDA available, then rebuild Build/Native/Audio2XBridge to enable the real backend.";
    return A2XBridgeResult_BackendUnavailable;
#endif
}

AUDIO2X_BRIDGE_API int A2XBridge_SubmitPcm16Mono(void* session, const int16_t* samples, int sampleCount, int sampleRate)
{
    if (session == nullptr || samples == nullptr || sampleCount < 0 || sampleRate <= 0)
        return A2XBridgeResult_InvalidArgument;

    A2XBridgeSession& typedSession = *static_cast<A2XBridgeSession*>(session);

#if defined(AUDIO2X_BRIDGE_USE_SDK)
    if (!typedSession.IsConfigured || !typedSession.FaceBundle)
    {
        typedSession.LastError = "Audio2XBridge session must be configured before audio can be submitted.";
        return A2XBridgeResult_InvalidArgument;
    }

    if (sampleRate != kRequiredInputSampleRate)
    {
        typedSession.LastError = "Audio2XBridge only accepts 16 kHz mono PCM input.";
        return A2XBridgeResult_InvalidArgument;
    }

    if (sampleCount == 0)
        return A2XBridgeResult_Success;

    typedSession.AudioScratch.resize(static_cast<size_t>(sampleCount));
    constexpr float sampleScale = 1.0f / 32768.0f;
    for (int index = 0; index < sampleCount; ++index)
        typedSession.AudioScratch[static_cast<size_t>(index)] = static_cast<float>(samples[index]) * sampleScale;

    std::error_code accumulateError = typedSession.FaceBundle->GetAudioAccumulator(0).Accumulate(
        nva2x::HostTensorFloatConstView(typedSession.AudioScratch.data(), typedSession.AudioScratch.size()),
        typedSession.FaceBundle->GetCudaStream().Data());
    if (accumulateError)
    {
        typedSession.LastError = "Failed to accumulate audio samples: " + accumulateError.message();
        return A2XBridgeResult_InternalError;
    }

    if (!TryProcessAvailableData(typedSession))
        return A2XBridgeResult_InternalError;

    return A2XBridgeResult_Success;
#else
    typedSession.LastError = "Audio2XBridge.Native stub received audio, but no NVIDIA backend is linked.";
    return A2XBridgeResult_BackendUnavailable;
#endif
}

AUDIO2X_BRIDGE_API int A2XBridge_GetBlendshapeLayout(void* session, char* utf8Buffer, int bufferCapacity, int* outRequiredBytes, int* outCount)
{
    if (outCount != nullptr)
        *outCount = 0;

    if (session == nullptr)
        return A2XBridgeResult_InvalidArgument;

    A2XBridgeSession& typedSession = *static_cast<A2XBridgeSession*>(session);
    std::lock_guard lock(typedSession.Sync);
    if (outCount != nullptr)
        *outCount = static_cast<int>(typedSession.BlendshapeNames.size());
    return CopyUtf8(typedSession.BlendshapeLayout, utf8Buffer, bufferCapacity, outRequiredBytes);
}

AUDIO2X_BRIDGE_API int A2XBridge_GetEmotionLayout(void* session, char* utf8Buffer, int bufferCapacity, int* outRequiredBytes, int* outCount)
{
    if (outCount != nullptr)
        *outCount = 0;

    if (session == nullptr)
        return A2XBridgeResult_InvalidArgument;

    A2XBridgeSession& typedSession = *static_cast<A2XBridgeSession*>(session);
    std::lock_guard lock(typedSession.Sync);
    if (outCount != nullptr)
        *outCount = static_cast<int>(typedSession.EmotionNames.size());
    return CopyUtf8(typedSession.EmotionLayout, utf8Buffer, bufferCapacity, outRequiredBytes);
}

AUDIO2X_BRIDGE_API int A2XBridge_PollBlendshapeWeights(void* session, float* weights, int capacity, int* outCount)
{
    if (outCount != nullptr)
        *outCount = 0;

    if (session == nullptr)
        return A2XBridgeResult_InvalidArgument;

    A2XBridgeSession& typedSession = *static_cast<A2XBridgeSession*>(session);
    std::lock_guard lock(typedSession.Sync);
    if (!typedSession.HasBlendshapeFrame)
        return A2XBridgeResult_NoData;

    if (weights == nullptr || capacity < static_cast<int>(typedSession.LatestBlendshapeWeights.size()))
        return A2XBridgeResult_InvalidArgument;

    if (!typedSession.LatestBlendshapeWeights.empty())
    {
        std::memcpy(
            weights,
            typedSession.LatestBlendshapeWeights.data(),
            typedSession.LatestBlendshapeWeights.size() * sizeof(float));
    }

    typedSession.HasBlendshapeFrame = false;
    if (outCount != nullptr)
        *outCount = static_cast<int>(typedSession.LatestBlendshapeWeights.size());
    return A2XBridgeResult_Success;
}

AUDIO2X_BRIDGE_API int A2XBridge_PollEmotionWeights(void* session, float* weights, int capacity, int* outCount)
{
    if (outCount != nullptr)
        *outCount = 0;

    if (session == nullptr)
        return A2XBridgeResult_InvalidArgument;

    A2XBridgeSession& typedSession = *static_cast<A2XBridgeSession*>(session);
    std::lock_guard lock(typedSession.Sync);
    if (!typedSession.HasEmotionFrame)
        return A2XBridgeResult_NoData;

    if (weights == nullptr || capacity < static_cast<int>(typedSession.LatestEmotionWeights.size()))
        return A2XBridgeResult_InvalidArgument;

    if (!typedSession.LatestEmotionWeights.empty())
    {
        std::memcpy(
            weights,
            typedSession.LatestEmotionWeights.data(),
            typedSession.LatestEmotionWeights.size() * sizeof(float));
    }

    typedSession.HasEmotionFrame = false;
    if (outCount != nullptr)
        *outCount = static_cast<int>(typedSession.LatestEmotionWeights.size());
    return A2XBridgeResult_Success;
}

AUDIO2X_BRIDGE_API int A2XBridge_GetLastError(void* session, char* utf8Buffer, int bufferCapacity, int* outRequiredBytes)
{
    if (session == nullptr)
        return A2XBridgeResult_InvalidArgument;

    A2XBridgeSession& typedSession = *static_cast<A2XBridgeSession*>(session);
    std::lock_guard lock(typedSession.Sync);
    return CopyUtf8(typedSession.LastError, utf8Buffer, bufferCapacity, outRequiredBytes);
}