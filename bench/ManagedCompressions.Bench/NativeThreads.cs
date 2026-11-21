using System;
using System.Runtime.InteropServices;

namespace ManagedCompressions.Bench;

/// <summary>The timed threads' priority and placement, as each platform lets a process set them.</summary>
internal static class NativeThreads
{
    private const int QosClassUserInteractive = 0x21;

    private const int ThreadPriorityHighest = 2;

    /// <summary>
    /// The current thread on the performance cores at their full clock (macOS's user-interactive QoS),
    /// or at a high priority (Windows), as an interactive application's would be.
    /// </summary>
    public static void Raise()
    {
        if (OperatingSystem.IsMacOS())
        {
            _ = pthread_set_qos_class_self_np(QosClassUserInteractive, 0);
        }
        else if (OperatingSystem.IsWindows())
        {
            _ = SetThreadPriority(GetCurrentThread(), ThreadPriorityHighest);
        }
    }

    /// <summary>The current thread kept on logical processor <paramref name="cpu"/>, on Windows; elsewhere nothing.</summary>
    public static void PinCurrentThread(int cpu)
    {
        if (OperatingSystem.IsWindows())
        {
            _ = SetThreadAffinityMask(GetCurrentThread(), (nuint)1 << cpu);
        }
    }

    [DllImport("libSystem.dylib")]
    private static extern int pthread_set_qos_class_self_np(int qosClass, int relativePriority);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll")]
    private static extern bool SetThreadPriority(nint thread, int priority);

    [DllImport("kernel32.dll")]
    private static extern nuint SetThreadAffinityMask(nint thread, nuint mask);
}
