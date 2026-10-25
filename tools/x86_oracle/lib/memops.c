/* Controlled buffer library: rep movs/stos/scas/cmps/lods via inline asm, hashes, string scans, all on data-section buffers. */
typedef unsigned u32; typedef unsigned char u8; typedef unsigned short u16;
u8 g_a[256]; u8 g_b[256]; u8 g_s[64]; u32 g_w[32];
static u32 fnv(const u8 *p, u32 n) { u32 h = 2166136261u; while (n--) h = (h ^ *p++) * 16777619u; return h; }
u32 m_fill(u32 seed) { for (int i = 0; i < 256; i++) { seed = seed * 1103515245u + 12345u; g_a[i] = (u8)(seed >> 16); } return fnv(g_a, 256); }
u32 m_hash(u32 off, u32 len) { off &= 127; len &= 127; return fnv(g_a + off, len); }
u32 m_hashb(u32 off, u32 len) { off &= 127; len &= 127; return fnv(g_b + off, len); }
u32 m_movsb(u32 d, u32 s, u32 n) { u8 *dp = g_b + (d & 127); const u8 *sp = g_a + (s & 127); n &= 127; __asm__ volatile("cld; rep movsb" : "+D"(dp), "+S"(sp), "+c"(n) :: "memory", "cc"); return (u32)(dp - g_b) ^ n ^ fnv(g_b, 256); }
u32 m_movsd(u32 d, u32 s, u32 n) { u32 *dp = g_w + (d & 15); const u32 *sp = (const u32 *)g_a + (s & 15); n &= 15; __asm__ volatile("cld; rep movsl" : "+D"(dp), "+S"(sp), "+c"(n) :: "memory", "cc"); return (u32)(dp - g_w) ^ fnv((u8 *)g_w, 128); }
u32 m_movsw(u32 d, u32 s, u32 n) { u16 *dp = (u16 *)g_b + (d & 31); const u16 *sp = (const u16 *)g_a + (s & 31); n &= 31; __asm__ volatile("cld; rep movsw" : "+D"(dp), "+S"(sp), "+c"(n) :: "memory", "cc"); return fnv(g_b, 256); }
u32 m_movs_back(u32 d, u32 s, u32 n) { n &= 63; u8 *dp = g_b + 100 + (d & 31) + n; const u8 *sp = g_a + 100 + (s & 31) + n; if (n) { dp--; sp--; } __asm__ volatile("std; rep movsb; cld" : "+D"(dp), "+S"(sp), "+c"(n) :: "memory", "cc"); return fnv(g_b, 256) ^ (u32)(dp - g_b); }
u32 m_stosb(u32 d, u32 n, u32 v) { u8 *dp = g_b + (d & 127); n &= 127; __asm__ volatile("cld; rep stosb" : "+D"(dp), "+c"(n) : "a"(v) : "memory", "cc"); return fnv(g_b, 256) ^ (u32)(dp - g_b); }
u32 m_stosd(u32 d, u32 n, u32 v) { u32 *dp = g_w + (d & 15); n &= 15; __asm__ volatile("cld; rep stosl" : "+D"(dp), "+c"(n) : "a"(v) : "memory", "cc"); return fnv((u8 *)g_w, 128) ^ (u32)(dp - g_w); }
u32 m_stosw(u32 d, u32 n, u32 v) { u16 *dp = (u16 *)g_b + (d & 63); n &= 63; __asm__ volatile("cld; rep stosw" : "+D"(dp), "+c"(n) : "a"((u16)v) : "memory", "cc"); return fnv(g_b, 256); }
u32 m_scasb(u32 v, u32 n) { const u8 *p = g_a; u32 c = (n & 255) + 1; u8 z; __asm__ volatile("cld; repne scasb; setne %b1" : "+D"(p), "=q"(z), "+c"(c) : "a"(v) : "cc"); return (u32)(p - g_a) * 4 + z + c * 1024; }
u32 m_cmpsb(u32 x, u32 y, u32 n) { const u8 *p = g_a + (x & 127); const u8 *q = g_b + (y & 127); u32 c = (n & 127) + 1; u8 z; __asm__ volatile("cld; repe cmpsb; setne %b2" : "+S"(p), "+D"(q), "=q"(z), "+c"(c) :: "cc"); return (u32)(p - g_a) + z * 256 + c * 1024; }
u32 m_lodsb(u32 s, u32 n) { const u8 *p = g_a + (s & 127); u32 c = n & 63, sum = 0, v; while (c--) { __asm__ volatile("cld; lodsb" : "=a"(v), "+S"(p)); sum = sum * 31 + (v & 0xff); } return sum; }
u32 m_setstr(u32 seed, u32 len) { len &= 63; for (u32 i = 0; i < len; i++) { seed = seed * 1103515245u + 12345u; g_s[i] = (u8)(1 + ((seed >> 16) % 255)); } g_s[len] = 0; return fnv(g_s, 64); }
u32 m_strlen_loop(void) { u32 n = 0; while (g_s[n]) n++; return n; }
u32 m_strlen_scas(void) { const u8 *p = g_s; u32 c = 0xffffffffu; __asm__ volatile("cld; repne scasb" : "+D"(p), "+c"(c) : "a"(0) : "cc"); return ~c - 1; }
u32 m_strhash(u32 salt) { u32 h = salt; for (const u8 *p = g_s; *p; p++) h = (h << 5) + h + *p; return h; }
u32 m_rev(u32 n) { n &= 127; for (u32 i = 0; i < n / 2; i++) { u8 t = g_b[i]; g_b[i] = g_b[n - 1 - i]; g_b[n - 1 - i] = t; } return fnv(g_b, 256); }
u32 m_xorsum(u32 off, u32 n) { u32 s = 0; off &= 127; n &= 127; for (u32 i = 0; i < n; i++) s = (s << 1 | s >> 31) ^ g_a[off + i]; return s; }
u32 m_crc(u32 off, u32 n) { u32 c = 0xffffffffu; off &= 127; n &= 127; for (u32 i = 0; i < n; i++) { c ^= g_a[off + i]; for (int k = 0; k < 8; k++) c = (c >> 1) ^ (0xedb88320u & -(c & 1)); } return ~c; }
u32 m_wordsum(u32 n) { u32 s = 0; for (u32 i = 0; i < (n & 31); i++) s += g_w[i] * (i + 1); return s; }
u32 m_bytes_to_words(u32 n) { n &= 31; for (u32 i = 0; i < n; i++) g_w[i] = g_a[i] | (g_a[i + 1] << 8) | (g_a[i + 2] << 16) | ((u32)g_a[i + 3] << 24); return fnv((u8 *)g_w, 128); }
