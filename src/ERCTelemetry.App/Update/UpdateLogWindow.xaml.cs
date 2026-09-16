using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ERCTelemetry.Core.Update;

namespace ERCTelemetry.App.Update;

/// <summary>Shown after an update when the stored last-seen app version differs from the
/// running one: renders the released version's UPDATELOG.md section (markdown-lite) so the
/// user sees what changed. The file ships inside the install directory (installer stage).</summary>
public partial class UpdateLogWindow : Window
{
    public UpdateLogWindow(string version, string sectionMarkdown)
    {
        InitializeComponent();
        Title = $"Update-Log — ERCTelemetry {version}";
        HeaderText.Text = "Was ist neu?";
        SubHeaderText.Text = $"ERCTelemetry {version}";
        foreach (var control in RenderBlocks(sectionMarkdown))
        {
            LogPanel.Children.Add(control);
        }
    }

    /// <summary>Minimal markdown renderer for the update log: `##/###` headings, `-`
    /// bullets, plain paragraphs; `**bold**` markers are stripped. No external markdown
    /// dependency — the log is our own file with a known shape.</summary>
    public static IReadOnlyList<UIElement> RenderBlocks(string markdown)
    {
        var blocks = new List<UIElement>();
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                blocks.Add(new TextBlock { Height = 6 });
            }
            else if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                blocks.Add(MakeBlock(line[4..].Trim(), 16, FontWeights.Bold, "#6EA8FE", 14, 0));
            }
            else if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                blocks.Add(MakeBlock(line[3..].Trim(), 18, FontWeights.Bold, "#F4F6FB", 18, 0));
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                var text = Strip(line[2..].Trim());
                blocks.Add(new TextBlock
                {
                    Text = "•  " + text,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                    Foreground = Brush("#B8BFCB"),
                    Margin = new Thickness(0, 2, 0, 2),
                });
            }
            else
            {
                blocks.Add(MakeBlock(Strip(line), 13, FontWeights.Normal, "#B8BFCB", 2, 0));
            }
        }

        return blocks;
    }

    private static TextBlock MakeBlock(string text, double size, FontWeight weight,
        string hex, double topMargin, double bottomMargin)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(hex),
            Margin = new Thickness(0, topMargin, 0, bottomMargin),
        };
        return tb;
    }

    private static Brush Brush(string hex)
    {
        try
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
        catch (FormatException)
        {
            return Brushes.Gray;
        }
    }

    private static string Strip(string text) => text.Replace("**", string.Empty);

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
