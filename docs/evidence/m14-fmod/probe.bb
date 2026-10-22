Print M13Version()
Local output%=M13Output(0)
Print output
Print output And 255
Print "init-valid"
Print M13Init(44100,64,0) And 255
Print M13Error()
Print "init-while-active"
Print M13Init(44100,64,0) And 255
Print M13Error()
M13Close
Print "closed-once"
Print "reinit-valid-flag"
Print M13Init(44100,64,128) And 255
Print M13Error()
M13Close
Print "closed-twice"
Print "init-zero-rate"
Print M13Init(0,64,0) And 255
Print M13Error()
Print "init-zero-channels"
Print M13Init(44100,0,0) And 255
Print M13Error()
Print "init-negative-channels"
Print M13Init(44100,-1,0) And 255
Print M13Error()
Print "init-unknown-flag-bit"
Print M13Init(44100,64,-2147483648) And 255
Print M13Error()
M13Close
Print "closed-final"
