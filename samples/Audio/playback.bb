<?cs module
public static class PlaybackProbe
{
    public static int Wait(int milliseconds) { System.Threading.Thread.Sleep(milliseconds); return 0; }
    public static int Snapshot()
    {
        System.Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(Blitz3DCS.Audio.FSOUND.Statistics));
        return 0;
    }
}
?>
If FSOUND_Init(44100,64,0)=0 Then RuntimeError "Audio init failed"
file$=GetEnv("BLITZCS_AUDIO_ASSET")
sound%=LoadSound(file$)
If sound%=0 Then RuntimeError "Sample load failed"
LoopSound sound%
channel%=PlaySound(sound%)
If channel%=0 Then RuntimeError "Sample playback failed"
stream%=FSOUND_Stream_Open(file$,2,0)
If stream%=0 Then RuntimeError "Stream load failed"
streamChannel%=FSOUND_Stream_Play(-1,stream%)
If streamChannel%=-1 Then RuntimeError "Stream playback failed"
FSOUND_SetVolume(streamChannel%,128)
FSOUND_SetPan(streamChannel%,0)
CS::PlaybackProbe.Wait(300)
FSOUND_SetPaused(streamChannel%,1)
ChannelVolume(channel%,0.2)
CS::PlaybackProbe.Wait(200)
FSOUND_SetPaused(streamChannel%,0)
FSOUND_SetFrequency(streamChannel%,22050)
CS::PlaybackProbe.Wait(500)
CS::PlaybackProbe.Snapshot()
StopChannel(channel%)
FSOUND_StopSound(streamChannel%)
FSOUND_Stream_Stop(stream%)
FSOUND_Stream_Close(stream%)
FreeSound(sound%)
FSOUND_Close()
CS::PlaybackProbe.Snapshot()
