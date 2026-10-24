; The original FMOD 3.75 (fmod.dll, untouched) driven from plain BlitzBasic.
;   blitzcs build main.bb --externals fmod.json --assets assets     (see README.md)
; fmod.json declares the FSOUND functions and the library; this program has no compiler- or library-specific code.
; assets\tone.wav (44.1 kHz, 16-bit, mono) is installed next to the program: the library reads it by the relative name below.

; FSOUND_OUTPUTTYPES: 0 = no sound (FMOD's silent mixer), 1 = Windows Multimedia (waveOut, the real sound device), 2 = DirectSound (the host's own dsound.dll).
Const FSOUND_OUTPUT_NOSOUND = 0
Const FSOUND_OUTPUT_WINMM = 1
Const FSOUND_OUTPUT_DSOUND = 2
Const OUTPUT_TYPE = FSOUND_OUTPUT_NOSOUND   ; the single switch: FSOUND_OUTPUT_WINMM or FSOUND_OUTPUT_DSOUND play through the host sound device

Const FSOUND_FREE = -1                      ; Sample_Load / PlaySound: take any free sample slot / channel
Const FSOUND_NORMAL = 304                   ; FSOUND_16BITS (16) + FSOUND_MONO (32) + FSOUND_SIGNED (256)

; Delay is not part of the compiler's runtime; a three-line C# bridge supplies it.
<?cs module
public static class Clock { public static int Sleep(int ms) { System.Threading.Thread.Sleep(ms); return 0; } }
?>
Function Delay(ms%)
    CS::Clock.Sleep(ms)
End Function

Print "version " + FSOUND_GetVersion()
Print "mixer before init " + FSOUND_GetMixer()
Print "set output " + (FSOUND_SetOutput(OUTPUT_TYPE) And 255)
Print "output " + FSOUND_GetOutput()
Print "drivers " + (FSOUND_GetNumDrivers() > 0)
Print "set driver " + (FSOUND_SetDriver(0) And 255)
Print "init " + (FSOUND_Init(44100, 32, 0) And 255)
Print "error " + FSOUND_GetError()
Print "mixer resolved " + (FSOUND_GetMixer() <> 4)   ; before Init the mixer is FSOUND_MIXER_QUALITY_AUTODETECT (4); Init picks the CPU's

sample = FSOUND_Sample_Load(FSOUND_FREE, "tone.wav", FSOUND_NORMAL, 0, 0)
Print "sample " + (sample <> 0)
Print "samples " + FSOUND_Sample_GetLength(sample)
Print "mode " + FSOUND_Sample_GetMode(sample)

channel = FSOUND_PlaySound(FSOUND_FREE, sample)
Print "channel " + (channel >= 0)
Print "playing " + (FSOUND_IsPlaying(channel) And 255)
Print "volume " + (FSOUND_SetVolume(channel, 200) And 255)
Delay 300
Print "playing " + (FSOUND_IsPlaying(channel) And 255)
Print "stop " + (FSOUND_StopSound(channel) And 255)
Print "playing " + (FSOUND_IsPlaying(channel) And 255)

; Play once more to the end: one second of tone, then the channel frees itself.
channel = FSOUND_PlaySound(FSOUND_FREE, sample)
Delay 1300
Print "finished " + (FSOUND_IsPlaying(channel) And 255)

FSOUND_Sample_Free sample
Print "error " + FSOUND_GetError()
FSOUND_Close()
Print "closed"
