Print "version":Print M15Version()
Print "output":Print M15Output(1)
Print "init":Print M15Init(44100,64,0) And 255:Print "error":Print M15Error()
Print "channel-zero-is-playing":Print M15IsPlaying(0) And 255:Print M15Error()
Print "channel-zero-volume":Print M15SetVolume(0,32) And 255:Print M15Error()
Print "channel-zero-pause":Print M15SetPaused(0,1) And 255:Print M15Error()
Print "channel-zero-stop":Print M15StopChannel(0) And 255:Print M15Error()
Print "channel-large-is-playing":Print M15IsPlaying(999999) And 255:Print M15Error()
Print "open-length-minus-one":Local stream2%=M15Open("Z:\tmp\blitzcs-m15-fmod-gwq__s2u\tone.wav",2,0,-1):Print stream2%:Print M15Error():Print M15CloseStream(stream2%) And 255
Print "open-loop-valid":Local stream%=M15Open("Z:\tmp\blitzcs-m15-fmod-gwq__s2u\tone.wav",2,0,0):Print stream%:Print M15Error()
Print "open-error-read-again":Print M15Error()
Print "play-free":Local channel%=M15Play(-1,stream%):Print channel%:Print M15Error()
Print "playing-before-pause":Print M15IsPlaying(channel%) And 255
Print "volume-128":Print M15SetVolume(channel%,128) And 255:Print M15GetVolume(channel%)
Print "volume-zero":Print M15SetVolume(channel%,0) And 255:Print M15GetVolume(channel%)
Print "volume-255":Print M15SetVolume(channel%,255) And 255:Print M15GetVolume(channel%)
Print "volume-256":Print M15SetVolume(channel%,256) And 255:Print M15Error()
Print "volume-minus-one":Print M15SetVolume(channel%,-1) And 255:Print M15GetVolume(channel%):Print M15Error()
Print "restore-volume-128":Print M15SetVolume(channel%,128) And 255:Print M15GetVolume(channel%)
Print "frequency-11025":Print M15SetFrequency(channel%,11025) And 255:Print M15GetFrequency(channel%)
Print "frequency-zero":Print M15SetFrequency(channel%,0) And 255:Print M15GetFrequency(channel%):Print M15Error()
Print "frequency-minus-one":Print M15SetFrequency(channel%,-1) And 255:Print M15GetFrequency(channel%):Print M15Error()
Print "frequency-reset-22050":Print M15SetFrequency(channel%,22050) And 255:Print M15GetFrequency(channel%):Print M15Error()
Print "pan-left":Print M15SetPan(channel%,0) And 255
Print "pause":Print M15SetPaused(channel%,1) And 255:Print M15GetPaused(channel%)
Print "playing-paused":Print M15IsPlaying(channel%) And 255
Print "resume":Print M15SetPaused(channel%,0) And 255:Print M15GetPaused(channel%)
Delay 3300
Print "playing-after-source-duration-loop-mode-2":Print M15IsPlaying(channel%) And 255:Print M15Error()
Print "volume-all-64":Print M15SetVolume(-3,64) And 255:Print M15Error()
Print "pan-right":Print M15SetPan(channel%,255) And 255
Print "channel-stop":Print M15StopChannel(channel%) And 255:Print M15Error()
Print "playing-after-stop":Print M15IsPlaying(channel%) And 255
Print "stream-stop-after-channel-stop":Print M15StopStream(stream%) And 255:Print M15Error()
Print "stream-close":Print M15CloseStream(stream%) And 255:Print M15Error()
Print "channel-playing-after-stream-close":Print M15IsPlaying(channel%) And 255:Print M15Error()
Print "open-missing":Local missing%=M15Open("Z:\tmp\blitzcs-m15-fmod-gwq__s2u\missing.wav",1,0,0):Print missing%:Print M15Error()
Print "close":M15Close:Print "closed"
Print "channel-playing-after-device-close":Print M15IsPlaying(channel%) And 255:Print M15Error()
Print "device-close-error-read-again":Print M15Error()
Print "reinit-after-device-close":Print M15Init(44100,64,0) And 255:Print M15Error()
Print "close-after-reinit":M15Close:Print "closed-again"
