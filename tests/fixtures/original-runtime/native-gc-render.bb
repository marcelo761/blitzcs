; Exercise real native argument cleanup, string helpers, return boxing and rendering.
Graphics3D 640,480,0,2
AppTitle "Original Blitz3D native ABI regression"
camera=CreateCamera()
PositionEntity camera,0,0,-5
cube=CreateCube()
bank=CreateBank(64)
PokeInt bank,0,123
For i=0 To 99999
    name$="cube "+i+Chr(0)+"tail"
    NameEntity cube,name
    If EntityName(cube)<>name Then
        <?cs throw new System.InvalidOperationException("Native string round trip failed"); ?>
    EndIf
    If PeekInt(bank,0)<>123 Then
        <?cs throw new System.InvalidOperationException("Native bank contents changed"); ?>
    EndIf
    If BankSize(bank)<>64 Then
        <?cs throw new System.InvalidOperationException("Native bank size changed"); ?>
    EndIf
Next
FreeBank bank
For frame=1 To 120
    TurnEntity cube,0.1,0.2,0
    UpdateWorld
    RenderWorld
    Flip 0
    Delay 1
Next
<?cs System.GC.Collect(); System.GC.WaitForPendingFinalizers(); ?>
Print "PASS original-runtime native ABI regression"
End
