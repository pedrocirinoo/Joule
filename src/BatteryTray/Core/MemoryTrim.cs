using System.Runtime;
using System.Runtime.InteropServices;

namespace BatteryTray.Core;

/// <summary>
/// Fork pedrocirinoo: o app passa o dia parado na bandeja. Depois de uma leitura
/// ou de fechar o painel, devolve ao Windows a memoria que nao esta em uso.
/// </summary>
public static class MemoryTrim
{
    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    public static void Now()
    {
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
        }
        catch (Exception ex) { Log.Error("MemoryTrim", ex); }
    }
}
