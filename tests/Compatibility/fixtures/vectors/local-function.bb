Function Build%(offset%)
  Local values%[2]
  values[0]=offset
  values[2]=42
  Return values[0]+values[2]
End Function
Print Build(7)
