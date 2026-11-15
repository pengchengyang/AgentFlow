// -----------------------------------------------------------------------
// Copyright (c) 2026 Damon Yang. All rights reserved.
// -----------------------------------------------------------------------
#ifndef _CRT_SECURE_NO_WARNINGS
#  define _CRT_SECURE_NO_WARNINGS
#endif
#ifndef AGENTFLOW_WORKER_BUILD
#  define AGENTFLOW_WORKER_BUILD
#endif

#include "AgentFlowWorker.h"
#include "cJSON.h"

#include <ctype.h>
#include <errno.h>
#include <inttypes.h>
#include <math.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#if defined(_WIN32)
#  include <windows.h>
#else
#  include <pthread.h>
#  include <sys/socket.h>
#  include <sys/un.h>
#  include <unistd.h>
#endif

#define AGF_PROTOCOL_VERSION 1
#define AGF_SDK_VERSION "0.1.0"
#define AGF_DEFAULT_MAX_LINE_BYTES (1024 * 1024)

typedef struct agf_node agf_node;
typedef struct agf_event agf_event;
typedef struct agf_outbound agf_outbound;
typedef struct agf_registered_node agf_registered_node;

#if defined(_WIN32)
typedef SRWLOCK agf_mutex;
typedef CONDITION_VARIABLE agf_cond;
typedef HANDLE agf_thread;
typedef HANDLE agf_transport;
#else
typedef pthread_mutex_t agf_mutex;
typedef pthread_cond_t agf_cond;
typedef pthread_t agf_thread;
typedef int agf_transport;
#endif

enum agf_event_kind { AGF_EVENT_CONFIGURE, AGF_EVENT_INIT, AGF_EVENT_RUN,
    AGF_EVENT_STOP, AGF_EVENT_SHUTDOWN, AGF_EVENT_SAMPLE, AGF_EVENT_BROADCAST };

struct agf_event {
    enum agf_event_kind kind;
    char *correlation_id;
    char *run_id;
    char *pin;
    char *topic;
    char *payload_kind;
    char *json;
    agf_event *next;
};
struct agf_node_context {
    agf_runtime *runtime;
    agf_node *node;
    int instance_id;
    cJSON *parameters;
    char *parameters_json;
    char *run_id;
};
struct agf_node {
    agf_runtime *runtime;
    int instance_id;
    char *node_type;
    agf_node_callbacks callbacks;
    void *userdata;
    agf_node_context context;
    agf_mutex queue_lock;
    agf_cond queue_cond;
    agf_event *queue_head;
    agf_event *queue_tail;
    int closed;
    volatile int cancelled;
    int running;
    agf_thread thread;
};
struct agf_outbound { char *line; agf_outbound *next; };
struct agf_registered_node {
    char *node_type;
    agf_node_callbacks callbacks;
    agf_registered_node *next;
};
struct agf_runtime {
    char *manifest_path;
    char *package_root;
    cJSON *manifest;
    char *package_id;
    char *package_version;
    cJSON *capabilities;
    cJSON *node_types;
    char *endpoint;
    char *token;
    agf_transport transport;
    int transport_connected;
    volatile int closing;
    int max_line_bytes;
    int heartbeat_interval_ms;
    int next_message_id;
    char *worker_id;
    agf_mutex out_lock;
    agf_cond out_cond;
    agf_outbound *out_head;
    agf_outbound *out_tail;
    agf_thread writer_thread;
    agf_thread heartbeat_thread;
    int writer_started;
    int heartbeat_started;
    agf_mutex node_lock;
    agf_node **nodes;
    size_t node_count;
    size_t node_capacity;
    agf_registered_node *registered_nodes;
    char *read_buffer;
    size_t read_buffer_length;
    size_t read_buffer_capacity;
};

