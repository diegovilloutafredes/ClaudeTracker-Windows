using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ClaudeTracker.Core;

namespace ClaudeTracker.App;

/// <summary>The colours a view draws with, decided once per render for light or dark.</summary>
internal sealed record Palette(bool IsDark, Brush Primary, Brush Secondary, Brush Tertiary, Rgb Background)
{
    /// <summary>The secondary text colour as a colour, for strokes drawn at partial strength.</summary>
    public Color Ink => ((SolidColorBrush)Secondary).Color;

    public static Color With(Color color, double strength) => Color.FromArgb((byte)Math.Round(255 * strength), color.R, color.G, color.B);

    public static Color From(Rgb color) => Color.FromRgb(color.R8, color.G8, color.B8);
}

/// <summary>One line of a chart: its points, its stroke, and whether the area under it is filled.</summary>
internal sealed record ChartLine(IReadOnlyList<(DateTimeOffset Time, double Value)> Points, Color Stroke, double Thickness,
                                 double[]? Dash = null, Color? Fill = null);

/// <summary>
/// Everything a chart draws: the span of time across it, the value at its top (the bottom is
/// zero), its lines, and where the two axes are marked and what they say there.
/// </summary>
internal sealed record Plot(DateTimeOffset Lower, DateTimeOffset Upper, double Top, IReadOnlyList<ChartLine> Lines,
                            IReadOnlyList<double> YTicks, Func<double, string> YLabel,
                            IReadOnlyList<DateTimeOffset> XTicks, Func<DateTimeOffset, string> XLabel, Color Ink);

/// <summary>
/// A small chart drawn by hand: a grid, lines with an optional fill, axis labels in a fixed
/// column on the right, and a vertical rule at a marked moment. The app draws its own charts
/// rather than take on a charting library; what to draw is decided by <see cref="ChartsTab"/>
/// and the arithmetic by <see cref="ChartLayout"/>.
/// </summary>
internal sealed class MiniChart : FrameworkElement
{
    private const double PlotHeight = 60;
    private const double AxisHeight = 16;
    /// <summary>Width of the y-axis labels' column. Fixed, so stacked charts share one time axis whatever their labels say.</summary>
    private const double LabelColumn = 34;

    private Plot? plot;

    /// <summary>The moment marked by a vertical rule, if any. Call <see cref="UIElement.InvalidateVisual"/> after changing it.</summary>
    public DateTimeOffset? Rule { get; set; }

    /// <summary>Told the moment under the pointer, to the minute; null when the pointer leaves.</summary>
    public Action<DateTimeOffset?>? Pointed { get; set; }

    public MiniChart()
    {
        Height = PlotHeight + AxisHeight;
    }

    /// <summary>Draws this from now on. The element stays the same one; what it shows changes.</summary>
    public void Show(Plot shown)
    {
        plot = shown;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawing)
    {
        // Something has to be drawn everywhere for the pointer to be noticed everywhere.
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (plot is not { } shown) return;
        var width = ActualWidth - LabelColumn;
        var span = (shown.Upper - shown.Lower).TotalSeconds;
        if (width <= 0 || !(span > 0) || !(shown.Top > 0)) return;

        double X(DateTimeOffset time) => (time - shown.Lower).TotalSeconds / span * width;
        double Y(double value) => PlotHeight - Math.Clamp(value / shown.Top, 0, 1) * PlotHeight;
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var labelBrush = new SolidColorBrush(shown.Ink);
        var typeface = new Typeface("Segoe UI");
        FormattedText Label(string text) =>
            new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 9.5, labelBrush, pixelsPerDip);

