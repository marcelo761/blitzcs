/* Fastcall/thiscall entry points: the translator must diagnose these as unsupported, never mistranslate them. */
typedef unsigned u32;
__attribute__((fastcall)) u32 u_fast2(u32 a, u32 b) { return a * 3 + b; }
__attribute__((fastcall)) u32 u_fast3(u32 a, u32 b, u32 c) { return a * 3 + b + c * 5; }
__attribute__((thiscall)) u32 u_this1(u32 self, u32 a) { return self ^ (a << 2); }
__attribute__((thiscall)) u32 u_this0(u32 self) { return self + 1; }
