root$=GetEnv("BLITZCS_M12_ROOT")
Print FileSize(root$+"\subdir")
Print FileSize(root$+"\Data.*")
Print FileType(root$+"\Data.*")
