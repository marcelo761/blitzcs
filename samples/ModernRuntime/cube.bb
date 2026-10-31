Graphics3D 320,240,0,2
camera = CreateCamera()
PositionEntity camera,0,0,-5
CameraRange camera,0.1,100
CameraClsColor camera,12,18,28
CameraProjMode camera,1
CameraZoom camera,1
original = CreateCube()
cube = CopyEntity(original)
FreeEntity original
started = MilliSecs()
For frame = 1 To 120
    If frame = 61 Then
        CameraProjMode camera,2
        CameraZoom camera,0.5
    EndIf
    TurnEntity cube,0,1,0
    RenderWorld
    Flip 0
Next
Print "cube-triangles=" + CountTriangles(GetSurface(cube,1))
Print "elapsed-ms=" + (MilliSecs() - started)
EndGraphics
