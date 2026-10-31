using Talktrace.Core;
using Talktrace.Core.Models;

namespace Talktrace.App;

/// <summary>An entry in the Speakers dropdown.</summary>
public sealed record SpeakerChoice(string Label, SpeakerSetting Setting)
{
    // What screen readers and UI Automation announce for the item.
    public override string ToString() => Label;
}

/// <summary>An entry in the Model dropdown. Profiles whose speech model isn't installed can't be chosen.</summary>
public sealed record ProfileChoice(ModelProfile Profile, bool IsInstalled)
{
    public string Label => IsInstalled ? Profile.DisplayName : $"{Profile.DisplayName} (not installed)";

    public override string ToString() => Label;
}
