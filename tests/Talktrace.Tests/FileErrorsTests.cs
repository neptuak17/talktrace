using System.Runtime.InteropServices;
using Talktrace.Core;

namespace Talktrace.Tests;

public class FileErrorsTests
{
    [Theory]
    [InlineData(unchecked((int)0x80070020), "in use by another app")]
    [InlineData(unchecked((int)0x80070021), "in use by another app")]
    [InlineData(unchecked((int)0x80070070), "enough free space")]
    [InlineData(unchecked((int)0x8007016A), "stored online only")]
    [InlineData(unchecked((int)0x80070185), "stored online only")]
    [InlineData(unchecked((int)0x80070035), "network folder")]
    [InlineData(unchecked((int)0x80070002), "can't find")]
    public void Recognises_windows_errors_when_reading(int hresult, string expected)
    {
        Assert.Contains(expected, FileErrors.Reading(new IOException("x", hresult), "meeting.m4a"));
        Assert.Contains(expected, FileErrors.Reading(new COMException("x", hresult), "meeting.m4a"));
    }

    [Fact]
    public void Says_what_to_do_when_saving()
    {
        Assert.Equal(
            "meeting - transcript.txt is open in another app. Close it there, then save again.",
            FileErrors.Saving(new IOException("x", unchecked((int)0x80070020)), "meeting - transcript.txt"));
        Assert.Equal(
            "Windows won't let Talktrace save meeting - transcript.txt in that folder.",
            FileErrors.Saving(new UnauthorizedAccessException(), "meeting - transcript.txt"));
        Assert.Equal(
            "The folder for meeting - transcript.txt no longer exists.",
            FileErrors.Saving(new DirectoryNotFoundException(), "meeting - transcript.txt"));
    }

    [Fact]
    public void Falls_back_to_windows_own_message()
    {
        Assert.Equal(
            "Talktrace couldn't read meeting.m4a. Something odd.",
            FileErrors.Reading(new IOException("Something odd.", unchecked((int)0x80004005)), "meeting.m4a"));
    }

    [Fact]
    public void Looks_inside_a_wrapping_exception()
    {
        var wrapped = new InvalidOperationException("outer", new IOException("x", unchecked((int)0x80070020)));

        Assert.Contains("in use by another app", FileErrors.Reading(wrapped, "meeting.m4a"));
    }
}
