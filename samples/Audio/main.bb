Global initialized%
initialized%=FSOUND_Init(44100,64,0)
Print initialized%
<?cs
System.Console.WriteLine(BB.initialized);
if (BB.initialized == 0) System.Console.Error.WriteLine(Blitz3DCS.Audio.FSOUND.LastError);
?>
FSOUND_Close()
