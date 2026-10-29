using System.Xml.Linq;

namespace AcsTool.Presentation.Tests;

/// <summary>
/// Layout lint for the WPF views (the App itself can't be rendered on Linux): every tab scrolls instead of clipping,
/// windows are resizable and tab content has no fixed pixel size (user-reported clipped tab).
/// </summary>
public class XamlLayoutTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private static string ViewsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AcsTool.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "AcsTool.App", "Views");
    }

    private static XDocument Load(string file) => XDocument.Load(Path.Combine(ViewsDir(), file));

    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("SettingsWindow.xaml")]
    public void EveryTabItem_ContentIsAScrollViewer(string file)
    {
        var tabs = Load(file).Descendants(Wpf + "TabItem").ToList();
        Assert.NotEmpty(tabs);
        Assert.All(tabs, t => Assert.Equal("ScrollViewer", t.Elements().Single().Name.LocalName));
    }

    [Theory]
    [InlineData("MainWindow.xaml")]
    [InlineData("SettingsWindow.xaml")]
    public void Window_IsResizable_WithModestMinimumSize(string file)
    {
        var w = Load(file).Root!;
        Assert.Equal("CanResize", (string?)w.Attribute("ResizeMode"));
        Assert.True(int.Parse((string)w.Attribute("MinWidth")!, System.Globalization.CultureInfo.InvariantCulture) <= 800);
        Assert.True(int.Parse((string)w.Attribute("MinHeight")!, System.Globalization.CultureInfo.InvariantCulture) <= 600);
    }

    [Fact]
    public void SimulatorTab_MinimumsFitDefaultWindow_NoNestedScrollbarAtDefaultSize()
    {
        // Review R-m1 / NEW-01: at the default 1280x820 window the tab viewport is ~660 px.
        static int Px(XElement e, string a) => int.Parse((string)e.Attribute(a)!, System.Globalization.CultureInfo.InvariantCulture);
        var simView = Load("MainWindow.xaml").Descendants(Wpf + "TabItem")
            .Select(t => t.Elements().Single().Elements().Single()).Single(v => v.Name.LocalName == "SimulatorView");
        Assert.InRange(Px(simView, "MinHeight"), 400, 600);
        var sim = Load("SimulatorView.xaml");
        var rowMins = sim.Root!.Elements().Single().Element(Wpf + "Grid.RowDefinitions")!.Elements()
            .Sum(r => r.Attribute("MinHeight") is null ? 0 : Px(r, "MinHeight"));
        Assert.True(rowMins <= Px(simView, "MinHeight"), $"row minimums {rowMins} exceed the view minimum");
        var editor = sim.Descendants(Wpf + "ScrollViewer").Single().Elements().Single();
        Assert.InRange(Px(editor, "MinHeight"), 200, 400);
    }

    [Fact]
    public void SimulatorSplitter_ResizesEditorAgainstFeedArea()
    {
        // Review NEW-02: the splitter sat between the editor and the Auto-sized "Running CPEs" row.
        var root = Load("SimulatorView.xaml").Root!.Elements().Single();
        var rows = root.Element(Wpf + "Grid.RowDefinitions")!.Elements().ToList();
        static int RowOf(XElement e) => int.Parse((string?)e.Attribute("Grid.Row") ?? "0", System.Globalization.CultureInfo.InvariantCulture);
        var splitter = Assert.Single(root.Elements(Wpf + "GridSplitter"));
        var r = RowOf(splitter);
        Assert.Equal("Rows", (string?)splitter.Attribute("ResizeDirection"));
        Assert.Equal("PreviousAndNext", (string?)splitter.Attribute("ResizeBehavior"));
        Assert.All(new[] { rows[r - 1], rows[r + 1] }, row => Assert.EndsWith("*", (string)row.Attribute("Height")!, StringComparison.Ordinal));
        Assert.Equal(r + 1, rows.Count - 1); // nothing below the lower pane

        var above = root.Elements().Where(e => e.Name.LocalName != "Grid.RowDefinitions" && RowOf(e) == r - 1).ToList();
        var below = root.Elements().Where(e => e.Name.LocalName != "Grid.RowDefinitions" && e.Name.LocalName != "GridSplitter" && RowOf(e) == r + 1).ToList();
        Assert.Contains(above.Descendants(), d => d.Name.LocalName == "ScrollViewer"); // profile editor
        var lower = Assert.Single(below);
        Assert.Contains(lower.Descendants(), d => d.Name.LocalName == "SessionFeedView");
        Assert.Contains(lower.Descendants(), d => d.Name.LocalName == "DataGrid" && ((string?)d.Attribute("ItemsSource") ?? "").Contains("RunningCpes", StringComparison.Ordinal));
    }

    [Fact]
    public void SimulatorPassword_IsAPasswordBox_NotPlainText()
    {
        // Review m9.
        var sim = Load("SimulatorView.xaml");
        Assert.DoesNotContain(sim.Descendants(Wpf + "TextBox"), t => ((string?)t.Attribute("Text") ?? "").Contains("Password", StringComparison.Ordinal));
        var box = Assert.Single(sim.Descendants(Wpf + "PasswordBox"));
        Assert.Contains(box.Attributes(), a => a.Name.LocalName == "PasswordBoxAssistant.BoundPassword" && a.Value.Contains("Binding Password", StringComparison.Ordinal));
        // Review R2-M1: PasswordChanged must be wired independently of the bound value (null for profiles without a password).
        Assert.Contains(box.Attributes(), a => a.Name.LocalName == "PasswordBoxAssistant.Attach" && a.Value == "True");
    }

    [Fact]
    public void TabContentViews_SizeToViewport_NoFixedPixels()
    {
        foreach (var sv in Load("MainWindow.xaml").Descendants(Wpf + "TabItem").Select(t => t.Elements().Single()))
        {
            var view = sv.Elements().Single();
            Assert.Contains("ViewportWidth", (string?)view.Attribute("Width") ?? "", StringComparison.Ordinal);
            Assert.Contains("ViewportHeight", (string?)view.Attribute("Height") ?? "", StringComparison.Ordinal);
            Assert.NotNull(view.Attribute("MinWidth"));
            Assert.NotNull(view.Attribute("MinHeight"));
        }
        foreach (var file in new[] { "ProxyView.xaml", "SimulatorView.xaml" })
        {
            var root = Load(file).Root!.Elements().Single();
            Assert.Null(root.Attribute("Width"));
            Assert.Null(root.Attribute("Height"));
        }
    }
}
