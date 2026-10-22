Function ReadAtLater%()
    Restore later
    Read value%
    Return value
End Function
Data 10
.later
Data 30
Data 40
Print ReadAtLater()
Read next_value%
Print next_value
