#ifndef AGENTFLOW_NODE_H
#define AGENTFLOW_NODE_H

#include "AgentFlowWorker.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct AgentFlowNodeCallbacks {
    void (*Configure)(AgentFlowNodeContext *Context, const char *ParametersJson);
    void (*Init)(AgentFlowNodeContext *Context);
    void (*Run)(AgentFlowNodeContext *Context);
    void (*Stop)(AgentFlowNodeContext *Context);
    void (*Shutdown)(AgentFlowNodeContext *Context);
    void (*OnSample)(AgentFlowNodeContext *Context, const char *Pin, const AgentFlowSample *Sample);
    void (*OnBroadcast)(AgentFlowNodeContext *Context, const char *Topic,
                        const char *PayloadKind, const char *PayloadJson);
} AgentFlowNodeCallbacks;

typedef struct AgentFlowNodeDefinition {
    const char *NodeType;
    size_t StateSize;
    void (*StateInit)(void *State, AgentFlowNodeContext *Context);
    void (*StateDestroy)(void *State, AgentFlowNodeContext *Context);
    AgentFlowNodeCallbacks Callbacks;
} AgentFlowNodeDefinition;

AGF_API int AgentFlowRunNodes(
    const AgentFlowNodeDefinition *Definitions,
    size_t DefinitionCount,
    int Argc,
    char **Argv);

AGF_API int AgentFlowRunNodesWithManifest(
    const AgentFlowNodeDefinition *Definitions,
    size_t DefinitionCount,
    const char *ManifestPath,
    int Argc,
    char **Argv);

AGF_API void *AgentFlowGetState(AgentFlowNodeContext *Context);
AGF_API int AgentFlowGetInstanceId(AgentFlowNodeContext *Context);
AGF_API const char *AgentFlowGetNodeType(AgentFlowNodeContext *Context);
AGF_API const char *AgentFlowGetPackageId(AgentFlowNodeContext *Context);
AGF_API const char *AgentFlowGetPackageRoot(AgentFlowNodeContext *Context);
AGF_API const char *AgentFlowGetRunId(AgentFlowNodeContext *Context);
AGF_API int AgentFlowIsCancelled(AgentFlowNodeContext *Context);
AGF_API int AgentFlowSleep(AgentFlowNodeContext *Context, int Milliseconds);
AGF_API int AgentFlowWaitForStop(AgentFlowNodeContext *Context, int TimeoutMs);

AGF_API const char *AgentFlowParamString(
    AgentFlowNodeContext *Context, const char *Name, const char *DefaultValue);
AGF_API int AgentFlowParamBool(AgentFlowNodeContext *Context, const char *Name, int DefaultValue);
AGF_API int32_t AgentFlowParamInt32(AgentFlowNodeContext *Context, const char *Name, int32_t DefaultValue);
AGF_API int64_t AgentFlowParamInt64(AgentFlowNodeContext *Context, const char *Name, int64_t DefaultValue);
AGF_API float AgentFlowParamFloat(AgentFlowNodeContext *Context, const char *Name, float DefaultValue);
AGF_API double AgentFlowParamDouble(AgentFlowNodeContext *Context, const char *Name, double DefaultValue);

AGF_API int AgentFlowSendString(
    AgentFlowNodeContext *Context, const char *Pin, const char *Value, const char *Key);
AGF_API int AgentFlowSendInt32(
    AgentFlowNodeContext *Context, const char *Pin, int32_t Value, const char *Key);
AGF_API int AgentFlowSendInt64(
    AgentFlowNodeContext *Context, const char *Pin, int64_t Value, const char *Key);
AGF_API int AgentFlowSendBool(
    AgentFlowNodeContext *Context, const char *Pin, int Value, const char *Key);
AGF_API int AgentFlowSendFloat(
    AgentFlowNodeContext *Context, const char *Pin, float Value, const char *Key);
AGF_API int AgentFlowSendDouble(
    AgentFlowNodeContext *Context, const char *Pin, double Value, const char *Key);
AGF_API int AgentFlowSendBin(
    AgentFlowNodeContext *Context, const char *Pin,
    const unsigned char *Value, size_t Length, const char *Key);

AGF_API int AgentFlowLogTrace(AgentFlowNodeContext *Context, const char *Message);
AGF_API int AgentFlowLogDebug(AgentFlowNodeContext *Context, const char *Message);
AGF_API int AgentFlowLogInfo(AgentFlowNodeContext *Context, const char *Message);
AGF_API int AgentFlowLogWarning(AgentFlowNodeContext *Context, const char *Message);
AGF_API int AgentFlowLogError(AgentFlowNodeContext *Context, const char *Message);
AGF_API int AgentFlowLogCritical(AgentFlowNodeContext *Context, const char *Message);
AGF_API int AgentFlowEmitError(
    AgentFlowNodeContext *Context, const char *Code, const char *Message, int Fatal);

AGF_API int AgentFlowSubscribe(AgentFlowNodeContext *Context, const char *Topic);
AGF_API int AgentFlowUnsubscribe(AgentFlowNodeContext *Context, const char *Topic);
AGF_API int AgentFlowPublishJson(
    AgentFlowNodeContext *Context, const char *Topic, const char *PayloadJson);

#ifdef __cplusplus
}
#endif

#endif

