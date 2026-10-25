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
