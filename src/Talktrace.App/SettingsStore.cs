using Talktrace.Core.Settings;

namespace Talktrace.App;

/// <summary>The settings both tabs share, saved to <c>settings.json</c> whenever one changes.</summary>
public sealed class SettingsStore(string path)
{
    public UserSettings Current { get; private set; } = SettingsFile.Load(path);

    public void Update(Func<UserSettings, UserSettings> change)
    {
        Current = change(Current);
        SettingsFile.Save(path, Current);
    }
}
