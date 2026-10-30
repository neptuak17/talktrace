using System.Reflection;
using System.Runtime.InteropServices;

namespace Talktrace.Core.Speakers;

/// <summary>
/// Checks whether a sherpa-onnx object loaded. sherpa-onnx returns a null handle when a model fails to
/// load, and its C# wrappers don't check: using that handle crashes the process. The wrappers keep the
/// handle in a private field, so read it here. Re-check after any sherpa-onnx upgrade.
/// </summary>
internal static class NativeHandle
{
    public static bool IsNull(object wrapper) =>
        wrapper.GetType().GetField("_handle", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(wrapper)
            is HandleRef { Handle: 0 };
}
