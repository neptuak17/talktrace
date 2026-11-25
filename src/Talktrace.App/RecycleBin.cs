using System.IO;
using System.Runtime.InteropServices;

namespace Talktrace.App;

/// <summary>Moves files to the Recycle Bin, where they can be restored, without any prompt.</summary>
internal static partial class RecycleBin
{
    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    /// <summary>Recycles the files that exist. Throws <see cref="IOException"/> if Windows refuses.</summary>
    public static void Send(params string[] paths)
    {
        string[] existing = paths.Where(File.Exists).Select(Path.GetFullPath).ToArray();
        if (existing.Length == 0)
            return;

        var operation = new FileOperation
        {
            Function = FO_DELETE,
            From = string.Join('\0', existing) + "\0\0",
            Flags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        int result = SHFileOperation(ref operation);
        if (result != 0 || operation.AnyOperationsAborted)
            throw new IOException($"Windows couldn't move the recording to the Recycle Bin (error {result}).");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileOperation
    {
        public IntPtr Window;
        public uint Function;
        [MarshalAs(UnmanagedType.LPWStr)] public string From;
        [MarshalAs(UnmanagedType.LPWStr)] public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref FileOperation operation);
}
