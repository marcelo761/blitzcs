root$=GetEnv("BLITZCS_M12_ROOT")
Print FileSize(root$+"\Data.bin"+Chr(0)+"missing")
Print FileType(root$+"\Data.bin"+Chr(0)+"missing")
Print Len(GetEnv("BLITZCS_M12_ROOT"+Chr(0)+"missing"))>0
