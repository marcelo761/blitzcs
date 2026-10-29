using System.Runtime.CompilerServices;

namespace BlitzCS.BinaryCompiler.Runtime;

public sealed unsafe partial class NativeBlitzCommandLibrary
{
    private uint InvokeInteger(uint rva, ReadOnlySpan<uint> args)
    {
        var target = CodeAddress(rva);
        return args.Length switch
        {
            0 => ((delegate* unmanaged[Cdecl]<uint>)target)(),
            1 => ((delegate* unmanaged[Cdecl]<uint, uint>)target)(args[0]),
            2 => ((delegate* unmanaged[Cdecl]<uint, uint, uint>)target)(args[0], args[1]),
            3 => ((delegate* unmanaged[Cdecl]<uint, uint, uint, uint>)target)(args[0], args[1], args[2]),
            4 => ((delegate* unmanaged[Cdecl]<uint, uint, uint, uint, uint>)target)(args[0], args[1], args[2], args[3]),
            5 => ((delegate* unmanaged[Cdecl]<uint, uint, uint, uint, uint, uint>)target)(args[0], args[1], args[2], args[3], args[4]),
            6 => ((delegate* unmanaged[Cdecl]<uint, uint, uint, uint, uint, uint, uint>)target)(args[0], args[1], args[2], args[3], args[4], args[5]),
            7 => ((delegate* unmanaged[Cdecl]<uint, uint, uint, uint, uint, uint, uint, uint>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6]),
            8 => ((delegate* unmanaged[Cdecl]<uint, uint, uint, uint, uint, uint, uint, uint, uint>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7]),
            9 => ((delegate* unmanaged[Cdecl]<uint, uint, uint, uint, uint, uint, uint, uint, uint, uint>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8]),
            10 => ((delegate* unmanaged[Cdecl]<uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9]),
            11 => ((delegate* unmanaged[Cdecl]<uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10]),
            12 => ((delegate* unmanaged[Cdecl]<uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11]),
            13 => ((delegate* unmanaged[Cdecl]<uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11], args[12]),
            14 => ((delegate* unmanaged[Cdecl]<uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11], args[12], args[13]),
            15 => ((delegate* unmanaged[Cdecl]<uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11], args[12], args[13], args[14]),
            16 => ((delegate* unmanaged[Cdecl]<uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11], args[12], args[13], args[14], args[15]),
            _ => throw new NotSupportedException($"InvokeInteger supports arities 0 through 16; got {args.Length}.")
        };
    }

    private uint InvokeStdcallInteger(uint rva, ReadOnlySpan<uint> args)
    {
        var target = CodeAddress(rva);
        return args.Length switch
        {
            0 => ((delegate* unmanaged[Stdcall]<uint>)target)(),
            1 => ((delegate* unmanaged[Stdcall]<uint, uint>)target)(args[0]),
            2 => ((delegate* unmanaged[Stdcall]<uint, uint, uint>)target)(args[0], args[1]),
            3 => ((delegate* unmanaged[Stdcall]<uint, uint, uint, uint>)target)(args[0], args[1], args[2]),
            4 => ((delegate* unmanaged[Stdcall]<uint, uint, uint, uint, uint>)target)(args[0], args[1], args[2], args[3]),
            5 => ((delegate* unmanaged[Stdcall]<uint, uint, uint, uint, uint, uint>)target)(args[0], args[1], args[2], args[3], args[4]),
            6 => ((delegate* unmanaged[Stdcall]<uint, uint, uint, uint, uint, uint, uint>)target)(args[0], args[1], args[2], args[3], args[4], args[5]),
            7 => ((delegate* unmanaged[Stdcall]<uint, uint, uint, uint, uint, uint, uint, uint>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6]),
            8 => ((delegate* unmanaged[Stdcall]<uint, uint, uint, uint, uint, uint, uint, uint, uint>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7]),
            9 => ((delegate* unmanaged[Stdcall]<uint, uint, uint, uint, uint, uint, uint, uint, uint, uint>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8]),
            10 => ((delegate* unmanaged[Stdcall]<uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9]),
            11 => ((delegate* unmanaged[Stdcall]<uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10]),
            12 => ((delegate* unmanaged[Stdcall]<uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11]),
            13 => ((delegate* unmanaged[Stdcall]<uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11], args[12]),
            14 => ((delegate* unmanaged[Stdcall]<uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11], args[12], args[13]),
            15 => ((delegate* unmanaged[Stdcall]<uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11], args[12], args[13], args[14]),
            16 => ((delegate* unmanaged[Stdcall]<uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11], args[12], args[13], args[14], args[15]),
            _ => throw new NotSupportedException($"InvokeStdcallInteger supports arities 0 through 16; got {args.Length}.")
        };
    }

