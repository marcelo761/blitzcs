; Fixed procedural scene for the x86-native / x64-runtime graphics bridge.
; Keep byte-identical for both compilers; no external assets or game state.

SeedRnd 1
Graphics3D 800, 600, 0, 2
SetBuffer BackBuffer()

cam = CreateCamera()
CameraRange cam, 0.1, 500.0
CameraClsColor cam, 8, 12, 20
PositionEntity cam, 0.0, 6.0, -12.0
target = CreatePivot()
PositionEntity target, 0.0, 0.0, 22.0
PointEntity cam, target

AmbientLight 72, 72, 72
light = CreateLight(1)
RotateEntity light, 35.0, 25.0, 0.0

; 64 independently transformed meshes, each with a 24-segment sphere.
; Stagger layers so geometry remains visible instead of stacking exactly.
base = CreateSphere(24)
ScaleEntity base, 0.85, 0.85, 0.85
For layer = 0 To 3
	For row = 0 To 3
		For col = 0 To 3
			If layer = 0 And row = 0 And col = 0 Then
				mesh = base
			Else
				mesh = CopyEntity(base)
			EndIf
			If (row + col + layer) Mod 2 = 0 Then
				EntityColor mesh, 70 + col * 32, 100 + row * 28, 180 + layer * 20
			Else
				EntityColor mesh, 180 + layer * 18, 80 + col * 30, 70 + row * 32
			EndIf
			PositionEntity mesh, (Float(col) - 1.5) * 3.5 + (Float(layer Mod 2) - 0.5) * 1.0, (Float(row) - 1.5) * 3.0 + (Float(layer Mod 2) - 0.5) * 0.8, 8.0 + Float(layer) * 9.0
		Next
	Next
Next

; Let renderer and runtime settle before timing.
For warmup = 1 To 120
	UpdateWorld
	RenderWorld
	Flip 0
Next

triangles_first = TrisRendered()
started = MilliSecs()
frames = 0
elapsed = 0
Repeat
	UpdateWorld
	RenderWorld
	Flip 0
	frames = frames + 1
	 elapsed = MilliSecs() - started
Until elapsed >= 10000 And frames >= 600

; Pixel/triangle verification is deliberately outside the timed interval.
UpdateWorld
RenderWorld
triangles_last = TrisRendered()
LockBuffer BackBuffer()
background = ReadPixelFast(5, 5, BackBuffer()) And $FFFFFF
center = ReadPixelFast(400, 300, BackBuffer()) And $FFFFFF
center_non_background = center <> background
non_background = 0
pixel_sum = 0
For py = 40 To 560 Step 40
	For px = 40 To 760 Step 40
		pixel = ReadPixelFast(px, py, BackBuffer()) And $FFFFFF
		If pixel <> background Then non_background = non_background + 1
		pixel_sum = pixel_sum + pixel
	Next
Next
UnlockBuffer

fps = Float(frames) * 1000.0 / Float(elapsed)
f = WriteFile("fps-profile.tsv")
WriteLine f, "scene=procedural-sphere-grid"
WriteLine f, "frames=" + frames
WriteLine f, "elapsed_ms=" + elapsed
WriteLine f, "fps=" + fps
WriteLine f, "mean_frame_ms=" + (Float(elapsed) / Float(frames))
WriteLine f, "triangles_first=" + triangles_first
WriteLine f, "triangles_last=" + triangles_last
WriteLine f, "center_pixel=" + center
WriteLine f, "center_non_background=" + center_non_background
WriteLine f, "non_background_pixels=" + non_background
WriteLine f, "pixel_sum=" + pixel_sum
CloseFile f
End
