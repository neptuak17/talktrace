using Talktrace.Core;
using Talktrace.Core.Settings;

namespace Talktrace.Tests;

public sealed class UserSettingsTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("talktrace-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Saves_and_loads_the_settings()
    {
        string path = Path.Combine(_folder, "Talktrace", "settings.json");
        var settings = new UserSettings
        {
            Speakers = "4",
            IncludeTimestamps = true,
            ClusteringThreshold = 0.55,
            Threads = 4,
            MaxSpeechSeconds = 18,
            MaxChunkSeconds = 22,
            Window = new WindowPlacement(10, 20, 1000, 800, Maximized: true),
        };

        SettingsFile.Save(path, settings);

        Assert.Equal(settings, SettingsFile.Load(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void A_missing_or_damaged_file_gives_the_defaults()
    {
        string path = Path.Combine(_folder, "settings.json");
        Assert.Equal(new UserSettings(), SettingsFile.Load(path));

        File.WriteAllText(path, "{ not json");
        Assert.Equal(new UserSettings(), SettingsFile.Load(path));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("auto")]
    [InlineData("1")]
    [InlineData("8")]
    public void Speaker_settings_round_trip(string text)
    {
        Assert.Equal(text, UserSettings.ToText(UserSettings.ParseSpeakers(text)!.Value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("9")]
    [InlineData("lots")]
    public void Unknown_speaker_settings_are_ignored(string? text)
    {
        Assert.Null(UserSettings.ParseSpeakers(text));
    }

    [Fact]
    public void Writes_speaker_settings_as_words_and_counts()
    {
        Assert.Equal("none", UserSettings.ToText(SpeakerSetting.None));
        Assert.Equal("3", UserSettings.ToText(SpeakerSetting.Exactly(3)));
    }
}
