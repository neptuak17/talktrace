namespace Talktrace.Core.Models;

/// <summary>A transcription profile and the folder its Parakeet model lives in (DESIGN.md, section 8).</summary>
/// <param name="Id">The profile's name on the command line and in the worker protocol.</param>
public sealed record ModelProfile(string Id, string DisplayName, string AsrDirectory)
{
    public static readonly ModelProfile Fast = new("fast", "Fast (English)", "sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8");

    public static readonly ModelProfile Multilingual = new("multilingual", "Multilingual", "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8");

    public static IReadOnlyList<ModelProfile> All { get; } = [Fast, Multilingual];

    public static ModelProfile? FromId(string id) => All.FirstOrDefault(p => p.Id == id);
}
