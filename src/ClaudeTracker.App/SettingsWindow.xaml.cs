using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using ClaudeTracker.Core;
using WinForms = System.Windows.Forms;

namespace ClaudeTracker.App;

/// <summary>
/// The Settings window: the accounts, and how the popover and the tray icon show their usage.
/// Every control applies at once; there is no Save.
///
/// Unlike the popover it is not rebuilt from scratch on every change: a slider being dragged, or
/// a switch holding the keyboard focus, would be replaced under the user's hand every time a
/// poll lands. The Display controls are made once and only their values are refreshed, and the
/// account list is rebuilt only when what it shows has changed.
/// </summary>
public partial class SettingsWindow : Window
{
    private static SettingsWindow? current;

    private readonly UsageViewModel viewModel;
    /// <summary>
    /// True while the code is setting the controls' values, so their change handlers do not
    /// write the same values straight back.
    /// </summary>
    private bool refreshing;
    private bool isDark;
    /// <summary>What the account list was last built from; it is rebuilt only when this changes.</summary>
    private string shownAccounts = "";

    private SettingsWindow(UsageViewModel viewModel)
    {
        this.viewModel = viewModel;
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height;
        // The window is as tall as what it shows, and grows downwards when a section opens.
        SizeChanged += (_, _) => KeepOnScreen();

        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";
        Title = L.F("Settings · v%@", version);
        AccountHeader.Text = L.T("Account");
        // A screen reader moves from section to section by its headings.
        foreach (var header in new[] { AccountHeader, DisplayHeader, ResetHeader, PaceHeader })
        {
            AutomationProperties.SetHeadingLevel(header, AutomationHeadingLevel.Level1);
        }
        AddAccountButton.Content = L.T("Add account");
        LaunchAtLoginSwitch.Content = L.T("Launch when I sign in to Windows");
        AutoUpdateSwitch.Content = L.T("Auto-install updates");
        OpenLogsButton.Content = L.T("Open Logs");
        OpenLogsCaption.Text = L.T("Error and API logs for debugging");
        Disclaimer.Text = L.T("Unofficial tool — not affiliated with or endorsed by Anthropic. May break if Anthropic changes their web API.");
        DisplayHeader.Text = L.T("Display");
        TrayShowsLabel.Text = L.T("Tray icon shows");
        PopupSizeLabel.Text = L.T("Popup size");
        ShowChartsSwitch.Content = L.T("Show charts tab");
        ShowModelsSwitch.Content = L.T("Show per-model usage");
        ShowPaceSwitch.Content = L.T("Show pace in usage tab");
        ShowTrayPaceSwitch.Content = L.T("Show pace in tray tooltip");
        RateUnitLabel.Text = L.T("Rate unit");
        TimeFormatLabel.Text = L.T("Time format");

        ResetHeader.Text = L.T("Window Resets");
        Notify5HourSwitch.Content = L.T("5-Hour window resets");
        Notify7DaySwitch.Content = L.T("7-Day window resets");
        ResetToastSwitch.Content = L.T("Toast near the system tray");
        ResetDurationLabel.Text = L.T("Duration");
        ResetPermanentSwitch.Content = L.T("Stay until dismissed");
        ResetSoundSwitch.Content = L.T("Sound");
        ResetTestButton.Content = L.T("Test");
        ResetTestCaption.Text = L.T("Simulates a window reset through all enabled channels");
        PaceHeader.Text = L.T("Pace Alerts");
        NotifyPaceSwitch.Content = L.T("Notify when approaching limit");
        WarningLabel.Text = L.T("Warn with less than");
        PaceToastSwitch.Content = L.T("Toast near the system tray");
        PaceDurationLabel.Text = L.T("Duration");
        PacePermanentSwitch.Content = L.T("Stay until dismissed");
        PaceSoundSwitch.Content = L.T("Sound");
        PaceTestButton.Content = L.T("Test");
        PaceTestCaption.Text = L.T("Simulates a pace alert through all enabled channels");
        PaceCaption.Text = L.T("Fires when a watched window is projected to fill before it resets, based on your current consumption rate.");
        foreach (var slider in new[] { ResetDurationSlider, PaceDurationSlider })
        {
            slider.Minimum = AlertSettings.ToastSecondsMinimum;
            slider.Maximum = AlertSettings.ToastSecondsMaximum;
            slider.TickFrequency = 1;
            slider.SmallChange = 1;
            slider.LargeChange = 5;
            AutomationProperties.SetName(slider, L.T("Duration"));
        }
        WarningSlider.Minimum = AlertSettings.WarningMinutesMinimum;
        WarningSlider.Maximum = AlertSettings.WarningMinutesMaximum;
        WarningSlider.TickFrequency = AlertSettings.WarningMinutesStep;
        WarningSlider.SmallChange = AlertSettings.WarningMinutesStep;
        WarningSlider.LargeChange = AlertSettings.WarningMinutesStep * 2;
        AutomationProperties.SetName(WarningSlider, WarningLabel.Text);

        foreach (var display in MenuBarDisplays.All) TrayShowsPicker.Items.Add(display.Label());
        foreach (var unit in PaceRateUnits.All) RateUnitPicker.Items.Add(unit.Label());
        TimeFormatPicker.Items.Add(L.T("AM/PM"));
        TimeFormatPicker.Items.Add(L.T("24-hour"));
        PopupSizeSlider.Minimum = PopupScale.Minimum;
        PopupSizeSlider.Maximum = PopupScale.Maximum;
        PopupSizeSlider.TickFrequency = PopupScale.Step;
        PopupSizeSlider.SmallChange = PopupScale.Step;
        PopupSizeSlider.LargeChange = PopupScale.Step * 2;
        // The pickers and the slider say what they are in their sibling text, which a screen
        // reader does not connect to them by itself.
        AutomationProperties.SetName(TrayShowsPicker, TrayShowsLabel.Text);
        AutomationProperties.SetName(PopupSizeSlider, PopupSizeLabel.Text);
        AutomationProperties.SetName(RateUnitPicker, RateUnitLabel.Text);
        AutomationProperties.SetName(TimeFormatPicker, TimeFormatLabel.Text);

        AddAccountButton.Click += (_, _) => viewModel.OpenLoginForNewAccount();
        CheckUpdatesButton.Click += (_, _) => viewModel.Updater.CheckForUpdates();
        UpdateAction.Click += (_, _) => viewModel.Updater.Act();
        BindSwitch(LaunchAtLoginSwitch, on => viewModel.LaunchAtLogin = on);
        BindSwitch(AutoUpdateSwitch, on => viewModel.Updater.AutoUpdate = on);
        OpenLogsButton.Click += (_, _) => OpenLogs();
        TrayShowsPicker.SelectionChanged += (_, _) =>
        {
            if (!refreshing && TrayShowsPicker.SelectedIndex >= 0) viewModel.MenuBarDisplay = MenuBarDisplays.All[TrayShowsPicker.SelectedIndex];
        };
        PopupSizeSlider.ValueChanged += (_, _) =>
        {
            if (!refreshing) viewModel.PopupScale = PopupSizeSlider.Value;
        };
        BindSwitch(ShowChartsSwitch, on => viewModel.ShowChartsTab = on);
        BindSwitch(ShowModelsSwitch, on => viewModel.ShowModelWindows = on);
        BindSwitch(ShowPaceSwitch, on => viewModel.ShowPace = on);
        BindSwitch(ShowTrayPaceSwitch, on => viewModel.ShowPaceMenuBar = on);
        RateUnitPicker.SelectionChanged += (_, _) =>
        {
            if (!refreshing && RateUnitPicker.SelectedIndex >= 0) viewModel.PaceRateUnit = PaceRateUnits.All[RateUnitPicker.SelectedIndex];
        };
        TimeFormatPicker.SelectionChanged += (_, _) =>
        {
            if (!refreshing && TimeFormatPicker.SelectedIndex >= 0) viewModel.Use24HourTime = TimeFormatPicker.SelectedIndex == 1;
        };

        BindSwitch(Notify5HourSwitch, on => viewModel.Notify5Hour = on);
        BindSwitch(Notify7DaySwitch, on => viewModel.Notify7Day = on);
        BindSwitch(ResetToastSwitch, on => viewModel.NotifyToast = on);
        BindSwitch(ResetPermanentSwitch, on => viewModel.ToastPermanent = on);
        BindSwitch(ResetSoundSwitch, on => viewModel.ResetSoundEnabled = on);
        BindSwitch(NotifyPaceSwitch, on => viewModel.NotifyPace = on);
        BindSwitch(PaceToastSwitch, on => viewModel.PaceToastEnabled = on);
        BindSwitch(PacePermanentSwitch, on => viewModel.PaceToastPermanent = on);
        BindSwitch(PaceSoundSwitch, on => viewModel.PaceSoundEnabled = on);
        ResetDurationSlider.ValueChanged += (_, _) =>
        {
            if (!refreshing) viewModel.ToastDuration = ResetDurationSlider.Value;
        };
        PaceDurationSlider.ValueChanged += (_, _) =>
        {
            if (!refreshing) viewModel.PaceToastDuration = PaceDurationSlider.Value;
        };
        WarningSlider.ValueChanged += (_, _) =>
        {
            if (!refreshing) viewModel.PaceWarningMinutes = WarningSlider.Value;
        };
        ResetTestButton.Click += (_, _) => viewModel.SendTestNotification();
        PaceTestButton.Click += (_, _) => viewModel.SendTestPaceNotification();

        viewModel.Changed += Refresh;
        // Light or dark may have changed while the window was in the background.
        Activated += (_, _) => Refresh();
        Closed += (_, _) =>
        {
            viewModel.Changed -= Refresh;
            if (ReferenceEquals(current, this)) current = null;
        };
        Refresh();
    }

