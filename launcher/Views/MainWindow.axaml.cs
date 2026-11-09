using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace Playtest64.Launcher.Views;

public partial class MainWindow : Window
{
    private readonly LauncherContext _context;
    private IReadOnlyList<BuildInfo> _builds = [];
    private LauncherRelease? _launcherUpdate;
    private InstallPlan? _installPlan;
    private bool _busy;
    private bool _settingBoxes;				// the boxes are being set from code, which is not the person changing them

    // The designer and the XAML loader want a constructor with no arguments
    public MainWindow() : this(new LauncherContext())
    {
    }

    public MainWindow(LauncherContext context)
    {
        _context = context;
        InitializeComponent();

        EmailBox.Text = context.Settings.Email;
        SetBoxes();
        ShowSeason();

        Opened += async (_, _) =>
        {
            if (context.Settings.SignedIn)
                await RefreshAsync();
            else
                EmailBox.Focus();
        };
    }

    private void ShowPage(bool builds)
    {
        SignInPanel.IsVisible = !builds;
        BuildsPanel.IsVisible = builds;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        IsEnabled = !busy;
    }

    private void SetBoxes()
    {
        _settingBoxes = true;
        AutostartBox.IsChecked = Autostart.IsEnabled();
        SeasonBox.IsChecked = _context.Settings.SeasonalLook;
        GraphicsBox.ItemsSource = Graphics.Labels;
        GraphicsBox.SelectedIndex = Math.Max(0, Array.IndexOf(Graphics.All, _context.Settings.GraphicsBackend));
        _settingBoxes = false;
    }

    // Which look is on, in words under the name
    private void ShowSeason()
    {
        var palette = Themes.Current(_context.Settings.SeasonalLook);

        SeasonText.Text = palette == Themes.Ordinary ? "Launcher für den Spieltest" : palette.Name + "-Look";
        UserText.Text = _context.Settings.SignedIn ? _context.Settings.Email : "";
    }

    private BuildInfo? Picked => BuildBox.SelectedIndex >= 0 && BuildBox.SelectedIndex < _builds.Count ? _builds[BuildBox.SelectedIndex] : null;

    // What the button says, and the notes, for the build picked
    private void UpdateSelection()
    {
        var build = Picked;

        ActionButton.IsEnabled = build != null;
        NotesText.Text = build?.Notes ?? "";
        NotesBox.IsVisible = !string.IsNullOrWhiteSpace(NotesText.Text);
        ActionButton.Content = build == null ? "Kein Build" : _context.Store.IsInstalled(build) ? "Spielen" : "Herunterladen und installieren";
    }

    private void FillBuilds(int select = 0)
    {
        BuildBox.ItemsSource = _builds.Select(b => b.Version + (_context.Store.IsInstalled(b) ? "  (installiert)" : "")).ToList();
        BuildBox.SelectedIndex = _builds.Count == 0 ? -1 : Math.Min(select, _builds.Count - 1);
        UpdateSelection();
    }

