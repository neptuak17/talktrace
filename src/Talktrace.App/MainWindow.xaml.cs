using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Talktrace.Core.Settings;

namespace Talktrace.App;

public partial class MainWindow : Window, IDialogs
{
    private readonly MainViewModel _viewModel;
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel(this, SettingsFile.DefaultPath);
        _viewModel.TranscriptRewriting += KeepTranscriptPosition;
        DataContext = _viewModel;
        RestorePlacement(_viewModel.SavedWindowPlacement);
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

    protected override void OnClosed(EventArgs e)
    {
        Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!bounds.IsEmpty)
            _viewModel.SaveWindowPlacement(new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, WindowState == WindowState.Maximized));
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    /// <summary>Puts the window back where it was, if that's still on a screen.</summary>
    private void RestorePlacement(WindowPlacement? placement)
    {
        if (placement is null)
            return;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var bounds = new Rect(placement.Left, placement.Top, Math.Max(placement.Width, MinWidth), Math.Max(placement.Height, MinHeight));
        Rect visible = Rect.Intersect(screen, bounds);
        if (visible.IsEmpty || visible.Width < 200 || visible.Height < 100)
            return; // the screen it was on has gone

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
        if (placement.Maximized)
            WindowState = WindowState.Maximized;
    }

    /// <summary>A rename replaces the whole text, which would jump the transcript to the top; this keeps the reader's place.</summary>
    private void KeepTranscriptPosition()
    {
        double offset = Transcript.VerticalOffset;
        int caret = Transcript.CaretIndex;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            Transcript.CaretIndex = Math.Min(caret, Transcript.Text.Length);
            Transcript.ScrollToVerticalOffset(offset);
        });
    }

    // A speaker's name applies on Enter or when the box loses focus; Esc puts back the name in use.
    private void OnSpeakerNameKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
            return;
        if (e.Key == Key.Enter)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            box.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            e.Handled = true;
        }
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

    public SaveChoice AskToSaveEdits(string? fileName) =>
        PromptWindow.Ask(this,
                fileName is null ? "Save the transcript?" : "Save your changes to the transcript?",
                fileName is null ? "This transcript hasn't been saved yet." : $"Your edits to {fileName} haven't been saved.",
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
