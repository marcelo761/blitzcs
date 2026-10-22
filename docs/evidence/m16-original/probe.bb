Local cpu$=SystemProperty("cpu")
Local os$=SystemProperty("os")
Local appdir$=SystemProperty("appdir")
Local apphwnd$=SystemProperty("apphwnd")
Local apphinstance$=SystemProperty("apphinstance")
Local windowsdir$=SystemProperty("windowsdir")
Local systemdir$=SystemProperty("systemdir")
Local tempdir$=SystemProperty("tempdir")
Local unknown$=SystemProperty("m16_unknown_property")
Local uppercpu$=SystemProperty("CPU")
Print "unknown-empty="+(unknown$="")
Print "cpu-case-insensitive="+(uppercpu$=cpu$)
Print "tempdir-nonempty="+(Len(tempdir$)>0)
Print "tempdir-trailing-separator="+(Right$(tempdir$,1)=Chr$(92) Or Right$(tempdir$,1)=Chr$(47))
Print "apphwnd-nonzero="+(apphwnd$<>"" And apphwnd$<>"0")
Print "cpu="+cpu$
Print "os="+os$
Print "appdir="+appdir$
Print "apphwnd="+apphwnd$
Print "apphinstance="+apphinstance$
Print "windowsdir="+windowsdir$
Print "systemdir="+systemdir$
Print "tempdir="+tempdir$