        var grid = new Pen(new SolidColorBrush(Palette.With(shown.Ink, 0.22)), 0.5);
        foreach (var tick in shown.YTicks)
        {
            var y = Math.Round(Y(tick)) + 0.5;
            drawing.DrawLine(grid, new Point(0, y), new Point(width, y));
            var label = Label(shown.YLabel(tick));
            drawing.DrawText(label, new Point(width + 5, Math.Clamp(y - label.Height / 2, 0, PlotHeight - label.Height)));
        }
        var labelled = double.NegativeInfinity;
        foreach (var tick in shown.XTicks)
        {
            var x = Math.Round(X(tick)) + 0.5;
            drawing.DrawLine(grid, new Point(x, 0), new Point(x, PlotHeight));
            var label = Label(shown.XLabel(tick));
            // Centred on its mark, kept inside the plot, and left out rather than drawn over its neighbour.
            var left = Math.Clamp(x - label.Width / 2, 0, Math.Max(width - label.Width, 0));
            if (left < labelled + 6) continue;
            drawing.DrawText(label, new Point(left, PlotHeight + 2));
            labelled = left + label.Width;
        }

        drawing.PushClip(new RectangleGeometry(new Rect(0, 0, width, PlotHeight)));
        foreach (var line in shown.Lines)
        {
            if (line.Points.Count < 2) continue;
            if (line.Fill is { } fill)
            {
                var area = new StreamGeometry();
                using (var context = area.Open())
                {
                    context.BeginFigure(new Point(X(line.Points[0].Time), PlotHeight), isFilled: true, isClosed: true);
                    foreach (var (time, value) in line.Points) context.LineTo(new Point(X(time), Y(value)), isStroked: false, isSmoothJoin: false);
                    context.LineTo(new Point(X(line.Points[^1].Time), PlotHeight), isStroked: false, isSmoothJoin: false);
                }
                area.Freeze();
                drawing.DrawGeometry(new SolidColorBrush(fill), null, area);
            }
            var path = new StreamGeometry();
            using (var context = path.Open())
            {
                context.BeginFigure(new Point(X(line.Points[0].Time), Y(line.Points[0].Value)), isFilled: false, isClosed: false);
                for (var i = 1; i < line.Points.Count; i++)
                {
                    context.LineTo(new Point(X(line.Points[i].Time), Y(line.Points[i].Value)), isStroked: true, isSmoothJoin: true);
                }
            }
            path.Freeze();
            var pen = new Pen(new SolidColorBrush(line.Stroke), line.Thickness) { LineJoin = PenLineJoin.Round };
            if (line.Dash is { } dash) pen.DashStyle = new DashStyle(dash.Select(length => length / line.Thickness), 0);
            drawing.DrawGeometry(null, pen, path);
        }
        if (Rule is { } rule && rule >= shown.Lower && rule <= shown.Upper)
        {
            var x = Math.Round(X(rule)) + 0.5;
            var pen = new Pen(new SolidColorBrush(Palette.With(shown.Ink, 0.55)), 1) { DashStyle = new DashStyle([3, 3], 0) };
            drawing.DrawLine(pen, new Point(x, 0), new Point(x, PlotHeight));
        }
        drawing.Pop();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var width = ActualWidth - LabelColumn;
        if (width <= 0 || plot is not { } shown) return;
        var fraction = Math.Clamp(e.GetPosition(this).X / width, 0, 1);
        Pointed?.Invoke(ChartLayout.QuantizeToMinute(shown.Lower.AddSeconds((shown.Upper - shown.Lower).TotalSeconds * fraction)));
    }

    protected override void OnMouseLeave(MouseEventArgs e) => Pointed?.Invoke(null);

    // An element drawn by hand is nothing to a screen reader until it says what it is.
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    /// <summary>Presents the chart as a picture; its name and its figures are set by whoever builds it.</summary>
    private sealed class Peer(MiniChart owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
        protected override string GetClassNameCore() => nameof(MiniChart);
        protected override bool IsContentElementCore() => true;
        protected override bool IsControlElementCore() => true;
    }
}

/// <summary>
/// A row of choices where one is on: the popover's tabs, the charts' range. One is made per
/// use and kept, and <see cref="Show"/> brings it up to date — made again at every render,
/// the choice holding the keyboard focus would lose it at every poll.
/// </summary>
internal sealed class Segmented : Border
{
    private static readonly ControlTemplate SegmentTemplate = MakeTemplate();
    private readonly UniformGrid row = new() { Rows = 1 };
    private readonly string group;
    private readonly Action<int> select;
    /// <summary>True while <see cref="Show"/> is setting the choices: a change then is not the user's.</summary>
    private bool showing;

