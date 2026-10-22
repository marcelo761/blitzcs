; Inclusive upper bounds, multiple dimensions, typed defaults and reset.
Dim grid%(2,1)
Dim names$(1)
Dim weights#(1)

For x=0 To 2
    For y=0 To 1
        grid(x,y)=x*10+y
    Next
Next

names(1)="ready"
weights(1)=1.5
Print grid(2,1)
Print "["+names(0)+"]"
Print names(1)
Print weights(1)

Dim grid(1,1)
Print grid(1,1)
