Graphics3D 320,240,0,0
camera = CreateCamera()
PositionEntity camera,0,0,-5
CameraRange camera,0.1,100
CameraClsColor camera,12,18,28
cube = CreateCube()
While True
    TurnEntity cube,0,1,0
    RenderWorld
    Flip
Wend
