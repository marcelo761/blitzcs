#include <stdint.h>
#include <stdatomic.h>
#include <stdlib.h>
#include <string.h>

static atomic_int live_resources = 0;

int32_t blitz_add(int32_t left, int32_t right) {
    return left + right;
}

float blitz_scale(float value, float factor) {
    return value * factor;
}

int8_t blitz_signed_byte(int32_t value) {
    return (int8_t)(uint8_t)value;
}

size_t blitz_utf8_copy(const uint8_t *input, size_t input_length,
                       uint8_t *output, size_t output_capacity) {
    if (output == NULL || output_capacity < input_length) {
        return input_length;
    }
    memcpy(output, input, input_length);
    return input_length;
}

uint8_t *blitz_make_message(const uint8_t *input, size_t input_length,
                            size_t *output_length) {
    static const uint8_t prefix[] = "native:";
    size_t length = sizeof(prefix) - 1 + input_length;
    uint8_t *result = malloc(length);
    if (result == NULL) {
        *output_length = 0;
        return NULL;
    }
    atomic_fetch_add(&live_resources, 1);
    memcpy(result, prefix, sizeof(prefix) - 1);
    memcpy(result + sizeof(prefix) - 1, input, input_length);
    *output_length = length;
    return result;
}

uint8_t *blitz_make_invalid_message(size_t *output_length) {
    uint8_t *result = malloc(2);
    if (result == NULL) {
        *output_length = 0;
        return NULL;
    }
    result[0] = 0xc3;
    result[1] = 0x28;
    *output_length = 2;
    atomic_fetch_add(&live_resources, 1);
    return result;
}

int32_t blitz_live_resource_count(void) {
    return atomic_load(&live_resources);
}

void blitz_free(void *resource) {
    if (resource != NULL) atomic_fetch_sub(&live_resources, 1);
    free(resource);
}
