namespace Talktrace.App;

public enum SaveChoice
{
    Save,
    DontSave,
    Cancel,
}

/// <summary>The dialogs the view model needs, shown by the window that owns them.</summary>
public interface IDialogs
{
    /// <returns>The chosen file, or null if cancelled.</returns>
    string? ChooseRecording(string? currentPath);

    /// <returns>The chosen path, or null if cancelled.</returns>
    string? ChooseSavePath(string suggestedPath);

    /// <param name="fileName">The file the transcript was last saved as, or null if it hasn't been saved.</param>
    SaveChoice AskToSaveEdits(string? fileName);

    /// <returns>True to stop the running job and close.</returns>
    bool ConfirmStopAndClose();
}
