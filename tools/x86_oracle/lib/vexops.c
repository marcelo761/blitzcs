// FLAGS: -mavx2 -mbmi
/* VEX-encoded AVX2 and BMI1 operations: 256-bit logic and compares, the AVX2 string-scan idiom (vpcmpeqb + vpmovmskb + tzcnt),
 * vpaddd/vpsubd, vpshufb, a 128-bit VEX xmm form, vzeroupper, and andn/tzcnt. Only run on hosts with AVX2 (the driver checks).
 * vx_buf is the shared operand/result area: every function reads and writes it, so the data-section hash covers the stores. */
/* mm_malloc.h would pull in the 32-bit libc headers (not installed); the intrinsics do not need it. */
#define _MM_MALLOC_H_INCLUDED
#include <immintrin.h>
typedef unsigned int u32; typedef unsigned char u8;

u8 vx_buf[160] = {
  3, 10, 1, 8, 15, 6, 13, 4, 11, 2, 9, 0, 7, 14, 5, 12,
  8, 15, 6, 13, 4, 11, 2, 9, 0, 7, 14, 5, 12, 3, 10, 1,
  13, 4, 11, 2, 9, 0, 7, 14, 5, 12, 3, 10, 1, 8, 15, 6,
  2, 9, 0, 7, 14, 5, 12, 3, 10, 1, 8, 15, 6, 13, 4, 11,
  7, 14, 5, 12, 3, 10, 1, 8, 15, 6, 13, 4, 11, 2, 9, 0,
  12, 3, 10, 1, 8, 15, 6, 13, 4, 11, 2, 9, 0, 7, 14, 5,
  1, 8, 15, 6, 13, 4, 11, 2, 9, 0, 7, 14, 5, 12, 3, 10,
  6, 13, 4, 11, 2, 9, 0, 7, 14, 5, 12, 3, 10, 1, 8, 15,
  11, 2, 9, 0, 7, 14, 5, 12, 3, 10, 1, 8, 15, 6, 13, 4,
  0, 7, 14, 5, 12, 3, 10, 1, 8, 15, 6, 13, 4, 11, 2, 9,
};

static inline __m256i ld(u32 off) { return _mm256_loadu_si256((const __m256i *)(vx_buf + off)); }
static inline void st(u32 off, __m256i v) { _mm256_storeu_si256((__m256i *)(vx_buf + off), v); }
static inline u32 sum8(__m256i v) { u32 t[8]; _mm256_storeu_si256((__m256i *)t, v); return t[0] + t[1] + t[2] + t[3] + t[4] + t[5] + t[6] + t[7]; }

u32 vx_pxor(u32 a, u32 b) { __m256i z = _mm256_xor_si256(ld(a & 31), ld(32 + (b & 31))); st(64, z); return (u32)_mm256_movemask_epi8(z); }
u32 vx_pand(u32 a, u32 b) { __m256i z = _mm256_and_si256(ld(a & 31), ld(32 + (b & 31))); st(96, z); return (u32)_mm256_movemask_epi8(z); }

/* the AVX2 string-scan idiom: compare 32 bytes with a byte, return the mask, and the index of the first hit */
u32 vx_pcmpeq_mask(u32 a, u32 b) { __m256i e = _mm256_cmpeq_epi8(ld(a & 31), _mm256_set1_epi8((char)(b & 15))); return (u32)_mm256_movemask_epi8(e); }
u32 vx_scan_first(u32 a, u32 b)
{
  __m256i e = _mm256_cmpeq_epi8(ld(a & 63), _mm256_set1_epi8((char)(b & 15)));
  u32 m = (u32)_mm256_movemask_epi8(e);
  st(128, e);
  return m ? (u32)__builtin_ctz(m) : 32u;
}

u32 vx_paddd(u32 a, u32 b) { __m256i s = _mm256_add_epi32(ld(a & 31), ld(32 + (b & 31))); st(96, s); return sum8(s); }
u32 vx_psubd(u32 a, u32 b) { __m256i s = _mm256_sub_epi32(ld(a & 31), ld(32 + (b & 31))); st(96, s); return sum8(s); }
u32 vx_pshufb(u32 a, u32 b) { __m256i s = _mm256_shuffle_epi8(ld(a & 31), ld(32 + (b & 31))); st(96, s); return sum8(s); }

/* 128-bit VEX form (vpxor xmm), then vzeroupper before returning to scalar/SSE code */
u32 vx_xor128(u32 a, u32 b)
{
  __m128i x = _mm_xor_si128(_mm_loadu_si128((const __m128i *)(vx_buf + (a & 31))), _mm_loadu_si128((const __m128i *)(vx_buf + 32 + (b & 31))));
  _mm_storeu_si128((__m128i *)(vx_buf + 96), x);
  return (u32)_mm_movemask_epi8(x);
}
u32 vx_zero_upper(u32 a, u32 b)
{
  __m256i y = ld(a & 31);
  __m128i x = _mm_loadu_si128((const __m128i *)(vx_buf + 32 + (b & 31)));
  st(64, y);
  _mm256_zeroupper();
  return (u32)_mm_movemask_epi8(_mm_xor_si128(x, _mm_set1_epi8(5)));
}

/* BMI1 */
u32 vx_andn(u32 a, u32 b) { return _andn_u32(a, b); }
u32 vx_tzcnt(u32 a) { return _tzcnt_u32(a); }