    /// <summary>
    /// Moves the window up when it has grown past the bottom of the screen. It opens in the
    /// middle of the screen at the height of what it shows; switching pace alerts on adds
    /// six rows underneath, and a window grows from its top edge: the last of them, with
    /// their Test button, landed below the taskbar.
    /// </summary>
    private void KeepOnScreen()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || WindowState != WindowState.Normal) return;
        var area = WinForms.Screen.FromHandle(handle).WorkingArea;
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        var (top, bottom) = (area.Top / scale, area.Bottom / scale);
        if (Top + ActualHeight > bottom) Top = Math.Max(top, bottom - ActualHeight);
    }

    /// <summary>
    /// Applies a switch when its state changes. Not on Click: a screen reader toggles a
    /// switch without clicking it, and the setting would stay as it was.
    /// </summary>
    private void BindSwitch(CheckBox toggle, Action<bool> apply)
    {
        RoutedEventHandler changed = (_, _) =>
        {
            if (!refreshing) apply(toggle.IsChecked == true);
        };
        toggle.Checked += changed;
        toggle.Unchecked += changed;
    }

    /// <summary>Opens the window, or brings the one already open to the front.</summary>
    internal static void Open(UsageViewModel viewModel)
    {
        // Windows may have been told something about the sign-in entry since the app last looked.
        viewModel.SyncLaunchAtLogin();
        if (current is null)
        {
            current = new SettingsWindow(viewModel);
            current.Show();
        }
        if (current.WindowState == WindowState.Minimized) current.WindowState = WindowState.Normal;
        current.Activate();
        Native.SetForegroundWindow(new WindowInteropHelper(current).Handle);
    }

    internal static void CloseCurrent() => current?.Close();

    // MARK: - Refreshing

    private Brush Primary => Gray(isDark ? (byte)0xFF : (byte)0x1B);

    private Brush Secondary => Gray(isDark ? (byte)0xC5 : (byte)0x5C);

    private static Brush Gray(byte value) => new SolidColorBrush(Color.FromRgb(value, value, value));

    private static readonly Brush Orange = new SolidColorBrush(Color.FromRgb(0xD9, 0x82, 0x1E));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xD1, 0x3B, 0x3B));
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x2E, 0xA0, 0x43));

    private void Refresh()
    {
        refreshing = true;
        try
        {
            isDark = SystemTheme.AppsAreDark;
            Background = Gray(isDark ? (byte)0x20 : (byte)0xF3);
            foreach (var text in new[] { AccountHeader, DisplayHeader, TrayShowsLabel, PopupSizeLabel, RateUnitLabel, TimeFormatLabel,
                                         ResetHeader, ResetDurationLabel, PaceHeader, WarningLabel, PaceDurationLabel })
            {
                text.Foreground = Primary;
            }
            foreach (var text in new[] { OpenLogsCaption, Disclaimer, PopupSizeValue, ResetDurationValue, ResetTestCaption,
                                         WarningValue, PaceDurationValue, PaceTestCaption, PaceCaption, AutoUpdateCaption })
            {
                text.Foreground = Secondary;
            }
            foreach (var toggle in new[] { LaunchAtLoginSwitch, AutoUpdateSwitch,
                                           ShowChartsSwitch, ShowModelsSwitch, ShowPaceSwitch, ShowTrayPaceSwitch, Notify5HourSwitch, Notify7DaySwitch,
                                           ResetToastSwitch, ResetPermanentSwitch, ResetSoundSwitch, NotifyPaceSwitch,
                                           PaceToastSwitch, PacePermanentSwitch, PaceSoundSwitch })
            {
                toggle.Foreground = Primary;
            }

            RefreshAccounts();
            // One line under the list: what the app just did on its own, else what is wrong.
            ErrorText.Text = viewModel.Notice ?? viewModel.Error ?? "";
            ErrorText.Foreground = viewModel.Notice is null ? Orange : Secondary;
            ErrorText.Visibility = ErrorText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            AddAccountButton.Visibility = viewModel.Accounts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshUpdates();

            // How the numbers are shown only matters once there are numbers.
            DisplaySection.Visibility = viewModel.IsAuthenticated ? Visibility.Visible : Visibility.Collapsed;
            TrayShowsPicker.SelectedIndex = IndexOf(MenuBarDisplays.All, viewModel.MenuBarDisplay);
            var scale = viewModel.PopupScale;
            PopupSizeSlider.Value = scale;
            PopupSizeValue.Text = ((int)Math.Round(scale * 100)).ToString(CultureInfo.InvariantCulture) + "%";
            AutomationProperties.SetHelpText(PopupSizeSlider, PopupSizeValue.Text);
            ShowChartsSwitch.IsChecked = viewModel.ShowChartsTab;
            ShowModelsSwitch.IsChecked = viewModel.ShowModelWindows;
            ShowPaceSwitch.IsChecked = viewModel.ShowPace;
            ShowTrayPaceSwitch.IsChecked = viewModel.ShowPaceMenuBar;
            // The unit applies to both places a pace is shown; with neither on it has no effect.
            RateUnitRow.Visibility = viewModel.ShowPace || viewModel.ShowPaceMenuBar || viewModel.NotifyPace ? Visibility.Visible : Visibility.Collapsed;
            RateUnitPicker.SelectedIndex = IndexOf(PaceRateUnits.All, viewModel.PaceRateUnit);
            TimeFormatPicker.SelectedIndex = viewModel.Use24HourTime ? 1 : 0;
            RefreshAlerts();
        }
        finally
        {
            refreshing = false;
        }
    }

    /// <summary>The update row and the two switches only an installed copy has a use for.</summary>
    private void RefreshUpdates()
    {
        var updater = viewModel.Updater;
        InstalledOptions.Visibility = viewModel.CanLaunchAtLogin ? Visibility.Visible : Visibility.Collapsed;
        LaunchAtLoginSwitch.IsChecked = viewModel.LaunchAtLogin;
        AutoUpdateSwitch.IsChecked = updater.AutoUpdate;
        AutoUpdateCaption.Text = updater.CheckIntervalLabel;
        AutoUpdateCaption.Visibility = updater.AutoUpdate ? Visibility.Visible : Visibility.Collapsed;

        if (updater.AvailableUpdate is not { } update)
        {
            UpdateFound.Visibility = Visibility.Collapsed;
            CheckUpdatesButton.Visibility = Visibility.Visible;
            CheckUpdatesButton.Content = updater.IsChecking ? L.T("Checking…") : L.T("Check for Updates");
            CheckUpdatesButton.IsEnabled = !updater.IsChecking;
            return;
        }
        CheckUpdatesButton.Visibility = Visibility.Collapsed;
        UpdateFound.Visibility = Visibility.Visible;
        var failed = updater.Phase == UpdatePhase.Failed;
        UpdateSymbol.Text = failed ? "\uE7BA" : "\uE896"; // a warning triangle, an arrow into a tray
        UpdateSymbol.Foreground = failed ? Orange : Green;
        UpdateSymbol.Visibility = updater.ProgressLabel is null ? Visibility.Visible : Visibility.Collapsed;
        UpdateText.Text = updater.ProgressLabel ?? (failed ? updater.FailureMessage : null) ?? L.F("v%@ available", update.Version);
        UpdateText.Foreground = updater.Phase == UpdatePhase.Idle ? Primary : Secondary;
        UpdateText.FontSize = failed ? 12 : 13;
        UpdateAction.Content = updater.ActionLabel;
        UpdateAction.Visibility = updater.ActionLabel is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshAlerts()
    {
        var signedIn = viewModel.IsAuthenticated ? Visibility.Visible : Visibility.Collapsed;
        ResetSection.Visibility = signedIn;
        PaceSection.Visibility = signedIn;

        Notify5HourSwitch.IsChecked = viewModel.Notify5Hour;
        Notify7DaySwitch.IsChecked = viewModel.Notify7Day;
        ResetToastSwitch.IsChecked = viewModel.NotifyToast;
        ResetToastOptions.Visibility = viewModel.NotifyToast ? Visibility.Visible : Visibility.Collapsed;
        ShowDuration(ResetDurationSlider, ResetDurationValue, ResetDurationLabel, viewModel.ToastDuration, viewModel.ToastPermanent);
        ResetPermanentSwitch.IsChecked = viewModel.ToastPermanent;
        ResetSoundSwitch.IsChecked = viewModel.ResetSoundEnabled;
        // With every channel off the test would do nothing, and look broken.
        ResetTestButton.IsEnabled = viewModel.NotifyToast || viewModel.ResetSoundEnabled;

        NotifyPaceSwitch.IsChecked = viewModel.NotifyPace;
        PaceOptions.Visibility = viewModel.NotifyPace ? Visibility.Visible : Visibility.Collapsed;
        WarningSlider.Value = viewModel.PaceWarningMinutes;
        WarningValue.Text = L.F("%lldm", (int)viewModel.PaceWarningMinutes);
        AutomationProperties.SetHelpText(WarningSlider, AlertSettings.MinutesInWords((int)viewModel.PaceWarningMinutes));
        PaceToastSwitch.IsChecked = viewModel.PaceToastEnabled;
        PaceToastOptions.Visibility = viewModel.PaceToastEnabled ? Visibility.Visible : Visibility.Collapsed;
        ShowDuration(PaceDurationSlider, PaceDurationValue, PaceDurationLabel, viewModel.PaceToastDuration, viewModel.PaceToastPermanent);
        PacePermanentSwitch.IsChecked = viewModel.PaceToastPermanent;
        PaceSoundSwitch.IsChecked = viewModel.PaceSoundEnabled;
        PaceTestButton.IsEnabled = viewModel.PaceToastEnabled || viewModel.PaceSoundEnabled;
    }

    /// <summary>A toast's duration: the seconds, or "∞" and a slider that is off while it stays until dismissed.</summary>
    private void ShowDuration(Slider slider, TextBlock value, TextBlock label, double seconds, bool permanent)
    {
        slider.Value = seconds;
        slider.IsEnabled = !permanent;
        value.Text = permanent ? L.T("∞") : L.F("%llds", (int)seconds);
        label.Opacity = permanent ? 0.5 : 1;
        AutomationProperties.SetHelpText(slider, permanent ? L.T("Stay until dismissed") : AlertSettings.SecondsInWords((int)seconds));
    }

    private static int IndexOf<T>(IReadOnlyList<T> all, T value)
    {
        for (var i = 0; i < all.Count; i++)
        {
            if (EqualityComparer<T>.Default.Equals(all[i], value)) return i;
        }
        return -1;
    }

    // MARK: - Accounts

    /// <summary>
    /// Rebuilds the account rows, but only when what they show has changed: a rebuild takes the
    /// keyboard focus and any open tooltip away from the row's buttons, and a poll lands every
    /// few seconds.
    /// </summary>
    private void RefreshAccounts()
    {
        var needsSignIn = viewModel.SessionNeedsSignIn;
        var shown = string.Join("\n", viewModel.Accounts.Select(a =>
            $"{a.Id}|{a.Label}|{a.Email}|{a.SubscriptionLabel}|{a.OrgName}|{a.Id == viewModel.ActiveAccountId}"))
            + $"\n{needsSignIn}|{isDark}";
        if (shown == shownAccounts) return;
        shownAccounts = shown;

        AccountList.Children.Clear();
        if (viewModel.Accounts.Count == 0)
        {
            AccountList.Children.Add(EmptyAccountRow());
            return;
        }
        foreach (var account in viewModel.Accounts)
        {
            var isActive = account.Id == viewModel.ActiveAccountId;
            AccountList.Children.Add(AccountRow(account, isActive, isActive && needsSignIn));
        }
    }

    private UIElement EmptyAccountRow()
    {
        var signIn = new Button { Content = L.T("Sign in"), Padding = new Thickness(14, 5, 14, 5), FontSize = 13 };
        signIn.Click += (_, _) => viewModel.OpenLoginForNewAccount();
        var said = new TextBlock { Text = L.T("Not signed in"), FontSize = 14, Foreground = Primary, VerticalAlignment = VerticalAlignment.Center };
        return Row(Dot(Red), said, [signIn], margin: 4);
    }

    private UIElement AccountRow(Account account, bool isActive, bool needsSignIn)
    {
        var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock { Text = account.Label, FontSize = 14, Foreground = Primary, TextTrimming = TextTrimming.CharacterEllipsis });
        if (account.SubscriptionLabel is { } plan) title.Children.Add(Badge(plan));
        lines.Children.Add(title);
        if (account.Email is { Length: > 0 } email) lines.Children.Add(Caption(email));
        // Only an organisation's plan has an organisation worth naming; a personal one repeats the person.
        if (account.OrgName is { Length: > 0 } organisation && account.IsOrganizationPlan) lines.Children.Add(Caption(organisation));

        // Left to right: what applies to this row, then rename, then remove at the edge.
        var buttons = new List<Button>();
        if (needsSignIn)
        {
            var signIn = new Button { Content = L.T("Sign in again"), Padding = new Thickness(12, 4, 12, 4), FontSize = 13, Margin = new Thickness(8, 0, 0, 0) };
            signIn.Click += (_, _) => viewModel.SignInAgain();
            buttons.Add(signIn);
        }
        if (!isActive)
        {
            var switchTo = new Button { Content = L.T("Switch"), Padding = new Thickness(12, 4, 12, 4), FontSize = 13, Margin = new Thickness(8, 0, 0, 0) };
            switchTo.Click += (_, _) => viewModel.SwitchAccount(account.Id);
            buttons.Add(switchTo);
        }
        var rename = IconButton("\uE70F", L.T("Rename account"), L.T("Rename account"), Secondary);
        rename.Click += (_, _) => AskForName(account);
        buttons.Add(rename);
        var remove = IconButton("\uE74D", L.T("Sign out & remove"), L.T("Sign out & remove account"), Red);
        remove.Click += (_, _) => ConfirmRemoval(account);
        buttons.Add(remove);

        return Row(Dot(isActive ? Green : Gray(isDark ? (byte)0x6A : (byte)0xB4)), lines, buttons, margin: 6);
    }

    /// <summary>
    /// One line of the account list: a dot, what it is, its buttons. Put into the row in that
    /// order because that is the order a screen reader reads them in and the Tab key visits
    /// them in — docked from the right edge inwards, "Sign out &amp; remove account" came
    /// before the name of the account it would remove.
    /// </summary>
    private static Grid Row(UIElement dot, FrameworkElement said, IReadOnlyList<Button> buttons, double margin)
    {
        var row = new Grid { Margin = new Thickness(0, margin, 0, margin) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var button in buttons) actions.Children.Add(button);
        Grid.SetColumn(said, 1);
        Grid.SetColumn(actions, 2);
        row.Children.Add(dot);
        row.Children.Add(said);
        row.Children.Add(actions);
        return row;
    }

    private static Ellipse Dot(Brush fill) => new()
    {
        Width = 8,
        Height = 8,
        Fill = fill,
        Margin = new Thickness(2, 0, 12, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private TextBlock Caption(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = Secondary,
        Margin = new Thickness(0, 2, 0, 0),
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    /// <summary>The plan badge, as in the popover's header. The label is translated here, where it is shown.</summary>
    private Border Badge(string plan) => new()
    {
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(7, 2, 7, 2),
        Margin = new Thickness(8, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(Color.FromArgb(0x33, 0x9B, 0x59, 0xD0)),
        Child = new TextBlock
        {
            Text = L.T(plan),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(isDark ? Color.FromRgb(0xC9, 0xA7, 0xF5) : Color.FromRgb(0x7A, 0x3E, 0xC8)),
        },
    };

    /// <summary>A button that is only a symbol. Its tooltip and its screen-reader name say what it does.</summary>
    private static Button IconButton(string glyph, string tooltip, string name, Brush brush)
    {
        var button = new Button
        {
            Content = Symbols.Text(glyph, 14),
            Foreground = brush,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = tooltip,
        };
        AutomationProperties.SetName(button, name);
        return button;
    }

    /// <summary>
    /// True while one of this window's questions is open. A question blocks the mouse and the
    /// keyboard from the window behind it, but not a program driving the buttons directly (a
    /// screen reader, a test): without this, each press would stack another question on top.
    /// </summary>
    private bool asking;

    private void AskForName(Account account)
    {
        if (asking) return;
        asking = true;
        try
        {
            // The view model does the trimming and ignores an empty name.
            if (Dialogs.AskText(this, L.T("Rename account"), L.T("Name"), account.Label, L.T("Save"), L.T("Cancel")) is { } name)
            {
                viewModel.RenameAccount(account.Id, name);
            }
        }
        finally
        {
            asking = false;
        }
    }

    private void ConfirmRemoval(Account account)
    {
        if (asking) return;
        asking = true;
        try
        {
            var message = L.F("Removes %@ from this app and signs it out. The account itself is unaffected.", account.Label);
            if (Dialogs.Confirm(this, L.T("Remove this account?"), message, L.T("Remove"), L.T("Cancel")))
            {
                viewModel.RemoveAccount(account.Id);
            }
        }
        finally
        {
            asking = false;
        }
    }

    private static void OpenLogs()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Logs);
            Process.Start(new ProcessStartInfo { FileName = AppPaths.Logs, UseShellExecute = true });
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AppLogger.Shared.Error($"could not open the log folder: {e.Message}");
        }
    }
}

