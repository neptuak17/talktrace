using NAudio.CoreAudioApi;

namespace Talktrace.Recording;

/// <summary>A microphone or playback device as Windows names it.</summary>
/// <param name="IsHeadphones">Windows reports it as headphones or a headset, which preselects the headphones mode.</param>
public sealed record AudioDevice(string Id, string Name, bool IsHeadphones)
{
    public override string ToString() => Name;
}

/// <summary>The microphones and playback devices Windows has, and its defaults (DESIGN.md section 9).</summary>
public static class AudioDevices
{
    // EndpointFormFactor values (mmdeviceapi.h)
    private const uint Headphones = 3;
    private const uint Headset = 5;

    public static IReadOnlyList<AudioDevice> Microphones() => List(DataFlow.Capture);

    public static IReadOnlyList<AudioDevice> PlaybackDevices() => List(DataFlow.Render);

    /// <summary>The Windows default communications microphone, which Teams' "Same as system" also follows; null if there is none.</summary>
    public static AudioDevice? DefaultMicrophone() => Default(DataFlow.Capture, Role.Communications);

    /// <summary>The Windows default playback device; null if there is none.</summary>
    public static AudioDevice? DefaultPlayback() => Default(DataFlow.Render, Role.Multimedia);

    /// <summary>The device's own sample rate in Windows' shared mode, or null if it can't be read.</summary>
    public static int? MixSampleRate(string deviceId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using MMDevice device = enumerator.GetDevice(deviceId);
            using AudioClient client = device.CreateAudioClient();
            return client.MixFormat.SampleRate;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Whether the device is muted in Windows (its own mute, not an app's), or null if it can't be read.</summary>
    public static bool? IsMuted(string deviceId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using MMDevice device = enumerator.GetDevice(deviceId);
            return device.AudioEndpointVolume.Mute;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return null;
        }
    }

    internal static MMDevice Open(string deviceId)
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.GetDevice(deviceId);
    }

    private static List<AudioDevice> List(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = new List<AudioDevice>();
        foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
                devices.Add(Describe(device));
        }
        return devices.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static AudioDevice? Default(DataFlow flow, Role role)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.HasDefaultAudioEndpoint(flow, role))
            return null;
        using MMDevice device = enumerator.GetDefaultAudioEndpoint(flow, role);
        return Describe(device);
    }

    private static AudioDevice Describe(MMDevice device)
    {
        bool headphones = false;
        try
        {
            if (device.Properties.Contains(PropertyKeys.PKEY_AudioEndpoint_FormFactor)
                && device.Properties[PropertyKeys.PKEY_AudioEndpoint_FormFactor].Value is uint formFactor)
                headphones = formFactor is Headphones or Headset;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            // Unknown form factor: guess speakers.
        }
        return new AudioDevice(device.ID, device.FriendlyName, headphones);
    }
}
