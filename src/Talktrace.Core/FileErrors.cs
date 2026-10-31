using System.Runtime.InteropServices;

namespace Talktrace.Core;

/// <summary>
/// Plain-language messages for the file errors people actually meet (DESIGN.md section 10): a recording that was moved,
/// one still being written by another app, an online-only cloud file, a full disk, a folder Talktrace can't write to.
/// </summary>
public static class FileErrors
{
    // Win32 error codes, as carried in an exception's HRESULT (0x8007xxxx).
    private const int FileNotFound = 2;
    private const int PathNotFound = 3;
    private const int AccessDenied = 5;
    private const int SharingViolation = 32;
    private const int LockViolation = 33;
    private const int HandleDiskFull = 39;
    private const int BadNetPath = 53;
    private const int NetNameDeleted = 64;
    private const int BadNetName = 67;
    private const int DiskFull = 112;
    private const int FilenameExcedRange = 206;
    // The ERROR_CLOUD_FILE_* codes: OneDrive and similar apps that keep files online until opened.
    private const int FirstCloudFileError = 358;
    private const int LastCloudFileError = 406;

    /// <summary>Why <paramref name="fileName"/> couldn't be read, in plain language.</summary>
    public static string Reading(Exception e, string fileName) =>
        Describe(e, fileName, saving: false) ?? $"Talktrace couldn't read {fileName}. {e.Message}";

    /// <summary>Why <paramref name="fileName"/> couldn't be saved, in plain language.</summary>
    public static string Saving(Exception e, string fileName) =>
        Describe(e, fileName, saving: true) ?? $"Talktrace couldn't save {fileName}. {e.Message}";

    /// <summary>A plain message for a file error Talktrace recognises, or null.</summary>
    internal static string? Describe(Exception e, string fileName, bool saving) => Kind(e) switch
    {
        ErrorKind.NotFound when saving =>
            $"The folder for {fileName} no longer exists.",
        ErrorKind.NotFound =>
            $"Talktrace can't find {fileName} any more. It may have been moved, renamed or deleted.",
        ErrorKind.AccessDenied when saving =>
            $"Windows won't let Talktrace save {fileName} in that folder.",
        ErrorKind.AccessDenied =>
            $"Windows won't let Talktrace open {fileName}. Check that you have permission to read it.",
        ErrorKind.InUse when saving =>
            $"{fileName} is open in another app. Close it there, then save again.",
        ErrorKind.InUse =>
            $"{fileName} is in use by another app, which may still be writing it. Wait for it to finish, then try again.",
        ErrorKind.DiskFull =>
            $"There isn't enough free space on the drive to save {fileName}. Free up some space, then try again.",
        ErrorKind.CloudFile =>
            $"{fileName} is stored online only, and couldn't be downloaded. Connect to the internet, or right-click it " +
            "in File Explorer and choose Always keep on this device, then try again.",
        ErrorKind.Network =>
            $"The network folder holding {fileName} isn't reachable. Check the connection, then try again.",
        ErrorKind.PathTooLong =>
            $"The path to {fileName} is too long for Windows. Use a folder with a shorter path.",
        _ => null,
    };

    private enum ErrorKind
    {
        Other,
        NotFound,
        AccessDenied,
        InUse,
        DiskFull,
        CloudFile,
        Network,
        PathTooLong,
    }

    private static ErrorKind Kind(Exception e)
    {
        switch (e)
        {
            case FileNotFoundException or DirectoryNotFoundException:
                return ErrorKind.NotFound;
            case UnauthorizedAccessException:
                return ErrorKind.AccessDenied;
            case PathTooLongException:
                return ErrorKind.PathTooLong;
        }
        if (e is not (IOException or COMException))
            return e.InnerException is { } inner ? Kind(inner) : ErrorKind.Other;

        // Win32 errors arrive as HRESULTs with facility 7 (0x8007xxxx).
        if ((e.HResult & 0xFFFF0000) != 0x80070000)
            return ErrorKind.Other;
        return (e.HResult & 0xFFFF) switch
        {
            FileNotFound or PathNotFound => ErrorKind.NotFound,
            AccessDenied => ErrorKind.AccessDenied,
            SharingViolation or LockViolation => ErrorKind.InUse,
            DiskFull or HandleDiskFull => ErrorKind.DiskFull,
            >= FirstCloudFileError and <= LastCloudFileError => ErrorKind.CloudFile,
            BadNetPath or NetNameDeleted or BadNetName => ErrorKind.Network,
            FilenameExcedRange => ErrorKind.PathTooLong,
            _ => ErrorKind.Other,
        };
    }
}
