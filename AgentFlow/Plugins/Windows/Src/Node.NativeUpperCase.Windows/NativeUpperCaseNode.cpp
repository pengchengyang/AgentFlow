#include "NativeUpperCaseNode.h"

#include <algorithm>
#include <cctype>
#include <cstring>
#include <cstddef>
#include <new>
#include <string>

namespace {

static_assert(sizeof(AfSample) == 64, "AfSample must remain 64 bytes on Windows x64.");
static_assert(offsetof(AfSample, data) == 48, "AfSample.data offset changed unexpectedly.");
static_assert(offsetof(AfSample, dataSize) == 56, "AfSample.dataSize offset changed unexpectedly.");

} // namespace

NativeUpperCaseNode::NativeUpperCaseNode(const AfNodeHostVTable& host, void* callbackContext)
    : host_(host), callbackContext_(callbackContext) {
}

AfStatus NativeUpperCaseNode::Initialize() {
    initialized_ = true;
    return Log(AF_LOG_INFO, "NativeUpperCase initialized.");
}

AfStatus NativeUpperCaseNode::Run() {
    if (!initialized_) {
        SetLastError("NativeUpperCase was not initialized.");
        return AF_ERR_NOT_INITIALIZED;
    }
    running_ = true;
    return Log(AF_LOG_INFO, "NativeUpperCase is running.");
}

AfStatus NativeUpperCaseNode::ReceiveSample(const AfSample* sample) {
    if (!initialized_ || !running_) {
        SetLastError("NativeUpperCase cannot receive data while not running.");
        return AF_ERR_NOT_INITIALIZED;
    }
    if (sample == nullptr) {
        SetLastError("NativeUpperCase received a null sample.");
        return AF_ERR_INVALID_ARGUMENT;
    }
    if (sample->structSize < sizeof(AfSample) || sample->abiVersion != AF_ABI_VERSION) {
        SetLastError("NativeUpperCase received an incompatible sample layout.");
        return AF_ERR_ABI_MISMATCH;
    }
    if (sample->type != AF_SAMPLE_STRING) {
        SetLastError("NativeUpperCase expects a STRING sample.");
        return AF_ERR_INVALID_ARGUMENT;
    }
    if (sample->dataSize > 0 && sample->data == nullptr) {
        SetLastError("NativeUpperCase received an empty string buffer pointer.");
        return AF_ERR_INVALID_ARGUMENT;
    }

    std::string input;
    if (sample->dataSize > 0)
        input.assign(reinterpret_cast<const char*>(sample->data), static_cast<size_t>(sample->dataSize));
    std::string output;
    output.reserve(input.size());
    for (const unsigned char ch : input) {
        // This demo intentionally performs deterministic ASCII uppercase conversion.
        output.push_back(ch >= 'a' && ch <= 'z' ? static_cast<char>(ch - ('a' - 'A')) : static_cast<char>(ch));
    }

    AfSample result{};
    result.structSize = sizeof(AfSample);
    result.abiVersion = AF_ABI_VERSION;
    result.type = AF_SAMPLE_STRING;
    result.data = reinterpret_cast<const uint8_t*>(output.data());
    result.dataSize = output.size();

    const char* pinName = "output_string";
    const AfStatus status = host_.sendSample(
        callbackContext_,
        pinName,
        static_cast<uint64_t>(std::strlen(pinName)),
        &result);
    if (status != AF_OK) {
        SetLastError("NativeUpperCase could not enqueue the transformed sample.");
        return status;
    }

    Log(AF_LOG_TRACE, "NativeUpperCase queued an output sample.");
    return AF_OK;
}

AfStatus NativeUpperCaseNode::Stop() {
    running_ = false;
    return Log(AF_LOG_INFO, "NativeUpperCase stopped.");
}

AfStatus NativeUpperCaseNode::Shutdown() {
    running_ = false;
    initialized_ = false;
    return Log(AF_LOG_INFO, "NativeUpperCase shut down.");
}

const char* NativeUpperCaseNode::LastError() const {
    return lastError_.c_str();
}

AfStatus NativeUpperCaseNode::Log(int32_t level, const char* message) {
    if (host_.log == nullptr) {
        return AF_OK;
    }
    host_.log(callbackContext_, level, message, static_cast<uint64_t>(std::strlen(message)));
    return AF_OK;
}

void NativeUpperCaseNode::SetLastError(const char* message) {
    lastError_ = message == nullptr ? "" : message;
}

extern "C" AF_NODE_API uint32_t AF_CALL af_node_get_abi_version(void) {
    return AF_ABI_VERSION;
}

extern "C" AF_NODE_API AfStatus AF_CALL af_node_create(
    const AfNodeHostVTable* host,
    void* callbackContext,
    void** nodeContext) {
    if (host == nullptr || nodeContext == nullptr) {
        return AF_ERR_INVALID_ARGUMENT;
    }
    if (host->structSize < sizeof(AfNodeHostVTable) || host->abiVersion != AF_ABI_VERSION) {
        return AF_ERR_ABI_MISMATCH;
    }
    if (host->sendSample == nullptr) {
        return AF_ERR_INVALID_ARGUMENT;
    }

    *nodeContext = nullptr;
    auto node = new (std::nothrow) NativeUpperCaseNode(*host, callbackContext);
    if (node == nullptr) {
        return AF_ERR_INTERNAL;
    }

    *nodeContext = node;
    return AF_OK;
}

extern "C" AF_NODE_API AfStatus AF_CALL af_node_initialize(void* nodeContext) {
    auto node = static_cast<NativeUpperCaseNode*>(nodeContext);
    return node == nullptr ? AF_ERR_INVALID_ARGUMENT : node->Initialize();
}

extern "C" AF_NODE_API AfStatus AF_CALL af_node_run(void* nodeContext) {
    auto node = static_cast<NativeUpperCaseNode*>(nodeContext);
    return node == nullptr ? AF_ERR_INVALID_ARGUMENT : node->Run();
}

extern "C" AF_NODE_API AfStatus AF_CALL af_node_receive_sample(void* nodeContext, const AfSample* sample) {
    auto node = static_cast<NativeUpperCaseNode*>(nodeContext);
    return node == nullptr ? AF_ERR_INVALID_ARGUMENT : node->ReceiveSample(sample);
}

extern "C" AF_NODE_API AfStatus AF_CALL af_node_stop(void* nodeContext) {
    auto node = static_cast<NativeUpperCaseNode*>(nodeContext);
    return node == nullptr ? AF_ERR_INVALID_ARGUMENT : node->Stop();
}

extern "C" AF_NODE_API AfStatus AF_CALL af_node_shutdown(void* nodeContext) {
    auto node = static_cast<NativeUpperCaseNode*>(nodeContext);
    return node == nullptr ? AF_ERR_INVALID_ARGUMENT : node->Shutdown();
}

extern "C" AF_NODE_API void AF_CALL af_node_destroy(void* nodeContext) {
    delete static_cast<NativeUpperCaseNode*>(nodeContext);
}

extern "C" AF_NODE_API const char* AF_CALL af_node_get_last_error(void* nodeContext) {
    auto node = static_cast<NativeUpperCaseNode*>(nodeContext);
    return node == nullptr ? "Invalid native node context." : node->LastError();
}
