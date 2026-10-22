Dim values%(2,1)
values(2,1)=42
Print values(0,0)
Print ReadLast()
Function ReadLast%()
    Return values(2,1)
End Function
