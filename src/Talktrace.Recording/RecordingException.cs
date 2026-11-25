using System.Runtime.InteropServices;

namespace Talktrace.Recording;

/// <summary>A recording problem described in plain language, safe to show as is.</summary>
public sealed class RecordingException(string message, Exception? inner = null) : Exception(message, inner)
{
    private const int AccessDenied = unchecked((int)0x80070005);
    private const int NotFound = unchecked((int)0x80070490);
    private const int DeviceInvalidated = unchecked((int)0x88890004); // AUDCLNT_E_DEVICE_INVALIDATED
    private const int DeviceInUse = unchecked((int)0x8889000A);       // AUDCLNT_E_DEVICE_IN_USE (exclusive mode)

    /// <summary>Turns a Windows audio error into what to do about it (DESIGN.md section 16: exclusive mode, microphone permission).</summary>
    public static RecordingException FromDevice(Exception e, AudioDevice device, bool loopback)
    {
        int code = e is COMException com ? com.HResult : e.HResult;
        string what = device.Name;
        string message = code switch
        {
            _ when e is UnauthorizedAccessException || code == AccessDenied => loopback
                ? $"Windows isn't letting Talktrace record {what}."
                : "Windows isn't letting Talktrace use the microphone. In Settings > Privacy & security > Microphone, " +
                  "turn on \"Microphone access\" and \"Let desktop apps access your microphone\".",
            DeviceInUse =>
                $"Another app has taken exclusive control of {device.Name}. Close that app, or in Windows' Sound settings open " +
                $"{device.Name}'s properties and turn off \"Allow applications to take exclusive control of this device\".",
            DeviceInvalidated or NotFound => $"{device.Name} isn't available any more.",
            _ => $"Talktrace couldn't record {what}.",
        };
        return new RecordingException(message, e);
    }
}
