; Deterministic smoke fixture; use SDL_VIDEO_DRIVER=dummy for headless validation.
Global rounds=0
For i=1 To 3
 Blitz3DCSOpenWindow 160,120,"BlitzCS input lifecycle"
 Print Blitz3DCSWindowOpen()
 MoveMouse 37,53
 Print MouseX()+","+MouseY()+","+MouseXSpeed()+","+MouseYSpeed()
 FlushMouse
 FlushKeys
 Print KeyDown(30)+","+KeyHit(30)+","+GetKey()+","+MouseDown(1)+","+MouseHit(1)+","+GetMouse()
 <?cs BB.rounds += 1; ?>
 Blitz3DCSCloseWindow
 Print Blitz3DCSWindowOpen()
Next
Print rounds
