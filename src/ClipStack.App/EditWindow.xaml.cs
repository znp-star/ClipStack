using System.Windows;
using System.Windows.Input;

namespace ClipStack.App;

/// <summary>
/// Plain-text editor for one clipboard item. Deliberately minimal: the plan rules out
/// rich text editing, so the dialog edits the text column and nothing else.
/// </summary>
public partial class EditWindow : Window
{
    public EditWindow(string text)
    {
        InitializeComponent();

        Editor.Text = text;
        Loaded += (_, _) =>
        {
            Editor.Focus();
            Editor.CaretIndex = Editor.Text.Length;
        };
    }

    /// <summary>The text after a successful save; only meaningful when the dialog returns true.</summary>
    public string EditedText { get; private set; } = string.Empty;

    private void Save_Click(object sender, RoutedEventArgs e) => TrySave();

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                DialogResult = false;
                e.Handled = true;
                break;

            case Key.Enter when Keyboard.Modifiers == ModifierKeys.Control:
                TrySave();
                e.Handled = true;
                break;
        }
    }

    private void TrySave()
    {
        var text = Editor.Text;
        if (text.Length == 0)
        {
            ErrorText.Text = "内容不能为空。Esc 取消，或输入后保存。";
            return;
        }

        EditedText = text;
        DialogResult = true;
    }
}
