#pragma once

#include "agentflow_node_api.h"

#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <iomanip>
#include <sstream>
#include <string>
#include <vector>

namespace agentflow_native {

inline std::string json_escape(const char* text) {
    std::string result;
    if (text == nullptr)
        return result;

    for (const char ch : std::string(text)) {
        switch (ch) {
            case '"': result += "\\\""; break;
            case '\\': result += "\\\\"; break;
            case '\b': result += "\\b"; break;
            case '\f': result += "\\f"; break;
            case '\n': result += "\\n"; break;
            case '\r': result += "\\r"; break;
            case '\t': result += "\\t"; break;
            default:
                if (static_cast<unsigned char>(ch) < 0x20) {
                    char buffer[7] = {};
                    std::snprintf(buffer, sizeof(buffer), "\\u%04x", static_cast<unsigned int>(ch));
                    result += buffer;
                } else {
                    result += ch;
                }
                break;
        }
    }
    return result;
}

inline std::string base64_encode(const uint8_t* data, uint64_t size) {
    static const char alphabet[] =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    std::string result;
    if (data == nullptr || size == 0)
        return result;

    for (uint64_t i = 0; i < size; i += 3) {
        const uint32_t b0 = data[i];
        const uint32_t b1 = i + 1 < size ? data[i + 1] : 0;
        const uint32_t b2 = i + 2 < size ? data[i + 2] : 0;
        const uint32_t bits = (b0 << 16) | (b1 << 8) | b2;

        result.push_back(alphabet[(bits >> 18) & 0x3f]);
        result.push_back(alphabet[(bits >> 12) & 0x3f]);
        result.push_back(i + 1 < size ? alphabet[(bits >> 6) & 0x3f] : '=');
        result.push_back(i + 2 < size ? alphabet[bits & 0x3f] : '=');
    }
    return result;
}

inline bool sample_is_valid(const AfSample* sample) {
    return sample != nullptr &&
        sample->structSize >= sizeof(AfSample) &&
        sample->abiVersion == AF_ABI_VERSION &&
        (sample->dataSize == 0 || sample->data != nullptr);
}

inline std::string sample_json(const AfSample& sample, const char* key) {
    const std::string escapedKey = json_escape(key == nullptr || *key == '\0' ? "sample" : key);
    switch (sample.type) {
        case AF_SAMPLE_STRING: {
            std::string value;
            if (sample.data != nullptr && sample.dataSize > 0)
                value.assign(reinterpret_cast<const char*>(sample.data), static_cast<size_t>(sample.dataSize));
            return "{\"key\":\"" + escapedKey + "\",\"dataType\":\"STRING\",\"encoding\":\"utf8\",\"value\":\"" +
                json_escape(value.c_str()) + "\"}";
        }
        case AF_SAMPLE_INT32:
            return "{\"key\":\"" + escapedKey + "\",\"dataType\":\"INT32\",\"encoding\":\"decimal\",\"value\":" +
                std::to_string(sample.int32Value) + "}";
        case AF_SAMPLE_INT64:
            return "{\"key\":\"" + escapedKey + "\",\"dataType\":\"INT64\",\"encoding\":\"decimal-string\",\"value\":\"" +
                std::to_string(sample.int64Value) + "\"}";
        case AF_SAMPLE_BOOL:
            return "{\"key\":\"" + escapedKey + "\",\"dataType\":\"BOOL\",\"encoding\":\"boolean\",\"value\":" +
                std::string(sample.boolValue == 0 ? "false" : "true") + "}";
        case AF_SAMPLE_FLOAT: {
            uint32_t bits = 0;
            std::memcpy(&bits, &sample.floatValue, sizeof(bits));
            std::ostringstream stream;
            stream << "{\"key\":\"" << escapedKey
                   << "\",\"dataType\":\"FLOAT\",\"encoding\":\"ieee754-binary32-bits-hex\",\"value\":\""
                   << std::hex << std::setw(8) << std::setfill('0') << bits << "\"}";
            return stream.str();
        }
        case AF_SAMPLE_DOUBLE: {
            uint64_t bits = 0;
            std::memcpy(&bits, &sample.doubleValue, sizeof(bits));
            std::ostringstream stream;
            stream << "{\"key\":\"" << escapedKey
                   << "\",\"dataType\":\"DOUBLE\",\"encoding\":\"ieee754-binary64-bits-hex\",\"value\":\""
                   << std::hex << std::setw(16) << std::setfill('0') << bits << "\"}";
            return stream.str();
        }
        case AF_SAMPLE_BINARY:
            return "{\"key\":\"" + escapedKey + "\",\"dataType\":\"BIN\",\"encoding\":\"base64\",\"value\":\"" +
                base64_encode(sample.data, sample.dataSize) + "\"}";
        default:
            return std::string();
    }
}

inline bool broadcast_is_supported(const AfNodeHostVTable& host) {
    return host.structSize >= offsetof(AfNodeHostVTable, publishBroadcast) + sizeof(host.publishBroadcast) &&
        host.publishBroadcast != nullptr &&
        host.subscribeBroadcast != nullptr &&
        host.unsubscribeBroadcast != nullptr;
}

inline AfStatus publish_broadcast(
    const AfNodeHostVTable& host,
    void* callbackContext,
    const char* topic,
    const char* payloadKind,
    const std::string& payloadJson) {
    if (!broadcast_is_supported(host)) {
        return AF_ERR_NOT_SUPPORTED;
    }
    if (topic == nullptr || *topic == '\0' || payloadKind == nullptr || *payloadKind == '\0') {
        return AF_ERR_INVALID_ARGUMENT;
    }

    AfBroadcast message{};
    message.structSize = sizeof(AfBroadcast);
    message.abiVersion = AF_ABI_VERSION;
    message.topic = topic;
    message.topicLength = static_cast<uint64_t>(std::strlen(topic));
    message.payloadKind = payloadKind;
    message.payloadKindLength = static_cast<uint64_t>(std::strlen(payloadKind));
    message.payloadJson = payloadJson.c_str();
    message.payloadJsonLength = static_cast<uint64_t>(payloadJson.size());
    return host.publishBroadcast(callbackContext, &message);
}

inline AfStatus publish_sample_broadcast(
    const AfNodeHostVTable& host,
    void* callbackContext,
    const char* topic,
    const AfSample& sample,
    const char* key = "sample") {
    if (!sample_is_valid(&sample)) {
        return AF_ERR_INVALID_ARGUMENT;
    }
    const std::string payloadJson = sample_json(sample, key);
    return payloadJson.empty()
        ? AF_ERR_INVALID_ARGUMENT
        : publish_broadcast(host, callbackContext, topic, "sample", payloadJson);
}

inline AfStatus publish_samples_broadcast(
    const AfNodeHostVTable& host,
    void* callbackContext,
    const char* topic,
    const AfSample* samples,
    size_t count) {
    if (samples == nullptr && count > 0) {
        return AF_ERR_INVALID_ARGUMENT;
    }

    std::string payloadJson = "[";
    for (size_t i = 0; i < count; ++i) {
        if (!sample_is_valid(&samples[i]))
            return AF_ERR_INVALID_ARGUMENT;
        if (i > 0)
            payloadJson.push_back(',');
        payloadJson += sample_json(samples[i], "sample");
    }
    payloadJson += "]";
    return publish_broadcast(host, callbackContext, topic, "samples", payloadJson);
}

inline AfStatus publish_sample_dictionary_broadcast(
    const AfNodeHostVTable& host,
    void* callbackContext,
    const char* topic,
    const char* const* keys,
    const AfSample* samples,
    size_t count) {
    if (keys == nullptr || (samples == nullptr && count > 0)) {
        return AF_ERR_INVALID_ARGUMENT;
    }

    std::string payloadJson = "{";
    for (size_t i = 0; i < count; ++i) {
        if (keys[i] == nullptr || *keys[i] == '\0' || !sample_is_valid(&samples[i]))
            return AF_ERR_INVALID_ARGUMENT;
        if (i > 0)
            payloadJson.push_back(',');
        payloadJson += "\"" + json_escape(keys[i]) + "\":" + sample_json(samples[i], keys[i]);
    }
    payloadJson += "}";
    return publish_broadcast(host, callbackContext, topic, "dictionary_string_sample", payloadJson);
}

inline AfStatus publish_sample_list_dictionary_broadcast(
    const AfNodeHostVTable& host,
    void* callbackContext,
    const char* topic,
    const char* const* keys,
    const AfSample* const* samplesByKey,
    const size_t* counts,
    size_t keyCount) {
    if (keys == nullptr || samplesByKey == nullptr || counts == nullptr) {
        return AF_ERR_INVALID_ARGUMENT;
    }

    std::string payloadJson = "{";
    for (size_t i = 0; i < keyCount; ++i) {
        if (keys[i] == nullptr || *keys[i] == '\0')
            return AF_ERR_INVALID_ARGUMENT;
        if (counts[i] > 0 && samplesByKey[i] == nullptr)
            return AF_ERR_INVALID_ARGUMENT;

        if (i > 0)
            payloadJson.push_back(',');
        payloadJson += "\"" + json_escape(keys[i]) + "\":[";
        for (size_t j = 0; j < counts[i]; ++j) {
            if (!sample_is_valid(&samplesByKey[i][j]))
                return AF_ERR_INVALID_ARGUMENT;
            if (j > 0)
                payloadJson.push_back(',');
            payloadJson += sample_json(samplesByKey[i][j], keys[i]);
        }
        payloadJson.push_back(']');
    }
    payloadJson += "}";
    return publish_broadcast(host, callbackContext, topic, "dictionary_string_samples", payloadJson);
}

inline AfStatus publish_mixed_broadcast(
    const AfNodeHostVTable& host,
    void* callbackContext,
    const char* topic,
    const char* payload,
    const AfSample& sample,
    const char* key = "sample") {
    if (!sample_is_valid(&sample)) {
        return AF_ERR_INVALID_ARGUMENT;
    }

    const std::string payloadJson =
        "{\"payload\":\"" + json_escape(payload == nullptr ? "" : payload) +
        "\",\"sample\":" + sample_json(sample, key) + "}";
    return publish_broadcast(host, callbackContext, topic, "mixed", payloadJson);
}

inline AfStatus subscribe_broadcast(
    const AfNodeHostVTable& host,
    void* callbackContext,
    const char* topic,
    AfReceiveBroadcastFn receive,
    void* receiverContext) {
    if (!broadcast_is_supported(host) || topic == nullptr || *topic == '\0' || receive == nullptr) {
        return AF_ERR_INVALID_ARGUMENT;
    }
    return host.subscribeBroadcast(
        callbackContext,
        topic,
        static_cast<uint64_t>(std::strlen(topic)),
        receive,
        receiverContext);
}

inline AfStatus unsubscribe_broadcast(
    const AfNodeHostVTable& host,
    void* callbackContext,
    const char* topic) {
    if (!broadcast_is_supported(host) || topic == nullptr || *topic == '\0')
        return AF_ERR_INVALID_ARGUMENT;
    return host.unsubscribeBroadcast(
        callbackContext,
        topic,
        static_cast<uint64_t>(std::strlen(topic)));
}

} // namespace agentflow_native
