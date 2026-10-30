# Third-party notices

Talktrace uses the open-source components below. The models are downloaded separately by `scripts/get-models.ps1`; they are not part of this repository or of the app download.

## Libraries shipped with the app

| Component | Licence | Source |
| --- | --- | --- |
| sherpa-onnx 2.0.1 (`org.k2fsa.sherpa.onnx`, `org.k2fsa.sherpa.onnx.runtime.win-arm64`) | Apache-2.0 | https://github.com/k2-fsa/sherpa-onnx |
| ONNX Runtime, included in the sherpa-onnx runtime package | MIT | https://github.com/microsoft/onnxruntime |
| NAudio 3.3.0 (`NAudio.Wasapi`, `NAudio.Core`) | MIT | https://github.com/naudio/NAudio |
| .NET runtime and WPF, included in the self-contained app | MIT | https://github.com/dotnet/runtime, https://github.com/dotnet/wpf |

## Models

| Model | Licence | Attribution |
| --- | --- | --- |
| Parakeet TDT 0.6B v2 (Fast profile) | CC-BY-4.0 | NVIDIA, https://huggingface.co/nvidia/parakeet-tdt-0.6b-v2. Converted to int8 ONNX by the sherpa-onnx project. |
| Parakeet TDT 0.6B v3 (Multilingual profile) | CC-BY-4.0 | NVIDIA, https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3. Converted to int8 ONNX by the sherpa-onnx project. |
| Silero VAD | MIT | Silero Team, https://github.com/snakers4/silero-vad |
| pyannote segmentation 3.0 | MIT | pyannote, https://huggingface.co/pyannote/segmentation-3.0. Exported to ONNX by the sherpa-onnx project. |
| TitaNet small | Apache-2.0 (the NeMo Toolkit licence, which NVIDIA's model card says covers it) | NVIDIA NeMo, https://catalog.ngc.nvidia.com/orgs/nvidia/teams/nemo/models/titanet_small. Exported to ONNX by the sherpa-onnx project. |

CC-BY-4.0 licence text: https://creativecommons.org/licenses/by/4.0/

## Development only (not shipped)

| Component | Licence | Source |
| --- | --- | --- |
| xUnit v3 | Apache-2.0 | https://github.com/xunit/xunit |
