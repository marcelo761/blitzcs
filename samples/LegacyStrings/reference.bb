; Owned deterministic string contract probe. Byte results use decimal Asc values.
Print "repeat="+String("ab",3)+":"+String("x",0)+":"+String("x",-2)
Print "left="+Left("abcd",2)+":"+Left("abcd",9)+":"+Left("abcd",0)
Print "right="+Right("abcd",2)+":"+Right("abcd",9)+":"+Right("abcd",0)
Print "mid="+Mid("abcd",2)+":"+Mid("abcd",2,2)+":"+Mid("abcd",99)+":"+Mid("abcd",2,-2)+":"+Mid("abcd",2,0)
Print "instr="+Instr("ababa","ba")+","+Instr("ababa","ba",3)+","+Instr("abc","z")+","+Instr("abc","",4)+","+Instr("abc","",5)
Print "replace="+Replace("aaaaa","aa","b")+":"+Replace("aba","a","aa")+":"+Replace("aaaa","aa","")+":"+Replace("","x","z")
Print "case="+Upper("aZ09!? ")+":"+Lower("aZ09!? ")
Print "trim="+Trim(Chr(0)+Chr(9)+Chr(10)+Chr(13)+" abc "+Chr(127))+":"+Len(Trim(Chr(0)+Chr(32)+Chr(127)))
Print "lset="+LSet("abcd",2)+":"+LSet("ab",4)+":"+LSet("abcd",0)
Print "rset="+RSet("abcd",2)+":"+RSet("ab",4)+":"+RSet("abcd",0)
Print "asc="+Asc("")+","+Asc(Chr(0))+","+Asc(Chr(255))+","+Asc(Chr(256))+","+Asc(Chr(-1))
Print "format="+Hex(0)+":"+Hex(-1)+":"+Hex($89abcdef)+":"+Bin(0)+":"+Bin(-1)
Local nul$="a"+Chr(0)+"b"
Print "nul="+Len(nul)+","+Asc(Mid(nul,2,1))+","+Instr(nul,Chr(0))+","+Len(Replace(nul,Chr(0),"xx"))
For i=0 To 255
 Print "byte="+i+","+Asc(Upper(Chr(i)))+","+Asc(Lower(Chr(i)))+","+Len(Trim(Chr(i)))
Next
