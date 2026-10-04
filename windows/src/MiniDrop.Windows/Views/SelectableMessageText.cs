using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Data;
using System.Windows.Media;

namespace MiniDrop.Windows.Views;

/// <summary>Read-only message body with native selection and clickable web links.</summary>
public sealed class SelectableMessageText : RichTextBox
{
    public static readonly DependencyProperty MessageTextProperty = DependencyProperty.Register(
        nameof(MessageText), typeof(string), typeof(SelectableMessageText),
        new PropertyMetadata("", (owner, _) => ((SelectableMessageText)owner).Rebuild()));

    public string MessageText
    {
        get => (string)GetValue(MessageTextProperty);
        set => SetValue(MessageTextProperty, value);
    }

    public SelectableMessageText()
    {
        IsReadOnly = true;
        IsDocumentEnabled = true;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        Background = Brushes.Transparent;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        IsUndoEnabled = false;
    }

    private void Rebuild()
    {
        var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = 20 };
        var offset = 0;
        foreach (var link in WebLinks(MessageText))
        {
            paragraph.Inlines.Add(new Run(MessageText[offset..link.Start]));
            var hyperlink = new Hyperlink(new Run(MessageText.Substring(link.Start, link.Length)))
            {
                NavigateUri = new Uri(link.Url), ToolTip = link.Url,
                Foreground = new SolidColorBrush(Color.FromRgb(53, 101, 220)),
            };
            hyperlink.RequestNavigate += (_, e) =>
            {
                try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
                catch { if (DataContext is ViewModels.MessageViewModel) ToolTip = "无法打开链接"; }
                e.Handled = true;
            };
            paragraph.Inlines.Add(hyperlink);
            offset = link.Start + link.Length;
        }
        paragraph.Inlines.Add(new Run(MessageText[offset..]));
        var document = new FlowDocument(paragraph) { PagePadding = new Thickness(0) };
        BindingOperations.SetBinding(document, FlowDocument.FontFamilyProperty, new Binding(nameof(FontFamily)) { Source = this });
        BindingOperations.SetBinding(document, FlowDocument.FontSizeProperty, new Binding(nameof(FontSize)) { Source = this });
        BindingOperations.SetBinding(document, FlowDocument.ForegroundProperty, new Binding(nameof(Foreground)) { Source = this });
        Document = document;
    }

    public static IEnumerable<(int Start, int Length, string Url)> WebLinks(string text)
    {
        foreach (Match match in Regex.Matches(text, @"(?i)(?<![a-z0-9_])(?:https?://|www\.)[^\s<>""“”‘’，。！？；、（）]+"))
        {
            var value = match.Value.TrimEnd('.', ',', '!', '?', ';', ':');
            while (value.EndsWith(')') && value.Count(c => c == ')') > value.Count(c => c == '(')) value = value[..^1];
            while (value.EndsWith(']') && value.Count(c => c == ']') > value.Count(c => c == '[')) value = value[..^1];
            var url = value.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + value : value;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
                yield return (match.Index, value.Length, url);
        }
    }
}
