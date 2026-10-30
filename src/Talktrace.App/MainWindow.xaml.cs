using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Talktrace.App;

public partial class MainWindow : Window, IDialogs
{
    private readonly MainViewModel _viewModel;
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel(this);
        DataContext = _viewModel;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _closeConfirmed || _viewModel.CanCloseNow)
            return;

        // Ask once the Closing event is over: the window can't be closed again from inside it.
        e.Cancel = true;
        Dispatcher.InvokeAsync(async () =>
        {
            if (!await _viewModel.ConfirmCloseAsync())
                return;
            _closeConfirmed = true;
            Close();
        });
    }

    // Recordings dropped anywhere on the window, including on the transcript box, which would otherwise take them as text.
    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;
        e.Effects = _viewModel.IsIdle ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnPreviewDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files)
            return;
        e.Handled = true;
        await _viewModel.OpenRecordingAsync(files[0]);
    }

    public string? ChooseRecording(string? currentPath)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a recording",
            Filter = "Recordings|*.wav;*.mp3;*.m4a;*.mp4;*.wma;*.aac;*.m4v;*.mov;*.wmv|All files|*.*",
        };
        if (currentPath is not null)
            dialog.InitialDirectory = Path.GetDirectoryName(currentPath);
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    public string? ChooseSavePath(string suggestedPath)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save the transcript",
            Filter = "Text files|*.txt|All files|*.*",
            DefaultExt = ".txt",
            InitialDirectory = Path.GetDirectoryName(suggestedPath),
            FileName = Path.GetFileName(suggestedPath),
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    public SaveChoice AskToSaveEdits(string fileName) =>
        PromptWindow.Ask(this, "Save your changes to the transcript?", $"Your edits to {fileName} haven't been saved.",
                "Save", "Don't save", "Cancel") switch
        {
            0 => SaveChoice.Save,
            1 => SaveChoice.DontSave,
            _ => SaveChoice.Cancel,
        };

    public bool ConfirmStopAndClose() =>
        PromptWindow.Ask(this, "Stop transcribing and close?", "The transcription in progress will be cancelled.",
            "Stop and close", "Keep transcribing") == 0;
}
