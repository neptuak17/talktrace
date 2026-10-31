using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Talktrace.Core.Transcripts;

namespace Talktrace.App;

/// <summary>One speaker in the speakers panel (DESIGN.md section 10, item 6): their label, name, sample and play button.</summary>
public sealed partial class SpeakerRow : ObservableObject
{
    private readonly MainViewModel _owner;
    private bool _resetting;

    public SpeakerRow(MainViewModel owner, int number, Turn? sampleTurn)
    {
        _owner = owner;
        Number = number;
        Label = TranscriptFormatter.SpeakerName(number);
        CurrentName = Label;
        SampleTurn = sampleTurn;
        Sample = sampleTurn is null ? "" : $"“{SpeakerNames.SampleSentence(sampleTurn)}”";
        _resetting = true;
        NameText = "";
        _resetting = false;
    }

    public int Number { get; }

    /// <summary>Speaker 1, Speaker 2 and so on.</summary>
    public string Label { get; }

    /// <summary>The name the transcript uses now: the label until the speaker is renamed.</summary>
    public string CurrentName { get; internal set; }

    /// <summary>The turn the sample sentence comes from, which the play button plays.</summary>
    public Turn? SampleTurn { get; }

    public string Sample { get; }

    public bool HasSample => SampleTurn is not null;

    /// <summary>What's in the name box. Bound so it changes on Enter or when the box loses focus, which applies the rename.</summary>
    [ObservableProperty]
    public partial string NameText { get; set; }

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNameError))]
    public partial string? NameError { get; set; }

    public bool HasNameError => NameError is not null;

    /// <summary>Puts the name box back to the name in use: empty for a speaker who hasn't been renamed.</summary>
    internal void ResetNameText()
    {
        _resetting = true;
        NameText = CurrentName == Label ? "" : CurrentName;
        _resetting = false;
    }

    partial void OnNameTextChanged(string value)
    {
        if (!_resetting)
            _owner.RenameSpeaker(this, value);
    }

    [RelayCommand]
    private void PlaySample() => _owner.ToggleSample(this);
}
