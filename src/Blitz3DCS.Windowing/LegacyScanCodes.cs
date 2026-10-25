namespace Blitz3DCS.Windowing;

/// <summary>Maps SDL physical scancodes to PC/DirectInput set-1 keyboard codes.</summary>
internal static class LegacyScanCodes
{
    private static readonly int[] SdlToLegacy = CreateMap();
    private static readonly int[] LegacyToSdl = CreateReverseMap();
    internal static int FromSdl(int scan) => (uint)scan < SdlToLegacy.Length ? SdlToLegacy[scan] : 0;
    internal static int ToSdl(int scan) => (uint)scan < LegacyToSdl.Length ? LegacyToSdl[scan] : 0;

    private static int[] CreateMap()
    {
        var map = new int[512];
        int[] letters = [30,48,46,32,18,33,34,35,23,36,37,38,50,49,24,25,16,19,31,20,22,47,17,45,21,44];
        letters.CopyTo(map, 4);
        int[] main = [2,3,4,5,6,7,8,9,10,11,28,1,14,15,57,12,13,26,27,43,43,39,40,41,51,52,53,58,
            59,60,61,62,63,64,65,66,67,68,87,88,183,70,197,210,199,201,211,207,209,205,203,208,200,
            69,181,55,74,78,156,79,80,81,75,76,77,71,72,73,82,83,86,221,222,141,100,101,102];
        main.CopyTo(map, 30);
        map[133] = 179; // keypad comma
        map[135] = 115; map[136] = 112; map[137] = 125; map[138] = 121; map[139] = 123;
        map[144] = 242; map[145] = 241; map[148] = 148;
        map[224] = 29; map[225] = 42; map[226] = 56; map[227] = 219;
        map[228] = 157; map[229] = 54; map[230] = 184; map[231] = 220;
        return map;
    }

    private static int[] CreateReverseMap()
    {
        var map = new int[256];
        for (var sdl = 1; sdl < SdlToLegacy.Length; sdl++)
        {
            var legacy = SdlToLegacy[sdl];
            if (legacy != 0 && map[legacy] == 0) map[legacy] = sdl;
        }
        return map;
    }
}