    private float InvokeStdcallFloat(uint rva, ReadOnlySpan<uint> args)
    {
        var target = CodeAddress(rva);
        return args.Length switch
        {
            0 => ((delegate* unmanaged[Stdcall]<float>)target)(),
            1 => ((delegate* unmanaged[Stdcall]<uint, float>)target)(args[0]),
            2 => ((delegate* unmanaged[Stdcall]<uint, uint, float>)target)(args[0], args[1]),
            3 => ((delegate* unmanaged[Stdcall]<uint, uint, uint, float>)target)(args[0], args[1], args[2]),
            4 => ((delegate* unmanaged[Stdcall]<uint, uint, uint, uint, float>)target)(args[0], args[1], args[2], args[3]),
            5 => ((delegate* unmanaged[Stdcall]<uint, uint, uint, uint, uint, float>)target)(args[0], args[1], args[2], args[3], args[4]),
            6 => ((delegate* unmanaged[Stdcall]<uint, uint, uint, uint, uint, uint, float>)target)(args[0], args[1], args[2], args[3], args[4], args[5]),
            7 => ((delegate* unmanaged[Stdcall]<uint, uint, uint, uint, uint, uint, uint, float>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6]),
            8 => ((delegate* unmanaged[Stdcall]<uint, uint, uint, uint, uint, uint, uint, uint, float>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7]),
            9 => ((delegate* unmanaged[Stdcall]<uint, uint, uint, uint, uint, uint, uint, uint, uint, float>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8]),
            10 => ((delegate* unmanaged[Stdcall]<uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, float>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9]),
            11 => ((delegate* unmanaged[Stdcall]<uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, float>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10]),
            12 => ((delegate* unmanaged[Stdcall]<uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, float>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11]),
            13 => ((delegate* unmanaged[Stdcall]<uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, float>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11], args[12]),
            14 => ((delegate* unmanaged[Stdcall]<uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, float>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11], args[12], args[13]),
            15 => ((delegate* unmanaged[Stdcall]<uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, float>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11], args[12], args[13], args[14]),
            16 => ((delegate* unmanaged[Stdcall]<uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, float>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11], args[12], args[13], args[14], args[15]),
            _ => throw new NotSupportedException($"InvokeStdcallFloat supports arities 0 through 16; got {args.Length}.")
        };
    }

    private float InvokeFloat(uint rva, ReadOnlySpan<uint> args)
    {
        var target = CodeAddress(rva);
        return args.Length switch
        {
            0 => ((delegate* unmanaged[Cdecl]<float>)target)(),
            1 => ((delegate* unmanaged[Cdecl]<uint, float>)target)(args[0]),
            2 => ((delegate* unmanaged[Cdecl]<uint, uint, float>)target)(args[0], args[1]),
            3 => ((delegate* unmanaged[Cdecl]<uint, uint, uint, float>)target)(args[0], args[1], args[2]),
            4 => ((delegate* unmanaged[Cdecl]<uint, uint, uint, uint, float>)target)(args[0], args[1], args[2], args[3]),
            5 => ((delegate* unmanaged[Cdecl]<uint, uint, uint, uint, uint, float>)target)(args[0], args[1], args[2], args[3], args[4]),
            6 => ((delegate* unmanaged[Cdecl]<uint, uint, uint, uint, uint, uint, float>)target)(args[0], args[1], args[2], args[3], args[4], args[5]),
            7 => ((delegate* unmanaged[Cdecl]<uint, uint, uint, uint, uint, uint, uint, float>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6]),
            8 => ((delegate* unmanaged[Cdecl]<uint, uint, uint, uint, uint, uint, uint, uint, float>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7]),
            9 => ((delegate* unmanaged[Cdecl]<uint, uint, uint, uint, uint, uint, uint, uint, uint, float>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8]),
            10 => ((delegate* unmanaged[Cdecl]<uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, float>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9]),
            11 => ((delegate* unmanaged[Cdecl]<uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, float>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10]),
            12 => ((delegate* unmanaged[Cdecl]<uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, float>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11]),
            13 => ((delegate* unmanaged[Cdecl]<uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, float>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11], args[12]),
            14 => ((delegate* unmanaged[Cdecl]<uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, float>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11], args[12], args[13]),
            15 => ((delegate* unmanaged[Cdecl]<uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, float>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11], args[12], args[13], args[14]),
            16 => ((delegate* unmanaged[Cdecl]<uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, uint, float>)target)(args[0], args[1], args[2], args[3], args[4], args[5], args[6], args[7], args[8], args[9], args[10], args[11], args[12], args[13], args[14], args[15]),
            _ => throw new NotSupportedException($"InvokeFloat supports arities 0 through 16; got {args.Length}.")
        };
    }

    private uint InvokeThis(uint rva, uint self, ReadOnlySpan<uint> args)
    {
        var target = CodeAddress(rva);
        return args.Length switch
        {
            0 => ((delegate* unmanaged[Thiscall]<uint, uint>)target)(self),
            1 => ((delegate* unmanaged[Thiscall]<uint, uint, uint>)target)(self, args[0]),
            2 => ((delegate* unmanaged[Thiscall]<uint, uint, uint, uint>)target)(self, args[0], args[1]),
            3 => ((delegate* unmanaged[Thiscall]<uint, uint, uint, uint, uint>)target)(self, args[0], args[1], args[2]),
            _ => throw new NotSupportedException($"InvokeThis supports 0 through 3 explicit arguments; got {args.Length}.")
        };
    }
}

