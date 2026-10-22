For i=1 To 2
    Repeat
        Select i
            Case 1
                Print "one"
                Exit
            Default
                Print "other"
                Exit
        End Select
        Print "unreachable"
    Forever
Next
