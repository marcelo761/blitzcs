Global calls%
Global values%[1]
Function NextIndex%()
  calls=calls+1
  Return calls-1
End Function
values[NextIndex()]=42
Print calls
Print values[0]
