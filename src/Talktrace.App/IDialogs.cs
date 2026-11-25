namespace Talktrace.App;

public enum SaveChoice
{
    Save,
    DontSave,
    Cancel,
}

public enum RecoverChoice
{
    RecoverAndTranscribe,
    Recover,
    NotNow,
}

/// <summary>The dialogs the view models need, shown by the window that owns them.</summary>
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

    /// <returns>The chosen folder, or null if cancelled.</returns>
    string? ChooseFolder(string currentFolder);

    /// <returns>True to cancel the running transcription and start recording.</returns>
    bool ConfirmCancelJobToRecord();

    /// <returns>True to stop and save the recording, then close.</returns>
    bool ConfirmStopRecordingAndClose();

    /// <summary>Offers to recover a recording Talktrace didn't finish.</summary>
    RecoverChoice AskToRecover(DateTime startedAt, TimeSpan recorded);
}
