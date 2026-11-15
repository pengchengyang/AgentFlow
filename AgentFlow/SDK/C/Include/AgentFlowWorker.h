#ifndef AGENTFLOW_WORKER_H
#define AGENTFLOW_WORKER_H

#ifdef __cplusplus
extern "C" {
#endif

#include <stddef.h>
#include <stdint.h>

#if defined(AGENTFLOW_WORKER_STATIC)
#  define AGF_API
#elif defined(_WIN32)
#  if defined(AGENTFLOW_WORKER_BUILD)
#    define AGF_API __declspec(dllexport)
#  else
#    define AGF_API __declspec(dllimport)
#  endif
#else
#  define AGF_API __attribute__((visibility("default")))
#endif

typedef struct agf_runtime agf_runtime;
typedef struct agf_node_context agf_node_context;

typedef struct agf_sample {
    const char *data_type;
    const char *key;
    const char *encoding;
    const char *string_value;
    int32_t int32_value;
    int64_t int64_value;
    int bool_value;
    float float_value;
    double double_value;
    const unsigned char *bin_value;
    size_t bin_length;
} agf_sample;

typedef void *(*agf_create_userdata_ex)(void *registration_userdata, agf_node_context *ctx);

typedef struct agf_node_callbacks {
    /* Registration data is copied into each node instance. */
    void *userdata;
    /* Optional per-node-instance state factory. A Worker may host several node instances,
       so state must not be stored in a process-wide static variable. */
    void *(*create_userdata)(const char *parameters_json);
    /* Preferred factory for state that needs the new NodeContext. */
    agf_create_userdata_ex create_userdata_ex;
    void (*destroy_userdata)(void *userdata);
    void (*configure)(agf_node_context *ctx, const char *parameters_json);
    void (*init)(agf_node_context *ctx);
    void (*run)(agf_node_context *ctx);
    void (*stop)(agf_node_context *ctx);
    void (*shutdown)(agf_node_context *ctx);
    void (*on_sample)(agf_node_context *ctx, const char *pin, const agf_sample *sample);
    void (*on_broadcast)(agf_node_context *ctx, const char *topic,
                         const char *payload_kind, const char *payload_json);
} agf_node_callbacks;

/* PascalCase compatibility aliases for the simplified C API. */
typedef agf_runtime AgentFlowRuntime;
typedef agf_node_context AgentFlowNodeContext;
typedef agf_sample AgentFlowSample;
typedef agf_node_callbacks AgentFlowAdvancedCallbacks;

AGF_API agf_runtime *agf_runtime_create_from_manifest(
    const char *manifest_path,
    const char **error_message);

AGF_API int agf_register_node(
    agf_runtime *runtime,
    const char *node_type,
    const agf_node_callbacks *callbacks,
    const char **error_message);

AGF_API int agf_run(agf_runtime *runtime, int argc, char **argv);

AGF_API void agf_runtime_destroy(agf_runtime *runtime);

AGF_API void *agf_context_userdata(agf_node_context *ctx);
AGF_API int agf_context_instance_id(agf_node_context *ctx);
AGF_API const char *agf_context_node_type(agf_node_context *ctx);
AGF_API const char *agf_context_package_id(agf_node_context *ctx);
AGF_API const char *agf_context_package_root(agf_node_context *ctx);
AGF_API const char *agf_context_run_id(agf_node_context *ctx);
AGF_API int agf_context_is_cancelled(agf_node_context *ctx);
AGF_API int agf_context_sleep(agf_node_context *ctx, int milliseconds);
AGF_API int agf_context_wait_for_stop(agf_node_context *ctx, int timeout_ms);
AGF_API const char *agf_context_parameters_json(agf_node_context *ctx);
AGF_API const char *agf_context_param_string(
    agf_node_context *ctx,
    const char *name,
    const char *default_value);
AGF_API int agf_context_param_bool(agf_node_context *ctx, const char *name, int default_value);
AGF_API int32_t agf_context_param_int32(agf_node_context *ctx, const char *name, int32_t default_value);
AGF_API int64_t agf_context_param_int64(agf_node_context *ctx, const char *name, int64_t default_value);
AGF_API float agf_context_param_float(agf_node_context *ctx, const char *name, float default_value);
AGF_API double agf_context_param_double(agf_node_context *ctx, const char *name, double default_value);

AGF_API int agf_send_sample(agf_node_context *ctx, const char *pin, const agf_sample *sample);
AGF_API int agf_send_string(agf_node_context *ctx, const char *pin,
                            const char *value, const char *key);
AGF_API int agf_send_int32(agf_node_context *ctx, const char *pin,
                           int32_t value, const char *key);
AGF_API int agf_send_int64(agf_node_context *ctx, const char *pin,
                           int64_t value, const char *key);
AGF_API int agf_send_bool(agf_node_context *ctx, const char *pin,
                          int value, const char *key);
AGF_API int agf_send_float(agf_node_context *ctx, const char *pin,
                           float value, const char *key);
AGF_API int agf_send_double(agf_node_context *ctx, const char *pin,
                            double value, const char *key);
AGF_API int agf_send_bin(agf_node_context *ctx, const char *pin,
                         const unsigned char *value, size_t length, const char *key);

AGF_API int agf_log(agf_node_context *ctx, const char *level, const char *message);
AGF_API int agf_emit_error(agf_node_context *ctx, const char *code,
                           const char *message, int fatal);
AGF_API int agf_subscribe(agf_node_context *ctx, const char *topic);
AGF_API int agf_unsubscribe(agf_node_context *ctx, const char *topic);
AGF_API int agf_publish_json(agf_node_context *ctx, const char *topic,
                             const char *payload_json);
AGF_API int agf_publish_sample(agf_node_context *ctx, const char *topic,
                               const agf_sample *sample);

#ifdef __cplusplus
}
#endif

#endif
