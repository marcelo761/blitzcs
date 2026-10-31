#include <stddef.h>
#include <stdint.h>
#include <string.h>

typedef struct {
    int32_t left;
    int32_t top;
    int32_t right;
    int32_t bottom;
} BlitzRect;

size_t blitz_rect_layout(int field) {
    switch (field) {
        case 0: return sizeof(BlitzRect);
        case 1: return _Alignof(BlitzRect);
        case 2: return offsetof(BlitzRect, left);
        case 3: return offsetof(BlitzRect, top);
        case 4: return offsetof(BlitzRect, right);
        case 5: return offsetof(BlitzRect, bottom);
        default: return SIZE_MAX;
    }
}

int blitz_rect_fill(void *buffer, size_t byte_length) {
    if (buffer == NULL || byte_length < sizeof(BlitzRect)) return 0;
    const BlitzRect rect = { INT32_C(0x01020304), -2, 300, -400 };
    memcpy(buffer, &rect, sizeof(rect));
    return 1;
}

/* Mixed 32-bit, pointer-sized and 64-bit fields: the BASIC field image uses 4-byte slots. */
typedef struct {
    uint32_t length;
    int32_t input;
    float scale;
    size_t big;
    size_t small;
    uint64_t wide;
    int32_t output;
} BlitzStatus;

size_t blitz_status_layout(int field) {
    switch (field) {
        case 0: return sizeof(BlitzStatus);
        case 1: return _Alignof(BlitzStatus);
        case 2: return offsetof(BlitzStatus, length);
        case 3: return offsetof(BlitzStatus, input);
        case 4: return offsetof(BlitzStatus, scale);
        case 5: return offsetof(BlitzStatus, big);
        case 6: return offsetof(BlitzStatus, small);
        case 7: return offsetof(BlitzStatus, wide);
        case 8: return offsetof(BlitzStatus, output);
        default: return SIZE_MAX;
    }
}

/* Reads input fields, writes every output field and returns the observed input (or -1 for NULL). */
int blitz_status_update(BlitzStatus *status) {
    if (status == NULL) return -1;
    const int32_t observed = status->input;
    status->length = (uint32_t)sizeof(BlitzStatus);
    status->output = status->input * 2 + (int32_t)status->scale;
    status->big = SIZE_MAX;
    status->small = status->small + 1;
    status->wide = status->wide + 1;
    return observed;
}