    public Segmented(string group, Action<int> select)
    {
        this.group = group;
        this.select = select;
        CornerRadius = new CornerRadius(7);
        Padding = new Thickness(2);
        Child = row;
    }

    public void Show(IReadOnlyList<string> labels, int selected, Palette palette)
    {
        showing = true;
        try
        {
            Background = new SolidColorBrush(palette.IsDark ? Color.FromRgb(0x38, 0x38, 0x38) : Color.FromRgb(0xE6, 0xE6, 0xE6));
            var chosen = new SolidColorBrush(palette.IsDark ? Color.FromRgb(0x4A, 0x4A, 0x4A) : Color.FromRgb(0xFF, 0xFF, 0xFF));
            while (row.Children.Count > labels.Count) row.Children.RemoveAt(row.Children.Count - 1);
            for (var i = 0; i < labels.Count; i++)
            {
                if (i == row.Children.Count) row.Children.Add(Option(i));
                var option = (RadioButton)row.Children[i];
                option.Content = labels[i];
                option.Foreground = palette.Primary;
                // The template fills the chosen segment with the button's own background.
                option.Background = chosen;
                option.IsChecked = i == selected;
            }
        }
        finally
        {
            showing = false;
        }
    }

    private RadioButton Option(int index)
    {
        // Radio buttons, so a screen reader hears "selected" and the arrow keys move the choice.
        var option = new RadioButton
        {
            GroupName = group,
            Template = SegmentTemplate,
            FontSize = 12,
            Cursor = Cursors.Hand,
            // The theme's radio button is at least 120 wide and sits at the left of its
            // cell; five of those in a row hid their own labels. A segment fills its cell.
            MinWidth = 0,
            MinHeight = 0,
            Margin = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        option.Checked += (_, _) =>
        {
            if (!showing) select(index);
        };
        AutomationProperties.SetAutomationId(option, $"{group}-{index}");
        return option;
    }

    private static ControlTemplate MakeTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border), "Segment");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetValue(Border.PaddingProperty, new Thickness(8, 3, 8, 4));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        border.AppendChild(content);
        var template = new ControlTemplate(typeof(RadioButton)) { VisualTree = border };
        var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        // A binding, not a template binding: a setter takes no other kind.
        var chosen = new Binding { Path = new PropertyPath(Control.BackgroundProperty), RelativeSource = RelativeSource.TemplatedParent };
        on.Setters.Add(new Setter(BackgroundProperty, chosen, "Segment"));
        template.Triggers.Add(on);
        template.Seal();
        return template;
    }
}

