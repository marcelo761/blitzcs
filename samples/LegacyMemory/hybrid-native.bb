; A real C fixture fills owned bank memory; no window API is simulated.
Global memory%=CreateBank(16)
PokeInt memory,0,41
<?cs module
public static class MemoryBridge
{
    public static int ReadBasicBank(int bank) => BB.readleft(bank);
}
?>
<?cs
BlitzCS.LegacyRuntime.LegacyBanks.PokeInt(BB.memory, 0,
    BlitzCS.LegacyRuntime.LegacyBanks.PeekInt(BB.memory, 0) + 1);
?>
Print CS::MemoryBridge.ReadBasicBank(memory)
Print WriteNativeRect(memory)
Print PeekInt(memory,0)
Print PeekInt(memory,4)
Print PeekInt(memory,8)
Print PeekInt(memory,12)
Print NativePointerBits()
FreeBank memory
Print CloseNativeMemory()
<?cs System.Console.WriteLine(BlitzCS.LegacyRuntime.LegacyBanks.OpenBankCount); ?>
End

Function ReadLeft%(bank%)
    Return PeekInt(bank,0)
End Function
