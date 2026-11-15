// -----------------------------------------------------------------------
// Copyright (c) 2026 Damon Yang. All rights reserved.
// -----------------------------------------------------------------------
#include "AgentFlowNode.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#if defined(_WIN32)
#  include <windows.h>
#else
#  include <unistd.h>
#endif

typedef struct AgentFlowNodeInstance {
    const AgentFlowNodeDefinition *Definition;
    void *State;
} AgentFlowNodeInstance;

static void *AgentFlowCreateNodeInstance(void *RegistrationData, AgentFlowNodeContext *Context) {
    const AgentFlowNodeDefinition *definition = (const AgentFlowNodeDefinition *)RegistrationData;
    AgentFlowNodeInstance *instance;
    if (definition == NULL) return NULL;
    instance = (AgentFlowNodeInstance *)calloc(1, sizeof(*instance));
    if (instance == NULL) return NULL;
    instance->Definition = definition;
    if (definition->StateSize > 0) {
        instance->State = calloc(1, definition->StateSize);
        if (instance->State == NULL) {
            free(instance);
            return NULL;
        }
    }
    if (definition->StateInit != NULL)
        definition->StateInit(instance->State, Context);
    return instance;
}

static void AgentFlowDestroyNodeInstance(void *UserData) {
    AgentFlowNodeInstance *instance = (AgentFlowNodeInstance *)UserData;
    if (instance == NULL) return;
    if (instance->Definition != NULL && instance->Definition->StateDestroy != NULL)
        instance->Definition->StateDestroy(instance->State, NULL);
    free(instance->State);
    free(instance);
}

static const AgentFlowNodeDefinition *AgentFlowDefinition(AgentFlowNodeContext *Context) {
    AgentFlowNodeInstance *instance = (AgentFlowNodeInstance *)agf_context_userdata(Context);
    return instance != NULL ? instance->Definition : NULL;
}

static void AgentFlowConfigureBridge(AgentFlowNodeContext *Context, const char *ParametersJson) {
    const AgentFlowNodeDefinition *definition = AgentFlowDefinition(Context);
    if (definition != NULL && definition->Callbacks.Configure != NULL)
        definition->Callbacks.Configure(Context, ParametersJson);
}

static void AgentFlowInitBridge(AgentFlowNodeContext *Context) {
    const AgentFlowNodeDefinition *definition = AgentFlowDefinition(Context);
    if (definition != NULL && definition->Callbacks.Init != NULL)
        definition->Callbacks.Init(Context);
}

static void AgentFlowRunBridge(AgentFlowNodeContext *Context) {
    const AgentFlowNodeDefinition *definition = AgentFlowDefinition(Context);
    if (definition != NULL && definition->Callbacks.Run != NULL)
        definition->Callbacks.Run(Context);
}

static void AgentFlowStopBridge(AgentFlowNodeContext *Context) {
    const AgentFlowNodeDefinition *definition = AgentFlowDefinition(Context);
    if (definition != NULL && definition->Callbacks.Stop != NULL)
        definition->Callbacks.Stop(Context);
}

static void AgentFlowShutdownBridge(AgentFlowNodeContext *Context) {
    const AgentFlowNodeDefinition *definition = AgentFlowDefinition(Context);
    if (definition != NULL && definition->Callbacks.Shutdown != NULL)
        definition->Callbacks.Shutdown(Context);
}

static void AgentFlowSampleBridge(
    AgentFlowNodeContext *Context,
    const char *Pin,
    const AgentFlowSample *Sample) {
    const AgentFlowNodeDefinition *definition = AgentFlowDefinition(Context);
    if (definition != NULL && definition->Callbacks.OnSample != NULL)
        definition->Callbacks.OnSample(Context, Pin, Sample);
}

static void AgentFlowBroadcastBridge(
    AgentFlowNodeContext *Context,
    const char *Topic,
    const char *PayloadKind,
    const char *PayloadJson) {
    const AgentFlowNodeDefinition *definition = AgentFlowDefinition(Context);
    if (definition != NULL && definition->Callbacks.OnBroadcast != NULL)
        definition->Callbacks.OnBroadcast(Context, Topic, PayloadKind, PayloadJson);
}

