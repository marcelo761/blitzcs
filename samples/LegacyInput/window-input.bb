; This sample needs SDL3 and a real desktop display.
; The console reports input; the window has no renderer.
Global inputevents=0

Blitz3DCSOpenWindow 640,480,"BlitzCS keyboard and mouse"
MoveMouse 320,240
FlushKeys
FlushMouse
Print "Focus the window, type, click, move the mouse, or turn the wheel."
Print "Escape or the window close button exits. Output appears in this console."

While Blitz3DCSWindowOpen() And (Not KeyHit(1))
 Blitz3DCSPumpEvents
 Local key=GetKey()
 Local button=GetMouse()
 Local dx=MouseXSpeed(),dy=MouseYSpeed(),dz=MouseZSpeed()
 If key<>0 Or button<>0 Or dx<>0 Or dy<>0 Or dz<>0 Then
  <?cs BB.inputevents += 1; ?>
  Print "event="+inputevents+" key="+key+" button="+button+" x="+MouseX()+" y="+MouseY()+" z="+MouseZ()+" dx="+dx+" dy="+dy+" dz="+dz
 EndIf
 <?cs System.Threading.Thread.Sleep(10); ?>
Wend

Blitz3DCSCloseWindow
Print "Input event batches: "+inputevents
