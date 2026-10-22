Global calls%=0
If 0 And Touch() Then Print 99
If 1 Or Touch() Then Print calls
Function Touch%()
    calls=calls+1
    Return 1
End Function