static const char *AgentFlowFindPackageRoot(int argc, char **argv) {
    int index;
    for (index = 0; index + 1 < argc; index++) {
        if (strcmp(argv[index], "--package-root") == 0)
            return argv[index + 1];
    }
    return NULL;
}

static char *AgentFlowManifestFromArguments(int argc, char **argv) {
    const char *root = AgentFlowFindPackageRoot(argc, argv);
    size_t length;
    char *path;
    if (root == NULL) return NULL;
    length = strlen(root) + sizeof("/plugin.json");
    path = (char *)malloc(length);
    if (path == NULL) return NULL;
    snprintf(path, length, "%s/plugin.json", root);
    return path;
}

int AgentFlowRunNodesWithManifest(
    const AgentFlowNodeDefinition *Definitions,
    size_t DefinitionCount,
    const char *ManifestPath,
    int Argc,
    char **Argv) {
    AgentFlowRuntime *runtime;
    const char *error = NULL;
    size_t index;
    int result;

    if (Definitions == NULL || DefinitionCount == 0 || ManifestPath == NULL || *ManifestPath == '\0')
        return 1;

    runtime = agf_runtime_create_from_manifest(ManifestPath, &error);
    if (runtime == NULL) {
        fprintf(stderr, "AgentFlow runtime create failed: %s\n", error != NULL ? error : "unknown error");
        return 1;
    }

    for (index = 0; index < DefinitionCount; index++) {
        agf_node_callbacks callbacks;
        memset(&callbacks, 0, sizeof(callbacks));
        callbacks.userdata = (void *)&Definitions[index];
        callbacks.create_userdata_ex = AgentFlowCreateNodeInstance;
        callbacks.destroy_userdata = AgentFlowDestroyNodeInstance;
        callbacks.configure = AgentFlowConfigureBridge;
        callbacks.init = AgentFlowInitBridge;
        callbacks.run = AgentFlowRunBridge;
        callbacks.stop = AgentFlowStopBridge;
        callbacks.shutdown = AgentFlowShutdownBridge;
        callbacks.on_sample = AgentFlowSampleBridge;
        callbacks.on_broadcast = AgentFlowBroadcastBridge;
        if (agf_register_node(runtime, Definitions[index].NodeType, &callbacks, &error) != 0) {
            fprintf(stderr, "AgentFlow node registration failed: %s\n", error != NULL ? error : "unknown error");
            agf_runtime_destroy(runtime);
            return 1;
        }
    }

    result = agf_run(runtime, Argc, Argv);
    agf_runtime_destroy(runtime);
    return result;
}

int AgentFlowRunNodes(
    const AgentFlowNodeDefinition *Definitions,
    size_t DefinitionCount,
    int Argc,
    char **Argv) {
    char *manifest = AgentFlowManifestFromArguments(Argc, Argv);
    int result;
    if (manifest != NULL) {
        result = AgentFlowRunNodesWithManifest(Definitions, DefinitionCount, manifest, Argc, Argv);
        free(manifest);
        return result;
    }
    return AgentFlowRunNodesWithManifest(Definitions, DefinitionCount, "../plugin.json", Argc, Argv);
}

void *AgentFlowGetState(AgentFlowNodeContext *Context) {
    AgentFlowNodeInstance *instance = (AgentFlowNodeInstance *)agf_context_userdata(Context);
    return instance != NULL ? instance->State : NULL;
}

int AgentFlowGetInstanceId(AgentFlowNodeContext *Context) { return agf_context_instance_id(Context); }
const char *AgentFlowGetNodeType(AgentFlowNodeContext *Context) { return agf_context_node_type(Context); }
const char *AgentFlowGetPackageId(AgentFlowNodeContext *Context) { return agf_context_package_id(Context); }
const char *AgentFlowGetPackageRoot(AgentFlowNodeContext *Context) { return agf_context_package_root(Context); }
const char *AgentFlowGetRunId(AgentFlowNodeContext *Context) { return agf_context_run_id(Context); }
int AgentFlowIsCancelled(AgentFlowNodeContext *Context) { return agf_context_is_cancelled(Context); }
int AgentFlowSleep(AgentFlowNodeContext *Context, int Milliseconds) { return agf_context_sleep(Context, Milliseconds); }
int AgentFlowWaitForStop(AgentFlowNodeContext *Context, int TimeoutMs) { return agf_context_wait_for_stop(Context, TimeoutMs); }

