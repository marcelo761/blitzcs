/* Controlled library that reaches the emulated Win32 / C runtime services through IAT slots, exactly like a real DLL. */
#include "w32iat.h"
u32 g_out[16] = {0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF,
                 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF};
u32 g_cs[8] = {1, 1, 1, 1, 1, 1, 1, 1};
u32 g_counter = 7;

/* process heap: zeroed alloc, fill, grow, size, double free. Results in g_out[0..5]; returns a checksum of the grown block. */
u32 t_heap(u32 n) {
    u32 heap = GetProcessHeap();
    unsigned char *p = (unsigned char *)HeapAlloc(heap, 8, n);
    u32 zero = 0;
    for (u32 i = 0; i < n; i++) zero += p[i];
    for (u32 i = 0; i < n; i++) p[i] = (unsigned char)(i * 7 + 1);
    unsigned char *q = (unsigned char *)HeapReAlloc(heap, 0, (u32)p, n * 3);
    u32 sum = 0;
    for (u32 i = 0; i < n; i++) sum += q[i];
    g_out[0] = zero; g_out[1] = HeapSize(heap, 0, (u32)q); g_out[2] = HeapFree(heap, 0, (u32)q);
    g_out[3] = HeapFree(heap, 0, (u32)q); g_out[4] = GetLastError(); g_out[5] = q != 0;
    return sum;
}

/* cdecl C runtime through the IAT: malloc/memcpy/strlen/free; the cdecl convention must leave the stack balanced. */
u32 t_crt(u32 src, u32 n) {
    u32 buf = crt_malloc(n + 1);
    crt_memcpy(buf, src, n);
    ((unsigned char *)buf)[n] = 0;
    u32 len = crt_strlen(buf);
    crt_free(buf);
    g_out[6] = len;
    return len * 3 + n;
}

u32 t_cs(u32 n) {
    InitializeCriticalSection((u32)g_cs);
    for (u32 i = 0; i < n; i++) { EnterCriticalSection((u32)g_cs); EnterCriticalSection((u32)g_cs); g_counter++; LeaveCriticalSection((u32)g_cs); LeaveCriticalSection((u32)g_cs); }
    g_out[7] = g_cs[1]; /* LockCount: -1 when unlocked */
    return g_counter;
}

u32 t_tls(u32 v) {
    u32 idx = TlsAlloc();
    u32 before = TlsGetValue(idx);
    TlsSetValue(idx, v);
    g_out[8] = before; g_out[9] = TlsGetValue(idx);
    return idx;
}

/* NLS: CP1252 bytes -> UTF-16 -> UTF-8 round trip. */
u32 t_nls(u32 src, u32 wbuf, u32 out) {
    u32 w = MultiByteToWideChar(1252, 0, src, 0xFFFFFFFF, wbuf, 64);
    u32 u = WideCharToMultiByte(65001, 0, wbuf, 0xFFFFFFFF, out, 128, 0, 0);
    g_out[10] = w; g_out[11] = u;
    return u;
}

u32 t_interlocked(u32 n) {
    for (u32 i = 0; i < n; i++) InterlockedIncrement((u32)&g_counter);
    return g_counter;
}

u32 t_out(u32 i) { return g_out[i & 15]; }
