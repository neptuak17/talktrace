using System.Text.Json;
using System.Text.Json.Serialization;
using Talktrace.Core.Models;
using Talktrace.Core.Transcripts;

namespace Talktrace.Core.Worker;

/// <summary>What the app sends the worker on stdin, one JSON object per line: a job first, then perhaps a cancel.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(WorkerJob), "job")]
[JsonDerivedType(typeof(WorkerCancel), "cancel")]
public abstract record WorkerCommand;

/// <summary>Transcribe <paramref name="InputPath"/>. The transcript is saved where <see cref="TranscribeOptions.OutputPath"/> says.</summary>
public sealed record WorkerJob(string InputPath, TranscribeOptions Options) : WorkerCommand;

/// <summary>Stop the job at the next chunk. Partial results are discarded.</summary>
public sealed record WorkerCancel : WorkerCommand;

/// <summary>What the worker writes to stdout, one JSON object per line: progress, then a result or an error.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(WorkerProgress), "progress")]
[JsonDerivedType(typeof(WorkerResult), "result")]
[JsonDerivedType(typeof(WorkerError), "error")]
public abstract record WorkerMessage;

public sealed record WorkerProgress(TranscribeStage Stage, int Done, int Total) : WorkerMessage;

/// <summary>The finished job. The worker has already saved the transcript to <see cref="TranscriptResult.SavedPath"/>.</summary>
public sealed record WorkerResult(TranscriptResult Result) : WorkerMessage;

/// <param name="Message">Plain language, safe to show as is.</param>
/// <param name="Details">The exception and stack trace, for Copy details.</param>
public sealed record WorkerError(string Message, string Details) : WorkerMessage;

/// <summary>The worker protocol between the app and <c>Talktrace.Cli --worker</c> (DESIGN.md section 6).</summary>
public static class WorkerProtocol
{
    public const string WorkerArgument = "--worker";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(), new ModelProfileConverter() },
    };

    public static string Serialize(WorkerCommand command) => JsonSerializer.Serialize(command, Json);

    public static string Serialize(WorkerMessage message) => JsonSerializer.Serialize(message, Json);

    /// <summary>The command on this line, or null if it isn't one.</summary>
    public static WorkerCommand? ParseCommand(string? line) => Parse<WorkerCommand>(line);

    /// <summary>The message on this line, or null if it isn't one: native libraries can write their own lines to stdout.</summary>
    public static WorkerMessage? ParseMessage(string? line) => Parse<WorkerMessage>(line);

    private static T? Parse<T>(string? line) where T : class
    {
        if (line is null || !line.TrimStart().StartsWith('{'))
            return null;
        try
        {
            return JsonSerializer.Deserialize<T>(line, Json);
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return null; // not JSON, or no known "type"
        }
    }

    /// <summary>A profile travels as its id, so the worker uses its own copy of the profile's model folder.</summary>
    private sealed class ModelProfileConverter : JsonConverter<ModelProfile>
    {
        public override ModelProfile Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            ModelProfile.FromId(reader.GetString() ?? "") ?? throw new JsonException("Unknown model profile.");

        public override void Write(Utf8JsonWriter writer, ModelProfile value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Id);
    }
}