/// <summary>The two small questions the Settings window asks: yes or no, and a line of text.</summary>
internal static class Dialogs
{
    /// <summary>True when the user chose <paramref name="confirm"/>.</summary>
    public static bool Confirm(Window owner, string title, string message, string confirm, string cancel)
    {
        var dialog = Make(owner, title);
        var panel = (StackPanel)dialog.Content;
        panel.Children.Add(new TextBlock { Text = message, FontSize = 14, TextWrapping = TextWrapping.Wrap, MaxWidth = 380, Foreground = dialog.Foreground });
        panel.Children.Add(Buttons(dialog, confirm, cancel, defaultIsConfirm: false));
        return dialog.ShowDialog() == true;
    }

    /// <summary>The text the user entered, or null when they cancelled.</summary>
    public static string? AskText(Window owner, string title, string fieldName, string initial, string save, string cancel)
    {
        var dialog = Make(owner, title);
        var panel = (StackPanel)dialog.Content;
        var field = new TextBox { Text = initial, FontSize = 14, MinWidth = 320, Padding = new Thickness(6, 5, 6, 5) };
        AutomationProperties.SetName(field, fieldName);
        panel.Children.Add(field);
        panel.Children.Add(Buttons(dialog, save, cancel, defaultIsConfirm: true));
        dialog.Loaded += (_, _) =>
        {
            field.Focus();
            field.SelectAll();
        };
        return dialog.ShowDialog() == true ? field.Text : null;
    }

