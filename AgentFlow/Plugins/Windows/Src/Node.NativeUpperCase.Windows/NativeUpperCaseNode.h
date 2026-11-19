#pragma once

#include "agentflow_node_api.h"
#include "agentflow_broadcast_helpers.h"

#include <cstdint>
#include <string>

class NativeUpperCaseNode final {
public:
    NativeUpperCaseNode(const AfNodeHostVTable& host, void* callbackContext);

    AfStatus Initialize();
    AfStatus Run();
    AfStatus ReceiveSample(const AfSample* sample);
    AfStatus Stop();
    AfStatus Shutdown();

    const char* LastError() const;

private:
    AfStatus Log(int32_t level, const char* message);
    void SetLastError(const char* message);

    AfNodeHostVTable host_{};
    void* callbackContext_ = nullptr;
    bool initialized_ = false;
    bool running_ = false;
    std::string lastError_;
};
