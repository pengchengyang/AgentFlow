// -----------------------------------------------------------------------
// Copyright (c) 2026 Damon Yang. All rights reserved.
// -----------------------------------------------------------------------
#include <algorithm>
#include <cctype>
#include <cstdio>
#include <string>

#include "AgentFlowNode.hpp"

class CppUpperNode final : public AgentFlow::ExternalNode {
public:
    void Configure(AgentFlow::NodeContext& context, const std::string& parametersJson) override {
        (void)parametersJson;
        context.LogInfo("configure");
    }

    void Init(AgentFlow::NodeContext& context) override {
        context.LogInfo("init");
    }

    void Run(AgentFlow::NodeContext& context) override {
        context.LogInfo("run");
        context.PublishJson("Demo.External.Stats", "{\"event\":\"run\",\"language\":\"cpp\"}");
    }

    void Stop(AgentFlow::NodeContext& context) override {
        context.LogInfo("stop");
    }

    void Shutdown(AgentFlow::NodeContext& context) override {
        context.LogInfo("shutdown");
    }

    void OnSample(
        AgentFlow::NodeContext& context,
        const std::string& pin,
        const AgentFlow::Sample& sample) override {
        std::string value = sample.AsString();
        if (pin == "input_string_toupper") {
            std::transform(value.begin(), value.end(), value.begin(),
                [](unsigned char ch) { return static_cast<char>(std::toupper(ch)); });
            context.SendString("output_string_toupper",
                value + context.ParamString("Suffix", ":cpp"), "output_string_toupper");
        } else if (pin == "input_string_tolower") {
            std::transform(value.begin(), value.end(), value.begin(),
                [](unsigned char ch) { return static_cast<char>(std::tolower(ch)); });
            context.SendString("output_string_tolower",
                value + context.ParamString("Suffix", ":cpp"), "output_string_tolower");
        } else {
            context.EmitError("PIN_UNKNOWN", "unknown input pin", false);
        }
    }

    void OnBroadcast(
        AgentFlow::NodeContext& context,
        const std::string& topic,
        const std::string&,
        const std::string&) override {
        context.LogInfo(topic);
        context.PublishJson("Demo.External.PingAck", "{\"event\":\"ping-ack\",\"language\":\"cpp\"}");
    }
};

int main(int argc, char** argv) {
    try {
        AgentFlow::NodeApplication application;
        application.RegisterNode<CppUpperNode>("Node.CppUpper");
        return application.Run(argc, argv);
    } catch (const std::exception& error) {
        std::fprintf(stderr, "Node.CppUpper failed: %s\n", error.what());
        return 1;
    }
}