static void agf_mutex_init(agf_mutex *m) {
#if defined(_WIN32)
    InitializeSRWLock(m);
#else
    pthread_mutex_init(m, NULL);
#endif
}
static void agf_mutex_destroy(agf_mutex *m) {
#if !defined(_WIN32)
    pthread_mutex_destroy(m);
#else
    (void)m;
#endif
}
static void agf_mutex_lock(agf_mutex *m) {
#if defined(_WIN32)
    AcquireSRWLockExclusive(m);
#else
    pthread_mutex_lock(m);
#endif
}
static void agf_mutex_unlock(agf_mutex *m) {
#if defined(_WIN32)
    ReleaseSRWLockExclusive(m);
#else
    pthread_mutex_unlock(m);
#endif
}
static void agf_cond_init(agf_cond *c) {
#if defined(_WIN32)
    InitializeConditionVariable(c);
#else
    pthread_cond_init(c, NULL);
#endif
}
static void agf_cond_destroy(agf_cond *c) {
#if !defined(_WIN32)
    pthread_cond_destroy(c);
#else
    (void)c;
#endif
}
static void agf_cond_wait(agf_cond *c, agf_mutex *m) {
#if defined(_WIN32)
    SleepConditionVariableSRW(c, m, INFINITE, 0);
#else
    pthread_cond_wait(c, m);
#endif
}
static void agf_cond_signal(agf_cond *c) {
#if defined(_WIN32)
    WakeConditionVariable(c);
#else
    pthread_cond_signal(c);
#endif
}
static void agf_cond_broadcast(agf_cond *c) {
#if defined(_WIN32)
    WakeAllConditionVariable(c);
#else
    pthread_cond_broadcast(c);
#endif
}
static char *agf_strdup(const char *text) {
    size_t n; char *copy;
    if (text == NULL) return NULL;
    n = strlen(text); copy = (char *)malloc(n + 1);
    if (copy != NULL) memcpy(copy, text, n + 1);
    return copy;
}
static void agf_set_error(const char **error_message, const char *message) {
    if (error_message != NULL) *error_message = message;
}
static char *agf_read_file(const char *path) {
    FILE *f; long n; char *text;
    if (path == NULL) return NULL;
    f = fopen(path, "rb"); if (f == NULL) return NULL;
    if (fseek(f, 0, SEEK_END) != 0) { fclose(f); return NULL; }
    n = ftell(f); if (n < 0) { fclose(f); return NULL; }
    rewind(f); text = (char *)malloc((size_t)n + 1);
    if (text == NULL) { fclose(f); return NULL; }
    if (fread(text, 1, (size_t)n, f) != (size_t)n) { free(text); fclose(f); return NULL; }
    text[n] = '\0'; fclose(f); return text;
}
static int agf_next_message_number(agf_runtime *runtime) {
#if defined(_WIN32)
    return (int)InterlockedIncrement((volatile LONG *)&runtime->next_message_id);
#else
    return __sync_add_and_fetch(&runtime->next_message_id, 1);
#endif
}
static char *agf_next_message_id(agf_runtime *runtime) {
    char buffer[64];
    snprintf(buffer, sizeof(buffer), "msg-%d", agf_next_message_number(runtime));
    return agf_strdup(buffer);
}
static cJSON *agf_new_message(agf_runtime *runtime, const char *type, int with_message_id) {
    cJSON *message = cJSON_CreateObject();
    char *message_id;
    if (message == NULL) return NULL;
    cJSON_AddNumberToObject(message, "protocolVersion", AGF_PROTOCOL_VERSION);
    cJSON_AddStringToObject(message, "type", type);
    if (with_message_id) {
        message_id = agf_next_message_id(runtime);
        if (message_id != NULL) { cJSON_AddStringToObject(message, "messageId", message_id); free(message_id); }
    }
    return message;
}
static int agf_transport_write(agf_runtime *runtime, const char *data, size_t length) {
    if (!runtime->transport_connected) return -1;
#if defined(_WIN32)
    { DWORD written = 0; return WriteFile(runtime->transport, data, (DWORD)length, &written, NULL) && written == length ? 0 : -1; }
#else
    { size_t offset = 0; while (offset < length) { ssize_t n = write(runtime->transport, data + offset, length - offset); if (n <= 0) return -1; offset += (size_t)n; } return 0; }
#endif
}
static int agf_transport_write_line(agf_runtime *runtime, const char *line) {
    size_t n = strlen(line);
    if (agf_transport_write(runtime, line, n) != 0) return -1;
    if (n > 0 && line[n - 1] == '\n') return 0;
    return agf_transport_write(runtime, "\n", 1);
}
static void agf_enqueue_line(agf_runtime *runtime, char *line) {
    agf_outbound *item; if (line == NULL) return;
    item = (agf_outbound *)calloc(1, sizeof(*item));
    if (item == NULL) { free(line); return; }
    item->line = line;
    agf_mutex_lock(&runtime->out_lock);
    if (runtime->closing) { agf_mutex_unlock(&runtime->out_lock); free(line); free(item); return; }
    if (runtime->out_tail != NULL) runtime->out_tail->next = item; else runtime->out_head = item;
    runtime->out_tail = item; agf_cond_signal(&runtime->out_cond); agf_mutex_unlock(&runtime->out_lock);
}
static int agf_send_message(agf_runtime *runtime, cJSON *message) {
    char *json; if (message == NULL) return -1;
    json = cJSON_PrintUnformatted(message); cJSON_Delete(message);
    if (json == NULL) return -1; agf_enqueue_line(runtime, json); return 0;
}
static agf_outbound *agf_take_outbound(agf_runtime *runtime) {
    agf_outbound *item;
    agf_mutex_lock(&runtime->out_lock);
    while (runtime->out_head == NULL && !runtime->closing) agf_cond_wait(&runtime->out_cond, &runtime->out_lock);
    if (runtime->closing) { agf_mutex_unlock(&runtime->out_lock); return NULL; }
    item = runtime->out_head;
    if (item != NULL) { runtime->out_head = item->next; if (runtime->out_head == NULL) runtime->out_tail = NULL; }
    agf_mutex_unlock(&runtime->out_lock); return item;
}
#if defined(_WIN32)
static DWORD WINAPI agf_writer_entry(LPVOID arg) { agf_runtime *runtime = (agf_runtime *)arg;
#else
static void *agf_writer_entry(void *arg) { agf_runtime *runtime = (agf_runtime *)arg;
#endif
    while (!runtime->closing) { agf_outbound *item = agf_take_outbound(runtime); if (item == NULL) break;
        if (agf_transport_write_line(runtime, item->line) != 0) { free(item->line); free(item); runtime->closing = 1; break; }
        free(item->line); free(item); }
#if defined(_WIN32)
    return 0;
#else
    return NULL;
#endif
}
#if defined(_WIN32)
static DWORD WINAPI agf_heartbeat_entry(LPVOID arg) { agf_runtime *runtime = (agf_runtime *)arg;
#else
static void *agf_heartbeat_entry(void *arg) { agf_runtime *runtime = (agf_runtime *)arg;
#endif
    int interval_ms = runtime->heartbeat_interval_ms > 0 ? runtime->heartbeat_interval_ms : 5000;
    while (!runtime->closing) {
        cJSON *message;
        int remaining_ms = interval_ms;
#if defined(_WIN32)
        while (!runtime->closing && remaining_ms > 0) {
            int step_ms = remaining_ms > 100 ? 100 : remaining_ms;
            Sleep((DWORD)step_ms);
            remaining_ms -= step_ms;
        }
#else
        while (!runtime->closing && remaining_ms > 0) {
            int step_ms = remaining_ms > 100 ? 100 : remaining_ms;
            struct timespec delay;
            delay.tv_sec = step_ms / 1000;
            delay.tv_nsec = (long)(step_ms % 1000) * 1000000L;
            nanosleep(&delay, NULL);
            remaining_ms -= step_ms;
        }
#endif
        if (runtime->closing) break;
        message = agf_new_message(runtime, "worker.heartbeat", 1);
        if (message == NULL) continue;
        cJSON_AddStringToObject(message, "workerId", runtime->worker_id != NULL ? runtime->worker_id : "");
        cJSON_AddNumberToObject(message, "sequence", agf_next_message_number(runtime));
        agf_send_message(runtime, message);
    }
#if defined(_WIN32)
    return 0;
#else
    return NULL;
#endif
}
static int agf_connect_transport(agf_runtime *runtime, const char *endpoint) {
    runtime->transport_connected = 0;
#if defined(_WIN32)
    runtime->transport = CreateFileA(endpoint, GENERIC_READ | GENERIC_WRITE, 0, NULL, OPEN_EXISTING, 0, NULL);
    if (runtime->transport == INVALID_HANDLE_VALUE) return -1;
#else
    { struct sockaddr_un address; int s = socket(AF_UNIX, SOCK_STREAM, 0); if (s < 0) return -1;
      memset(&address, 0, sizeof(address)); address.sun_family = AF_UNIX;
      if (strlen(endpoint) >= sizeof(address.sun_path)) { close(s); return -1; }
      strcpy(address.sun_path, endpoint);
      if (connect(s, (struct sockaddr *)&address, sizeof(address)) != 0) { close(s); return -1; }
      runtime->transport = s; }
#endif
    runtime->transport_connected = 1; return 0;
}
static void agf_close_transport(agf_runtime *runtime) {
    if (!runtime->transport_connected) return;
    runtime->transport_connected = 0;
#if defined(_WIN32)
    if (runtime->transport != INVALID_HANDLE_VALUE && runtime->transport != NULL) { CancelIoEx(runtime->transport, NULL); CloseHandle(runtime->transport); runtime->transport = INVALID_HANDLE_VALUE; }
#else
    if (runtime->transport >= 0) close(runtime->transport); runtime->transport = -1;
#endif
}
static char *agf_read_line(agf_runtime *runtime) {
    char chunk[8192];
    while (1) {
        char *newline;
        if (runtime->read_buffer != NULL && (newline = strchr(runtime->read_buffer, '\n')) != NULL) {
            size_t line_length = (size_t)(newline - runtime->read_buffer);
            size_t remaining = runtime->read_buffer_length - line_length - 1;
            char *line = (char *)malloc(line_length + 1);
            if (line == NULL) return NULL;
            if (line_length > 0 && runtime->read_buffer[line_length - 1] == '\r') line_length--;
            memcpy(line, runtime->read_buffer, line_length); line[line_length] = '\0';
            memmove(runtime->read_buffer, newline + 1, remaining); runtime->read_buffer_length = remaining;
            runtime->read_buffer[remaining] = '\0'; return line;
        }
#if defined(_WIN32)
        { DWORD available = 0; DWORD read_count = 0;
          if (!PeekNamedPipe(runtime->transport, NULL, 0, NULL, &available, NULL)) {
              DWORD error = GetLastError();
              if (error == ERROR_BROKEN_PIPE || error == ERROR_PIPE_NOT_CONNECTED) return NULL;
              return NULL;
          }
          if (available == 0) {
              if (runtime->closing) return NULL;
              Sleep(10);
              continue;
          }
          if (available > sizeof(chunk)) available = (DWORD)sizeof(chunk);
          if (!ReadFile(runtime->transport, chunk, available, &read_count, NULL) || read_count == 0) return NULL;
          if (runtime->read_buffer_length + read_count + 1 > runtime->read_buffer_capacity) { size_t cap = runtime->read_buffer_capacity == 0 ? 16384 : runtime->read_buffer_capacity * 2; char *buf;
            while (cap < runtime->read_buffer_length + read_count + 1) cap *= 2; buf = (char *)realloc(runtime->read_buffer, cap); if (buf == NULL) return NULL; runtime->read_buffer = buf; runtime->read_buffer_capacity = cap; }
          memcpy(runtime->read_buffer + runtime->read_buffer_length, chunk, read_count); runtime->read_buffer_length += read_count; runtime->read_buffer[runtime->read_buffer_length] = '\0'; }
#else
        { ssize_t read_count = read(runtime->transport, chunk, sizeof(chunk)); if (read_count <= 0) return NULL;
          if (runtime->read_buffer_length + (size_t)read_count + 1 > runtime->read_buffer_capacity) { size_t cap = runtime->read_buffer_capacity == 0 ? 16384 : runtime->read_buffer_capacity * 2; char *buf;
            while (cap < runtime->read_buffer_length + (size_t)read_count + 1) cap *= 2; buf = (char *)realloc(runtime->read_buffer, cap); if (buf == NULL) return NULL; runtime->read_buffer = buf; runtime->read_buffer_capacity = cap; }
          memcpy(runtime->read_buffer + runtime->read_buffer_length, chunk, (size_t)read_count); runtime->read_buffer_length += (size_t)read_count; runtime->read_buffer[runtime->read_buffer_length] = '\0'; }
#endif
        if (runtime->read_buffer_length > (size_t)runtime->max_line_bytes) return NULL;
    }
}
static void agf_free_sample(agf_sample *sample) {
    if (sample == NULL) return;
    free((void *)sample->string_value); free((void *)sample->bin_value); memset(sample, 0, sizeof(*sample));
}
static int agf_base64_decode(const char *text, unsigned char **bytes, size_t *length) {
    size_t n = text == NULL ? 0 : strlen(text), i, oi = 0;
    unsigned char *out;
    if (n == 0) { *bytes = NULL; *length = 0; return 0; }
    if (n % 4 != 0) return -1;
    out = (unsigned char *)malloc(n / 4 * 3 + 1); if (out == NULL) return -1;
    for (i = 0; i < n; i += 4) {
        int c1 = text[i] - 'A', c2 = text[i + 1] - 'A';
        int c3 = text[i + 2] == '=' ? -1 : text[i + 2] - 'A';
        int c4 = text[i + 3] == '=' ? -1 : text[i + 3] - 'A';
        if (c1 < 0 || c2 < 0) { free(out); return -1; }
        out[oi++] = (unsigned char)((c1 << 2) | (c2 >> 4));
        if (c3 >= 0) { out[oi++] = (unsigned char)(((c2 & 15) << 4) | (c3 >> 2)); if (c4 >= 0) out[oi++] = (unsigned char)(((c3 & 3) << 6) | c4); }
    }
    *bytes = out; *length = oi; return 0;
}
static char *agf_base64_encode(const unsigned char *bytes, size_t length) {
    static const char alphabet[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    char *out; size_t i, oi = 0, n = ((length + 2) / 3) * 4;
    if (length == 0) return agf_strdup("");
    out = (char *)malloc(n + 1); if (out == NULL) return NULL;
    for (i = 0; i < length; i += 3) { size_t rem = length - i; unsigned int b0 = bytes[i], b1 = rem > 1 ? bytes[i+1] : 0, b2 = rem > 2 ? bytes[i+2] : 0;
        out[oi++] = alphabet[b0 >> 2]; out[oi++] = alphabet[((b0 & 3) << 4) | (b1 >> 4)];
        out[oi++] = rem > 1 ? alphabet[((b1 & 15) << 2) | (b2 >> 6)] : '='; out[oi++] = rem > 2 ? alphabet[b2 & 63] : '='; }
    out[oi] = '\0'; return out;
}
static uint64_t agf_parse_hex_u64(const char *text) {
    uint64_t v = 0; if (text == NULL) return 0;
    while (*text) { int d; if (*text >= '0' && *text <= '9') d = *text - '0'; else if (*text >= 'a' && *text <= 'f') d = *text - 'a' + 10; else if (*text >= 'A' && *text <= 'F') d = *text - 'A' + 10; else break; v = (v << 4) | (uint64_t)d; text++; }
    return v;
}
static agf_sample agf_sample_from_json(const cJSON *json) {
    agf_sample sample; const cJSON *type, *encoding, *key, *value;
    memset(&sample, 0, sizeof(sample)); if (json == NULL) return sample;
    type = cJSON_GetObjectItemCaseSensitive(json, "dataType"); encoding = cJSON_GetObjectItemCaseSensitive(json, "encoding");
    key = cJSON_GetObjectItemCaseSensitive(json, "key"); value = cJSON_GetObjectItemCaseSensitive(json, "value");
    sample.data_type = cJSON_IsString(type) ? type->valuestring : NULL; sample.encoding = cJSON_IsString(encoding) ? encoding->valuestring : NULL;
    sample.key = cJSON_IsString(key) ? key->valuestring : NULL; if (sample.data_type == NULL) return sample;
    if (strcmp(sample.data_type, "STRING") == 0) sample.string_value = cJSON_IsString(value) ? agf_strdup(value->valuestring) : agf_strdup("");
    else if (strcmp(sample.data_type, "INT32") == 0) sample.int32_value = cJSON_IsNumber(value) ? value->valueint : 0;
    else if (strcmp(sample.data_type, "INT64") == 0) sample.int64_value = cJSON_IsString(value) ? strtoll(value->valuestring, NULL, 10) : 0;
    else if (strcmp(sample.data_type, "BOOL") == 0) sample.bool_value = cJSON_IsTrue(value) ? 1 : 0;
    else if (strcmp(sample.data_type, "FLOAT") == 0 && cJSON_IsString(value)) { uint32_t bits = (uint32_t)agf_parse_hex_u64(value->valuestring); memcpy(&sample.float_value, &bits, sizeof(float)); }
    else if (strcmp(sample.data_type, "DOUBLE") == 0 && cJSON_IsString(value)) { uint64_t bits = agf_parse_hex_u64(value->valuestring); memcpy(&sample.double_value, &bits, sizeof(double)); }
    else if (strcmp(sample.data_type, "BIN") == 0 && cJSON_IsString(value)) agf_base64_decode(value->valuestring, (unsigned char **)&sample.bin_value, &sample.bin_length);
    return sample;
}
static cJSON *agf_sample_to_json(const agf_sample *sample, const char *fallback_key) {
    cJSON *object; const char *key;
    if (sample == NULL || sample->data_type == NULL) return NULL;
    object = cJSON_CreateObject(); if (object == NULL) return NULL;
    key = sample->key != NULL && sample->key[0] != '\0' ? sample->key : fallback_key;
    cJSON_AddStringToObject(object, "key", key != NULL ? key : ""); cJSON_AddStringToObject(object, "dataType", sample->data_type);
    if (strcmp(sample->data_type, "STRING") == 0) { cJSON_AddStringToObject(object, "encoding", "utf8"); cJSON_AddStringToObject(object, "value", sample->string_value != NULL ? sample->string_value : ""); return object; }
    if (strcmp(sample->data_type, "INT32") == 0) { cJSON_AddStringToObject(object, "encoding", "decimal"); cJSON_AddNumberToObject(object, "value", sample->int32_value); return object; }
    if (strcmp(sample->data_type, "INT64") == 0) { char text[64]; snprintf(text, sizeof(text), "%" PRId64, sample->int64_value); cJSON_AddStringToObject(object, "encoding", "decimal-string"); cJSON_AddStringToObject(object, "value", text); return object; }
    if (strcmp(sample->data_type, "BOOL") == 0) { cJSON_AddStringToObject(object, "encoding", "boolean"); cJSON_AddBoolToObject(object, "value", sample->bool_value != 0); return object; }
    if (strcmp(sample->data_type, "FLOAT") == 0) { uint32_t bits; char text[32]; memcpy(&bits, &sample->float_value, sizeof(float)); snprintf(text, sizeof(text), "%08" PRIx32, bits); cJSON_AddStringToObject(object, "encoding", "ieee754-binary32-bits-hex"); cJSON_AddStringToObject(object, "value", text); return object; }
    if (strcmp(sample->data_type, "DOUBLE") == 0) { uint64_t bits; char text[32]; memcpy(&bits, &sample->double_value, sizeof(double)); snprintf(text, sizeof(text), "%016" PRIx64, bits); cJSON_AddStringToObject(object, "encoding", "ieee754-binary64-bits-hex"); cJSON_AddStringToObject(object, "value", text); return object; }
    if (strcmp(sample->data_type, "BIN") == 0) { char *encoded = agf_base64_encode(sample->bin_value, sample->bin_length); cJSON_AddStringToObject(object, "encoding", "base64"); cJSON_AddStringToObject(object, "value", encoded != NULL ? encoded : ""); free(encoded); return object; }
    cJSON_Delete(object); return NULL;
}
int agf_send_sample(agf_node_context *ctx, const char *pin, const agf_sample *sample) {
    cJSON *message, *sample_json;
    if (ctx == NULL || ctx->node == NULL || pin == NULL) return -1;
    message = agf_new_message(ctx->runtime, "pin.send", 1); if (message == NULL) return -1;
    cJSON_AddNumberToObject(message, "nodeInstanceId", ctx->instance_id); cJSON_AddStringToObject(message, "sourcePin", pin);
    cJSON_AddNumberToObject(message, "sequence", agf_next_message_number(ctx->runtime));
    sample_json = agf_sample_to_json(sample, pin); if (sample_json == NULL) { cJSON_Delete(message); return -1; }
    cJSON_AddItemToObject(message, "sample", sample_json); return agf_send_message(ctx->runtime, message);
}
static int agf_send_simple_sample(agf_node_context *ctx, const char *pin, const agf_sample *sample, const char *key) {
    agf_sample copy = *sample; copy.key = key; return agf_send_sample(ctx, pin, &copy);
}
int agf_send_string(agf_node_context *ctx, const char *pin, const char *value, const char *key) { agf_sample s; memset(&s,0,sizeof(s)); s.data_type="STRING"; s.string_value=value!=NULL?value:""; return agf_send_simple_sample(ctx,pin,&s,key); }
int agf_send_int32(agf_node_context *ctx, const char *pin, int32_t value, const char *key) { agf_sample s; memset(&s,0,sizeof(s)); s.data_type="INT32"; s.int32_value=value; return agf_send_simple_sample(ctx,pin,&s,key); }
int agf_send_int64(agf_node_context *ctx, const char *pin, int64_t value, const char *key) { agf_sample s; memset(&s,0,sizeof(s)); s.data_type="INT64"; s.int64_value=value; return agf_send_simple_sample(ctx,pin,&s,key); }
int agf_send_bool(agf_node_context *ctx, const char *pin, int value, const char *key) { agf_sample s; memset(&s,0,sizeof(s)); s.data_type="BOOL"; s.bool_value=value!=0; return agf_send_simple_sample(ctx,pin,&s,key); }
int agf_send_float(agf_node_context *ctx, const char *pin, float value, const char *key) { agf_sample s; memset(&s,0,sizeof(s)); s.data_type="FLOAT"; s.float_value=value; return agf_send_simple_sample(ctx,pin,&s,key); }
int agf_send_double(agf_node_context *ctx, const char *pin, double value, const char *key) { agf_sample s; memset(&s,0,sizeof(s)); s.data_type="DOUBLE"; s.double_value=value; return agf_send_simple_sample(ctx,pin,&s,key); }
int agf_send_bin(agf_node_context *ctx, const char *pin, const unsigned char *value, size_t length, const char *key) { agf_sample s; memset(&s,0,sizeof(s)); s.data_type="BIN"; s.bin_value=value; s.bin_length=length; return agf_send_simple_sample(ctx,pin,&s,key); }
int agf_log(agf_node_context *ctx, const char *level, const char *message) {
    cJSON *msg; if (ctx == NULL || ctx->node == NULL) return -1;
    msg = agf_new_message(ctx->runtime, "log", 0); if (msg == NULL) return -1;
    cJSON_AddNumberToObject(msg, "nodeInstanceId", ctx->instance_id); cJSON_AddStringToObject(msg, "level", level != NULL ? level : "info");
    cJSON_AddStringToObject(msg, "message", message != NULL ? message : ""); return agf_send_message(ctx->runtime, msg);
}
int agf_emit_error(agf_node_context *ctx, const char *code, const char *message, int fatal) {
    cJSON *msg; if (ctx == NULL || ctx->node == NULL) return -1;
    msg = agf_new_message(ctx->runtime, "node.error", 0); if (msg == NULL) return -1;
    cJSON_AddNumberToObject(msg, "nodeInstanceId", ctx->instance_id); cJSON_AddStringToObject(msg, "code", code != NULL ? code : "NODE_ERROR");
    cJSON_AddStringToObject(msg, "message", message != NULL ? message : ""); cJSON_AddBoolToObject(msg, "fatal", fatal != 0);
    return agf_send_message(ctx->runtime, msg);
}
static int agf_send_subscription(agf_node_context *ctx, const char *topic, int subscribe) {
    cJSON *msg; if (ctx == NULL || ctx->node == NULL || topic == NULL) return -1;
    msg = agf_new_message(ctx->runtime, subscribe ? "broadcast.subscribe" : "broadcast.unsubscribe", 1); if (msg == NULL) return -1;
    cJSON_AddNumberToObject(msg, "nodeInstanceId", ctx->instance_id); cJSON_AddStringToObject(msg, "workerId", ctx->runtime->worker_id != NULL ? ctx->runtime->worker_id : "");
    cJSON_AddStringToObject(msg, "topic", topic); return agf_send_message(ctx->runtime, msg);
}
int agf_subscribe(agf_node_context *ctx, const char *topic) { return agf_send_subscription(ctx, topic, 1); }
int agf_unsubscribe(agf_node_context *ctx, const char *topic) { return agf_send_subscription(ctx, topic, 0); }
int agf_publish_json(agf_node_context *ctx, const char *topic, const char *payload_json) {
    cJSON *msg, *payload; if (ctx == NULL || ctx->node == NULL || topic == NULL) return -1;
    payload = payload_json != NULL ? cJSON_Parse(payload_json) : cJSON_CreateNull(); if (payload == NULL) return -1;
    msg = agf_new_message(ctx->runtime, "broadcast.publish", 1); if (msg == NULL) { cJSON_Delete(payload); return -1; }
    cJSON_AddNumberToObject(msg, "nodeInstanceId", ctx->instance_id); cJSON_AddStringToObject(msg, "workerId", ctx->runtime->worker_id != NULL ? ctx->runtime->worker_id : "");
    cJSON_AddStringToObject(msg, "topic", topic); cJSON_AddStringToObject(msg, "payloadKind", "json"); cJSON_AddItemToObject(msg, "payload", payload);
    return agf_send_message(ctx->runtime, msg);
}
int agf_publish_sample(agf_node_context *ctx, const char *topic, const agf_sample *sample) {
    cJSON *msg, *payload; if (ctx == NULL || ctx->node == NULL || topic == NULL) return -1;
    payload = agf_sample_to_json(sample, "broadcast"); if (payload == NULL) return -1;
    msg = agf_new_message(ctx->runtime, "broadcast.publish", 1); if (msg == NULL) { cJSON_Delete(payload); return -1; }
    cJSON_AddNumberToObject(msg, "nodeInstanceId", ctx->instance_id); cJSON_AddStringToObject(msg, "workerId", ctx->runtime->worker_id != NULL ? ctx->runtime->worker_id : "");
    cJSON_AddStringToObject(msg, "topic", topic); cJSON_AddStringToObject(msg, "payloadKind", "sample"); cJSON_AddItemToObject(msg, "payload", payload);
    return agf_send_message(ctx->runtime, msg);
}
static void agf_send_state(agf_node *node, const char *state) {
    cJSON *message = agf_new_message(node->runtime, "node.state", 0); if (message == NULL) return;
    cJSON_AddNumberToObject(message, "nodeInstanceId", node->instance_id); cJSON_AddStringToObject(message, "state", state); agf_send_message(node->runtime, message);
}
static cJSON *agf_make_command_result(agf_node *node, agf_event *event, const char *command, const char *status, const char *code, const char *text) {
    cJSON *r = agf_new_message(node->runtime, "node.command.result", 0); if (r == NULL) return NULL;
    if (event->correlation_id != NULL) cJSON_AddStringToObject(r, "correlationId", event->correlation_id);
    cJSON_AddNumberToObject(r, "nodeInstanceId", node->instance_id); cJSON_AddStringToObject(r, "command", command); cJSON_AddStringToObject(r, "status", status);
    if (code != NULL) cJSON_AddStringToObject(r, "code", code); if (text != NULL) cJSON_AddStringToObject(r, "message", text); return r;
}
static char *agf_package_root_from_manifest(const char *manifest_path) {
    const char *forward;
    const char *backward;
    const char *separator;
    size_t length;
    char *root;
    if (manifest_path == NULL) return NULL;
    forward = strrchr(manifest_path, '/');
    backward = strrchr(manifest_path, '\\');
    separator = forward;
    if (backward != NULL && (separator == NULL || backward > separator)) separator = backward;
    if (separator == NULL) return agf_strdup(".");
    length = (size_t)(separator - manifest_path);
    if (length == 0) length = 1;
    root = (char *)malloc(length + 1);
    if (root == NULL) return NULL;
    memcpy(root, manifest_path, length);
    root[length] = '\0';
    return root;
}
static void agf_free_event(agf_event *event) {
    if (event == NULL) return; free(event->correlation_id); free(event->run_id); free(event->pin); free(event->topic); free(event->payload_kind); free(event->json); free(event);
}
static void agf_handle_event(agf_node *node, agf_event *event) {
    agf_node_context *ctx = &node->context; const agf_node_callbacks *cb = &node->callbacks;
    if (event->kind == AGF_EVENT_INIT) { if (cb->init != NULL) cb->init(ctx); agf_send_message(node->runtime, agf_make_command_result(node,event,"init","ok",NULL,NULL)); }
    else if (event->kind == AGF_EVENT_RUN) {
        free(ctx->run_id); ctx->run_id = agf_strdup(event->run_id); node->cancelled = 0; node->running = 1;
        agf_send_state(node,"Running"); if (cb->run != NULL) cb->run(ctx); agf_send_message(node->runtime, agf_make_command_result(node,event,"run","ok",NULL,NULL));
    }
    else if (event->kind == AGF_EVENT_STOP) { node->cancelled = 1; if (cb->stop != NULL) cb->stop(ctx); node->running = 0; agf_send_state(node,"Stopped"); agf_send_message(node->runtime, agf_make_command_result(node,event,"stop","ok",NULL,NULL)); }
    else if (event->kind == AGF_EVENT_SHUTDOWN) { node->cancelled = 1; if (cb->shutdown != NULL) cb->shutdown(ctx); node->running = 0; agf_send_state(node,"Shutdown"); agf_send_message(node->runtime, agf_make_command_result(node,event,"shutdown","ok",NULL,NULL)); }
    else if (event->kind == AGF_EVENT_CONFIGURE) {
        cJSON *params = event->json != NULL ? cJSON_Parse(event->json) : cJSON_CreateObject(); cJSON *response;
        free(ctx->parameters_json);
        if (ctx->parameters != NULL) cJSON_Delete(ctx->parameters); ctx->parameters = params != NULL ? params : cJSON_CreateObject();
        ctx->parameters_json = ctx->parameters != NULL ? cJSON_PrintUnformatted(ctx->parameters) : agf_strdup("{}");
        if (cb->configure != NULL) cb->configure(ctx, ctx->parameters_json != NULL ? ctx->parameters_json : "{}");
        response = agf_new_message(node->runtime, "node.configure.result", 0);
        if (response != NULL) { if (event->correlation_id != NULL) cJSON_AddStringToObject(response,"correlationId",event->correlation_id); cJSON_AddNumberToObject(response,"nodeInstanceId",node->instance_id); cJSON_AddStringToObject(response,"status","ok"); agf_send_message(node->runtime,response); }
    }
    else if (event->kind == AGF_EVENT_SAMPLE) {
        cJSON *json = event->json != NULL ? cJSON_Parse(event->json) : NULL; agf_sample sample = json != NULL ? agf_sample_from_json(json) : (agf_sample){0};
        if (cb->on_sample != NULL) cb->on_sample(ctx, event->pin != NULL ? event->pin : "", &sample); agf_free_sample(&sample); cJSON_Delete(json);
    }
    else if (event->kind == AGF_EVENT_BROADCAST && cb->on_broadcast != NULL) cb->on_broadcast(ctx, event->topic != NULL ? event->topic : "", event->payload_kind != NULL ? event->payload_kind : "json", event->json != NULL ? event->json : "null");
}
#if defined(_WIN32)
static DWORD WINAPI agf_node_entry(LPVOID arg) { agf_node *node = (agf_node *)arg;
#else
static void *agf_node_entry(void *arg) { agf_node *node = (agf_node *)arg;
#endif
    for (;;) { agf_event *event;
        agf_mutex_lock(&node->queue_lock);
        while (node->queue_head == NULL && !node->closed) agf_cond_wait(&node->queue_cond, &node->queue_lock);
        event = node->queue_head; if (event != NULL) { node->queue_head = event->next; if (node->queue_head == NULL) node->queue_tail = NULL; }
        agf_mutex_unlock(&node->queue_lock);
        if (event == NULL) break; agf_handle_event(node,event); agf_free_event(event); }
#if defined(_WIN32)
    return 0;
#else
    return NULL;
#endif
}
static void agf_push_event(agf_node *node, agf_event *event) {
    if (node == NULL || event == NULL) return; agf_mutex_lock(&node->queue_lock);
    if (node->closed) { agf_mutex_unlock(&node->queue_lock); agf_free_event(event); return; }
    if (node->queue_tail != NULL) node->queue_tail->next = event; else node->queue_head = event;
    node->queue_tail = event; agf_cond_signal(&node->queue_cond); agf_mutex_unlock(&node->queue_lock);
}
static agf_registered_node *agf_find_registered_node(agf_runtime *runtime, const char *node_type) {
    agf_registered_node *r; for (r = runtime->registered_nodes; r != NULL; r = r->next) if (strcmp(r->node_type,node_type)==0) return r; return NULL;
}
static agf_node *agf_find_node(agf_runtime *runtime, int instance_id) {
    size_t i; agf_node *result = NULL; agf_mutex_lock(&runtime->node_lock);
    for (i = 0; i < runtime->node_count; i++) if (runtime->nodes[i]->instance_id == instance_id) { result = runtime->nodes[i]; break; }
    agf_mutex_unlock(&runtime->node_lock); return result;
}
static int agf_add_node(agf_runtime *runtime, agf_node *node) {
    agf_mutex_lock(&runtime->node_lock);
    if (runtime->node_count == runtime->node_capacity) { size_t cap = runtime->node_capacity == 0 ? 8 : runtime->node_capacity * 2; agf_node **nodes = (agf_node **)realloc(runtime->nodes, cap * sizeof(agf_node *)); if (nodes == NULL) { agf_mutex_unlock(&runtime->node_lock); return -1; } runtime->nodes = nodes; runtime->node_capacity = cap; }
    runtime->nodes[runtime->node_count++] = node; agf_mutex_unlock(&runtime->node_lock); return 0;
}
static void agf_destroy_node(agf_runtime *runtime, agf_node *node) {
    if (node == NULL) return;
    if (runtime != NULL) {
        size_t i;
        agf_mutex_lock(&runtime->node_lock);
        for (i = 0; i < runtime->node_count; i++) {
            if (runtime->nodes[i] == node) {
                memmove(&runtime->nodes[i], &runtime->nodes[i + 1],
                        (runtime->node_count - i - 1) * sizeof(agf_node *));
                runtime->node_count--;
                break;
            }
        }
        agf_mutex_unlock(&runtime->node_lock);
    }
    agf_mutex_lock(&node->queue_lock);
    node->closed = 1;
    agf_cond_broadcast(&node->queue_cond);
    agf_mutex_unlock(&node->queue_lock);
#if defined(_WIN32)
    if (node->thread != NULL) { WaitForSingleObject(node->thread, 5000); CloseHandle(node->thread); }
#else
    if (node->thread != 0) pthread_join(node->thread, NULL);
#endif
    while (node->queue_head != NULL) {
        agf_event *event = node->queue_head;
        node->queue_head = event->next;
        agf_free_event(event);
    }
    if (node->callbacks.destroy_userdata != NULL)
        node->callbacks.destroy_userdata(node->userdata);
    cJSON_Delete(node->context.parameters);
    free(node->context.parameters_json);
    free(node->context.run_id);
    agf_cond_destroy(&node->queue_cond);
    agf_mutex_destroy(&node->queue_lock);
    free(node->node_type);
    free(node);
}

static agf_node *agf_create_node(agf_runtime *runtime, int instance_id, const char *node_type, const cJSON *parameters) {
    agf_registered_node *registered = agf_find_registered_node(runtime,node_type);
    agf_node *node;
    char *parameters_json;
    if (registered == NULL) return NULL;
    node = (agf_node *)calloc(1,sizeof(*node)); if (node == NULL) return NULL;
    node->runtime = runtime; node->instance_id = instance_id; node->node_type = agf_strdup(node_type); node->callbacks = registered->callbacks;
    node->context.runtime = runtime; node->context.node = node; node->context.instance_id = instance_id;
    node->context.parameters = parameters != NULL ? cJSON_Duplicate(parameters,1) : cJSON_CreateObject();
    parameters_json = node->context.parameters != NULL ? cJSON_PrintUnformatted(node->context.parameters) : agf_strdup("{}");
    node->context.parameters_json = agf_strdup(parameters_json != NULL ? parameters_json : "{}");
    node->userdata = node->callbacks.create_userdata_ex != NULL
        ? node->callbacks.create_userdata_ex(node->callbacks.userdata, &node->context)
        : (node->callbacks.create_userdata != NULL
            ? node->callbacks.create_userdata(parameters_json != NULL ? parameters_json : "{}")
            : node->callbacks.userdata);
    if ((node->callbacks.create_userdata_ex != NULL || node->callbacks.create_userdata != NULL) && node->userdata == NULL) {
        free(parameters_json);
        cJSON_Delete(node->context.parameters);
        free(node->context.parameters_json);
        free(node->node_type);
        free(node);
        return NULL;
    }
    agf_mutex_init(&node->queue_lock); agf_cond_init(&node->queue_cond);
    if (node->callbacks.configure != NULL)
        node->callbacks.configure(&node->context, parameters_json != NULL ? parameters_json : "{}");
    free(parameters_json);
#if defined(_WIN32)
    node->thread = CreateThread(NULL,0,agf_node_entry,node,0,NULL); if (node->thread == NULL)
#else
    if (pthread_create(&node->thread,NULL,agf_node_entry,node) != 0)
#endif
    { agf_destroy_node(NULL, node); return NULL; }
    if (agf_add_node(runtime,node) != 0) { agf_destroy_node(NULL, node); return NULL; }
    return node;
}
static cJSON *agf_create_response_for_request(agf_runtime *runtime, const cJSON *request, const char *type, const char *status, const char *code, const char *text) {
    const cJSON *correlation = cJSON_GetObjectItemCaseSensitive(request,"messageId"); cJSON *response = agf_new_message(runtime,type,0); if (response == NULL) return NULL;
    if (cJSON_IsString(correlation)) cJSON_AddStringToObject(response,"correlationId",correlation->valuestring);
    if (code != NULL) cJSON_AddStringToObject(response,"code",code); if (text != NULL) cJSON_AddStringToObject(response,"message",text); if (status != NULL) cJSON_AddStringToObject(response,"status",status); return response;
}
static void agf_create_event_command(agf_runtime *runtime, const cJSON *message, const char *command) {
    const cJSON *instance_item = cJSON_GetObjectItemCaseSensitive(message,"nodeInstanceId"); const cJSON *message_id = cJSON_GetObjectItemCaseSensitive(message,"messageId");
    agf_node *node = agf_find_node(runtime, cJSON_IsNumber(instance_item) ? instance_item->valueint : 0); agf_event *event;
    if (node == NULL || command == NULL) return; event = (agf_event *)calloc(1,sizeof(*event)); if (event == NULL) return;
    if (cJSON_IsString(message_id)) event->correlation_id = agf_strdup(message_id->valuestring);
    if (strcmp(command,"init")==0) event->kind=AGF_EVENT_INIT; else if (strcmp(command,"run")==0) event->kind=AGF_EVENT_RUN; else if (strcmp(command,"stop")==0) { event->kind=AGF_EVENT_STOP; node->cancelled=1; } else if (strcmp(command,"shutdown")==0) { event->kind=AGF_EVENT_SHUTDOWN; node->cancelled=1; } else { free(event); return; }
    agf_push_event(node,event);
}
static void agf_handle_reader_message(agf_runtime *runtime, const cJSON *message) {
    const cJSON *type_item = cJSON_GetObjectItemCaseSensitive(message,"type"); const char *type = cJSON_IsString(type_item) ? type_item->valuestring : "";
    if (strcmp(type,"node.create")==0) {
        const cJSON *instance_item=cJSON_GetObjectItemCaseSensitive(message,"nodeInstanceId"); const cJSON *node_type_item=cJSON_GetObjectItemCaseSensitive(message,"nodeType"); const cJSON *parameters=cJSON_GetObjectItemCaseSensitive(message,"parameters");
        int instance_id=cJSON_IsNumber(instance_item)?instance_item->valueint:0; const char *node_type=cJSON_IsString(node_type_item)?node_type_item->valuestring:"";
        agf_node *node=agf_create_node(runtime,instance_id,node_type,cJSON_IsObject(parameters)?parameters:NULL);
        cJSON *response=agf_create_response_for_request(runtime,message,"node.created",node!=NULL?"ok":"error",node!=NULL?NULL:"NODE_CREATE_FAILED",node!=NULL?NULL:"No node factory is registered for the requested nodeType.");
        if(response!=NULL){cJSON_AddNumberToObject(response,"nodeInstanceId",instance_id);agf_send_message(runtime,response);} return;
    }
    if (strcmp(type,"node.command")==0) { const cJSON *command_item=cJSON_GetObjectItemCaseSensitive(message,"command"); if(cJSON_IsString(command_item))agf_create_event_command(runtime,message,command_item->valuestring); return; }
    if (strcmp(type,"node.configure")==0) {
        const cJSON *instance_item=cJSON_GetObjectItemCaseSensitive(message,"nodeInstanceId"); const cJSON *parameters=cJSON_GetObjectItemCaseSensitive(message,"parameters"); const cJSON *message_id=cJSON_GetObjectItemCaseSensitive(message,"messageId");
        agf_node *node=agf_find_node(runtime,cJSON_IsNumber(instance_item)?instance_item->valueint:0); agf_event *event; if(node==NULL)return; event=(agf_event*)calloc(1,sizeof(*event));if(event==NULL)return;
        event->kind=AGF_EVENT_CONFIGURE; if(cJSON_IsString(message_id))event->correlation_id=agf_strdup(message_id->valuestring); event->json=parameters!=NULL?cJSON_PrintUnformatted(parameters):agf_strdup("{}"); agf_push_event(node,event); return;
    }
    if (strcmp(type,"pin.receive")==0) {
        const cJSON *instance_item=cJSON_GetObjectItemCaseSensitive(message,"nodeInstanceId"); const cJSON *pin_item=cJSON_GetObjectItemCaseSensitive(message,"targetPin"); const cJSON *sample_item=cJSON_GetObjectItemCaseSensitive(message,"sample");
        agf_node *node=agf_find_node(runtime,cJSON_IsNumber(instance_item)?instance_item->valueint:0); agf_event *event; if(node==NULL)return; event=(agf_event*)calloc(1,sizeof(*event));if(event==NULL)return;
        event->kind=AGF_EVENT_SAMPLE; event->pin=cJSON_IsString(pin_item)?agf_strdup(pin_item->valuestring):NULL; event->json=sample_item!=NULL?cJSON_PrintUnformatted(sample_item):agf_strdup("null"); agf_push_event(node,event); return;
    }
    if (strcmp(type,"broadcast.receive")==0) {
        const cJSON *target_item=cJSON_GetObjectItemCaseSensitive(message,"targetNodeInstanceId"); const cJSON *topic_item=cJSON_GetObjectItemCaseSensitive(message,"topic"); const cJSON *kind_item=cJSON_GetObjectItemCaseSensitive(message,"payloadKind"); const cJSON *payload_item=cJSON_GetObjectItemCaseSensitive(message,"payload");
        agf_node *node=agf_find_node(runtime,cJSON_IsNumber(target_item)?target_item->valueint:0); agf_event *event; if(node==NULL)return; event=(agf_event*)calloc(1,sizeof(*event));if(event==NULL)return;
        event->kind=AGF_EVENT_BROADCAST; event->topic=cJSON_IsString(topic_item)?agf_strdup(topic_item->valuestring):NULL; event->payload_kind=cJSON_IsString(kind_item)?agf_strdup(kind_item->valuestring):NULL; event->json=payload_item!=NULL?cJSON_PrintUnformatted(payload_item):agf_strdup("null"); agf_push_event(node,event); return;
    }
    if (strcmp(type,"broadcast.subscribe")==0 || strcmp(type,"broadcast.unsubscribe")==0) {
        cJSON *response=agf_create_response_for_request(runtime,message,"broadcast.subscription","ok",NULL,NULL);
        if(response!=NULL){const cJSON *instance_item=cJSON_GetObjectItemCaseSensitive(message,"nodeInstanceId");const cJSON *topic_item=cJSON_GetObjectItemCaseSensitive(message,"topic");cJSON_AddNumberToObject(response,"nodeInstanceId",cJSON_IsNumber(instance_item)?instance_item->valueint:0);if(cJSON_IsString(topic_item))cJSON_AddStringToObject(response,"topic",topic_item->valuestring);agf_send_message(runtime,response);} return;
    }
    if (strcmp(type,"error")==0) { const cJSON *code=cJSON_GetObjectItemCaseSensitive(message,"code"); const cJSON *text=cJSON_GetObjectItemCaseSensitive(message,"message"); fprintf(stderr,"Core protocol error %s: %s\n",cJSON_IsString(code)?code->valuestring:"",cJSON_IsString(text)?text->valuestring:""); }
}
static int agf_parse_arguments(agf_runtime *runtime, int argc, char **argv) {
    int i; for(i=1;i<argc;i++){if(strcmp(argv[i],"--endpoint")==0&&i+1<argc)runtime->endpoint=agf_strdup(argv[++i]);else if(strcmp(argv[i],"--token")==0&&i+1<argc)runtime->token=agf_strdup(argv[++i]);else if(strcmp(argv[i],"--package-root")==0&&i+1<argc)i++;}
    return runtime->endpoint!=NULL?0:-1;
}
static int agf_read_handshake(agf_runtime *runtime, const char *expected_type, cJSON **result) {
    for(;;){char *line=agf_read_line(runtime);cJSON *message;const cJSON *version;const cJSON *type;if(line==NULL){fprintf(stderr,"Worker handshake %s: line read failed.\n",expected_type);return -1;}message=cJSON_Parse(line);free(line);if(message==NULL){fprintf(stderr,"Worker handshake %s: JSON parse failed.\n",expected_type);return -1;}version=cJSON_GetObjectItemCaseSensitive(message,"protocolVersion");type=cJSON_GetObjectItemCaseSensitive(message,"type");if(!cJSON_IsNumber(version)||version->valueint!=AGF_PROTOCOL_VERSION){cJSON_Delete(message);return -1;}if(cJSON_IsString(type)&&strcmp(type->valuestring,expected_type)==0){*result=message;return 0;}cJSON_Delete(message);}
}
static int agf_handshake(agf_runtime *runtime) {
    cJSON *hello=agf_new_message(runtime,"worker.hello",1); cJSON *welcome=NULL; cJSON *register_message=agf_new_message(runtime,"worker.register",1); cJSON *registered=NULL; char *json; const cJSON *worker_id; const cJSON *interval; const cJSON *max_line; cJSON *ready;
    if(hello==NULL||register_message==NULL)return -1;
    cJSON_AddStringToObject(hello,"packageId",runtime->package_id!=NULL?runtime->package_id:""); cJSON_AddStringToObject(hello,"authToken",runtime->token!=NULL?runtime->token:"");
#if defined(_WIN32)
    cJSON_AddNumberToObject(hello,"processId",(double)GetCurrentProcessId());
#else
    cJSON_AddNumberToObject(hello,"processId",(double)getpid());
#endif
    cJSON_AddStringToObject(hello,"sdkVersion",AGF_SDK_VERSION); cJSON_AddItemToObject(hello,"capabilities",cJSON_Duplicate(runtime->capabilities,1));
    json=cJSON_PrintUnformatted(hello);cJSON_Delete(hello);if(json==NULL||agf_transport_write_line(runtime,json)!=0){free(json);cJSON_Delete(register_message);return -1;}free(json);
    if(agf_read_handshake(runtime,"worker.welcome",&welcome)!=0){cJSON_Delete(register_message);return -1;}
    worker_id=cJSON_GetObjectItemCaseSensitive(welcome,"workerId");if(cJSON_IsString(worker_id)){runtime->worker_id=agf_strdup(worker_id->valuestring);}
    interval=cJSON_GetObjectItemCaseSensitive(welcome,"heartbeatIntervalMs");if(cJSON_IsNumber(interval))runtime->heartbeat_interval_ms=interval->valueint;
    max_line=cJSON_GetObjectItemCaseSensitive(welcome,"maxLineBytes");if(cJSON_IsNumber(max_line))runtime->max_line_bytes=max_line->valueint;cJSON_Delete(welcome);
    cJSON_AddStringToObject(register_message,"packageId",runtime->package_id!=NULL?runtime->package_id:"");cJSON_AddStringToObject(register_message,"workerVersion",runtime->package_version!=NULL?runtime->package_version:"");
    cJSON_AddStringToObject(register_message,"workerId",runtime->worker_id!=NULL?runtime->worker_id:"");cJSON_AddItemToObject(register_message,"capabilities",cJSON_Duplicate(runtime->capabilities,1));cJSON_AddItemToObject(register_message,"nodeTypes",cJSON_Duplicate(runtime->node_types,1));
    json=cJSON_PrintUnformatted(register_message);cJSON_Delete(register_message);if(json==NULL||agf_transport_write_line(runtime,json)!=0){free(json);return -1;}free(json);
    if(agf_read_handshake(runtime,"worker.registered",&registered)!=0)return -1;cJSON_Delete(registered);ready=agf_new_message(runtime,"worker.ready",1);if(ready==NULL)return -1;cJSON_AddStringToObject(ready,"workerId",runtime->worker_id!=NULL?runtime->worker_id:"");cJSON_AddBoolToObject(ready,"subscriptionsActive",1);agf_send_message(runtime,ready);return 0;
}
agf_runtime *agf_runtime_create_from_manifest(const char *manifest_path, const char **error_message) {
    char *text; cJSON *manifest; cJSON *worker; agf_runtime *runtime; const cJSON *package_id; const cJSON *version; const cJSON *capabilities; const cJSON *node_types; cJSON *interval;
    text=agf_read_file(manifest_path); if(text==NULL){agf_set_error(error_message,"Cannot read plugin.json.");return NULL;} manifest=cJSON_Parse(text);free(text);if(manifest==NULL){agf_set_error(error_message,"plugin.json is not valid JSON.");return NULL;}
    worker=cJSON_GetObjectItemCaseSensitive(manifest,"worker");node_types=cJSON_GetObjectItemCaseSensitive(manifest,"nodeTypes");package_id=cJSON_GetObjectItemCaseSensitive(manifest,"packageId");version=cJSON_GetObjectItemCaseSensitive(manifest,"version");capabilities=cJSON_GetObjectItemCaseSensitive(worker,"capabilities");
    if(!cJSON_IsString(package_id)||!cJSON_IsString(version)||!cJSON_IsArray(node_types)){cJSON_Delete(manifest);agf_set_error(error_message,"Manifest is missing packageId, version, worker, or nodeTypes.");return NULL;}
    runtime=(agf_runtime*)calloc(1,sizeof(*runtime));if(runtime==NULL){cJSON_Delete(manifest);agf_set_error(error_message,"Out of memory.");return NULL;}
    runtime->manifest=manifest;runtime->manifest_path=agf_strdup(manifest_path);runtime->package_root=agf_package_root_from_manifest(manifest_path);runtime->package_id=agf_strdup(package_id->valuestring);runtime->package_version=agf_strdup(version->valuestring);runtime->node_types=cJSON_Duplicate(node_types,1);runtime->capabilities=cJSON_IsArray(capabilities)?cJSON_Duplicate(capabilities,1):cJSON_CreateArray();runtime->max_line_bytes=AGF_DEFAULT_MAX_LINE_BYTES;runtime->heartbeat_interval_ms=5000;interval=cJSON_GetObjectItemCaseSensitive(worker,"heartbeatIntervalMs");if(cJSON_IsNumber(interval)&&interval->valueint>0)runtime->heartbeat_interval_ms=interval->valueint;
    agf_mutex_init(&runtime->out_lock);agf_cond_init(&runtime->out_cond);agf_mutex_init(&runtime->node_lock);return runtime;
}
int agf_register_node(agf_runtime *runtime, const char *node_type, const agf_node_callbacks *callbacks, const char **error_message) {
    agf_registered_node *r; if(runtime==NULL||node_type==NULL||callbacks==NULL){agf_set_error(error_message,"Invalid registration arguments.");return -1;}r=(agf_registered_node*)calloc(1,sizeof(*r));if(r==NULL){agf_set_error(error_message,"Out of memory.");return -1;}r->node_type=agf_strdup(node_type);r->callbacks=*callbacks;r->next=runtime->registered_nodes;runtime->registered_nodes=r;return 0;
}
int agf_run(agf_runtime *runtime, int argc, char **argv) {
    int result=1; if(runtime==NULL)return 1; if(agf_parse_arguments(runtime,argc,argv)!=0){fprintf(stderr,"Missing --endpoint argument.\n");return 1;} if(agf_connect_transport(runtime,runtime->endpoint)!=0){fprintf(stderr,"Cannot connect to Worker endpoint.\n");return 1;} if(agf_handshake(runtime)!=0)goto cleanup;
#if defined(_WIN32)
    runtime->writer_thread=CreateThread(NULL,0,agf_writer_entry,runtime,0,NULL);runtime->writer_started=runtime->writer_thread!=NULL;runtime->heartbeat_thread=CreateThread(NULL,0,agf_heartbeat_entry,runtime,0,NULL);runtime->heartbeat_started=runtime->heartbeat_thread!=NULL;
#else
    runtime->writer_started=pthread_create(&runtime->writer_thread,NULL,agf_writer_entry,runtime)==0;runtime->heartbeat_started=pthread_create(&runtime->heartbeat_thread,NULL,agf_heartbeat_entry,runtime)==0;
#endif
    if(!runtime->writer_started||!runtime->heartbeat_started){fprintf(stderr,"Worker threads failed to start (writer=%d heartbeat=%d).\n",runtime->writer_started,runtime->heartbeat_started);goto cleanup;}
    while(!runtime->closing){char *line=agf_read_line(runtime);cJSON *message;if(line==NULL)break;message=cJSON_Parse(line);free(line);if(message==NULL)continue;agf_handle_reader_message(runtime,message);cJSON_Delete(message);}result=0;
cleanup:
    runtime->closing=1;agf_close_transport(runtime);agf_mutex_lock(&runtime->out_lock);agf_cond_broadcast(&runtime->out_cond);agf_mutex_unlock(&runtime->out_lock);
    return result;
}
void agf_runtime_destroy(agf_runtime *runtime) {
    if(runtime==NULL)return;runtime->closing=1;agf_close_transport(runtime);agf_mutex_lock(&runtime->out_lock);agf_cond_broadcast(&runtime->out_cond);agf_mutex_unlock(&runtime->out_lock);
    while(runtime->node_count>0){agf_node *node=runtime->nodes[runtime->node_count-1];agf_destroy_node(runtime,node);}
#if defined(_WIN32)
    if(runtime->writer_started){WaitForSingleObject(runtime->writer_thread,5000);CloseHandle(runtime->writer_thread);}if(runtime->heartbeat_started){WaitForSingleObject(runtime->heartbeat_thread,5000);CloseHandle(runtime->heartbeat_thread);}
#else
    if(runtime->writer_started)pthread_join(runtime->writer_thread,NULL);if(runtime->heartbeat_started)pthread_join(runtime->heartbeat_thread,NULL);
#endif
    while(runtime->out_head!=NULL){agf_outbound *item=runtime->out_head;runtime->out_head=item->next;free(item->line);free(item);}while(runtime->registered_nodes!=NULL){agf_registered_node *r=runtime->registered_nodes;runtime->registered_nodes=r->next;free(r->node_type);free(r);}free(runtime->nodes);free(runtime->read_buffer);free(runtime->worker_id);free(runtime->endpoint);free(runtime->token);free(runtime->package_id);free(runtime->package_version);free(runtime->manifest_path);free(runtime->package_root);cJSON_Delete(runtime->capabilities);cJSON_Delete(runtime->node_types);cJSON_Delete(runtime->manifest);agf_mutex_destroy(&runtime->out_lock);agf_cond_destroy(&runtime->out_cond);agf_mutex_destroy(&runtime->node_lock);free(runtime);
}
void *agf_context_userdata(agf_node_context *ctx) { return ctx != NULL && ctx->node != NULL ? ctx->node->userdata : NULL; }
int agf_context_instance_id(agf_node_context *ctx) { return ctx != NULL ? ctx->instance_id : 0; }
const char *agf_context_node_type(agf_node_context *ctx) { return ctx != NULL && ctx->node != NULL && ctx->node->node_type != NULL ? ctx->node->node_type : ""; }
const char *agf_context_package_id(agf_node_context *ctx) { return ctx != NULL && ctx->runtime != NULL && ctx->runtime->package_id != NULL ? ctx->runtime->package_id : ""; }
const char *agf_context_package_root(agf_node_context *ctx) { return ctx != NULL && ctx->runtime != NULL && ctx->runtime->package_root != NULL ? ctx->runtime->package_root : ""; }
const char *agf_context_run_id(agf_node_context *ctx) { return ctx != NULL && ctx->run_id != NULL ? ctx->run_id : ""; }
int agf_context_is_cancelled(agf_node_context *ctx) { return ctx != NULL && ctx->node != NULL ? ctx->node->cancelled : 1; }
int agf_context_sleep(agf_node_context *ctx, int milliseconds) {
    int remaining = milliseconds;
    while (remaining > 0 && !agf_context_is_cancelled(ctx)) {
        int step = remaining > 10 ? 10 : remaining;
#if defined(_WIN32)
        Sleep((DWORD)step);
#else
        usleep((useconds_t)(step * 1000));
#endif
        remaining -= step;
    }
    return agf_context_is_cancelled(ctx);
}
int agf_context_wait_for_stop(agf_node_context *ctx, int timeout_ms) {
    int elapsed = 0;
    while (!agf_context_is_cancelled(ctx)) {
        if (timeout_ms >= 0 && elapsed >= timeout_ms) return 0;
        agf_context_sleep(ctx, 10);
        elapsed += 10;
    }
    return 1;
}
const char *agf_context_parameters_json(agf_node_context *ctx) { static const char empty[]="{}"; return ctx != NULL && ctx->parameters_json != NULL ? ctx->parameters_json : empty; }
const cJSON *agf_context_param_item(agf_node_context *ctx, const char *name) {
    if (ctx == NULL || ctx->parameters == NULL || name == NULL) return NULL;
    return cJSON_GetObjectItemCaseSensitive(ctx->parameters, name);
}
const char *agf_context_param_string(agf_node_context *ctx, const char *name, const char *default_value) {
    const cJSON *item = agf_context_param_item(ctx, name);
    return cJSON_IsString(item) ? item->valuestring : default_value;
}
int agf_context_param_bool(agf_node_context *ctx, const char *name, int default_value) {
    const cJSON *item = agf_context_param_item(ctx, name);
    return cJSON_IsBool(item) ? (cJSON_IsTrue(item) ? 1 : 0) : default_value;
}
int32_t agf_context_param_int32(agf_node_context *ctx, const char *name, int32_t default_value) {
    const cJSON *item = agf_context_param_item(ctx, name);
    return cJSON_IsNumber(item) ? (int32_t)item->valueint : default_value;
}
int64_t agf_context_param_int64(agf_node_context *ctx, const char *name, int64_t default_value) {
    const cJSON *item = agf_context_param_item(ctx, name);
    return cJSON_IsNumber(item) ? (int64_t)item->valuedouble : default_value;
}
float agf_context_param_float(agf_node_context *ctx, const char *name, float default_value) {
    const cJSON *item = agf_context_param_item(ctx, name);
    return cJSON_IsNumber(item) ? (float)item->valuedouble : default_value;
}
double agf_context_param_double(agf_node_context *ctx, const char *name, double default_value) {
    const cJSON *item = agf_context_param_item(ctx, name);
    return cJSON_IsNumber(item) ? item->valuedouble : default_value;
}


