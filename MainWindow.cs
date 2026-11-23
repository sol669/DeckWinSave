using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace GameSavePreview;

internal sealed class MainWindow : Window
{
    private const double TopInset = 18;
    private const double RowSpacing = 5;
    private const double RowHeight = 46;
    private const double CardPadding = 13;
    private const double CardBorder = 1;
    private const double FieldColumnSpacing = 12;
    private const double LabelColumnWeight = 1.42;

    private readonly Grid root = new();
    private readonly Grid layout = new();
    private readonly ColumnDefinition menuColumn = new() { Width = new GridLength(280) };
    private readonly ScrollViewer navigation = new();
    private readonly ScrollViewer content = new() { VerticalContentAlignment = VerticalAlignment.Stretch };
    private Button? winScpShortcut;
    private Button? connectionButton;
    private TextBlock? connectionLabel, connectionIndicator;
    private readonly List<Button> gameNavigation = [];
    private readonly StackPanel settingsActions = new()
    {
        Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 10, 0), Visibility = Visibility.Collapsed
    };
    private readonly Grid bottom = new();
    private readonly Grid footer = new() { ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private Settings settings = new(null, []);
    private bool connected, connecting, connectionFailed, busy, allowClose;
    private string? lastDeckAddress;
    private DeckSettings? pendingConnectionAlert;
    private string page = "settings";
    private GameProfile? draft;
    private int profileIndex = -1;
    private Comparison? comparison;
    private Func<bool>? changed;
    private Func<Task<bool>>? commit;
    private Func<bool>? canCommit;
    private Action? footerState;
    private bool English => settings.Language == "en";
    private string W(string ru, string en) => English ? en : ru;
    private static Windows.UI.Color Color(byte r, byte g, byte b) => ColorHelper.FromArgb(255, r, g, b);
    private bool Dark => settings.Theme == "dark" || (settings.Theme != "light" &&
        new UISettings().GetColorValue(UIColorType.Background).R < 128);
    private static SolidColorBrush Brush(Windows.UI.Color color) => new(color);
    private Windows.UI.Color Muted => Dark ? Color(172, 170, 180) : Color(92, 89, 100);
    private nint Hwnd => WindowNative.GetWindowHandle(this);
    private static Style Style(string name) => (Style)Application.Current.Resources[name];
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    internal MainWindow()
    {
        Title = "DeckWinSave";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "DeckWinSave.ico"));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
        root.Children.Add(layout);
        layout.ColumnDefinitions.Add(menuColumn);
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(navigation);
        Grid.SetColumn(content, 1);
        layout.Children.Add(content);
        var copyright = new StackPanel { Margin = new Thickness(28, 10, 10, 18), Spacing = 0 };
        copyright.Children.Add(Text($"DeckWinSave {typeof(MainWindow).Assembly.GetName().Version!.ToString(3)}", muted: true));
        var credits = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        credits.Children.Add(Text("sol669 ·", muted: true));
        credits.Children.Add(new HyperlinkButton { Content = "GitHub", NavigateUri = new Uri("https://github.com/sol669/DeckWinSave"),
            Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center });
        copyright.Children.Add(credits);
        Grid.SetRow(copyright, 1);
        layout.Children.Add(copyright);
        bottom.ColumnDefinitions.Add(new ColumnDefinition());
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.VerticalAlignment = VerticalAlignment.Center;
        var statusArea = new Grid();
        statusArea.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        statusArea.ColumnDefinitions.Add(new ColumnDefinition());
        statusArea.Children.Add(settingsActions);
        Grid.SetColumn(status, 1);
        statusArea.Children.Add(status);
        bottom.Children.Add(statusArea);
        Grid.SetColumn(footer, 1);
        bottom.Children.Add(footer);
        Grid.SetRow(bottom, 1); Grid.SetColumn(bottom, 1);
        layout.Children.Add(bottom);
        Content = root;
        status.Style = Style("SecondaryText");
        layout.SizeChanged += (_, e) =>
            menuColumn.Width = new GridLength(Math.Clamp(e.NewSize.Width * .286, 145, 280));
        try { settings = Settings.Read(); }
        catch (Exception e) { Status(e.Message); }
        AppWindow.Closing += (sender, e) =>
        {
            if (allowClose || changed?.Invoke() != true) return;
            e.Cancel = true;
            if (!busy) _ = Run(async () => { if (await Leave()) { allowClose = true; Close(); } });
        };
        root.Loaded += (_, _) =>
        {
            if (initialized) return;
            initialized = true;
            if (settings.AutoConnect && settings.Deck is not null) _ = ConnectSavedDeck(automatic: true);
        };
        ApplyTheme();
        ShowSettings();
    }
    private bool initialized;

    public void SetSize()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var scale = Math.Max(GetDpiForWindow(Hwnd), 96u) / 96d;
        var work = area.WorkArea;
        var margin = (int)Math.Round(24 * scale);
        var width = Math.Min((int)Math.Round(980 * scale), Math.Max(1, work.Width - margin));
        var height = Math.Min((int)Math.Round(848 * scale), Math.Max(1, work.Height - margin));
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - width) / 2,
            work.Y + (work.Height - height) / 2, width, height));
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        root.RequestedTheme = settings.Theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        int useDark = Dark ? 1 : 0;
        if (DwmSetWindowAttribute(Hwnd, 20, ref useDark, sizeof(int)) != 0)
            DwmSetWindowAttribute(Hwnd, 19, ref useDark, sizeof(int));
    }

    private TextBlock Text(string value, double size = 14, bool muted = false) => new()
    {
        Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
        Style = muted ? Style("SecondaryText") : null
    };
    private static StackPanel Stack(double spacing = 8) => new() { Spacing = spacing };
    private Button Button(string label, Func<Task> click, bool primary = false)
    {
        var button = new Button { Content = label, Style = Style(primary ? "PrimaryButton" : "SettingsButton") };
        button.Click += (_, _) => _ = Run(click);
        return button;
    }
    private static Border Box(UIElement child) => new() { Child = child, Style = Style("SettingsCard") };
    private static Border CellRow(UIElement child) => new()
    {
        Child = child, Style = Style("SettingsCard"), Height = RowHeight,
        Padding = new Thickness(CardPadding, 3, CardPadding, 3), BorderThickness = new Thickness(CardBorder)
    };
    private static Grid Columns(FrameworkElement left, FrameworkElement right, bool valueColumn = false)
    {
        var row = new Grid { ColumnSpacing = FieldColumnSpacing };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelColumnWeight, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = valueColumn ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        row.Children.Add(left); Grid.SetColumn(right, 1); row.Children.Add(right);
        left.HorizontalAlignment = right.HorizontalAlignment = HorizontalAlignment.Stretch;
        left.VerticalAlignment = right.VerticalAlignment = VerticalAlignment.Center;
        return row;
    }
    private void Section(StackPanel panel, string ru, string en)
    {
        var heading = Text(W(ru, en), 14, true);
        heading.Opacity = .78;
        heading.Margin = new Thickness(1, 0, 0, 0);
        panel.Children.Add(new Border { Height = RowHeight, Child = heading });
    }
    private TextBlock? Row(StackPanel panel, string label, FrameworkElement element, string? detail = null, bool equal = false)
    {
        AutomationProperties.SetName(element, label);
        if (element is TextBox input) input.Style = Style("SettingsInput");
        if (element is ComboBox choice) choice.Style = Style("SettingsChoice");
        FrameworkElement left = Text(label);
        TextBlock? detailText = null;
        if (!string.IsNullOrEmpty(detail))
        {
            var lines = Stack(2);
            lines.Children.Add(left);
            var path = Text(detail, 12, true);
            path.TextWrapping = TextWrapping.NoWrap;
            path.TextTrimming = TextTrimming.CharacterEllipsis;
            ToolTipService.SetToolTip(path, detail);
            lines.Children.Add(path);
            left = lines;
            detailText = path;
        }
        panel.Children.Add(CellRow(Columns(left, element, equal || element is Button)));
        return detailText;
    }
    private void AddFooterButton(Button button)
    {
        Grid.SetColumn(button, footer.Children.Count);
        footer.Children.Add(button);
    }

    private StackPanel Begin(string key, bool gamePage = false)
    {
        page = key; changed = null; commit = null; canCommit = null; footerState = null;
        footer.Children.Clear();
        settingsActions.Children.Clear();
        settingsActions.Visibility = Visibility.Collapsed;
        bool alignWithFields = gamePage || key == "settings";
        double fieldInset = alignWithFields ? CardPadding + CardBorder : 0;
        bottom.Margin = new Thickness(26 + fieldInset, 10, 44 + fieldInset, 18);
        bottom.ColumnSpacing = alignWithFields ? FieldColumnSpacing : 10;
        bottom.ColumnDefinitions[0].Width = new GridLength(alignWithFields ? LabelColumnWeight : 1, GridUnitType.Star);
        bottom.ColumnDefinitions[1].Width = alignWithFields ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        footer.ColumnDefinitions[0].Width = alignWithFields ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        footer.ColumnDefinitions[1].Width = key == "settings" ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        var panel = Stack(RowSpacing); panel.Margin = new Thickness(26, TopInset, 44, gamePage ? 2 : 8);
        content.VerticalScrollMode = ScrollMode.Enabled;
        content.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        content.Content = panel;
        content.ChangeView(null, 0, null, true);
        DrawNav();
        return panel;
    }
    private void Status(string text) => status.Text = text;
    private void DrawNav()
    {
        var nav = Stack(RowSpacing);
        nav.Margin = new Thickness(20, TopInset, 16, 12);
        navigation.Content = nav;
        gameNavigation.Clear();
        nav.Children.Add(Nav(W("Настройки", "Settings"), "settings",
            () => { ShowSettings(); return Task.CompletedTask; }));
        nav.Children.Add(Divider());
        connectionButton = SideButton("", null);
        connectionLabel = Text("");
        connectionIndicator = Text("●", 12);
        var connectionContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        connectionContent.Children.Add(connectionLabel);
        connectionContent.Children.Add(connectionIndicator);
        connectionButton.Content = connectionContent;
        connectionButton.Click += async (_, _) =>
        {
            if (connected)
            {
                connected = false;
                connectionFailed = false;
                UpdateConnectionUi();
            }
            else if (settings.Deck is null)
                Status(W("Укажите параметры Steam Deck в настройках.", "Enter your Steam Deck details in Settings."));
            else await ConnectSavedDeck();
        };
        nav.Children.Add(connectionButton);
        nav.Children.Add(Nav(W("Инструкция по подключению", "Connection guide"), "guide",
            () => { ShowGuide(); return Task.CompletedTask; }));
        winScpShortcut = SideButton(W("Открыть WinSCP", "Open WinSCP"), null);
        winScpShortcut.Click += (_, _) => _ = Run(async () =>
        {
            await ConnectSavedDeck();
            if (connected) Settings.OpenWinScp(Deck());
        });
        nav.Children.Add(winScpShortcut);
        nav.Children.Add(Divider());
        for (int i = 0; i < settings.Games.Count; i++)
        {
            var index = i;
            var game = settings.Games[i];
            var gameButton = Nav(game.Name, "game" + i, () => OpenGame(index), game.WindowsExe);
            gameNavigation.Add(gameButton);
            nav.Children.Add(gameButton);
        }
        var add = Nav(W("+ Добавить игру", "+ Add game"), "new", () => OpenGame(-1));
        gameNavigation.Add(add);
        nav.Children.Add(add);
        UpdateConnectionUi();

        Border Divider() => new()
        {
            Height = RowHeight,
            IsHitTestVisible = false,
            Child = new Border { Background = Brush(Muted), Opacity = .26, Height = 1,
                Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center }
        };

        Button SideButton(string label, string? key)
        {
            var button = new Button { Style = Style(page == key ? "SelectedNavigationButton" : "NavigationButton"),
                Height = RowHeight, Content = new TextBlock { Text = label,
                    TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center } };
            AutomationProperties.SetName(button, label);
            return button;
        }

        Button Nav(string label, string key, Func<Task> action, string? iconPath = null)
        {
            var button = SideButton(label, key);
            button.Click += (_, _) => _ = Run(() => Navigate(action));
            if (!string.IsNullOrWhiteSpace(iconPath) && Path.IsPathFullyQualified(iconPath))
            {
                var picture = new Image { Width = 22, Height = 22 };
                var name = (FrameworkElement)button.Content;
                button.Content = null;
                button.Content = Columns(name, picture);
                _ = LoadIcon(iconPath, picture);
            }
            return button;
        }
    }

    private void UpdateConnectionUi()
    {
        bool ready = connected && !connecting;
        if (connectionButton is not null && connectionLabel is not null && connectionIndicator is not null)
        {
            connectionLabel.Text = connecting ? W("Подключаемся…", "Connecting…")
                : connected ? W("Отключить Steam Deck", "Disconnect Steam Deck")
                : W("Подключить Steam Deck", "Connect Steam Deck");
            connectionIndicator.Foreground = Brush(ready ? (Dark ? Color(108, 202, 130) : Color(36, 112, 66))
                : connectionFailed ? (Dark ? Color(235, 122, 129) : Color(164, 52, 63)) : Muted);
            connectionButton.IsEnabled = !connecting && !busy;
            AutomationProperties.SetName(connectionButton, connectionLabel.Text);
            string connectionState = connecting ? connectionLabel.Text
                : connected ? W("Steam Deck подключён", "Steam Deck connected")
                : connectionFailed ? W("Ошибка подключения", "Connection failed")
                : W("Steam Deck отключён", "Steam Deck disconnected");
            AutomationProperties.SetHelpText(connectionButton, connectionState);
            ToolTipService.SetToolTip(connectionButton, connectionState);
        }
        if (winScpShortcut is not null)
            winScpShortcut.IsEnabled = ready && !busy;
        foreach (var button in gameNavigation) button.IsEnabled = ready && !busy;
        content.IsEnabled = !busy && (page is "settings" or "guide" || ready);
        if (!busy) footerState?.Invoke();
    }

    private async Task Run(Func<Task> action)
    {
        if (busy) return;
        busy = true;
        UpdateConnectionUi();
        SetFooterEnabled(false);
        Status("");
        try { await action(); }
        catch (Exception e) { ReportOperationError(e); }
        finally
        {
            busy = false;
            SetFooterEnabled(true);
            UpdateConnectionUi();
            var alert = pendingConnectionAlert;
            pendingConnectionAlert = null;
            if (alert is not null && alert == settings.Deck && !connected)
                await ShowConnectionAlert();
        }
    }

    private void SetFooterEnabled(bool enabled)
    {
        foreach (var button in footer.Children.Concat(settingsActions.Children).OfType<Button>())
            button.IsEnabled = enabled;
    }

    private async Task Navigate(Func<Task> action)
    {
        if (await Leave()) await action();
    }
    private async Task<bool> Leave()
    {
        if (changed?.Invoke() != true) return true;
        var dialog = Dialog(W("Сохранить изменения?", "Save changes?"),
            W("Сохранить изменения", "Save changes"), W("Не сохранять", "Discard"), W("Отмена", "Cancel"));
        dialog.IsPrimaryButtonEnabled = canCommit?.Invoke() ?? true;
        return (await dialog.ShowAsync()) switch
        {
            ContentDialogResult.Primary => await SaveDraft(),
            ContentDialogResult.Secondary => true,
            _ => false
        };
    }
    private async Task<bool> SaveDraft()
    {
        return commit is null || await commit();
    }
    private async Task<bool> SaveBeforeAction()
    {
        if (changed?.Invoke() != true) return true;
        var dialog = Dialog(W("Сохранить изменения в профиле?", "Save profile changes?"),
            W("Сохранить и продолжить", "Save and continue"), null, W("Отмена", "Cancel"));
        dialog.IsPrimaryButtonEnabled = canCommit?.Invoke() ?? true;
        return await dialog.ShowAsync() == ContentDialogResult.Primary && await SaveDraft();
    }
    private ContentDialog Dialog(string title, string first, string? second = null, string? close = null,
        bool centerSingleButton = false) => new UniformDialog
    {
        XamlRoot = root.XamlRoot, Title = title, PrimaryButtonText = first, SecondaryButtonText = second ?? "",
        CloseButtonText = close ?? W("Отмена", "Cancel"), DefaultButton = ContentDialogButton.Close,
        CenterSingleButton = centerSingleButton
    };

    private sealed class UniformDialog : ContentDialog
    {
        public bool CenterSingleButton { get; init; }

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            foreach (var name in new[] { "PrimaryButton", "SecondaryButton", "CloseButton" })
            {
                if (GetTemplateChild(name) is not Button button) continue;
                button.MinWidth = 0;
                button.Width = 160;
                button.Height = 34;
                button.Padding = new Thickness(2, 0, 2, 0);
                button.HorizontalAlignment = HorizontalAlignment.Center;
            }
            if (!CenterSingleButton) return;
            if (GetTemplateChild("Title") is ContentControl title)
            {
                title.HorizontalAlignment = HorizontalAlignment.Stretch;
                title.HorizontalContentAlignment = HorizontalAlignment.Center;
            }
            if (GetTemplateChild("CommandSpace") is Grid commands && GetTemplateChild("CloseButton") is Button close)
            {
                Grid.SetColumn(close, 0);
                Grid.SetColumnSpan(close, commands.ColumnDefinitions.Count);
            }
        }
    }

    private DeckSettings Deck()
    {
        var deck = settings.Deck ?? throw new IOException(W("Сначала привяжите Steam Deck.", "Pair your Steam Deck first."));
        return connected && lastDeckAddress is not null ? deck with { Address = lastDeckAddress } : deck;
    }
    private Sync Engine(GameProfile game) => new(game, Deck().Fingerprint);
    private SaveFolder Folder(GameProfile game, bool deck) => deck
        ? new DeckFolder(Deck(), game.DeckPath) : new WindowsFolder(game.WindowsPath);
    private void Persist(Settings updated) { updated.Save(); settings = updated; }
    private async Task ConnectSavedDeck(bool automatic = false)
    {
        if (connecting || settings.Deck is not DeckSettings deck) return;
        connecting = true;
        connectionFailed = false;
        UpdateConnectionUi();
        Status("");
        var unavailable = false;
        try
        {
            var preferredAddress = lastDeckAddress;
            var endpoint = await Task.Run(() => Pairing.FindEndpoint(deck, preferredAddress, (candidate, timeout) =>
            {
                using var remote = Pairing.Connect(candidate, timeout);
                remote.ListDirectory(".");
                return true;
            }));
            if (settings.Deck == deck)
            {
                connected = endpoint is not null;
                unavailable = !connected;
                if (endpoint is not null) lastDeckAddress = endpoint.Address;
            }
        }
        catch (Exception)
        {
            if (settings.Deck == deck)
            {
                connected = false;
                unavailable = true;
            }
        }
        finally
        {
            connecting = false;
            connectionFailed = unavailable;
            UpdateConnectionUi();
        }
        if (!unavailable) return;
        if (automatic && busy) pendingConnectionAlert = deck;
        else await ShowConnectionAlert();
    }

    private Task ShowConnectionAlert() => ShowNotice(W("Steam Deck недоступен", "Steam Deck is unavailable"));

    private async Task ShowNotice(string title, string? message = null, string? details = null)
    {
        if (root.XamlRoot is null) return;
        var dialog = Dialog(title, "", close: W("ОК", "OK"), centerSingleButton: true);
        if (message is not null)
        {
            var body = Stack();
            var summary = Text(message);
            summary.IsTextSelectionEnabled = true;
            body.Children.Add(summary);
            if (details is not null)
            {
                var technical = Text(details, 12, true);
                technical.IsTextSelectionEnabled = true;
                body.Children.Add(new Expander { Header = W("Подробнее", "Details"), Content = technical,
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
            }
            dialog.Content = new ScrollViewer { Content = body, MaxHeight = 360 };
        }
        await dialog.ShowAsync();
    }

    private void ShowGuide()
    {
        var path = LocalFiles.GuidePath(English);
        var guide = File.ReadAllLines(path);
        var panel = Begin("guide");
        Section(panel, "Инструкция по подключению", "Connection guide");
        var steps = Stack(5);
        panel.Children.Add(steps);
        foreach (var line in guide)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("    ", StringComparison.Ordinal))
            {
                var command = line[4..];
                var code = Text(command, 12);
                code.FontFamily = new FontFamily("Consolas");
                code.IsTextSelectionEnabled = true;
                var copyLabel = W("Копировать команду", "Copy command");
                var copy = Button(copyLabel, () =>
                {
                    CopyToClipboard(command);
                    Status(W("Команда скопирована.", "Command copied."));
                    return Task.CompletedTask;
                });
                copy.Content = new SymbolIcon(Symbol.Copy);
                copy.Width = copy.Height = 28;
                copy.MinHeight = 0;
                copy.Padding = new Thickness(0);
                AutomationProperties.SetName(copy, copyLabel);
                ToolTipService.SetToolTip(copy, copyLabel);
                var snippet = Box(Columns(code, copy));
                snippet.MinHeight = 0;
                snippet.Padding = new Thickness(10, 2, 10, 2);
                snippet.Margin = new Thickness(36, 0, 0, 2);
                steps.Children.Add(snippet);
                continue;
            }

            var separator = line.IndexOf(' ');
            if (separator == 2 && char.IsAsciiDigit(line[0]) && line[1] == '.')
            {
                var heading = Text(line, 16);
                heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                heading.Margin = new Thickness(0, steps.Children.Count == 0 ? 0 : 9, 0, 2);
                steps.Children.Add(heading);
            }
            else if (separator == 3 && char.IsAsciiDigit(line[0]) && line[1] == '.' && char.IsAsciiDigit(line[2]))
            {
                var step = new Grid { ColumnSpacing = 8 };
                step.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                step.ColumnDefinitions.Add(new ColumnDefinition());
                step.Children.Add(Text(line[..separator], 12, muted: true));
                var instruction = Text(line[(separator + 1)..], 12);
                instruction.IsTextSelectionEnabled = true;
                Grid.SetColumn(instruction, 1);
                step.Children.Add(instruction);
                steps.Children.Add(step);
            }
            else
            {
                var paragraph = Text(line, 12, muted: true);
                paragraph.IsTextSelectionEnabled = true;
                steps.Children.Add(paragraph);
            }
        }
        AddFooterButton(Button(W("Открыть в TXT", "Open as TXT"), () =>
        {
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return Task.CompletedTask;
        }));
    }

    private static void CopyToClipboard(string text)
    {
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
        Clipboard.Flush();
    }

    private void ReportOperationError(Exception error)
    {
        UpdateConnectionAfterError(error);
        Status(error.Message);
    }

    private bool UpdateConnectionAfterError(Exception error)
    {
        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
        {
            if (cause is not DeckConnectionException) continue;
            connected = false;
            connectionFailed = true;
            UpdateConnectionUi();
            return true;
        }
        return false;
    }

    private async Task RunSaveOperation(string failureTitle, Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception error)
        {
            Status("");
            bool disconnected = UpdateConnectionAfterError(error);
            if (disconnected && error is DeckConnectionException)
                await ShowNotice(failureTitle, W("Проверьте подключение Steam Deck и повторите попытку.",
                    "Check your Steam Deck connection and try again."), error.Message);
            else await ShowNotice(failureTitle, error.Message);
        }
    }

    private void ShowSettings()
    {
        var panel = Begin("settings");
        var original = settings.Deck;
        string savedPin = original is null ? "" : "SHA256:" + Pairing.OpenSshDigest(original.Fingerprint);
        Section(panel, "Сетевые настройки", "Network settings");
        var host = new TextBox { Text = original?.Address ?? "", PlaceholderText = W("IP-адрес или сетевое имя", "IP address or hostname") };
        var vpn = new TextBox { Text = original?.VpnAddress ?? "", PlaceholderText = W("IP-адрес в туннеле", "IP address in the tunnel") };
        var user = new TextBox { Text = original?.User ?? "deck" };
        var port = new TextBox { Text = (original?.Port ?? 22).ToString() };
        var pin = new TextBox { Text = savedPin, PlaceholderText = W("Вставьте строку SHA256:…", "Paste the SHA256:… fingerprint") };
        Row(panel, W("Адрес Steam Deck", "Steam Deck address"), host, equal: true);
        Row(panel, W("Адрес для WG соединения (необязательно)", "WG connection address (optional)"), vpn, equal: true);
        Row(panel, W("Имя пользователя SteamOS", "SteamOS username"), user, equal: true);
        Row(panel, W("Порт SSH", "SSH port"), port, equal: true);
        Row(panel, W("Отпечаток SSH", "SSH fingerprint"), pin, equal: true);

        DeckSettings? verified = null;
        string? verifiedAddress = null;
        var state = Text("", muted: true);
        void SetState(string message, Windows.UI.Color? color = null)
        {
            state.Text = "● " + message;
            if (color is { } value) state.Foreground = Brush(value);
            else state.ClearValue(TextBlock.ForegroundProperty);
        }
        var success = Dark ? Color(108, 202, 130) : Color(36, 112, 66);
        var failure = Dark ? Color(235, 122, 129) : Color(164, 52, 63);
        void Unchecked() => SetState(W("SSH не проверен", "SSH not checked"));
        Unchecked();

        string? EnteredPin() => pin.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(value => value.StartsWith("SHA256:", StringComparison.Ordinal) && value.Length > 7);
        string EnteredHost() => FieldAddress(host.Text, original?.Address);
        string? EnteredVpn() => string.IsNullOrWhiteSpace(vpn.Text) ? null : FieldAddress(vpn.Text, original?.VpnAddress);
        static string FieldAddress(string input, string? saved) =>
            input.Trim().Equals(saved, StringComparison.OrdinalIgnoreCase) ? saved! : Pairing.NormalizeAddress(input);
        bool NetworkChanged() => original is null
            ? EnteredHost().Length > 0 || EnteredVpn() is not null || user.Text.Trim() != "deck"
                || port.Text.Trim() != "22" || pin.Text.Trim().Length > 0
            : !EnteredHost().Equals(original.Address, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(EnteredVpn(), original.VpnAddress, StringComparison.OrdinalIgnoreCase)
                || user.Text.Trim() != original.User || !int.TryParse(port.Text, out int number) || number != original.Port
                || EnteredPin() != savedPin;

        var check = Button(W("Проверить SSH", "Check SSH"), async () =>
        {
            verified = null;
            verifiedAddress = null;
            SetState(W("Проверка SSH…", "Checking SSH…"));
            var candidate = new DeckSettings(EnteredHost(), int.Parse(port.Text), user.Text.Trim(), "")
                { VpnAddress = EnteredVpn() };
            var supplied = EnteredPin();
            var mismatch = false;
            string? matchedFingerprint = null;
            var endpoint = await Task.Run(() => Pairing.FindEndpoint(candidate, null, (target, timeout) =>
            {
                var fingerprint = Pairing.Probe(target, timeout);
                if (supplied is null || Pairing.OpenSshDigest(supplied) == Pairing.OpenSshDigest(fingerprint))
                {
                    matchedFingerprint = fingerprint;
                    return true;
                }
                mismatch = true;
                return false;
            }));
            if (endpoint is null)
            {
                if (!NetworkChanged())
                {
                    connected = false;
                    connectionFailed = true;
                }
                SetState(mismatch ? W("Отпечаток не совпал", "Fingerprint mismatch")
                    : W("SSH недоступен", "SSH unavailable"), failure);
                return;
            }
            if (supplied is null)
            {
                SetState(W("SSH доступен. Введите отпечаток.", "SSH is available. Enter the fingerprint."));
                return;
            }
            verified = candidate with { Fingerprint = original is not null && supplied == savedPin
                ? original.Fingerprint : matchedFingerprint! };
            verifiedAddress = endpoint.Address;
            SetState(W("SSH доступен, отпечаток совпал", "SSH available, fingerprint matches"), success);
            ToolTipService.SetToolTip(state, endpoint.Address);
        });
        AutomationProperties.SetName(check, W("Проверить SSH", "Check SSH"));
        panel.Children.Add(CellRow(Columns(state, check, true)));

        Section(panel, "Система", "System");
        var autoConnect = ToggleRow(W("Автоматически подключиться к Steam Deck при запуске",
            "Automatically connect to Steam Deck on launch"), settings.AutoConnect);
        var themes = new[] { "system", "light", "dark" };
        var theme = new ComboBox { ItemsSource = new[] { W("Как в Windows", "Windows default"), W("Светлая", "Light"), W("Тёмная", "Dark") },
            SelectedIndex = Math.Max(0, Array.IndexOf(themes, settings.Theme)) };
        var language = new ComboBox { ItemsSource = new[] { "Русский", "English" }, SelectedIndex = English ? 1 : 0 };
        Row(panel, W("Тема", "Theme"), theme, equal: true);
        Row(panel, W("Язык", "Language"), language, equal: true);
        string Language() => language.SelectedIndex == 1 ? "en" : "ru";
        changed = () => NetworkChanged() || autoConnect.IsOn != settings.AutoConnect
            || themes[theme.SelectedIndex] != settings.Theme || Language() != settings.Language;
        canCommit = () => !NetworkChanged() || verified is not null;
        commit = async () =>
        {
            var deck = original;
            if (NetworkChanged())
            {
                deck = verified ?? throw new IOException(W("Сначала проверьте SSH.", "Check SSH first."));
                if (!await AuthorizeDeck(deck with { Address = verifiedAddress! })) return false;
            }
            Persist(settings with { Deck = deck, AutoConnect = autoConnect.IsOn,
                Theme = themes[theme.SelectedIndex], Language = Language() });
            if (deck != original)
            {
                connected = false;
                connectionFailed = false;
                lastDeckAddress = verifiedAddress;
            }
            changed = () => false;
            ApplyTheme();
            return true;
        };
        var save = Button(W("Сохранить", "Save"), async () =>
        {
            if (!await SaveDraft()) return;
            ShowSettings();
            Status(W("Настройки сохранены.", "Settings saved."));
        }, true);
        var close = Button(W("Закрыть", "Close"), async () => { if (await Leave()) { allowClose = true; Close(); } });
        close.HorizontalAlignment = save.HorizontalAlignment = HorizontalAlignment.Stretch;
        AddFooterButton(close);
        AddFooterButton(save);
        if (original is not null)
        {
            settingsActions.Children.Add(Button(W("Отвязать Steam Deck", "Unpair Steam Deck"), UnpairDeck));
            settingsActions.Visibility = Visibility.Visible;
        }

        void Update()
        {
            check.IsEnabled = Uri.CheckHostName(EnteredHost()) != UriHostNameType.Unknown
                && (EnteredVpn() is not string vpnAddress || Uri.CheckHostName(vpnAddress) != UriHostNameType.Unknown)
                && System.Text.RegularExpressions.Regex.IsMatch(user.Text.Trim(), "^[a-zA-Z0-9_-]+$")
                && int.TryParse(port.Text, out int value) && value is > 0 and <= 65535;
            save.IsEnabled = changed?.Invoke() == true && canCommit?.Invoke() == true;
        }
        foreach (var field in new[] { host, vpn, user, port, pin })
            field.TextChanged += (_, _) =>
            {
                verified = null;
                verifiedAddress = null;
                ToolTipService.SetToolTip(state, null);
                Unchecked();
                Update();
            };
        autoConnect.Toggled += (_, _) => Update();
        theme.SelectionChanged += (_, _) => Update();
        language.SelectionChanged += (_, _) => Update();
        footerState = Update;
        Update();

        ToggleSwitch ToggleRow(string label, bool enabled)
        {
            var toggle = new ToggleSwitch { IsOn = enabled, MinWidth = 0, Width = 44,
                OnContent = "", OffContent = "", VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(toggle, label);
            var labelState = Text(toggle.IsOn ? W("Вкл.", "On") : W("Откл.", "Off"), muted: true);
            toggle.Toggled += (_, _) => labelState.Text = toggle.IsOn ? W("Вкл.", "On") : W("Откл.", "Off");
            var control = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            control.Children.Add(labelState);
            control.Children.Add(toggle);
            Row(panel, label, control);
            return toggle;
        }
    }

    private async Task UnpairDeck()
    {
        var confirm = Dialog(W("Отвязать этот Steam Deck?", "Unpair this Steam Deck?"), W("Отвязать", "Unpair"));
        var explanation = Stack();
        explanation.Children.Add(Text(W(
            "Привязка будет удалена из DeckWinSave на этом PC. Профили игр, сохранения, бэкапы и ключ SSH сохранятся.",
            "The pairing will be removed from DeckWinSave on this PC. Game profiles, saves, backups and the SSH key will be kept.")));
        if (changed?.Invoke() == true)
            explanation.Children.Add(Text(W("Несохранённые настройки будут сброшены.", "Unsaved settings will be discarded.")));
        confirm.Content = explanation;
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        Persist(settings with { Deck = null });
        connected = false;
        connectionFailed = false;
        lastDeckAddress = null;
        pendingConnectionAlert = null;
        comparison = null;
        ShowSettings();
        Status(W("Steam Deck отвязан.", "Steam Deck unpaired."));
    }

    private async Task<bool> AuthorizeDeck(DeckSettings candidate)
    {
        await Task.Run(Pairing.PublicKeyCommand);
        try
        {
            await Task.Run(() => { using var connection = Pairing.Connect(candidate); connection.ListDirectory("."); });
        }
        catch (WinSCP.SessionRemoteException)
        {
            var password = new PasswordBox { PlaceholderText = W("Введите пароль SteamOS (sudo)", "Enter your SteamOS (sudo) password") };
            var dialog = Dialog("DeckWinSave", W("Привязать", "Pair"));
            var section = Stack();
            section.Children.Add(Text(W("Введите пароль SteamOS (sudo)", "Enter your SteamOS (sudo) password"), 18));
            section.Children.Add(password);
            dialog.Content = section;
            dialog.IsPrimaryButtonEnabled = false;
            password.PasswordChanged += (_, _) => dialog.IsPrimaryButtonEnabled = password.Password.Length > 0;
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) { password.Password = ""; return false; }
            using var secret = new SecureString();
            foreach (var character in password.Password) secret.AppendChar(character);
            password.Password = "";
            secret.MakeReadOnly();
            await Task.Run(() => Pairing.InstallKey(candidate, secret));
        }
        return true;
    }

    private async Task OpenGame(int index)
    {
        if (!connected) return;
        profileIndex = index;
        draft = index < 0 ? new GameProfile("", "", "", []) : settings.Games[index];
        comparison = null;
        ShowGame();
        if (index >= 0) await RefreshGame();
    }

    private async Task RefreshGame()
    {
        var game = settings.Games[profileIndex];
        try
        {
            Status(W("Сравниваем сохранения…", "Comparing saves…"));
            comparison = await Compare(game);
            Status("");
        }
        catch (Exception e) { comparison = null; ReportOperationError(e); }
        ShowGame();
    }

    private Task<Comparison> Compare(GameProfile game) => Task.Run(() =>
    {
        using var pc = Folder(game, false); using var deck = Folder(game, true);
        var sync = Engine(game);
        var result = sync.Compare(pc, deck);
        sync.RememberEqual(result);
        return result;
    });

    private void ShowGame()
    {
        var panel = Begin(profileIndex < 0 ? "new" : "game" + profileIndex, gamePage: true);
        var original = profileIndex < 0 ? null : settings.Games[profileIndex];
        if (original is not null) GameActions(panel);
        Section(panel, "Настройки игры", "Game settings");
        var name = new TextBox { Text = draft!.Name, PlaceholderText = W("Название игры", "Game name") };
        name.TextChanged += (_, _) => { draft = draft! with { Name = name.Text }; footerState?.Invoke(); };
        Row(panel, W("Название", "Name"), name, equal: true);
        Row(panel, W("Файл игры (.exe) на PC", "Game executable (.exe) on PC"), Button(W("Указать…", "Browse…"), () =>
        {
            var picked = LocalFiles.Pick(Hwnd, null, true).FirstOrDefault();
            if (picked is null) return Task.CompletedTask;
            var version = FileVersionInfo.GetVersionInfo(picked);
            var suggested = string.IsNullOrWhiteSpace(version.ProductName) ? Path.GetFileNameWithoutExtension(picked) : version.ProductName;
            draft = draft! with { WindowsExe = picked, Name = string.IsNullOrWhiteSpace(draft.Name) ? suggested : draft.Name };
            ShowGame(); return Task.CompletedTask;
        }), draft.WindowsExe);
        Section(panel, "Пути к сохранениям", "Save folders");
        var pathErrorColor = Dark ? Color(235, 122, 129) : Color(164, 52, 63);
        TextBlock PathError() => new() { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(pathErrorColor), Margin = new Thickness(13, 0, 13, 0) };
        void SetPathError(TextBlock field, string? message)
        {
            field.Text = message ?? "";
            field.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        }
        var pcError = PathError();
        Row(panel, "PC", Button(W("Выбрать папку…", "Choose folder…"), async () =>
        {
            var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, Hwnd);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) { draft = draft! with { WindowsPath = folder.Path }; ShowGame(); }
        }), draft.WindowsPath);
        panel.Children.Add(pcError);
        var deckError = PathError();
        TextBlock? pathDetail = null;
        var paste = Button(W("Вставить путь", "Paste path"), async () =>
        {
            var clipboard = Clipboard.GetContent();
            var path = clipboard.Contains(StandardDataFormats.Text) ? (await clipboard.GetTextAsync()).Trim() : "";
            if (!ValidDeckPath(path))
            {
                SetPathError(deckError, W("Скопируйте путь к папке из адресной строки WinSCP.",
                    "Copy the folder path from the WinSCP address bar."));
                return;
            }
            path = path.TrimEnd('/');
            SetPathError(deckError, null);
            draft = draft! with { DeckPath = path };
            pathDetail!.Text = path;
            ToolTipService.SetToolTip(pathDetail, path);
            footerState?.Invoke();
        });
        pathDetail = Row(panel, "Steam Deck", paste, string.IsNullOrEmpty(draft.DeckPath)
            ? W("Скопируйте путь к папке в WinSCP", "Copy the folder path in WinSCP")
            : draft.DeckPath);
        panel.Children.Add(deckError);
        Section(panel, "Исключения", "Exclusions");
        var exclusions = Button(W("Выбрать файлы…", "Choose files…"), () =>
        {
            var rootPath = draft!.WindowsPath;
            var paths = LocalFiles.Pick(Hwnd, rootPath, false).Select(p => LocalFiles.RelativeSave(rootPath, p)).ToArray();
            draft = draft with { Exceptions = draft.Exceptions.Concat(paths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
            ShowGame(); return Task.CompletedTask;
        });
        exclusions.IsEnabled = !string.IsNullOrEmpty(draft.WindowsPath);
        Row(panel, W("Добавить в исключения", "Add to exclusions"), exclusions);
        if (draft.Exceptions.Length > 0)
        {
            var list = Stack(3);
            foreach (var path in draft.Exceptions)
            {
                var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                var remove = Button("×", () =>
                {
                    draft = draft! with { Exceptions = draft.Exceptions.Where(e => !e.Equals(path, StringComparison.OrdinalIgnoreCase)).ToArray() };
                    ShowGame(); return Task.CompletedTask;
                });
                ToolTipService.SetToolTip(remove, W("Убрать из исключений", "Remove exclusion"));
                line.Children.Add(remove); line.Children.Add(Text(path)); list.Children.Add(line);
            }
            panel.Children.Add(Box(list));
        }
        changed = () => original is null
            ? draft!.Name.Length > 0 || draft.WindowsPath.Length > 0 || draft.DeckPath.Length > 0 || draft.WindowsExe is not null
            : original.Name != draft!.Name || original.WindowsPath != draft.WindowsPath || original.DeckPath != draft.DeckPath
                || original.WindowsExe != draft.WindowsExe || !original.Exceptions.SequenceEqual(draft.Exceptions);
        commit = async () =>
        {
            var saved = draft! with { Name = draft.Name.Trim(), DeckPath = draft.DeckPath.Trim().TrimEnd('/') };
            if (!Valid(saved)) throw new IOException(W("Укажите название и обе папки сохранений.", "Enter a name and both save folders."));
            bool checkPaths = original is null || original.WindowsPath != saved.WindowsPath || original.DeckPath != saved.DeckPath;
            if (checkPaths)
            {
                if (!await CanReadFolder(false, pcError, W("Не удалось открыть папку на PC.", "Could not open the PC folder.")))
                    return false;
                if (!await CanReadFolder(true, deckError, W("Не удалось открыть папку на Steam Deck.", "Could not open the Steam Deck folder.")))
                    return false;
            }
            var games = settings.Games.ToList();
            if (profileIndex < 0) { profileIndex = games.Count; games.Add(saved); }
            else games[profileIndex] = saved;
            Persist(settings with { Games = games });
            draft = saved; changed = () => false;
            return true;

            async Task<bool> CanReadFolder(bool onDeck, TextBlock field, string message)
            {
                SetPathError(field, null);
                try
                {
                    await Task.Run(() => { using var folder = Folder(saved, onDeck); folder.List(""); });
                    return true;
                }
                catch (IOException error) when (error is not DeckConnectionException)
                {
                    SetPathError(field, message);
                    return false;
                }
                catch (Exception error) when (error is UnauthorizedAccessException or WinSCP.SessionRemoteException)
                {
                    SetPathError(field, message);
                    return false;
                }
            }
        };
        var save = Button(original is null ? W("Добавить игру", "Add game") : W("Сохранить изменения", "Save changes"),
            async () => { if (await SaveDraft()) await RefreshGame(); }, true);
        var cancel = Button(W("Отмена", "Cancel"), async () =>
        {
            if (profileIndex < 0 || !connected) ShowSettings(); else await OpenGame(profileIndex);
        });
        cancel.HorizontalAlignment = HorizontalAlignment.Stretch;
        AddFooterButton(cancel);
        AddFooterButton(save);
        canCommit = () => connected && Valid(draft!);
        footerState = () => save.IsEnabled = changed?.Invoke() == true && canCommit();
        footerState();
    }

    private static bool Valid(GameProfile profile) => !string.IsNullOrWhiteSpace(profile.Name)
        && Path.IsPathFullyQualified(profile.WindowsPath) && ValidDeckPath(profile.DeckPath);

    private static bool ValidDeckPath(string value)
    {
        var path = value.Trim().TrimEnd('/');
        return path.StartsWith('/') && !path.Any(char.IsControl) && !path.Split('/').Any(part => part is "." or "..");
    }

    private void GameActions(StackPanel panel)
    {
        const double actionHeight = 34;
        Section(panel, "Синхронизация сохранений", "Save sync");
        var proposed = comparison;
        var transferFailure = W("Не удалось перенести сохранения", "Could not transfer saves");
        var sync = Button(W("Синхронизировать", "Sync"), () => RunSaveOperation(transferFailure, async () =>
        {
            if (!await SaveBeforeAction()) return;
            var game = settings.Games[profileIndex];
            if (proposed?.IsFirstTransfer == true)
            {
                await ManualTransfer(game);
                return;
            }
            Status(W("Сравниваем сохранения…", "Comparing saves…"));
            var latest = await Compare(game);
            Status("");
            comparison = latest; ShowGame();
            if (latest.IsFirstTransfer || latest.Reason is "Остались различия — выберите направление вручную"
                or "Сохранения изменились на обоих устройствах")
                await ManualTransfer(game, latest);
            else if (latest.ToDeck is bool direction) await PreviewTransfer(game, latest, direction);
            else if (latest.Reason is "Сохранения не найдены" or "На изменившейся стороне нет сохранений")
                await ShowNotice(W("Нет файлов для переноса", "No files to transfer"), Reason(latest.Reason));
        }), true);
        sync.Style = Style("SyncButton");
        sync.Height = actionHeight;
        sync.HorizontalAlignment = HorizontalAlignment.Stretch;
        var backups = Button(W("Открыть…", "Open…"), ManageBackups);
        var cloud = Button(W("Открыть…", "Open…"), () =>
            ShowNotice(W("Функция в разработке", "Feature in development")));
        var manual = Button(W("Выбрать направление…", "Choose direction…"), () => RunSaveOperation(transferFailure, async () =>
        {
            if (await SaveBeforeAction()) await ManualTransfer(settings.Games[profileIndex]);
        }));
        manual.Height = cloud.Height = backups.Height = actionHeight;
        var dates = new Grid { ColumnSpacing = 14, RowSpacing = 5 };
        dates.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dates.ColumnDefinitions.Add(new ColumnDefinition());
        var pcTime = proposed?.Pc.LastModified;
        var deckTime = proposed?.Deck.LastModified;
        var times = new[] { (Device: "PC", Time: pcTime), (Device: "Steam Deck", Time: deckTime) }
            .OrderByDescending(entry => entry.Time).ToArray();
        for (int i = 0; i < times.Length; i++)
        {
            var (device, time) = times[i];
            dates.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = Text(device);
            var date = Text(time?.ToString("g") ?? "—", 14, true);
            if (i == 0 && time is not null && pcTime != deckTime)
                date.Foreground = Brush(Dark ? Color(108, 202, 130) : Color(36, 112, 66));
            Grid.SetRow(label, i);
            Grid.SetRow(date, i);
            Grid.SetColumn(date, 1);
            dates.Children.Add(label);
            dates.Children.Add(date);
        }
        var directionHint = proposed?.IsFirstTransfer == true ? W("Выберите направление", "Choose a direction")
            : proposed?.ToDeck is bool toDeck ? Direction(toDeck)
            : proposed is null ? W("Направление не определено", "Direction undetermined") : Reason(proposed.Reason);
        var hint = Text(directionHint, 12, true);
        hint.Margin = new Thickness(0, 4, 0, 0);
        hint.TextAlignment = TextAlignment.Center;
        hint.TextWrapping = TextWrapping.NoWrap;
        hint.TextTrimming = TextTrimming.CharacterEllipsis;
        hint.VerticalAlignment = VerticalAlignment.Top;
        var syncCell = new Grid();
        syncCell.RowDefinitions.Add(new RowDefinition());
        syncCell.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        syncCell.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(sync, 1);
        Grid.SetRow(hint, 2);
        syncCell.Children.Add(sync);
        syncCell.Children.Add(hint);
        AutomationProperties.SetHelpText(sync, directionHint);
        if (directionHint.Length > 35) ToolTipService.SetToolTip(hint, directionHint);
        var overviewContent = Columns(dates, syncCell, true);
        syncCell.VerticalAlignment = VerticalAlignment.Stretch;
        var overview = Box(overviewContent);
        overview.Height = 2 * RowHeight + RowSpacing;
        panel.Children.Add(overview);
        Row(panel, W("Ручной перенос", "Manual transfer"), manual);
        Row(panel, W("Облачные сохранения", "Cloud saves"), cloud);
        Row(panel, W("Работа с бэкапами", "Manage backups"), backups);
    }

    private async Task ManageBackups()
    {
        var dialog = Dialog(W("Что вы хотите сделать?", "What would you like to do?"),
            W("Создать бэкап", "Create backup"), W("Восстановить из бэкапа", "Restore from backup"));
        var action = await dialog.ShowAsync();
        if (action == ContentDialogResult.None || !await SaveBeforeAction()) return;
        var game = settings.Games[profileIndex];
        if (action == ContentDialogResult.Primary)
            await RunSaveOperation(W("Не удалось создать бэкап", "Could not create backup"), () => CreateBackup(game));
        else
            await RunSaveOperation(W("Не удалось восстановить сохранения", "Could not restore saves"), () => Restore(game));
    }

    private async Task ManualTransfer(GameProfile game, Comparison? comparison = null)
    {
        var dialog = Dialog(W("Куда перенести сохранения?", "Which way should the saves go?"), Direction(true), Direction(false));
        var direction = await dialog.ShowAsync();
        if (direction == ContentDialogResult.None) return;
        if (comparison is null)
        {
            Status(W("Сравниваем сохранения…", "Comparing saves…"));
            comparison = await Compare(game);
            Status("");
        }
        await PreviewTransfer(game, comparison, direction == ContentDialogResult.Primary);
    }

    private static string Direction(bool toDeck) => toDeck ? "PC → Steam Deck" : "Steam Deck → PC";
    private string Reason(string ru) => ru switch
    {
        "Сохранения не найдены" => W(ru, "No saves found"),
        "Сохранения совпадают" => W(ru, "Saves match"),
        "Первый перенос — выберите направление вручную" => W(ru, "First transfer — choose a direction manually"),
        "Остались различия — выберите направление вручную" => W(ru, "Differences remain — choose a direction manually"),
        "Сохранения изменились на обоих устройствах" => W(ru, "Saves changed on both devices"),
        "Нет новых изменений после переноса" => W(ru, "No changes since the last transfer"),
        "На изменившейся стороне нет сохранений" => W(ru, "No saves on the device that changed"),
        _ => W(ru, "Open manual transfer to check")
    };
    private string ActionLabel(string ru) => ru switch
    {
        "Добавить" => W(ru, "Add"), "Заменить" => W(ru, "Replace"),
        "Уже совпадает" => W(ru, "Already matches"), _ => W(ru, "Excluded")
    };

    private async Task PreviewTransfer(GameProfile game, Comparison approved, bool toDeck)
    {
        var source = toDeck ? approved.Pc : approved.Deck;
        if (source.Files.Length == 0 && source.Excluded.Length == 0)
        {
            await ShowNotice(W("Нет файлов для переноса", "No files to transfer"),
                toDeck ? W("В папке сохранений на PC нет файлов.", "The PC save folder is empty.")
                    : W("В папке сохранений на Steam Deck нет файлов.", "The Steam Deck save folder is empty."));
            return;
        }
        var dialog = Dialog(W("Перенос сохранений", "Transfer saves"), W("Перенести", "Transfer"));
        var body = Stack();
        body.Children.Add(Text(game.Name + "  ·  " + Direction(toDeck), 17));
        var folders = Stack();
        foreach (var path in new[] {
            W("Откуда: ", "From: ") + (toDeck ? game.WindowsPath : game.DeckPath),
            W("Куда: ", "To: ") + (toDeck ? game.DeckPath : game.WindowsPath) })
        {
            var folder = Text(path, 12, true);
            folder.IsTextSelectionEnabled = true;
            folders.Children.Add(folder);
        }
        body.Children.Add(new Expander { Header = W("Показать папки", "Show folders"), Content = folders,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        body.Children.Add(Text(W("Закройте игру на обоих устройствах.", "Close the game on both devices."), 12, true));
        var boxes = new List<(CheckBox Box, Change Change)>();
        foreach (var change in approved.Preview(toDeck))
        {
            var label = Text(ActionLabel(change.Action), 12, !change.CanCopy);
            var box = new CheckBox { Content = Text(change.Path, 13), IsChecked = change.CanCopy, IsEnabled = change.CanCopy };
            boxes.Add((box, change));
            box.Checked += (_, _) => { label.Text = ActionLabel(change.Action); Update(); };
            box.Unchecked += (_, _) => { label.Text = W("Снята галочка", "Skipped"); Update(); };
            body.Children.Add(Box(Columns(box, label)));
        }
        dialog.Content = new ScrollViewer { Content = body, MaxHeight = 380 };
        void Update() => dialog.IsPrimaryButtonEnabled = boxes.Any(b => b.Box.IsChecked == true);
        Update();
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var selected = boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Change).ToArray();
            Status(W("Переносим сохранения…", "Transferring saves…"));
            await Task.Run(() => { using var pc = Folder(game, false); using var deck = Folder(game, true);
                Engine(game).Transfer(pc, deck, approved, toDeck, selected); });
            Status("");
            await ShowNotice(W("Сохранения перенесены", "Saves transferred"),
                game.Name + "  ·  " + Direction(toDeck) + "\n" +
                W($"Перенесено файлов: {selected.Length}.\nБэкапы созданы на обоих устройствах.",
                    $"Files transferred: {selected.Length}.\nBackups were created on both devices."));
            await RefreshGame();
        }
    }

    private async Task<bool?> SelectBackupDevice(string title)
    {
        var select = Dialog(title, W("На PC", "On PC"), W("На Steam Deck", "On Steam Deck"));
        return (await select.ShowAsync()) switch
        {
            ContentDialogResult.Primary => false,
            ContentDialogResult.Secondary => true,
            _ => null
        };
    }

    private async Task CreateBackup(GameProfile game)
    {
        if (await SelectBackupDevice(W("Где создать бэкап?", "Where should the backup be created?")) is not bool onDeck) return;
        Status(W("Создаём бэкап…", "Creating backup…"));
        var backup = await Task.Run(() => { using var folder = Folder(game, onDeck); return Engine(game).CreateBackup(folder, onDeck); });
        int count = (onDeck ? backup.Deck : backup.Pc).Length;
        string device = onDeck ? "Steam Deck" : "PC";
        Status("");
        await ShowNotice(W("Бэкап создан", "Backup created"),
            game.Name + "  ·  " + device + "\n" + W($"Файлов в бэкапе: {count}.", $"Files in the backup: {count}."));
    }

    private async Task Restore(GameProfile game)
    {
        if (await SelectBackupDevice(W("Где восстановить последний бэкап?", "Where should the latest backup be restored?")) is not bool onDeck) return;
        var plan = await Task.Run(() => { using var folder = Folder(game, onDeck); return Engine(game).PlanRestore(folder, onDeck); });
        var confirmation = Dialog(W("Восстановление", "Restore"), W("Восстановить", "Restore"));
        var body = Stack();
        body.Children.Add(Text((onDeck ? "Steam Deck" : "PC") + "  ·  " + plan.Operation.At.ToLocalTime().ToString("g")));
        body.Children.Add(Text(W("Закройте игру. Файлов в бэкапе: ", "Close the game. Files in backup: ") + plan.Files.Length));
        foreach (var file in plan.Files) body.Children.Add(Text(file.Path, 12, true));
        var remove = new CheckBox { Content = Text(W("Убрать неизменённые файлы, добавленные переносом",
            "Remove unchanged files added by the transfer"), 13) };
        if (plan.Added.Length > 0)
        {
            body.Children.Add(remove);
            foreach (var file in plan.Added) body.Children.Add(Text(file.Path, 12, true));
        }
        confirmation.Content = new ScrollViewer { Content = body, MaxHeight = 360 };
        if (await confirmation.ShowAsync() != ContentDialogResult.Primary) return;
        var removeAdded = remove.IsChecked == true;
        Status(W("Восстанавливаем сохранения…", "Restoring saves…"));
        var removed = await Task.Run(() => { using var folder = Folder(game, onDeck); return Engine(game).Restore(folder, onDeck, plan, removeAdded); });
        Status("");
        var result = W($"Восстановлено файлов: {plan.Files.Length}.", $"Files restored: {plan.Files.Length}.");
        if (removed != 0) result += "\n" + W($"Убрано файлов, добавленных переносом: {removed}.",
            $"Files added by the transfer and removed: {removed}.");
        await ShowNotice(W("Восстановление завершено", "Restore complete"),
            game.Name + "  ·  " + (onDeck ? "Steam Deck" : "PC") + "\n" + result);
        await RefreshGame();
    }

    private static async Task LoadIcon(string path, Image icon)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 48);
            if (thumbnail is null) return;
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(thumbnail);
            icon.Source = bitmap;
        }
        catch (Exception) { }
    }
}
