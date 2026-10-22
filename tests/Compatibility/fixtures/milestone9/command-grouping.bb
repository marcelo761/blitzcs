Function Show%(a%,b%)
 Print a+b
 Return 0
End Function
Function Ping%()
 Print 9
 Return 0
End Function
Show (1),2
Show (1+1),1
Show(1,2)
Ping:Ping
If 1 Then Ping Else Ping
