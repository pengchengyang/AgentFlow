// -----------------------------------------------------------------------
// Copyright (c) 2026 Damon Yang. All rights reserved.
// -----------------------------------------------------------------------
#pragma once

#include <cstddef>
#include <cstdint>
#include <memory>
#include <new>
#include <stdexcept>
#include <string>
#include <type_traits>
#include <utility>
#include <vector>

#include "AgentFlowNode.h"

namespace AgentFlow {

class Sample {
public:
    explicit Sample(const AgentFlowSample* value) noexcept : value_(value) {}

    const AgentFlowSample* NativeHandle() const noexcept { return value_; }
    std::string DataType() const noexcept { return value_ != nullptr && value_->data_type != nullptr ? value_->data_type : ""; }
    std::string Key() const noexcept { return value_ != nullptr && value_->key != nullptr ? value_->key : ""; }
    std::string Encoding() const noexcept { return value_ != nullptr && value_->encoding != nullptr ? value_->encoding : ""; }

    std::string AsString() const {
        return value_ != nullptr && value_->string_value != nullptr ? value_->string_value : "";
    }

    bool AsBool() const noexcept { return value_ != nullptr && value_->bool_value != 0; }
    int32_t AsInt32() const noexcept { return value_ != nullptr ? value_->int32_value : 0; }
    int64_t AsInt64() const noexcept { return value_ != nullptr ? value_->int64_value : 0; }
    float AsFloat() const noexcept { return value_ != nullptr ? value_->float_value : 0.0f; }
    double AsDouble() const noexcept { return value_ != nullptr ? value_->double_value : 0.0; }

private:
    const AgentFlowSample* value_;
};

class NodeContext {
public:
    explicit NodeContext(AgentFlowNodeContext* value) noexcept : value_(value) {}

    AgentFlowNodeContext* NativeHandle() const noexcept { return value_; }
    int InstanceId() const noexcept { return AgentFlowGetInstanceId(value_); }
    std::string NodeType() const noexcept { return AgentFlowGetNodeType(value_); }
    std::string PackageId() const noexcept { return AgentFlowGetPackageId(value_); }
    std::string PackageRoot() const noexcept { return AgentFlowGetPackageRoot(value_); }
    std::string RunId() const noexcept { return AgentFlowGetRunId(value_); }
    bool IsCancelled() const noexcept { return AgentFlowIsCancelled(value_) != 0; }

    std::string ParamString(const std::string& name, const std::string& fallback = {}) const {
        const char* value = AgentFlowParamString(value_, name.c_str(), fallback.c_str());
        return value != nullptr ? value : fallback;
    }

    bool ParamBool(const std::string& name, bool fallback = false) const noexcept {
        return AgentFlowParamBool(value_, name.c_str(), fallback ? 1 : 0) != 0;
    }

    int32_t ParamInt32(const std::string& name, int32_t fallback = 0) const noexcept {
        return AgentFlowParamInt32(value_, name.c_str(), fallback);
    }

    int64_t ParamInt64(const std::string& name, int64_t fallback = 0) const noexcept {
        return AgentFlowParamInt64(value_, name.c_str(), fallback);
    }

    float ParamFloat(const std::string& name, float fallback = 0.0f) const noexcept {
        return AgentFlowParamFloat(value_, name.c_str(), fallback);
    }

    double ParamDouble(const std::string& name, double fallback = 0.0) const noexcept {
        return AgentFlowParamDouble(value_, name.c_str(), fallback);
    }

    void SendString(const std::string& pin, const std::string& value, const std::string& key = {}) {
        AgentFlowSendString(value_, pin.c_str(), value.c_str(), key.c_str());
    }

    void SendInt32(const std::string& pin, int32_t value, const std::string& key = {}) {
        AgentFlowSendInt32(value_, pin.c_str(), value, key.c_str());
    }

    void SendInt64(const std::string& pin, int64_t value, const std::string& key = {}) {
        AgentFlowSendInt64(value_, pin.c_str(), value, key.c_str());
    }

    void SendBool(const std::string& pin, bool value, const std::string& key = {}) {
        AgentFlowSendBool(value_, pin.c_str(), value ? 1 : 0, key.c_str());
    }

    void SendFloat(const std::string& pin, float value, const std::string& key = {}) {
        AgentFlowSendFloat(value_, pin.c_str(), value, key.c_str());
    }

    void SendDouble(const std::string& pin, double value, const std::string& key = {}) {
        AgentFlowSendDouble(value_, pin.c_str(), value, key.c_str());
    }

    void SendBin(const std::string& pin, const unsigned char* value, size_t length,
                 const std::string& key = {}) {
        AgentFlowSendBin(value_, pin.c_str(), value, length, key.c_str());
    }

    void LogTrace(const std::string& message) { AgentFlowLogTrace(value_, message.c_str()); }
    void LogDebug(const std::string& message) { AgentFlowLogDebug(value_, message.c_str()); }
    void LogInfo(const std::string& message) { AgentFlowLogInfo(value_, message.c_str()); }
    void LogWarning(const std::string& message) { AgentFlowLogWarning(value_, message.c_str()); }
    void LogError(const std::string& message) { AgentFlowLogError(value_, message.c_str()); }
    void LogCritical(const std::string& message) { AgentFlowLogCritical(value_, message.c_str()); }

    void EmitError(const std::string& code, const std::string& message, bool fatal = false) {
        AgentFlowEmitError(value_, code.c_str(), message.c_str(), fatal ? 1 : 0);
    }

