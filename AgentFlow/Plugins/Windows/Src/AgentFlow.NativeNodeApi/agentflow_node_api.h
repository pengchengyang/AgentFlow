#ifndef AGENTFLOW_NODE_API_H
#define AGENTFLOW_NODE_API_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#  if defined(AGENTFLOW_NODE_API_BUILD)
#    define AF_NODE_API __declspec(dllexport)
#  else
#    define AF_NODE_API __declspec(dllimport)
#  endif
#  define AF_CALL __cdecl
#else
#  define AF_NODE_API __attribute__((visibility("default")))
#  define AF_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define AF_ABI_VERSION 1u

typedef int32_t AfStatus;

#define AF_OK ((AfStatus)0)
#define AF_ERR_INVALID_ARGUMENT ((AfStatus)-1)
#define AF_ERR_NOT_INITIALIZED ((AfStatus)-2)
#define AF_ERR_ABI_MISMATCH ((AfStatus)-3)
#define AF_ERR_QUEUE_FULL ((AfStatus)-4)
#define AF_ERR_NATIVE_ERROR ((AfStatus)-5)
#define AF_ERR_INTERNAL ((AfStatus)-6)

typedef enum AfSampleType {
    AF_SAMPLE_UNKNOWN = 0,
    AF_SAMPLE_STRING = 1,
    AF_SAMPLE_INT32 = 2,
    AF_SAMPLE_INT64 = 3,
    AF_SAMPLE_BOOL = 4,
    AF_SAMPLE_FLOAT = 5,
    AF_SAMPLE_DOUBLE = 6,
    AF_SAMPLE_BINARY = 7
} AfSampleType;

typedef enum AfLogLevel {
    AF_LOG_TRACE = 0,
    AF_LOG_DEBUG = 1,
    AF_LOG_INFO = 2,
    AF_LOG_WARNING = 3,
    AF_LOG_ERROR = 4,
    AF_LOG_CRITICAL = 5
} AfLogLevel;

/*
 * Sample values are borrowed for the duration of the callback or ABI call only.
 * Receivers must copy string and binary buffers before returning. The structSize and
 * abiVersion fields allow future ABI extensions without changing existing layouts.
 */
typedef struct AfSample {
    uint32_t structSize;
    uint32_t abiVersion;
    int32_t type;
    int32_t boolValue;
    int32_t int32Value;
    int64_t int64Value;
    float floatValue;
    double doubleValue;
    const uint8_t* data;
    uint64_t dataSize;
} AfSample;

typedef AfStatus (AF_CALL *AfSendSampleFn)(
    void* callbackContext,
    const char* pinName,
    uint64_t pinNameLength,
    const AfSample* sample);

typedef void (AF_CALL *AfLogFn)(
    void* callbackContext,
    int32_t level,
    const char* message,
    uint64_t messageLength);

typedef struct AfNodeHostVTable {
    uint32_t structSize;
    uint32_t abiVersion;
    void* callbackContext;
    AfSendSampleFn sendSample;
    AfLogFn log;
} AfNodeHostVTable;

AF_NODE_API uint32_t AF_CALL af_node_get_abi_version(void);

AF_NODE_API AfStatus AF_CALL af_node_create(
    const AfNodeHostVTable* host,
    void* callbackContext,
    void** nodeContext);

AF_NODE_API AfStatus AF_CALL af_node_initialize(void* nodeContext);
AF_NODE_API AfStatus AF_CALL af_node_run(void* nodeContext);
AF_NODE_API AfStatus AF_CALL af_node_receive_sample(void* nodeContext, const AfSample* sample);
AF_NODE_API AfStatus AF_CALL af_node_stop(void* nodeContext);
AF_NODE_API AfStatus AF_CALL af_node_shutdown(void* nodeContext);
AF_NODE_API void AF_CALL af_node_destroy(void* nodeContext);
AF_NODE_API const char* AF_CALL af_node_get_last_error(void* nodeContext);

#ifdef __cplusplus
}
#endif

#endif
