/*
 * Import slots of the PE fixture used by the Win32 integration test: .idata at RVA 0x3000, IAT at +0x400 => VA 0x10003400, 4 bytes
 * each, in declaration order, with one empty slot terminating each imported DLL's thunk array (slot 15 here). The order below MUST match Win32IntegrationTests.Imports. Win32 APIs are stdcall, the C runtime is cdecl.
 */
typedef unsigned u32;
#define STD(n, ...) (*(u32 (__attribute__((stdcall)) * volatile *)(__VA_ARGS__))(0x10003400 + 4 * (n)))
#define CDE(n, ...) (*(u32 (* volatile *)(__VA_ARGS__))(0x10003400 + 4 * (n)))
#define GetProcessHeap          STD(0, void)
#define HeapAlloc               STD(1, u32, u32, u32)
#define HeapReAlloc             STD(2, u32, u32, u32, u32)
#define HeapSize                STD(3, u32, u32, u32)
#define HeapFree                STD(4, u32, u32, u32)
#define InitializeCriticalSection STD(5, u32)
#define EnterCriticalSection    STD(6, u32)
#define LeaveCriticalSection    STD(7, u32)
#define GetLastError            STD(8, void)
#define TlsAlloc                STD(9, void)
#define TlsSetValue             STD(10, u32, u32)
#define TlsGetValue             STD(11, u32)
#define WideCharToMultiByte     STD(12, u32, u32, u32, u32, u32, u32, u32, u32)
#define MultiByteToWideChar     STD(13, u32, u32, u32, u32, u32, u32)
#define InterlockedIncrement    STD(14, u32)
#define crt_memcpy              CDE(16, u32, u32, u32)
#define crt_strlen              CDE(17, u32)
#define crt_malloc              CDE(18, u32)
#define crt_free                CDE(19, u32)