/// <summary>
/// The Charts tab: a range picker and a content menu, then per usage window its utilization,
/// its pace, and a forecast of the window in progress. Unlike the rest of the popover it is
/// one element, kept and brought up to date (<see cref="Refresh"/>): its menu stays open
/// while a poll lands, its list stays where it was scrolled to, its range picker keeps the
/// keyboard, and a chart stays the same element to the pointer and to a screen reader.
/// </summary>
internal sealed class ChartsTab
{
    private readonly UsageViewModel viewModel;
    private readonly StackPanel root = new();
    private readonly Segmented ranges;
    private readonly Button filter;
    /// <summary>Stands in for the list: "Nothing selected", "No data for this period".</summary>
    private readonly TextBlock message = new() { FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 26, 0, 16) };
    /// <summary>
    /// The scroll bar is drawn over the list's right edge, not beside it: the margin is its
    /// lane. Without one it covers the ends of the figures and the axis labels.
    /// </summary>
    private readonly StackPanel list = new() { Margin = new Thickness(0, 0, ScrollBarLane, 0) };
    private const double ScrollBarLane = 14;
    private readonly ScrollViewer viewer;
    private readonly ViewCache views = new();
    private ContextMenu? menu;
    /// <summary>The moment under the pointer, shared by every chart.</summary>
    private DateTimeOffset? pointer;
    /// <summary>One per chart on screen: applies <see cref="pointer"/> to its rule and its figures.</summary>
    private readonly List<Action> followers = [];

    public ChartsTab(UsageViewModel viewModel)
    {
        this.viewModel = viewModel;
        ranges = new Segmented("chart-range", index => viewModel.ChartTimeRange = ChartTimeRanges.All[index]);
        filter = new Button
        {
            Content = Symbols.Text("\uE71C", 13), // the filter
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = L.T("Chart content"),
        };
        AutomationProperties.SetName(filter, L.T("Chart content"));
        // A name that does not change with the language, for whoever drives the app from a script.
        AutomationProperties.SetAutomationId(filter, "ChartContent");
        filter.Click += (_, _) => OpenMenu();

        viewer = new ScrollViewer
        {
            Content = list,
            Margin = new Thickness(0, 14, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
        AutomationProperties.SetName(viewer, L.T("Charts"));
        // The keyboard reaches the list only while there is something to scroll: then the
        // arrows and Page Up and Down move it. Otherwise it would be a stop that does nothing.
        viewer.ScrollChanged += (_, _) => viewer.Focusable = viewer.IsTabStop = viewer.ScrollableHeight > 0;

        var controls = new DockPanel();
        DockPanel.SetDock(filter, Dock.Right);
        controls.Children.Add(filter);
        controls.Children.Add(ranges);
        root.Children.Add(controls);
        root.Children.Add(message);
        root.Children.Add(viewer);
    }

    /// <summary>Brings the tab up to date with the view model and returns it: always the same element.</summary>
    public UIElement Refresh(Palette palette, double maxListHeight)
    {
        followers.Clear();
        var now = DateTimeOffset.UtcNow;
        var lower = now.AddHours(-viewModel.ChartTimeRange.Hours());
        var history = viewModel.UsageHistory;
        var hidden = viewModel.HiddenChartSeries;
        var shown = viewModel.ChartSeries.Where(series => !hidden.Contains(series.Key)).ToList();
        var anyKind = viewModel.ChartShowUtilization || viewModel.ChartShowPace || viewModel.ChartShowForecast;

        ranges.Show(ChartTimeRanges.All.Select(range => range.RawValue()).ToList(),
                    ChartTimeRanges.All.ToList().IndexOf(viewModel.ChartTimeRange), palette);
        filter.Foreground = palette.Secondary;

        var nothing = shown.Count == 0 || !anyKind ? L.T("Nothing selected")
            : !history.Any(point => point.Timestamp >= lower) ? L.T("No data for this period")
            : null;
        message.Text = nothing ?? "";
        message.Foreground = palette.Secondary;
        message.Visibility = nothing is null ? Visibility.Collapsed : Visibility.Visible;
        viewer.Visibility = nothing is null ? Visibility.Visible : Visibility.Collapsed;
        viewer.MaxHeight = maxListHeight;

        var sections = new List<UIElement>();
        if (nothing is null)
        {
            foreach (var series in shown)
            {
                if (sections.Count > 0)
                {
                    var rule = views.Keep<Border>(series.Key + "/rule");
                    rule.Height = 1;
                    rule.Margin = new Thickness(0, 14, 0, 14);
                    rule.Background = new SolidColorBrush(Palette.With(palette.Ink, 0.25));
                    sections.Add(rule);
                }
                sections.Add(Section(series, history, lower, now, palette));
            }
        }
        ViewCache.SetChildren(list, sections);
        views.Sweep();
        return root;
    }

    /// <summary>The popover is going away: its menu goes with it, and the pointer is no longer on a chart.</summary>
    public void Leave()
    {
        if (menu is { } open) open.IsOpen = false;
        menu = null;
        pointer = null;
    }

    // MARK: - What to show

    private void OpenMenu()
    {
        if (menu is { } already) already.IsOpen = false;
        var hidden = viewModel.HiddenChartSeries;
        var opened = new ContextMenu { PlacementTarget = filter, Placement = PlacementMode.Bottom };
        opened.Items.Add(MenuHeading(L.T("Windows")));
        foreach (var series in viewModel.ChartSeries)
        {
            opened.Items.Add(Choice(series.Title, !hidden.Contains(series.Key), on => viewModel.SetChartSeriesShown(series.Key, on)));
        }
        opened.Items.Add(new Separator());
        opened.Items.Add(MenuHeading(L.T("Charts")));
        opened.Items.Add(Choice(L.T("Utilization"), viewModel.ChartShowUtilization, on => viewModel.ChartShowUtilization = on));
        opened.Items.Add(Choice(L.T("Pace"), viewModel.ChartShowPace, on => viewModel.ChartShowPace = on));
        opened.Items.Add(Choice(L.T("Forecast"), viewModel.ChartShowForecast, on => viewModel.ChartShowForecast = on));
        opened.Closed += (_, _) =>
        {
            if (ReferenceEquals(menu, opened)) menu = null;
        };
        menu = opened;
        opened.IsOpen = true;
    }

    private static MenuItem MenuHeading(string text) => new() { Header = text, IsEnabled = false };

    /// <summary>A tick that stays open when pressed: several are usually changed in one visit, and each shows at once.</summary>
    private static MenuItem Choice(string text, bool on, Action<bool> set)
    {
        var item = new MenuItem { Header = text.Replace("_", "__"), IsCheckable = true, IsChecked = on, StaysOpenOnClick = true };
        item.Checked += (_, _) => set(true);
        item.Unchecked += (_, _) => set(false);
        return item;
    }

    // MARK: - One window

    private UIElement Section(ChartSeries series, IReadOnlyList<UsageDataPoint> history, DateTimeOffset lower, DateTimeOffset now, Palette palette)
    {
        var key = series.Key;
        // The same staleness rule as the Usage tab's rows: a forecast anchored to a window
        // that has already reset would show old numbers as current.
        var live = series.Window is { } window && !viewModel.IsWindowStale(window) ? window : null;
        var pace = viewModel.Pace(key);
        var band = PaceMath.AccentUrgency(pace?.ProjectedHours, live?.ResetsAtDate, live is null, now);
        // The pace chart and the forecast take the window's pace band, like the row's pace
        // line: a rate in %/hr is not a utilization, and must not be coloured as one.
        var paceColor = band > 0 ? Palette.From(Urgency.Color(band)) : palette.Ink;
        var unit = viewModel.PaceRateUnit;

        var title = views.Keep<TextBlock>(key + "/title");
        title.Text = series.Title;
        title.FontSize = 12;
        title.FontWeight = FontWeights.SemiBold;
        title.Foreground = palette.Primary;
        AutomationProperties.SetHeadingLevel(title, AutomationHeadingLevel.Level2);
        var parts = new List<UIElement> { title };
        if (viewModel.ChartShowUtilization)
        {
            var pairs = ChartLayout.Pairs(history, point => point.Utilization(key), lower);
            var color = Palette.From(Urgency.Color(Math.Min((pairs.Count > 0 ? pairs[^1].Value : 0) / 100, 1)));
            parts.Add(TimeChart(key + "/utilization", L.T("Utilization"), pairs, lower, now, top: 100, ticks: [0, 50, 100], color,
                                value => ((int)value).ToString(CultureInfo.InvariantCulture) + "%",
                                value => ((int)value).ToString(CultureInfo.InvariantCulture) + "%", palette));
        }
        if (viewModel.ChartShowPace)
        {
            var pairs = ChartLayout.Pairs(history, point => point.PaceRate(key), lower);
            var top = ChartLayout.PaceScaleTop(pairs);
            parts.Add(TimeChart(key + "/pace", L.T("Pace"), pairs, lower, now, top, ticks: [0, top / 2, top], paceColor,
                                value => unit.Format(value), value => unit.AxisLabel(value), palette));
        }
        if (viewModel.ChartShowForecast && live is { ResetsAtDate: { } reset })
        {
            parts.Add(ForecastChart(key + "/forecast", series, history, live, reset, pace?.Rate, paceColor, palette));
        }
        // A bare title would read as broken, not as empty.
        if (parts.Count == 1) parts.Add(Collecting(key + "/collecting", 50, palette));

        var panel = views.Keep<StackPanel>(key + "/section");
        ViewCache.SetChildren(panel, parts);
        return panel;
    }

    /// <param name="chart">The chart this stands in for, said first: its heading is for the eye only.</param>
    private TextBlock Collecting(string name, double height, Palette palette, string? chart = null)
    {
        var text = views.Keep<TextBlock>(name);
        text.Text = L.T("Collecting…");
        AutomationProperties.SetName(text, chart is null ? text.Text : chart + ", " + text.Text);
        text.FontSize = 11;
        text.Foreground = palette.Tertiary;
        text.Height = height;
        text.Padding = new Thickness(0, height / 2 - 8, 0, 0);
        text.TextAlignment = TextAlignment.Center;
        return text;
    }

    /// <summary>
    /// A chart's heading: its name on the left, its figures on the right. Both are for the
    /// eye: the chart under them says its own name and the same figures in whole words
    /// ("peak", not "pk"), so a screen reader is given the chart and not these.
    /// </summary>
    private (DockPanel Row, TextBlock Figures) Heading(string name, string label, Palette palette)
    {
        var title = views.Keep<QuietText>(name + "/name");
        title.Text = label;
        title.FontSize = 11;
        title.Foreground = palette.Secondary;
        DockPanel.SetDock(title, Dock.Left);
        var figures = views.Keep<QuietText>(name + "/figures");
        figures.FontSize = 10.5;
        figures.Foreground = palette.Secondary;
        figures.VerticalAlignment = VerticalAlignment.Bottom;
        DockPanel.SetDock(figures, Dock.Right);
        var row = views.Keep<DockPanel>(name + "/heading");
        row.Margin = new Thickness(0, 12, 0, 6);
        row.LastChildFill = false;
        ViewCache.SetChildren(row, [title, figures]);
        return (row, figures);
    }

    /// <summary>
    /// A utilization or pace chart over the chosen range: every sample counts for the figures
    /// and for the pointer, and at most two per time bucket are drawn.
    /// </summary>
    private UIElement TimeChart(string name, string label, List<(DateTimeOffset Time, double Value)> pairs, DateTimeOffset lower, DateTimeOffset upper,
                                double top, double[] ticks, Color color, Func<double, string> format, Func<double, string> axis, Palette palette)
    {
        var panel = views.Keep<StackPanel>(name);
        var (heading, figures) = Heading(name, label, palette);
        if (pairs.Count < 2)
        {
            figures.Text = "";
            ViewCache.SetChildren(panel, [heading, Collecting(name + "/collecting", 50, palette, label)]);
            return panel;
        }

        var span = (upper - lower).TotalSeconds;
        var use24Hour = viewModel.Use24HourTime;
        var dates = TimeText.DisplayCulture();
        var stats = ChartLayout.Stats(pairs)!.Value;
        var chart = views.Keep<MiniChart>(name + "/chart");
        chart.Show(new Plot(lower, upper, top,
                            [new ChartLine(Charts.Downsample(pairs, Charts.Buckets, lower, upper), color, 1.5, Fill: Palette.With(color, 0.15))],
                            ticks, axis, ChartLayout.TimeTicks(lower, upper), time => ChartLayout.TimeLabel(time, span, use24Hour, culture: dates), palette.Ink));
        AutomationProperties.SetName(chart, label);
        AutomationProperties.SetHelpText(chart, L.F("now %@, peak %@, average %@", format(stats.Now), format(stats.Peak), format(Math.Round(stats.Average))));

        void Follow()
        {
            // The rule marks the matched reading's own moment, not the pointer's: the figure
            // belongs where the reading is. Inside a gap nothing matches, and nothing is marked.
            var reading = pointer is { } moment ? Charts.NearestSample(pairs, moment, span) : null;
            chart.Rule = reading?.Time;
            var first = reading is { } found
                ? L.F("@ %@  %@", format(found.Value), ChartLayout.TimeLabel(found.Time, span, use24Hour, culture: dates))
                : L.F("now %@", format(stats.Now));
            figures.Text = L.F("%@  pk %@  avg %@", first, format(stats.Peak), format(Math.Round(stats.Average)));
            chart.InvalidateVisual();
        }
        Join(chart, Follow);
        ViewCache.SetChildren(panel, [heading, chart]);
        return panel;
    }

    /// <summary>
    /// The window in progress: the even pace that would fill it exactly at its reset, the
    /// readings so far, and where the current pace leads. It always shows the whole window,
    /// whatever range the other charts are on.
    /// </summary>
    private UIElement ForecastChart(string name, ChartSeries series, IReadOnlyList<UsageDataPoint> history, UsageWindow live, DateTimeOffset reset,
                                    double? rate, Color accent, Palette palette)
    {
        var start = reset.AddSeconds(-series.DurationSeconds);
        var pairs = ChartLayout.Pairs(history, point => point.Utilization(series.Key), start);
        var frame = ChartLayout.ForecastFor(pairs, reset, series.DurationSeconds, live.Utilization, rate);
        var panel = views.Keep<StackPanel>(name);
        var (heading, figures) = Heading(name, L.T("Forecast"), palette);
        var unit = viewModel.PaceRateUnit;
        var resting = rate is { } current ? L.F("pace %@", unit.Format(current)) : "";
        figures.Text = resting;
        if (pairs.Count < 2)
        {
            ViewCache.SetChildren(panel, [heading, Collecting(name + "/collecting", 60, palette, L.T("Forecast"))]);
            return panel;
        }

        var span = (frame.End - frame.WindowStart).TotalSeconds;
        var use24Hour = viewModel.Use24HourTime;
        var dates = TimeText.DisplayCulture();
        var lines = new List<ChartLine>
        {
            new([(frame.WindowStart, 0), (frame.Reset, 100)], Palette.With(palette.Ink, 0.4), 1, Dash: [4, 4]),
            new(Charts.Downsample(pairs, Charts.Buckets, frame.WindowStart, frame.End), Palette.With(palette.Ink, 0.85), 1.5, Fill: Palette.With(palette.Ink, 0.12)),
        };
        if (frame is { LastTime: { } last, ProjectedFull: { } full })
        {
            lines.Add(new ChartLine([(last, frame.LastValue), (full, 100)], accent, 1.5, Dash: [5, 3]));
        }
        var chart = views.Keep<MiniChart>(name + "/chart");
        chart.Show(new Plot(frame.WindowStart, frame.End, Top: 105, lines, YTicks: [0, 25, 50, 75, 100],
                            value => ((int)value).ToString(CultureInfo.InvariantCulture) + "%",
                            ChartLayout.TimeTicks(frame.WindowStart, frame.End), time => ChartLayout.TimeLabel(time, span, use24Hour, culture: dates), palette.Ink));
        AutomationProperties.SetName(chart, L.T("Forecast chart"));
        AutomationProperties.SetHelpText(chart, L.F("now %@", ((int)frame.LastValue).ToString(CultureInfo.InvariantCulture) + "%"));

        void Follow()
        {
            // Here the rule follows the pointer itself, across the whole window: ahead of the
            // last reading there is nothing to snap to, and the line being read is the forecast.
            var inside = pointer is { } moment && moment >= frame.WindowStart && moment <= frame.End ? pointer : null;
            chart.Rule = inside;
            var reading = inside is { } at ? Charts.NearestSample(pairs, at, span) : null;
            figures.Text = reading is { } found
                ? L.F("actual %d%%  expected %d%%  %@", (int)found.Value,
                      (int)ChartLayout.ExpectedPercent(found.Time, frame.WindowStart, frame.Reset),
                      ChartLayout.TimeLabel(found.Time, span, use24Hour, culture: dates))
                : resting;
            chart.InvalidateVisual();
        }
        Join(chart, Follow);
        ViewCache.SetChildren(panel, [heading, chart]);
        return panel;
    }

    /// <summary>Makes a chart report the pointer and follow it — its own and every other chart's.</summary>
    private void Join(MiniChart chart, Action follow)
    {
        followers.Add(follow);
        chart.Pointed = moment =>
        {
            if (pointer == moment) return;
            pointer = moment;
            foreach (var each in followers) each();
        };
        follow();
    }
}