    void Subscribe(const std::string& topic) { AgentFlowSubscribe(value_, topic.c_str()); }
    void Unsubscribe(const std::string& topic) { AgentFlowUnsubscribe(value_, topic.c_str()); }
    void PublishJson(const std::string& topic, const std::string& payloadJson) {
        AgentFlowPublishJson(value_, topic.c_str(), payloadJson.c_str());
    }

    bool Sleep(int milliseconds) noexcept { return AgentFlowSleep(value_, milliseconds) != 0; }
    bool WaitForStop(int timeoutMs = -1) noexcept { return AgentFlowWaitForStop(value_, timeoutMs) != 0; }

private:
    AgentFlowNodeContext* value_;
};

class ExternalNode {
public:
    virtual ~ExternalNode() = default;

    virtual void Configure(NodeContext&, const std::string&) {}
    virtual void Init(NodeContext&) {}
    virtual void Run(NodeContext&) {}
    virtual void Stop(NodeContext&) {}
    virtual void Shutdown(NodeContext&) {}
    virtual void OnSample(NodeContext&, const std::string&, const Sample&) {}
    virtual void OnBroadcast(NodeContext&, const std::string&, const std::string&, const std::string&) {}
};

class NodeApplication {
public:
    NodeApplication() = default;
    NodeApplication(const NodeApplication&) = delete;
    NodeApplication& operator=(const NodeApplication&) = delete;

    template <typename NodeType>
    NodeApplication& RegisterNode(const std::string& nodeType) {
        static_assert(std::is_base_of_v<ExternalNode, NodeType>,
                      "NodeType must derive from AgentFlow::ExternalNode.");

        auto registration = std::make_unique<Registration>();
        registration->Type = nodeType;
        registration->Definition.NodeType = registration->Type.c_str();
        registration->Definition.StateSize = sizeof(NodeType);
        registration->Definition.StateInit = &StateInit<NodeType>;
        registration->Definition.StateDestroy = &StateDestroy<NodeType>;
        registration->Definition.Callbacks.Configure = &ConfigureCallback<NodeType>;
        registration->Definition.Callbacks.Init = &InitCallback<NodeType>;
        registration->Definition.Callbacks.Run = &RunCallback<NodeType>;
        registration->Definition.Callbacks.Stop = &StopCallback<NodeType>;
        registration->Definition.Callbacks.Shutdown = &ShutdownCallback<NodeType>;
        registration->Definition.Callbacks.OnSample = &OnSampleCallback<NodeType>;
        registration->Definition.Callbacks.OnBroadcast = &OnBroadcastCallback<NodeType>;
        registrations_.push_back(std::move(registration));
        return *this;
    }

    int Run(int argc, char** argv) {
        if (registrations_.empty())
            throw std::runtime_error("At least one external node must be registered.");

        std::vector<AgentFlowNodeDefinition> definitions;
        definitions.reserve(registrations_.size());
        for (const auto& registration : registrations_)
            definitions.push_back(registration->Definition);

        return AgentFlowRunNodes(definitions.data(), definitions.size(), argc, argv);
    }

private:
    struct Registration {
        std::string Type;
        AgentFlowNodeDefinition Definition{};
    };

    template <typename NodeType>
    static NodeType* State(AgentFlowNodeContext* context) {
        return static_cast<NodeType*>(AgentFlowGetState(context));
    }

    template <typename NodeType>
    static void StateInit(void* state, AgentFlowNodeContext*) {
        new (state) NodeType();
    }

    template <typename NodeType>
    static void StateDestroy(void* state, AgentFlowNodeContext*) {
        static_cast<NodeType*>(state)->~NodeType();
    }

    template <typename NodeType>
    static void ConfigureCallback(AgentFlowNodeContext* context, const char* parametersJson) {
        NodeContext wrapper(context);
        State<NodeType>(context)->Configure(wrapper, parametersJson != nullptr ? parametersJson : "{}");
    }

    template <typename NodeType>
    static void InitCallback(AgentFlowNodeContext* context) {
        NodeContext wrapper(context);
        State<NodeType>(context)->Init(wrapper);
    }

    template <typename NodeType>
    static void RunCallback(AgentFlowNodeContext* context) {
        NodeContext wrapper(context);
        State<NodeType>(context)->Run(wrapper);
    }

    template <typename NodeType>
    static void StopCallback(AgentFlowNodeContext* context) {
        NodeContext wrapper(context);
        State<NodeType>(context)->Stop(wrapper);
    }

    template <typename NodeType>
    static void ShutdownCallback(AgentFlowNodeContext* context) {
        NodeContext wrapper(context);
        State<NodeType>(context)->Shutdown(wrapper);
    }

    template <typename NodeType>
    static void OnSampleCallback(AgentFlowNodeContext* context, const char* pin, const AgentFlowSample* sample) {
        NodeContext wrapper(context);
        State<NodeType>(context)->OnSample(
            wrapper,
            pin != nullptr ? pin : "",
            Sample(sample));
    }

    template <typename NodeType>
    static void OnBroadcastCallback(AgentFlowNodeContext* context, const char* topic,
                            const char* payloadKind, const char* payloadJson) {
        NodeContext wrapper(context);
        State<NodeType>(context)->OnBroadcast(
            wrapper,
            topic != nullptr ? topic : "",
            payloadKind != nullptr ? payloadKind : "json",
            payloadJson != nullptr ? payloadJson : "null");
    }

    std::vector<std::unique_ptr<Registration>> registrations_;
};

using WorkerRuntime = NodeApplication;

} // namespace AgentFlow