    private async Task RefreshAsync()
    {
        SetBusy(true);
        StatusText.Text = "Lade die Liste ...";

        try
        {
            _builds = await _context.Api.GetBuildsAsync();
            ShowPage(true);
            FillBuilds();
            StatusText.Text = _builds.Count == 0 ? $"Für {Platform.Name} gibt es noch keinen Build." : "";

            // everything in the list has now been seen, so the background check does not announce it again
            if (_builds.Count > 0)
            {
                _context.Settings.AnnouncedBuild = _builds[0].BuildId;
                _context.Settings.Save();
            }

            ShowSeason();
            _ = LoadNewsAsync();
            _ = CheckLauncherAsync();
            _ = CheckSystemAsync();
        }
        catch (SignedOutException ex)
        {
            ShowPage(false);
            StatusText.Text = ex.Message;
        }
        catch (LauncherException ex)
        {
            // the list from before stays usable when there is simply no network at the moment
            ShowPage(_builds.Count > 0 || _context.Settings.SignedIn);
            StatusText.Text = ex.Message;
        }
        catch (Exception ex)
        {
            ShowPage(_builds.Count > 0 || _context.Settings.SignedIn);
            StatusText.Text = "Unerwarteter Fehler: " + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    // Looks at the machine (libraries, Vulkan) away from the window's thread, and shows what is missing and what could be done
    private async Task CheckSystemAsync()
    {
        SystemReport report;

        try
        {
            report = await Task.Run(SystemCheck.Run);
        }
        catch (Exception)
        {
            return;
        }

        SystemPanel.IsVisible = report.HasProblem;
        _installPlan = null;
        InstallButton.IsVisible = false;
        CommandBox.IsVisible = false;

        if (!report.HasProblem)
            return;

        SystemText.Text = string.Join("\n", report.Describe());

        if (!OperatingSystem.IsLinux())
        {
            SystemText.Text += "\nAuf Windows hilft meist ein Update des Grafiktreibers (von AMD, Intel oder NVIDIA).";
            return;
        }

        string osRelease;

        try
        {
            osRelease = File.ReadAllText("/etc/os-release");
        }
        catch (IOException)
        {
            osRelease = "";
        }

        _installPlan = InstallPlan.For(osRelease, report);

        if (_installPlan == null)
        {
            // a graphics card that is simply too old has nothing to install
            if (report.MissingLibraries.Count > 0)
                SystemText.Text += "\nInstalliere die Bibliotheken über den Paketmanager deiner Distribution.";

            return;
        }

        CommandBox.Text = "sudo " + _installPlan.Command;
        CommandBox.IsVisible = true;

        if (InstallPlan.CanAskForPassword)
        {
            InstallButton.Content = report.MissingLibraries.Count > 0 ? "Fehlende Bibliotheken installieren" : "Grafiktreiber installieren";
            InstallButton.IsVisible = true;
        }
    }

    private async void InstallButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var plan = _installPlan;

        if (plan == null || _busy)
            return;

        SetBusy(true);
        StatusText.Text = $"Installiere über {plan.Manager} ... (das Passwort-Fenster deines Desktops fragt nach der Erlaubnis)";

        try
        {
            var (code, output) = await plan.RunAsync();

            StatusText.Text = code == 0
                ? "Fertig. Prüfe noch einmal ..."
                : code is 126 or 127
                    ? "Die Installation wurde nicht erlaubt oder abgebrochen."
                    : "Die Installation ist fehlgeschlagen (Code " + code + "): " + output.Trim().Split('\n').LastOrDefault();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Die Installation konnte nicht gestartet werden: " + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }

        await CheckSystemAsync();

        if (SystemPanel.IsVisible == false)
            StatusText.Text = "Alles installiert. Dein Rechner ist bereit.";
    }

    // Whether the server has a newer launcher: the button in the bar appears when it has. Never in the way, never an error.
    private async Task CheckLauncherAsync()
    {
        try
        {
            var release = await _context.Api.GetLauncherReleaseAsync();

            _launcherUpdate = release != null && SelfUpdate.IsNewer(release.Version) ? release : null;
        }
        catch (Exception)
        {
            return;
        }

        UpdateButton.IsVisible = _launcherUpdate != null && SelfUpdate.OwnPath != null;
        UpdateButton.Content = _launcherUpdate == null ? "" : $"Launcher-Update auf {_launcherUpdate.Version}";

        // seen in the window, so the background check does not say it again
        if (_launcherUpdate != null)
        {
            _context.Settings.AnnouncedLauncher = _launcherUpdate.Version;
            _context.Settings.Save();
        }
    }

    private async void UpdateButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var release = _launcherUpdate;
        var own = SelfUpdate.OwnPath;

        if (release == null || own == null || _busy)
            return;

        SetBusy(true);
        StatusText.Text = $"Lade den Launcher {release.Version} ...";
        DownloadBar.Value = 0;

        try
        {
            await SelfUpdate.ApplyAsync(_context.Api, release, own, new Progress<double>(p => DownloadBar.Value = p));
            StatusText.Text = "Der Launcher startet neu ...";
            SelfUpdate.Restart(own);
            Close();
            (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        }
        catch (LauncherException ex)
        {
            StatusText.Text = ex.Message;
            SetBusy(false);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Unerwarteter Fehler: " + ex.Message;
            SetBusy(false);
        }
    }

    // The news come after the list and never hold it up: if they fail the person can still play
    private async Task LoadNewsAsync()
    {
        var panel = NewsPanel;
        var status = NewsStatus;

        try
        {
            var feed = await _context.Api.GetNewsAsync();

            panel.Children.Clear();
            status.Text = feed.Items.Count == 0 ? "Noch keine Neuigkeiten." : "";

            foreach (var item in feed.Items)
            {
                var card = NewsCards.Build(item, PickBuildByVersion);
                panel.Children.Add(card.Control);
                _ = LoadPicturesAsync(item, card.Pictures, feed.MediaTicket);
            }
        }
        catch (SignedOutException)
        {
            ShowPage(false);
            StatusText.Text = "Die Anmeldung ist abgelaufen. Bitte melde dich neu an.";
        }
        catch (Exception ex)
        {
            status.Text = ex is LauncherException ? ex.Message : "Die Neuigkeiten konnten nicht angezeigt werden.";
        }
    }

    // A post's screenshots, as they come in: a picture that cannot be had is left out, not an error
    private async Task LoadPicturesAsync(NewsItem item, Panel target, string ticket)
    {
        foreach (var picture in item.Pictures.Take(4))
        {
            try
            {
                var bytes = await _context.Api.GetPictureAsync(picture.Id, ticket);
                using var stream = new MemoryStream(bytes);

                target.Children.Add(new Image
                {
                    Source = new Bitmap(stream),
                    Stretch = Avalonia.Media.Stretch.Uniform,
                    StretchDirection = Avalonia.Media.StretchDirection.DownOnly,
                    MaxHeight = 280,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                });
            }
            catch (Exception)
            {
                // left out
            }
        }
    }

    // "Build v.0.2.1 auswählen" on a post: the list jumps to it
    private void PickBuildByVersion(string version)
    {
        var index = _builds.ToList().FindIndex(b => b.Version == version);

        if (index >= 0)
            BuildBox.SelectedIndex = index;
        else
            StatusText.Text = $"Der Build {version} gibt es für {Platform.Name} nicht.";
    }

    private async void SignInButton_OnClick(object? sender, RoutedEventArgs e)
    {
        SetBusy(true);
        StatusText.Text = "Melde an ...";

        try
        {
            await _context.Api.LoginAsync(EmailBox.Text?.Trim() ?? "", PasswordBox.Text ?? "");
            PasswordBox.Text = "";			// the password is not kept anywhere
        }
        catch (Exception ex)
        {
            StatusText.Text = ex is LauncherException ? ex.Message : "Unerwarteter Fehler: " + ex.Message;
            SetBusy(false);
            return;
        }

        SetBusy(false);
        await RefreshAsync();
    }

    private async void ReloadButton_OnClick(object? sender, RoutedEventArgs e) => await RefreshAsync();

    private void BuildBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateSelection();

    private async void ActionButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var build = Picked;

        if (build == null || _busy)
            return;

        if (_context.Store.IsInstalled(build))
        {
            try
            {
                _context.Store.Play(build, _context.Settings.GraphicsBackend);
                Close();
            }
            catch (Exception ex)
            {
                StatusText.Text = ex is LauncherException ? ex.Message : "Unerwarteter Fehler: " + ex.Message;
            }

            return;
        }

        var index = BuildBox.SelectedIndex;

        SetBusy(true);
        StatusText.Text = "Lade herunter ...";
        DownloadBar.Value = 0;

        // Progress reports arrive on the thread they were made on; the bar belongs to this one
        var progress = new Progress<double>(value => Dispatcher.UIThread.Post(() => DownloadBar.Value = value));

        try
        {
            await _context.Store.InstallAsync(build, progress);
            DownloadBar.Value = 1;
            StatusText.Text = "Installiert.";
            FillBuilds(index);
        }
        catch (SignedOutException ex)
        {
            DownloadBar.Value = 0;
            ShowPage(false);
            StatusText.Text = ex.Message;
        }
        catch (Exception ex)
        {
            DownloadBar.Value = 0;
            StatusText.Text = ex is LauncherException ? ex.Message : "Unerwarteter Fehler: " + ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void AutostartBox_OnChanged(object? sender, RoutedEventArgs e)
    {
        if (_settingBoxes)
            return;

        var wanted = AutostartBox.IsChecked == true;

        try
        {
            Autostart.SetEnabled(wanted);
        }
        catch (Exception ex) when (ex is LauncherException or IOException or UnauthorizedAccessException)
        {
            SetBoxes();
            StatusText.Text = "Das ließ sich nicht umstellen.";
        }
    }

    private void GraphicsBox_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_settingBoxes || GraphicsBox.SelectedIndex < 0)
            return;

        _context.Settings.GraphicsBackend = Graphics.All[GraphicsBox.SelectedIndex];
        _context.Settings.Save();
    }

    private void SeasonBox_OnChanged(object? sender, RoutedEventArgs e)
    {
        if (_settingBoxes)
            return;

        _context.Settings.SeasonalLook = SeasonBox.IsChecked == true;
        _context.Settings.Save();
        ((App)Avalonia.Application.Current!).ApplyTheme();
        ShowSeason();
    }

    // The website's feedback page, in the person's own browser
    private async void FeedbackButton_OnClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await Launcher.LaunchUriAsync(new Uri(_context.Settings.Server + "/feedback"));
        }
        catch (Exception ex)
        {
            StatusText.Text = "Die Feedback-Seite ließ sich nicht öffnen: " + ex.Message;
        }
    }

    private void SignOutButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _context.Settings.SignOut();
        _builds = [];
        StatusText.Text = "";
        NewsPanel.Children.Clear();
        ShowPage(false);
        ShowSeason();
    }
}
