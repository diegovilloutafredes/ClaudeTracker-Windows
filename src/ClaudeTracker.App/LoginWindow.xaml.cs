using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using ClaudeTracker.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ClaudeTracker.App;

/// <summary>
/// The sign-in window: claude.ai's own login page in a visible web view on the account's
/// browser profile. The user signs in as they would in a browser; the window watches the
/// profile's cookies and reports the moment a new session exists.
///
/// One window at a time. Opening it again abandons the attempt in progress — that attempt's
/// cancel callback runs first, then a fresh window is bound to the new account.
/// </summary>
public partial class LoginWindow : Window
{
    private static LoginWindow? current;

    private readonly ClaudeApiClient client;
    private readonly Action onSessionFound;
    private readonly Action? onCancel;
    private readonly CancellationTokenSource watching = new();
    private bool sessionFound;

    private LoginWindow(ClaudeApiClient client, Action onSessionFound, Action? onCancel)
    {
        this.client = client;
        this.onSessionFound = onSessionFound;
        this.onCancel = onCancel;
        InitializeComponent();
        Title = L.T("Sign in to Claude");
        ShowBanner(L.T("Sign in to your Claude account. The session will be captured automatically."), success: false);
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => OnClosed();
    }

    /// <param name="onSessionFound">Called once a new session cookie exists. The window closes itself 1.5 s later.</param>
    /// <param name="onCancel">Called if the window closes before any session was captured.</param>
    internal static void Open(ClaudeApiClient client, Action onSessionFound, Action? onCancel)
    {
        current?.Close();
        current = new LoginWindow(client, onSessionFound, onCancel);
        current.Show();
        current.Activate();
    }

    internal static void CloseCurrent() => current?.Close();

    private async Task StartAsync()
    {
        client.IsLoginInProgress = true;
        try
        {
            var environment = await WebViewHost.EnvironmentAsync();
            await WebView.EnsureCoreWebView2Async(environment, await WebViewHost.OptionsForAsync(client.Account));
            var core = WebView.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.NewWindowRequested += OnNewWindowRequested;

            // Only a sessionKey that differs from the one present now counts: signing an
            // expired account back in reuses its profile, which may still hold the rejected cookie.
            var baseline = await WebViewHost.SessionKeyAsync(core);
            core.Navigate(ClaudeApiClient.Origin + "/login");

            while (!watching.IsCancellationRequested)
            {
                await Task.Delay(1000, watching.Token);
                var session = await WebViewHost.SessionKeyAsync(core);
                // The window may have closed during the read. Its rollback has run by then, and
                // reporting a session now would land on whichever account is active instead.
                if (watching.IsCancellationRequested) return;
                if (session is not null && session != baseline)
                {
                    await SessionFoundAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The window closed.
        }
        catch (WebView2RuntimeNotFoundException e)
        {
            // Possible on Windows 10. Without this the window would stay blank, with no word why.
            AppLogger.Shared.Error($"sign-in window failed: {e.Message}");
            WebViewHost.ForgetEnvironment();
            ShowBanner(WebViewHost.MissingRuntimeMessage, success: false);
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException)
        {
            AppLogger.Shared.Error($"sign-in window failed: {e.Message}");
            WebViewHost.ForgetEnvironment();
            ShowBanner(e.Message, success: false);
        }
    }

    private async Task SessionFoundAsync()
    {
        sessionFound = true;
        client.IsLoginInProgress = false;
        ShowBanner(L.T("Signed in! Closing…"), success: true);
        onSessionFound();
        // Long enough to read the banner.
        try { await Task.Delay(1500, watching.Token); }
        catch (OperationCanceledException) { return; }
        Close();
    }

    /// <summary>
    /// Sign-in providers open a popup ("Continue with Google"). It must be a real web view on
    /// the same profile, handed back to the page, so the provider can talk to its opener.
    /// </summary>
    private async void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            var popup = new Window
            {
                Title = L.T("Sign in"),
                Width = 500,
                Height = 650,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            var view = new WebView2();
            popup.Content = view;
            popup.Closed += (_, _) => view.Dispose();
            popup.Show();
            await view.EnsureCoreWebView2Async(await WebViewHost.EnvironmentAsync(), await WebViewHost.OptionsForAsync(client.Account));
            view.CoreWebView2.WindowCloseRequested += (_, _) => popup.Close();
            e.NewWindow = view.CoreWebView2;
            e.Handled = true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            AppLogger.Shared.Error($"sign-in popup failed: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnClosed()
    {
        watching.Cancel();
        client.IsLoginInProgress = false;
        if (ReferenceEquals(current, this)) current = null;
        // Release the profile before the rollback below asks WebView2 to delete it.
        WebView.Dispose();
        if (!sessionFound) onCancel?.Invoke();
    }

    private void ShowBanner(string text, bool success)
    {
        BannerText.Text = text;
        BannerText.FontWeight = success ? FontWeights.SemiBold : FontWeights.Normal;
        var tint = success ? Color.FromRgb(0x2E, 0xA0, 0x43) : Color.FromRgb(0x1F, 0x6F, 0xEB);
        Banner.Background = new SolidColorBrush(Color.FromArgb(0x26, tint.R, tint.G, tint.B));
    }
}