    private static Window Make(Window owner, string title)
    {
        var isDark = SystemTheme.AppsAreDark;
        return new Window
        {
            Title = title,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            UseLayoutRounding = true,
            Background = new SolidColorBrush(isDark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3)),
            Foreground = new SolidColorBrush(isDark ? Colors.White : Color.FromRgb(0x1B, 0x1B, 0x1B)),
            Content = new StackPanel { Margin = new Thickness(22, 20, 22, 18) },
        };
    }

    /// <summary>
    /// The two buttons, confirm first. Enter presses the default one and Escape always cancels;
    /// a destructive confirmation is never the default.
    /// </summary>
    private static StackPanel Buttons(Window dialog, string confirm, string cancel, bool defaultIsConfirm)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var yes = new Button { Content = confirm, Padding = new Thickness(16, 6, 16, 6), FontSize = 13, MinWidth = 84, IsDefault = defaultIsConfirm };
        var no = new Button { Content = cancel, Padding = new Thickness(16, 6, 16, 6), FontSize = 13, MinWidth = 84, Margin = new Thickness(8, 0, 0, 0), IsCancel = true, IsDefault = !defaultIsConfirm };
        yes.Click += (_, _) => dialog.DialogResult = true;
        row.Children.Add(yes);
        row.Children.Add(no);
        return row;
    }
}
