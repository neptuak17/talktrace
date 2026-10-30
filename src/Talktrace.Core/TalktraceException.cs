namespace Talktrace.Core;

/// <summary>An error whose message is plain language, safe to show as is.</summary>
public class TalktraceException : Exception
{
    public TalktraceException(string message) : base(message)
    {
    }

    public TalktraceException(string message, Exception inner) : base(message, inner)
    {
    }
}
