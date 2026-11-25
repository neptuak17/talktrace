using System.Runtime.InteropServices;

namespace Talktrace.Recording;

/// <summary>
/// Stops the PC from sleeping while a recording runs (DESIGN.md section 9, Sleep). A Windows power request, held
/// until disposed; unlike <c>SetThreadExecutionState</c> it doesn't depend on which thread made it. Closing the lid can
/// still sleep the PC, depending on power settings.
/// </summary>
public sealed partial class KeepAwake : IDisposable
{
    private const int PowerRequestSystemRequired = 1;
    private const uint PowerRequestContextVersion = 0;
    private const uint PowerRequestContextSimpleString = 0x1;

    private readonly IntPtr _request;

    public KeepAwake(string reason)
    {
        var context = new ReasonContext
        {
            Version = PowerRequestContextVersion,
            Flags = PowerRequestContextSimpleString,
            SimpleReasonString = Marshal.StringToHGlobalUni(reason),
        };
        try
        {
            _request = PowerCreateRequest(ref context);
        }
        finally
        {
            Marshal.FreeHGlobal(context.SimpleReasonString);
        }
        if (_request == IntPtr.Zero || _request == new IntPtr(-1) || !PowerSetRequest(_request, PowerRequestSystemRequired))
            throw new RecordingException("Windows couldn't be kept awake for the recording.", new System.ComponentModel.Win32Exception());
    }

    public void Dispose()
    {
        PowerClearRequest(_request, PowerRequestSystemRequired);
        CloseHandle(_request);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        public IntPtr SimpleReasonString;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr PowerCreateRequest(ref ReasonContext context);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PowerSetRequest(IntPtr request, int requestType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PowerClearRequest(IntPtr request, int requestType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
