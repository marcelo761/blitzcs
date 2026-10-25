; Must fail before calling the native function: advertised output size exceeds bank capacity.
source = CreateBank(8)
destination = CreateBank(8)
length = CreateBank(4)
PokeInt length, 0, 9
Print ZlibWapi_Compress2(destination, length, source, 8, 6)
