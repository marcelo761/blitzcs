Type Node
    Field value%
End Type
previousHandle=0
errors=0
For i=1 To 1000
    obj.Node=New Node
    h=Handle(obj)
    If h<>i Then errors=errors+1
    Delete obj
    If Handle(obj)<>0 Then errors=errors+1
    If Object.Node(h)<>Null Then errors=errors+1
    obj=Null
    previousHandle=h
Next
Print previousHandle
Print errors
