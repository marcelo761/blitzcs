; Original-runtime fixture controlled by tools/probe_input_original.py.
; Stage files make external XTest events deterministic without OCR.
AppTitle "BlitzCS Original Input Probe"
Graphics 320,240,0,2
WaitSignal "warp"
MoveMouse 37,53
Snapshot "warp",MouseX()+","+MouseY()+","+MouseXSpeed()+","+MouseYSpeed()+","+MouseXSpeed()+","+MouseYSpeed()
WaitSignal "motion"
Snapshot "motion",MouseX()+","+MouseY()+","+MouseXSpeed()+","+MouseYSpeed()+","+MouseXSpeed()+","+MouseYSpeed()
WaitSignal "mouse-held"
Local md=MouseDown(1),mh=MouseHit(1),mh2=MouseHit(1)
FlushMouse
Snapshot "mouse-held",md+","+mh+","+mh2+","+MouseDown(1)+","+MouseHit(1)+","+GetMouse()
WaitSignal "mouse-three"
Snapshot "mouse-three",MouseDown(1)+","+MouseHit(1)+","+MouseHit(1)+","+GetMouse()+","+GetMouse()+","+GetMouse()+","+GetMouse()
WaitSignal "key-held"
Local kd=KeyDown(30),kh=KeyHit(30),kh2=KeyHit(30)
FlushKeys
Snapshot "key-held",kd+","+kh+","+kh2+","+KeyDown(30)+","+KeyHit(30)+","+GetKey()
WaitSignal "key-three"
Snapshot "key-three",KeyDown(30)+","+KeyHit(30)+","+KeyHit(30)+","+GetKey()+","+GetKey()+","+GetKey()+","+GetKey()
WaitSignal "arrows"
Snapshot "arrows",GetKey()+","+GetKey()+","+GetKey()+","+GetKey()+","+GetKey()+","+GetKey()+","+GetKey()+","+GetKey()+","+GetKey()+","+GetKey()
FlushKeys
WaitSignal "modifier-held"
Snapshot "modifier-held",GetKey()+","+GetKey()+","+KeyDown(42)
FlushKeys
WaitSignal "modifier-released"
Snapshot "modifier-released",GetKey()+","+KeyDown(42)
FlushKeys
WaitSignal "repeat-held"
Snapshot "repeat-held",KeyDown(30)+","+KeyHit(30)+","+GetKey()+","+GetKey()
FlushKeys
WaitSignal "focus-lost"
Snapshot "focus-lost",KeyDown(30)+","+MouseX()+","+MouseY()
WaitSignal "focus-gained"
Snapshot "focus-gained",KeyDown(30)+","+KeyHit(30)+","+MouseX()+","+MouseY()
FlushKeys
Snapshot "wait-ready","ready"
Local wk=WaitKey()
Snapshot "wait-key",wk
WaitSignal "wheel"
Local mz=MouseZ(),ms=MouseZSpeed(),ms2=MouseZSpeed()
FlushMouse
Snapshot "wheel",mz+","+ms+","+ms2+","+MouseZ()
Snapshot "done","complete"
End

Function Snapshot(stage$,value$)
 Local f=WriteFile(stage$+".txt")
 WriteLine f,value$
 CloseFile f
End Function

Function WaitSignal(stage$)
 Snapshot stage$+"-ready","ready"
 Local deadline=MilliSecs()+15000
 While FileType(stage$+".go")=0
  If MilliSecs()>deadline Then End
  Delay 5
 Wend
End Function