const char *AgentFlowParamString(AgentFlowNodeContext *Context, const char *Name, const char *DefaultValue) {
    return agf_context_param_string(Context, Name, DefaultValue);
}
int AgentFlowParamBool(AgentFlowNodeContext *Context, const char *Name, int DefaultValue) {
    return agf_context_param_bool(Context, Name, DefaultValue);
}
int32_t AgentFlowParamInt32(AgentFlowNodeContext *Context, const char *Name, int32_t DefaultValue) {
    return agf_context_param_int32(Context, Name, DefaultValue);
}
int64_t AgentFlowParamInt64(AgentFlowNodeContext *Context, const char *Name, int64_t DefaultValue) {
    return agf_context_param_int64(Context, Name, DefaultValue);
}
float AgentFlowParamFloat(AgentFlowNodeContext *Context, const char *Name, float DefaultValue) {
    return agf_context_param_float(Context, Name, DefaultValue);
}
double AgentFlowParamDouble(AgentFlowNodeContext *Context, const char *Name, double DefaultValue) {
    return agf_context_param_double(Context, Name, DefaultValue);
}

int AgentFlowSendString(AgentFlowNodeContext *Context, const char *Pin, const char *Value, const char *Key) {
    return agf_send_string(Context, Pin, Value, Key);
}
int AgentFlowSendInt32(AgentFlowNodeContext *Context, const char *Pin, int32_t Value, const char *Key) {
    return agf_send_int32(Context, Pin, Value, Key);
}
int AgentFlowSendInt64(AgentFlowNodeContext *Context, const char *Pin, int64_t Value, const char *Key) {
    return agf_send_int64(Context, Pin, Value, Key);
}
int AgentFlowSendBool(AgentFlowNodeContext *Context, const char *Pin, int Value, const char *Key) {
    return agf_send_bool(Context, Pin, Value, Key);
}
int AgentFlowSendFloat(AgentFlowNodeContext *Context, const char *Pin, float Value, const char *Key) {
    return agf_send_float(Context, Pin, Value, Key);
}
int AgentFlowSendDouble(AgentFlowNodeContext *Context, const char *Pin, double Value, const char *Key) {
    return agf_send_double(Context, Pin, Value, Key);
}
int AgentFlowSendBin(AgentFlowNodeContext *Context, const char *Pin,
                     const unsigned char *Value, size_t Length, const char *Key) {
    return agf_send_bin(Context, Pin, Value, Length, Key);
}

static int AgentFlowLog(AgentFlowNodeContext *Context, const char *Level, const char *Message) {
    return agf_log(Context, Level, Message);
}
int AgentFlowLogTrace(AgentFlowNodeContext *Context, const char *Message) { return AgentFlowLog(Context, "trace", Message); }
int AgentFlowLogDebug(AgentFlowNodeContext *Context, const char *Message) { return AgentFlowLog(Context, "debug", Message); }
int AgentFlowLogInfo(AgentFlowNodeContext *Context, const char *Message) { return AgentFlowLog(Context, "info", Message); }
int AgentFlowLogWarning(AgentFlowNodeContext *Context, const char *Message) { return AgentFlowLog(Context, "warning", Message); }
int AgentFlowLogError(AgentFlowNodeContext *Context, const char *Message) { return AgentFlowLog(Context, "error", Message); }
int AgentFlowLogCritical(AgentFlowNodeContext *Context, const char *Message) { return AgentFlowLog(Context, "critical", Message); }
int AgentFlowEmitError(AgentFlowNodeContext *Context, const char *Code, const char *Message, int Fatal) {
    return agf_emit_error(Context, Code, Message, Fatal);
}

int AgentFlowSubscribe(AgentFlowNodeContext *Context, const char *Topic) { return agf_subscribe(Context, Topic); }
int AgentFlowUnsubscribe(AgentFlowNodeContext *Context, const char *Topic) { return agf_unsubscribe(Context, Topic); }
int AgentFlowPublishJson(AgentFlowNodeContext *Context, const char *Topic, const char *PayloadJson) {
    return agf_publish_json(Context, Topic, PayloadJson);
}

