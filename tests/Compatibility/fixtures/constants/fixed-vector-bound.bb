Const GRID%=2+2
Global values%[GRID%]
Type Cell
  Field neighbors.Cell[GRID%]
End Type
Local cell.Cell=New Cell
values[GRID%]=42
cell\neighbors[GRID%]=New Cell
Print values[GRID%]
Print cell\neighbors[GRID%]=Null
Delete cell\neighbors[GRID%]
Delete cell
