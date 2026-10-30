using System.Globalization;
using System.Text;

namespace Talktrace.Core.Transcripts;

/// <summary>Writes the plain-text transcript format (DESIGN.md section 11).</summary>
public static class TranscriptFormatter
{
    private const string NewLine = "\r\n";

    public static string Format(TranscriptResult result, bool includeTimestamps)
    {
        var text = new StringBuilder();
        text.Append("Transcript: ").Append(result.SourceFileName).Append(NewLine);
        text.Append("Transcribed: ").Append(result.TranscribedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Append(" · Duration: ").Append(FormatDuration(result.Duration))
            .Append(" · Model: ").Append(result.ModelName).Append(NewLine);

        foreach (Turn turn in result.Turns)
        {
            text.Append(NewLine);
            if (includeTimestamps)
                text.Append('[').Append(FormatTimestamp(turn.Start)).Append("] ");
            text.Append(turn.Text).Append(NewLine);
        }

        return text.ToString();
    }

    /// <summary>52:14, or 1:02:14 for an hour or more.</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        var rounded = TimeSpan.FromSeconds(Math.Round(duration.TotalSeconds));
        return rounded.TotalHours >= 1
            ? $"{(int)rounded.TotalHours}:{rounded.Minutes:00}:{rounded.Seconds:00}"
            : $"{rounded.Minutes}:{rounded.Seconds:00}";
    }

    /// <summary>Always hours, minutes and seconds: 00:03:12.</summary>
    internal static string FormatTimestamp(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Floor(Math.Max(0, seconds)));
        return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}";
    }
}
