Print "init-valid"
Print FSOUND_Init(44100,64,0)
Print "init-while-active"
Print FSOUND_Init(44100,64,0)
FSOUND_Close()
Print "reinit-valid-flag"
Print FSOUND_Init(44100,64,128)
FSOUND_Close()
Print "init-zero-rate"
Print FSOUND_Init(0,64,0)
Print "init-zero-channels"
Print FSOUND_Init(44100,0,0)
Print "init-negative-channels"
Print FSOUND_Init(44100,-1,0)
Print "init-unknown-flag-bit"
Print FSOUND_Init(44100,64,-2147483648)
FSOUND_Close()
