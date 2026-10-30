using System.Windows;
using System.Windows.Controls;

namespace Talktrace.App;

/// <summary>A question with buttons named for what they do, such as Save, Don't save and Cancel.</summary>
public partial class PromptWindow : Window
{
    private int _choice = -1;

    private PromptWindow()
    {
        InitializeComponent();
    }

    /// <summary>Shows the prompt. The first button is the default; the last one also answers Esc and closing the window.</summary>
    /// <returns>The index of the button clicked.</returns>
    public static int Ask(Window owner, string heading, string body, params string[] buttons)
    {
        var prompt = new PromptWindow { Owner = owner };
        prompt.Heading.Text = heading;
        prompt.Body.Text = body;
        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;
            var button = new Button
            {
                Content = buttons[i],
                MinWidth = 96,
                Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0),
                IsDefault = i == 0,
                IsCancel = i == buttons.Length - 1,
            };
            if (i == 0 && prompt.TryFindResource("AccentButtonStyle") is Style accent)
                button.Style = accent;
            button.Click += (_, _) =>
            {
                prompt._choice = index;
                prompt.Close();
            };
            prompt.Buttons.Children.Add(button);
        }

        prompt.ShowDialog();
        return prompt._choice < 0 ? buttons.Length - 1 : prompt._choice;
    }
}
