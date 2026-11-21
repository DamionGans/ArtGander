using ArtGander.App.Services;
using ArtGander.Core.Services;

namespace ArtGander.App.Views;

/// <summary>
/// A text editor for descriptions with simple HTML (the markup the Printify and Shopify editors use): the toolbar wraps
/// the selected text in tags, "Clean up" turns plain text into paragraphs and removes unsupported markup, and
/// "Preview" shows how it looks.
/// </summary>
public partial class DescriptionEditor : ContentView
{
    public static readonly BindableProperty TextProperty = BindableProperty.Create(
        nameof(Text), typeof(string), typeof(DescriptionEditor), "", BindingMode.TwoWay,
        propertyChanged: (b, _, n) => ((DescriptionEditor)b).UpdatePreview());

    public DescriptionEditor()
    {
        InitializeComponent();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private void Wrap(string open, string close)
    {
        var text = TextEditor.Text ?? "";
        var start = Math.Clamp(TextEditor.CursorPosition, 0, text.Length);
        var length = Math.Clamp(TextEditor.SelectionLength, 0, text.Length - start);
        var selected = text.Substring(start, length);
        TextEditor.Text = text[..start] + open + selected + close + text[(start + length)..];
        TextEditor.Focus();
        TextEditor.CursorPosition = start + open.Length;
        TextEditor.SelectionLength = length;
    }

    /// <summary>Makes a list of the selected lines (or the current line).</summary>
    private void MakeList(string listTag)
    {
        var text = TextEditor.Text ?? "";
        var start = Math.Clamp(TextEditor.CursorPosition, 0, text.Length);
        var end = Math.Clamp(start + TextEditor.SelectionLength, start, text.Length);
        // Extend to whole lines.
        var lineStart = text.LastIndexOf('\n', Math.Max(0, start - 1)) is var i && i >= 0 && start > 0 ? i + 1 : 0;
        var lineEnd = text.IndexOf('\n', end) is var j && j >= 0 ? j : text.Length;
        var lines = text[lineStart..lineEnd].ReplaceLineEndings("\n").Split('\n').Where(l => l.Trim().Length > 0)
            .Select(l => "  <li>" + l.Trim() + "</li>");
        var list = $"<{listTag}>\n" + string.Join("\n", lines) + $"\n</{listTag}>";
        TextEditor.Text = text[..lineStart] + list + text[lineEnd..];
        TextEditor.Focus();
        TextEditor.CursorPosition = lineStart + list.Length;
    }

    private void OnParagraph(object? s, EventArgs e) => Wrap("<p>", "</p>");
    private void OnBold(object? s, EventArgs e) => Wrap("<strong>", "</strong>");
    private void OnItalic(object? s, EventArgs e) => Wrap("<em>", "</em>");
    private void OnUnderline(object? s, EventArgs e) => Wrap("<u>", "</u>");
    private void OnStrike(object? s, EventArgs e) => Wrap("<s>", "</s>");
    private void OnHeading(object? s, EventArgs e) => Wrap("<h3>", "</h3>");
    private void OnBullets(object? s, EventArgs e) => MakeList("ul");
    private void OnNumbers(object? s, EventArgs e) => MakeList("ol");

    private void OnLink(object? s, EventArgs e) => Ui.Fire(async () =>
    {
        var start = TextEditor.CursorPosition;
        var length = TextEditor.SelectionLength;
        var url = await Dialogs.PromptAsync("Link", "Web address of the link (https://…)", "https://", "Add link", "https://");
        if (string.IsNullOrWhiteSpace(url) || url.Trim() == "https://") return;
        TextEditor.CursorPosition = start;
        TextEditor.SelectionLength = length;
        Wrap($"<a href=\"{System.Net.WebUtility.HtmlEncode(url.Trim())}\">", "</a>");
    });

    private void OnCleanUp(object? s, EventArgs e) => TextEditor.Text = HtmlText.Clean(HtmlText.FromPlainText(TextEditor.Text));

    private void OnTogglePreview(object? s, EventArgs e)
    {
        PreviewBox.IsVisible = !PreviewBox.IsVisible;
        PreviewButton.Text = PreviewBox.IsVisible ? "Hide preview" : "Preview";
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (PreviewBox is null || !PreviewBox.IsVisible) return;
        PreviewLabel.Text = HtmlText.Clean(HtmlText.FromPlainText(Text));
    }
}
