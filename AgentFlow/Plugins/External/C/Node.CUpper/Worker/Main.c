// -----------------------------------------------------------------------
// Copyright (c) 2026 Damon Yang. All rights reserved.
// -----------------------------------------------------------------------
#include <ctype.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "AgentFlowNode.h"

typedef struct UpperState {
    char Suffix[128];
} UpperState;

static void UpperStateInit(void *State, AgentFlowNodeContext *Context) {
    UpperState *state = (UpperState *)State;
    (void)Context;
    snprintf(state->Suffix, sizeof(state->Suffix), "%s", ":c");
}

static void UpperConfigure(AgentFlowNodeContext *Context, const char *ParametersJson) {
    UpperState *state = (UpperState *)AgentFlowGetState(Context);
    const char *suffix = AgentFlowParamString(Context, "Suffix", ":c");
    (void)ParametersJson;
    snprintf(state->Suffix, sizeof(state->Suffix), "%s", suffix != NULL ? suffix : ":c");
    AgentFlowLogInfo(Context, "configure");
}

static void UpperInit(AgentFlowNodeContext *Context) {
    AgentFlowLogInfo(Context, "init");
}

static void UpperRun(AgentFlowNodeContext *Context) {
    AgentFlowLogInfo(Context, "run");
    AgentFlowPublishJson(Context, "Demo.External.Stats", "{\"event\":\"run\",\"language\":\"c\"}");
}

static void UpperStop(AgentFlowNodeContext *Context) {
    AgentFlowLogInfo(Context, "stop");
}

static void UpperShutdown(AgentFlowNodeContext *Context) {
    AgentFlowLogInfo(Context, "shutdown");
}

static void UpperOnSample(
    AgentFlowNodeContext *Context,
    const char *Pin,
    const AgentFlowSample *Sample) {
    UpperState *state = (UpperState *)AgentFlowGetState(Context);
    char output[4096];
    size_t index;
    const char *value;

    const char *outputPin;
    int uppercase;

    if (strcmp(Pin, "input_string_toupper") == 0) {
        outputPin = "output_string_toupper";
        uppercase = 1;
    } else if (strcmp(Pin, "input_string_tolower") == 0) {
        outputPin = "output_string_tolower";
        uppercase = 0;
    } else {
        AgentFlowEmitError(Context, "PIN_UNKNOWN", "unknown input pin", 0);
        return;
    }

    value = Sample != NULL && Sample->string_value != NULL ? Sample->string_value : "";
    snprintf(output, sizeof(output), "%s", value);
    for (index = 0; output[index] != '\0' && index + 1 < sizeof(output); index++)
        output[index] = (char)(uppercase ? toupper((unsigned char)output[index]) : tolower((unsigned char)output[index]));
    strncat(output, state->Suffix, sizeof(output) - strlen(output) - 1);
    AgentFlowSendString(Context, outputPin, output, outputPin);
}

static void UpperOnBroadcast(
    AgentFlowNodeContext *Context,
    const char *Topic,
    const char *PayloadKind,
    const char *PayloadJson) {
    (void)PayloadKind;
    (void)PayloadJson;
    AgentFlowLogInfo(Context, Topic);
    AgentFlowPublishJson(Context, "Demo.External.PingAck", "{\"event\":\"ping-ack\",\"language\":\"c\"}");
}

int main(int argc, char **argv) {
    AgentFlowNodeDefinition definitions[1];

    memset(definitions, 0, sizeof(definitions));
    definitions[0].NodeType = "Node.CUpper";
    definitions[0].StateSize = sizeof(UpperState);
    definitions[0].StateInit = UpperStateInit;
    definitions[0].Callbacks.Configure = UpperConfigure;
    definitions[0].Callbacks.Init = UpperInit;
    definitions[0].Callbacks.Run = UpperRun;
    definitions[0].Callbacks.Stop = UpperStop;
    definitions[0].Callbacks.Shutdown = UpperShutdown;
    definitions[0].Callbacks.OnSample = UpperOnSample;
    definitions[0].Callbacks.OnBroadcast = UpperOnBroadcast;

    return AgentFlowRunNodes(definitions, 1, argc, argv);
}
