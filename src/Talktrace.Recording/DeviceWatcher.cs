using NAudio.CoreAudioApi;

namespace Talktrace.Recording;

/// <summary>
/// Tells the recording screen when Windows' devices or defaults change, so its dropdowns stay current. Events arrive on
/// the thread that created the watcher (the UI thread).
/// </summary>
public sealed class DeviceWatcher : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly MMDeviceNotificationClient _client;

    public DeviceWatcher()
    {
        _client = _enumerator.CreateNotificationClient(useSynchronizationContext: true);
        _client.DeviceAdded += (_, _) => Changed?.Invoke();
        _client.DeviceRemoved += (_, _) => Changed?.Invoke();
        _client.DeviceStateChanged += (_, _) => Changed?.Invoke();
        _client.DefaultDeviceChanged += (_, _) => Changed?.Invoke();
    }

    public event Action? Changed;

    public void Dispose()
    {
        _client.Dispose();
        _enumerator.Dispose();
    }
}
