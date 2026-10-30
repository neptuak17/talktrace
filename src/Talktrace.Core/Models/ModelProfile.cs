namespace Talktrace.Core.Models;

/// <summary>A transcription profile and the folder its Parakeet model lives in (DESIGN.md, section 8).</summary>
public sealed record ModelProfile(string DisplayName, string AsrDirectory)
{
    public static readonly ModelProfile Fast = new("Fast (English)", "sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8");

    public static readonly ModelProfile Multilingual = new("Multilingual", "sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8");
}
