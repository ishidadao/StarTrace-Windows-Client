using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using StarTrace.Client.Models;
using StarTrace.Client.Services;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Microsoft.UI.Xaml.Shapes;
using FontWeight = Windows.UI.Text.FontWeight;
using FontWeights = Microsoft.UI.Text.FontWeights;

namespace StarTrace.Client;

public sealed partial class MainWindow : Window
{
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(45) };
    private readonly GenshinGachaService _genshin;
    private readonly WutheringWavesGachaService _wutheringWaves;
    private readonly CloudSyncService _sync;
    private readonly IdentityLoginService _identity = new();
    private readonly ItemIconService _icons;
    private readonly GameRecordDiscoveryService _discovery = new();
    private readonly LocalDataService _local = new();
    private readonly UigfService _uigf = new();

    private readonly Grid _root;
    private readonly ComboBox _gameSelector = new();
    private readonly TextBox _manualUrlBox = new();
    private readonly Button _autoFetchButton;
    private readonly Button _manualFetchButton;
    private readonly Button _syncButton;
    private readonly Button _uploadAllButton = SecondaryButton("一键上传");
    private readonly Button _backupButton = SecondaryButton("一键导出");
    private readonly Button _logoutButton = SecondaryButton("退出登录");
    private readonly Button _cancelLoginButton = SecondaryButton("取消登录");
    private readonly Button _importButton;
    private readonly Button _exportButton;
    private readonly TextBlock _statusText = Text("正在准备本地数据…", 11, "#6F7187");
    private readonly TextBlock _connectionText = Text("检查服务", 10, "#777A91");
    private readonly Ellipse _connectionDot = new() { Width = 7, Height = 7, Fill = Brush("#D7A64F") };
    private readonly TextBlock _uidText = Text("—", 17, "#25263D", FontWeights.SemiBold);
    private readonly TextBlock _totalText = Text("0", 24, "#25263D", FontWeights.SemiBold);
    private readonly TextBlock _fiveStarText = Text("0", 24, "#D49C42", FontWeights.SemiBold);
    private readonly TextBlock _pityText = Text("0", 24, "#7867E5", FontWeights.SemiBold);
    private readonly TextBlock _localStateText = Text("本地尚无记录", 11, "#777A91");
    private readonly StackPanel _recordPanel = new() { Spacing = 0 };
    private readonly StackPanel _fiveStarPanel = new() { Orientation = Orientation.Horizontal, Spacing = 10 };
    private readonly TextBlock _recordSubtitle = Text("读取后会按当前游戏与 UID 独立保存", 10, "#999CAF");
    private FetchResult? _current;
    private bool _serverReachable;
    private readonly CancellationTokenSource _iconCancellation = new();
    private CancellationTokenSource? _loginCancellation;
    private bool _hasUnifiedLogin;
    private bool? _connectionOnline;

    public MainWindow()
    {
        // 这台机器已验证非打包 XBF 在部分 Windows 预览版上会加载失败。
        // 主窗口使用等价的 C# WinUI 控件树，避免调用 MainWindow.InitializeComponent。
        _genshin = new GenshinGachaService(_httpClient);
        _wutheringWaves = new WutheringWavesGachaService(_httpClient);
        _sync = new CloudSyncService(_httpClient);
        _icons = new ItemIconService(_httpClient);
        _autoFetchButton = PrimaryButton("一键读取记录", "\uE8C8");
        _manualFetchButton = SecondaryButton("使用此链接");
        _syncButton = PrimaryButton("统一认证登录", "\uE753");
        _importButton = SecondaryButton("导入 UIGF");
        _exportButton = SecondaryButton("导出 UIGF");
        ExtendsContentIntoTitleBar = true;
        _root = BuildWindow();
        Content = _root;
        Title = "星迹 StarTrace";
        TrySetAppIcon();
        TrySetMica();
        _root.Loaded += async (_, _) => await InitializeAsync();
        Closed += (_, _) =>
        {
            _iconCancellation.Cancel();
            _loginCancellation?.Cancel();
            _iconCancellation.Dispose();
            _sync.Dispose();
            _httpClient.Dispose();
        };
    }

    private Grid BuildWindow()
    {
        var root = new Grid { Background = Brush("#F7F7FC") };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(228) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var titleBar = BuildTitleBar();
        Grid.SetColumnSpan(titleBar, 2);
        root.Children.Add(titleBar);
        SetTitleBar(titleBar);

        var sidebar = BuildSidebar();
        Grid.SetRow(sidebar, 1);
        root.Children.Add(sidebar);

        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = BuildMainContent(),
        };
        Grid.SetRow(scroll, 1);
        Grid.SetColumn(scroll, 1);
        root.Children.Add(scroll);
        return root;
    }

    private Grid BuildTitleBar()
    {
        var bar = new Grid
        {
            Height = 48,
            Background = Brush("#FAFAFD"),
        };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(228) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(10),
            Child = new Image
            {
                Source = new BitmapImage(new Uri("ms-appx:///Assets/StarTrace.png")),
                Stretch = Stretch.Uniform,
            },
        });
        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(Text("星迹", 15, "#25263D", FontWeights.SemiBold));
        name.Children.Add(Text("STARTRACE", 7, "#A5A5B7", FontWeights.SemiBold));
        brand.Children.Add(name);
        bar.Children.Add(brand);

        var centerTitle = Text("抽卡记录助手", 11, "#85879B");
        centerTitle.Margin = new Thickness(18, 0, 0, 0);
        centerTitle.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(centerTitle, 1);
        bar.Children.Add(centerTitle);
        return bar;
    }

    private Border BuildSidebar()
    {
        var panel = new Grid { Padding = new Thickness(18, 24, 18, 22) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var nav = new StackPanel { Spacing = 8 };
        nav.Children.Add(Text("工作台", 9, "#8F8CA7", FontWeights.SemiBold));
        nav.Children.Add(NavItem("自动读取", "\uE8C8", true));
        nav.Children.Add(NavItem("数据总览", "\uE9D2", false));
        nav.Children.Add(NavItem("云端同步", "\uE753", false));
        panel.Children.Add(nav);

        var tips = new Border
        {
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(14),
            Background = Brush("#302E49"),
            BorderBrush = Brush("#454160"),
            BorderThickness = new Thickness(1),
        };
        Grid.SetRow(tips, 2);
        var tipContent = new StackPanel { Spacing = 6 };
        tipContent.Children.Add(Text("本地优先 · UID 隔离", 11, "#DED9FF", FontWeights.SemiBold));
        tipContent.Children.Add(Text("记录先保存在本机；服务离线时进入队列，下次启动自动补传。", 9, "#9A97AF", null, TextAlignment.Left, VerticalAlignment.Top, true));
        tips.Child = tipContent;
        panel.Children.Add(tips);

        return new Border { Background = Gradient("#24233A", "#1D1C31"), Child = panel };
    }

    private FrameworkElement BuildMainContent()
    {
        var content = new StackPanel { Spacing = 16, Padding = new Thickness(34, 28, 34, 30), MaxWidth = 1240, HorizontalAlignment = HorizontalAlignment.Stretch };
        content.Children.Add(BuildHero());
        content.Children.Add(BuildFetchCard());
        content.Children.Add(BuildStats());
        content.Children.Add(BuildFiveStarCard());
        content.Children.Add(BuildRecordCard());
        return content;
    }

    private Grid BuildHero()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var copy = new StackPanel { Spacing = 7 };
        copy.Children.Add(Text("让每一次相遇，都有迹可循。", 26, "#25263D", FontWeights.SemiBold));
        copy.Children.Add(Text("打开游戏记录页，然后回到这里一键读取；授权地址不会上传到星迹。", 11, "#8E91A5"));
        grid.Children.Add(copy);

        var state = new Border
        {
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = new CornerRadius(16),
            Background = Brush("#FFFFFF"),
            BorderBrush = Brush("#E7E6EF"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var stateRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        stateRow.Children.Add(_connectionDot);
        stateRow.Children.Add(_connectionText);
        state.Child = stateRow;
        Grid.SetColumn(state, 1);
        grid.Children.Add(state);
        return grid;
    }

    private Border BuildFetchCard()
    {
        _gameSelector.MinWidth = 145;
        _gameSelector.Items.Add(new ComboBoxItem { Content = "原神", Tag = "genshin" });
        _gameSelector.Items.Add(new ComboBoxItem { Content = "鸣潮", Tag = "wuwa" });
        _gameSelector.SelectedIndex = 0;
        _exportButton.IsEnabled = false;
        _gameSelector.SelectionChanged += async (_, _) =>
        {
            var isGenshin = CurrentGame == "genshin";
            _importButton.IsEnabled = isGenshin;
            _exportButton.IsEnabled = isGenshin && _current?.Game == "genshin";
            var local = await _local.LoadLatestForGameAsync(CurrentGame);
            if (local is not null) ShowData(local);
        };
        _autoFetchButton.Click += AutoFetchButton_Click;
        _manualFetchButton.Click += ManualFetchButton_Click;
        _syncButton.Click += SyncButton_Click;
        _uploadAllButton.Click += UploadAllButton_Click;
        _backupButton.Click += BackupButton_Click;
        ToolTipService.SetToolTip(_uploadAllButton, "上传原神、鸣潮全部本地 UID；未登录时会先打开统一认证");
        ToolTipService.SetToolTip(_backupButton, "导出原神、鸣潮全部本地记录，不需要登录或联网");
        _logoutButton.Click += LogoutButton_Click;
        _cancelLoginButton.Click += (_, _) => _loginCancellation?.Cancel();
        _logoutButton.Visibility = Visibility.Collapsed;
        _cancelLoginButton.Visibility = Visibility.Collapsed;
        _importButton.Click += ImportButton_Click;
        _exportButton.Click += ExportButton_Click;
        _manualUrlBox.PlaceholderText = "高级方式：粘贴 getGachaLog 或唤取记录完整链接";

        var body = new StackPanel { Spacing = 15 };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var game = new StackPanel { Spacing = 7 };
        game.Children.Add(Caption("数据来源"));
        game.Children.Add(_gameSelector);
        row.Children.Add(game);

        var guide = new StackPanel { Spacing = 5, Margin = new Thickness(18, 1, 20, 0), VerticalAlignment = VerticalAlignment.Center };
        guide.Children.Add(Text("无需复制链接", 13, "#3C3E57", FontWeights.SemiBold));
        guide.Children.Add(Text("先在游戏中打开记录页，再点击右侧按钮", 10, "#9698AA"));
        Grid.SetColumn(guide, 1);
        row.Children.Add(guide);
        Grid.SetColumn(_autoFetchButton, 2);
        row.Children.Add(_autoFetchButton);
        body.Children.Add(row);

        var actions = new StackPanel { Spacing = 10 };
        var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        status.Children.Add(new FontIcon { Glyph = "\uE946", FontSize = 13, Foreground = Brush("#7B69E6") });
        status.Children.Add(_statusText);
        actions.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(_importButton);
        buttons.Children.Add(_exportButton);
        buttons.Children.Add(_backupButton);
        buttons.Children.Add(_uploadAllButton);
        buttons.Children.Add(_syncButton);
        buttons.Children.Add(_cancelLoginButton);
        buttons.Children.Add(_logoutButton);
        actions.Children.Add(buttons);
        body.Children.Add(actions);

        var advancedContent = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        advancedContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        advancedContent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _manualUrlBox.Margin = new Thickness(0, 0, 10, 0);
        advancedContent.Children.Add(_manualUrlBox);
        Grid.SetColumn(_manualFetchButton, 1);
        advancedContent.Children.Add(_manualFetchButton);
        var advanced = new StackPanel { Spacing = 8 };
        advanced.Children.Add(Caption("高级方式 · 仅在自动识别失败时粘贴完整链接"));
        advanced.Children.Add(advancedContent);
        body.Children.Add(new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(12),
            Background = Brush("#F7F5FF"),
            BorderBrush = Brush("#E5E1F8"),
            BorderThickness = new Thickness(1),
            Child = advanced,
        });
        return Card(body, 22);
    }

    private Grid BuildStats()
    {
        var grid = new Grid();
        for (var index = 0; index < 4; index++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddStat(grid, 0, "当前 UID", _uidText, "\uE77B", "#EEEAFE", "#7867E5");
        AddStat(grid, 1, "总抽数", _totalText, "\uE9D2", "#EAF4FF", "#568FD2");
        AddStat(grid, 2, "五星数量", _fiveStarText, "\uE735", "#FFF4DD", "#C28D34");
        AddStat(grid, 3, "当前垫抽", _pityText, "\uE72A", "#E5F8F2", "#43A993");
        return grid;
    }

    private Border BuildRecordCard()
    {
        var body = new StackPanel();
        var header = new Grid { Padding = new Thickness(20, 17, 20, 14) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new StackPanel { Spacing = 4 };
        title.Children.Add(Text("最近记录", 14, "#25263D", FontWeights.SemiBold));
        title.Children.Add(_recordSubtitle);
        header.Children.Add(title);
        _localStateText.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_localStateText, 1);
        header.Children.Add(_localStateText);
        body.Children.Add(header);
        body.Children.Add(new Border { Height = 1, Background = Brush("#EFEEF4") });
        _recordPanel.Children.Add(EmptyState());
        body.Children.Add(_recordPanel);
        return Card(body, 0);
    }

    private Border BuildFiveStarCard()
    {
        var body = new StackPanel { Spacing = 10, Padding = new Thickness(20, 16, 20, 16) };
        body.Children.Add(Text("最近五星记录", 13, "#25263D", FontWeights.SemiBold));
        body.Children.Add(_fiveStarPanel);
        return Card(body, 0);
    }

    private async Task InitializeAsync()
    {
        SetBusy(true);
        try
        {
            var settings = await _local.GetCloudSettingsAsync();
            UpdateIdentityUi(settings);
            var local = await _local.LoadLatestAsync();
            if (local is not null) ShowData(local);
            _serverReachable = await _sync.IsServerReachableAsync(IdentityLoginService.ApiOrigin);
            SetConnection(_serverReachable);
            if (!settings.UsesUnifiedIdentity)
            {
                _statusText.Text = local is null
                    ? "本地模式 · 点击“统一认证登录”后可同步"
                    : "本地记录已就绪 · 尚未登录云端，点击“一键上传”将自动登录并补传";
                return;
            }
            _statusText.Text = "正在检查星迹服务…";
            if (_serverReachable && !string.IsNullOrWhiteSpace(settings.AccessToken))
            {
                var uploaded = await FlushPendingAsync(settings);
                _statusText.Text = uploaded > 0 ? $"服务在线，已补传 {uploaded} 条本地记录" : local is null ? "服务在线，等待读取记录" : "本地记录已就绪";
            }
            else if (!_serverReachable)
            {
                _statusText.Text = "服务暂不可达；记录将保留在本机并等待下次启动";
            }
            else _statusText.Text = local is null ? "服务在线，等待读取记录" : "本地记录已就绪；登录后可同步";
        }
        catch (CloudAuthenticationException ex) { await HandleAuthenticationFailureAsync(ex); }
        catch (Exception ex) { _statusText.Text = $"本地初始化失败：{ex.Message}"; }
        finally { SetBusy(false); }
    }

    private async void AutoFetchButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            var progress = new Progress<string>(message => _statusText.Text = message);
            var url = await _discovery.DiscoverAsync(CurrentGame, progress);
            await FetchAndPersistAsync(url, progress);
        }
        catch (CloudAuthenticationException ex) { await HandleAuthenticationFailureAsync(ex); }
        catch (Exception ex) { _statusText.Text = $"自动读取失败：{ex.Message}"; }
        finally { SetBusy(false); }
    }

    private async void ManualFetchButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_manualUrlBox.Text)) { _statusText.Text = "请先填写记录链接"; return; }
        SetBusy(true);
        try { await FetchAndPersistAsync(_manualUrlBox.Text, new Progress<string>(message => _statusText.Text = message)); }
        catch (CloudAuthenticationException ex) { await HandleAuthenticationFailureAsync(ex); }
        catch (Exception ex) { _statusText.Text = $"读取失败：{ex.Message}"; }
        finally { SetBusy(false); }
    }

    private async Task FetchAndPersistAsync(string url, IProgress<string> progress)
    {
        var fetched = CurrentGame == "genshin"
            ? await _genshin.FetchAsync(url, progress)
            : await _wutheringWaves.FetchAsync(url, progress);
        _current = await _local.MergeAsync(fetched);
        ShowData(_current);
        await QueueOrUploadCurrentAsync();
    }

    private async Task QueueOrUploadCurrentAsync()
    {
        if (_current is null) return;
        var settings = await _local.GetCloudSettingsAsync();
        if (!settings.UsesUnifiedIdentity)
        {
            UpdateIdentityUi(settings);
            _statusText.Text = $"已保存 {_current.Records.Count} 条并加入待上传队列 · 尚未登录云端，请点击“一键上传”";
            return;
        }
        await _local.QueueSyncAsync(_current.Game, _current.Uid);
        _serverReachable = await _sync.IsServerReachableAsync(settings.BaseUrl);
        SetConnection(_serverReachable);
        if (!_serverReachable)
        {
            _statusText.Text = $"已保存在本机并加入待同步队列 · UID {_current.Uid}";
            return;
        }
        settings = await RefreshIdentityIfNeededAsync(settings);
        var count = await _sync.UploadAsync(settings.BaseUrl, settings.AccessToken, _current.Game, _current.Records);
        await _local.MarkSyncedAsync(_current.Game, _current.Uid);
        _statusText.Text = $"本地已保存，云端已同步 {count} 条 · UID {_current.Uid}";
    }

    private async void SyncButton_Click(object sender, RoutedEventArgs e)
        => await UploadLocalAsync(false);

    private async void UploadAllButton_Click(object sender, RoutedEventArgs e)
        => await UploadLocalAsync(true);

    private async Task UploadLocalAsync(bool uploadAll)
    {
        SetBusy(true);
        try
        {
            var settings = await _local.GetCloudSettingsAsync();
            if (uploadAll)
            {
                await _local.QueueAllAccountsAsync();
                if ((await _local.GetPendingAsync()).Count == 0)
                {
                    _statusText.Text = "没有可上传的本地记录，请先读取或导入记录。";
                    return;
                }
            }
            _serverReachable = await _sync.IsServerReachableAsync(IdentityLoginService.ApiOrigin);
            SetConnection(_serverReachable);
            if (!_serverReachable)
            {
                _statusText.Text = "服务暂不可达；已有令牌和记录保持不变，下次启动会自动重试";
                return;
            }
            if (!settings.UsesUnifiedIdentity)
            {
                _loginCancellation = new CancellationTokenSource();
                _cancelLoginButton.Visibility = Visibility.Visible;
                _statusText.Text = "请在浏览器中完成统一认证，完成后会自动返回客户端…";
                var login = await _identity.LoginAsync(_loginCancellation.Token);
                await _local.SaveIdentitySettingsAsync(login);
                settings = await _local.GetCloudSettingsAsync();
                UpdateIdentityUi(settings);
                await _local.QueueAllAccountsAsync();
            }
            if (_current is not null) await _local.QueueSyncAsync(_current.Game, _current.Uid);
            var uploaded = await FlushPendingAsync(settings);
            _statusText.Text = $"已登录 {settings.Username} · 云端已确认 {uploaded} 条记录，待上传队列已完成";
        }
        catch (OperationCanceledException) { _statusText.Text = "已取消统一认证，本地记录保持不变。"; }
        catch (CloudAuthenticationException ex) { await HandleAuthenticationFailureAsync(ex); }
        catch (Exception ex) { _statusText.Text = $"同步失败，记录仍保留在本机：{ex.Message}"; }
        finally
        {
            _cancelLoginButton.Visibility = Visibility.Collapsed;
            _loginCancellation?.Dispose();
            _loginCancellation = null;
            SetBusy(false);
        }
    }

    private async void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            var settings = await _local.GetCloudSettingsAsync();
            await _local.ClearCloudLoginAsync();
            UpdateIdentityUi(await _local.GetCloudSettingsAsync());
            _statusText.Text = "已退出客户端登录，本地记录和待同步队列已保留。";
            await _identity.RevokeAsync(settings);
        }
        catch (Exception ex) { _statusText.Text = $"退出登录失败：{ex.Message}"; }
        finally { SetBusy(false); }
    }

    private void UpdateIdentityUi(CloudSettings settings)
    {
        _hasUnifiedLogin = settings.UsesUnifiedIdentity;
        if (_syncButton.Content is StackPanel panel && panel.Children.LastOrDefault() is TextBlock label)
            label.Text = settings.UsesUnifiedIdentity ? "同步记录" : "统一认证登录";
        _logoutButton.Visibility = settings.UsesUnifiedIdentity ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(_syncButton, settings.UsesUnifiedIdentity
            ? $"当前账号：{settings.Username}" : "使用 Ishidadao ID 登录星迹");
        SetConnection(_connectionOnline);
    }

    private async Task HandleAuthenticationFailureAsync(CloudAuthenticationException error)
    {
        await _local.ClearCloudLoginAsync();
        UpdateIdentityUi(await _local.GetCloudSettingsAsync());
        _statusText.Text = error.Message;
    }

    private async void BackupButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            var accounts = await _local.GetAllAccountsAsync();
            if (accounts.Count == 0)
            {
                _statusText.Text = "没有可导出的本地记录。";
                return;
            }
            var picker = new FileSavePicker { SuggestedFileName = $"StarTrace-backup-{DateTime.Now:yyyyMMdd-HHmmss}" };
            picker.FileTypeChoices.Add("星迹完整备份 JSON", [".json"]);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            await File.WriteAllTextAsync(file.Path, BackupService.Serialize(accounts));
            _statusText.Text = $"已导出 {accounts.Count} 个游戏账号、{accounts.Sum(account => account.Records.Count)} 条记录（不含登录凭据）";
        }
        catch (Exception ex) { _statusText.Text = $"完整导出失败：{ex.Message}"; }
        finally { SetBusy(false); }
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            var groups = await _uigf.PickAndImportAsync(this);
            if (groups.Count == 0) return;
            var settings = await _local.GetCloudSettingsAsync();
            foreach (var group in groups)
            {
                var merged = await _local.MergeAsync(group);
                _current ??= merged;
                if (settings.UsesUnifiedIdentity) await _local.QueueSyncAsync(merged.Game, merged.Uid);
            }
            _current = await _local.LoadAccountAsync(groups[0].Game, groups[0].Uid);
            if (_current is not null) ShowData(_current);
            var imported = groups.Sum(group => group.Records.Count);
            if (settings.UsesUnifiedIdentity && await _sync.IsServerReachableAsync(settings.BaseUrl))
            {
                var uploaded = await FlushPendingAsync(settings);
                _statusText.Text = $"UIGF 导入 {imported} 条，云端同步 {uploaded} 条";
            }
            else _statusText.Text = $"UIGF 已按 {groups.Count} 个 UID 分组导入本机 · {imported} 条";
        }
        catch (CloudAuthenticationException ex) { await HandleAuthenticationFailureAsync(ex); }
        catch (Exception ex) { _statusText.Text = $"UIGF 导入失败：{ex.Message}"; }
        finally { SetBusy(false); }
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_current?.Game != "genshin") { _statusText.Text = "请先读取或导入原神记录"; return; }
        SetBusy(true);
        try { if (await _uigf.PickAndExportAsync(this, _current)) _statusText.Text = $"UIGF v4.2 已导出 · UID {_current.Uid}"; }
        catch (Exception ex) { _statusText.Text = $"UIGF 导出失败：{ex.Message}"; }
        finally { SetBusy(false); }
    }

    private async Task<int> FlushPendingAsync(CloudSettings settings)
    {
        if ((await _local.GetPendingAsync()).Count == 0) return 0;
        settings = await RefreshIdentityIfNeededAsync(settings);
        return await new CloudTransferService(_local, _sync).UploadPendingAsync(
            settings, new Progress<string>(message => _statusText.Text = message));
    }

    private async Task<CloudSettings> RefreshIdentityIfNeededAsync(CloudSettings settings)
    {
        if (!settings.UsesUnifiedIdentity)
            throw new InvalidOperationException("请先统一认证登录；本地记录保持不变。");
        if (settings.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return settings;
        var renewed = await _identity.RefreshAsync(settings.RefreshToken, settings.Username);
        await _local.SaveIdentitySettingsAsync(renewed);
        return await _local.GetCloudSettingsAsync();
    }

    private void ShowData(FetchResult data)
    {
        _current = data;
        _gameSelector.SelectedIndex = data.Game == "genshin" ? 0 : 1;
        _uidText.Text = data.Uid;
        _totalText.Text = data.Records.Count.ToString();
        _fiveStarText.Text = data.Records.Count(record => record.Rarity == 5).ToString();
        var pity = 0;
        var primaryPool = data.Game == "genshin" ? "301" : "1";
        foreach (var record in data.Records.Where(record => GachaPools.Normalize(data.Game, record.PoolType) == primaryPool))
        {
            if (record.Rarity == 5) break;
            pity++;
        }
        _pityText.Text = pity.ToString();
        _localStateText.Text = $"本机 · {data.Records.Count} 条";
        _recordSubtitle.Text = $"{(data.Game == "genshin" ? "原神" : "鸣潮")} · UID {data.Uid} · 最新记录优先";
        _exportButton.IsEnabled = data.Game == "genshin";
        RenderRecentFiveStars(data.Game, data.Records);
        RenderRecords(data.Game, data.Records);
    }

    private void RenderRecords(string game, IReadOnlyList<GachaRecord> records)
    {
        _recordPanel.Children.Clear();
        if (records.Count == 0)
        {
            _recordPanel.Children.Add(EmptyState());
            return;
        }
        foreach (var record in records.Take(120))
        {
            var rowGrid = new Grid
            {
                Padding = new Thickness(18, 10, 18, 10),
            };
            var row = new Border
            {
                BorderBrush = Brush("#F0EFF5"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = rowGrid,
            };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            rowGrid.Children.Add(CreateRecordIcon(game, record, 42));
            var item = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            item.Children.Add(Text(record.ItemName, 11, "#303249", FontWeights.SemiBold));
            item.Children.Add(Text($"{record.ItemType} · {GachaPools.DisplayName(game, record.PoolType)}", 9, "#A0A2B3"));
            Grid.SetColumn(item, 1);
            rowGrid.Children.Add(item);
            var rarity = Text(record.RarityText, 11, record.Rarity == 5 ? "#D5A044" : record.Rarity == 4 ? "#8874DF" : "#9EA0B1");
            rarity.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(rarity, 2);
            rowGrid.Children.Add(rarity);
            var time = Text(record.PulledAt, 9, "#A0A2B3");
            time.HorizontalAlignment = HorizontalAlignment.Right;
            time.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(time, 3);
            rowGrid.Children.Add(time);
            _recordPanel.Children.Add(row);
        }
        if (records.Count > 120)
        {
            var note = Text($"为保持流畅，仅显示最近 120 条；本机已完整保存 {records.Count} 条。", 9, "#9B9DAE", null, TextAlignment.Center);
            note.Margin = new Thickness(0, 14, 0, 14);
            _recordPanel.Children.Add(note);
        }
    }

    private void RenderRecentFiveStars(string game, IReadOnlyList<GachaRecord> records)
    {
        _fiveStarPanel.Children.Clear();
        var fiveStars = records.Where(record => record.Rarity == 5).Take(8).ToList();
        if (fiveStars.Count == 0)
        {
            _fiveStarPanel.Children.Add(Text("暂无五星记录", 10, "#9B9DAE"));
            return;
        }

        foreach (var record in fiveStars)
        {
            var card = new StackPanel { Width = 112, Spacing = 5 };
            card.Children.Add(CreateRecordIcon(game, record, 48));
            card.Children.Add(Text(record.ItemName, 10, "#303249", FontWeights.SemiBold));
            card.Children.Add(Text(GachaPools.DisplayName(game, record.PoolType), 8, "#9B9DAE"));
            _fiveStarPanel.Children.Add(card);
        }
    }

    private Border CreateRecordIcon(string game, GachaRecord record, double size)
    {
        var isWeapon = ItemIconService.IsWeapon(record);
        var fallbackColor = record.Rarity == 5 ? "#B98530" : record.Rarity == 4 ? "#7764DB" : "#4A84C8";
        var background = record.Rarity == 5 ? "#FFF3DC" : record.Rarity == 4 ? "#F0ECFF" : "#EAF4FF";
        var fallback = Text(
            record.ItemName.Length > 0 ? record.ItemName[..1] : "?",
            size >= 48 ? 16 : 14,
            fallbackColor,
            FontWeights.SemiBold,
            TextAlignment.Center,
            VerticalAlignment.Center);
        fallback.HorizontalAlignment = HorizontalAlignment.Center;

        var image = new Image
        {
            Width = size,
            Height = size,
            Stretch = isWeapon ? Stretch.Uniform : Stretch.UniformToFill,
            Opacity = 0,
        };
        var holder = new Grid
        {
            Width = size,
            Height = size,
            Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, size, size),
            },
        };
        holder.Children.Add(image);
        holder.Children.Add(fallback);
        var border = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 3.5),
            Background = Brush(background),
            Child = holder,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _ = LoadRecordIconAsync(game, record, image, fallback, _iconCancellation.Token);
        return border;
    }

    private async Task LoadRecordIconAsync(
        string game,
        GachaRecord record,
        Image image,
        TextBlock fallback,
        CancellationToken token)
    {
        try
        {
            var url = await _icons.ResolveIconUrlAsync(game, record, token);
            if (string.IsNullOrWhiteSpace(url)) return;
            var bytes = await _icons.GetImageBytesAsync(url, token);
            if (bytes is null || bytes.Length == 0 || token.IsCancellationRequested) return;

            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                // DataWriter owns its output stream by default. Detach it before
                // disposal so BitmapImage can seek and decode the cached bytes.
                writer.DetachStream();
            }
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            if (token.IsCancellationRequested) return;
            image.Source = bitmap;
            image.Opacity = 1;
            fallback.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private static FrameworkElement EmptyState()
    {
        var stack = new StackPanel { Spacing = 7, Padding = new Thickness(20, 35, 20, 35), HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(new FontIcon { Glyph = "\uE8A5", FontSize = 22, Foreground = Brush("#8A78E2"), HorizontalAlignment = HorizontalAlignment.Center });
        stack.Children.Add(Text("尚无抽卡记录", 11, "#777A91", FontWeights.SemiBold, TextAlignment.Center));
        stack.Children.Add(Text("打开游戏记录页后，使用上方的一键读取", 9, "#AAABBA", null, TextAlignment.Center));
        return stack;
    }

    private void SetBusy(bool busy)
    {
        _autoFetchButton.IsEnabled = !busy;
        _manualFetchButton.IsEnabled = !busy;
        _syncButton.IsEnabled = !busy;
        _uploadAllButton.IsEnabled = !busy;
        _backupButton.IsEnabled = !busy;
        _gameSelector.IsEnabled = !busy;
        _logoutButton.IsEnabled = !busy;
        _importButton.IsEnabled = !busy && CurrentGame == "genshin";
        _exportButton.IsEnabled = !busy && _current?.Game == "genshin";
        if (busy) _statusText.Text = "处理中…";
    }

    private void SetConnection(bool? online)
    {
        _connectionOnline = online;
        _connectionDot.Fill = Brush(online switch { true when _hasUnifiedLogin => "#59C5A8", true => "#D59A58", false => "#D59A58", null => "#999CAF" });
        _connectionText.Text = online switch
        {
            true when _hasUnifiedLogin => "服务在线 · 已登录",
            true => "服务在线 · 未登录云端",
            false => "离线模式 · 本地保存",
            null => "未登录 · 本地模式",
        };
    }

    private string CurrentGame => (_gameSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "genshin";

    private void TrySetMica()
    {
        try { SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt }; } catch { }
    }

    private void TrySetAppIcon()
    {
        try
        {
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "StarTrace.ico");
            if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        }
        catch { }
    }

    private static Border Card(UIElement child, double padding) => new()
    {
        Padding = new Thickness(padding),
        CornerRadius = new CornerRadius(18),
        Background = Brush("#FBFFFFFF"),
        BorderBrush = Brush("#E7E6EF"),
        BorderThickness = new Thickness(1),
        Child = child,
    };

    private static Border NavItem(string label, string glyph, bool active)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, Foreground = Brush(active ? "#B8ABFF" : "#86839D") });
        row.Children.Add(Text(label, 11, active ? "#FFFFFF" : "#ABA9BD", active ? FontWeights.SemiBold : null));
        return new Border
        {
            Padding = new Thickness(13, 11, 13, 11),
            CornerRadius = new CornerRadius(11),
            Background = active ? Brush("#3A3759") : new SolidColorBrush(Colors.Transparent),
            Child = row,
        };
    }

    private static void AddStat(Grid grid, int column, string label, TextBlock value, string glyph, string tile, string accent)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 13 };
        row.Children.Add(new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(12),
            Background = Brush(tile),
            Child = new FontIcon { Glyph = glyph, FontSize = 16, Foreground = Brush(accent), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        });
        var copy = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        copy.Children.Add(Caption(label));
        copy.Children.Add(value);
        row.Children.Add(copy);
        var card = Card(row, 17);
        card.Margin = new Thickness(column == 0 ? 0 : 6, 0, column == 3 ? 0 : 6, 0);
        Grid.SetColumn(card, column);
        grid.Children.Add(card);
    }

    private static Button PrimaryButton(string label, string glyph)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 13 });
        row.Children.Add(new TextBlock { Text = label });
        return new Button
        {
            Content = row,
            Padding = new Thickness(17, 10, 17, 10),
            CornerRadius = new CornerRadius(11),
            Background = Gradient("#8977EF", "#6D5BD8"),
            Foreground = Brush("#FFFFFF"),
            BorderThickness = new Thickness(0),
            FontWeight = FontWeights.SemiBold,
            FontSize = 11,
        };
    }

    private static Button SecondaryButton(string label) => new()
    {
        Content = label,
        Padding = new Thickness(13, 8, 13, 8),
        CornerRadius = new CornerRadius(10),
        Background = Brush("#F3F1FA"),
        Foreground = Brush("#66627F"),
        BorderBrush = Brush("#E4E1F1"),
        BorderThickness = new Thickness(1),
        FontSize = 10,
    };

    private static TextBlock Caption(string text) => Text(text, 9, "#9698AA", FontWeights.SemiBold);

    private static TextBlock Text(
        string text,
        double size,
        string color,
        FontWeight? weight = null,
        TextAlignment alignment = TextAlignment.Left,
        VerticalAlignment vertical = VerticalAlignment.Top,
        bool wrap = false) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = Brush(color),
        FontWeight = weight ?? FontWeights.Normal,
        TextAlignment = alignment,
        VerticalAlignment = vertical,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
    };

    private static SolidColorBrush Brush(string hex)
    {
        var value = hex.TrimStart('#');
        if (value.Length == 6) value = "FF" + value;
        var number = Convert.ToUInt32(value, 16);
        return new SolidColorBrush(ColorHelper.FromArgb(
            (byte)(number >> 24), (byte)(number >> 16), (byte)(number >> 8), (byte)number));
    }

    private static LinearGradientBrush Gradient(string from, string to)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        brush.GradientStops.Add(new GradientStop { Color = Brush(from).Color, Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = Brush(to).Color, Offset = 1 });
        return brush;
    }
}
