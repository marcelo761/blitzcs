Type Samples
  Field values#[2], labels$[1]
End Type
Local sample.Samples=New Samples
Print sample\values[0]
Print sample\values[2]
Print sample\labels[0]
sample\values[2]=3.5
sample\labels[1]="field"
Print sample\values[2]
Print sample\labels[1]
Delete sample
