using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Threading;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using WpfButton = System.Windows.Controls.Button;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfCursors = System.Windows.Input.Cursors;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfRichTextBox = System.Windows.Controls.RichTextBox;
using IoPath = System.IO.Path;

namespace ServerChanDesktop;

public partial class MainWindow : Window
{
    private const string InboxApiBase = "https://bot.ftqq.com";
    private const string HitokotoApi = "https://v1.hitokoto.cn/?encode=json&charset=utf-8&max_length=36";
    private const string AutoStartRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AutoStartValueName = "HurkinInbox";

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly MarkdownPipeline _markdownPipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly ObservableCollection<InboxMessageItem> _visibleMessages = [];
    private readonly DispatcherTimer _inboxRefreshTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly string _storageDirectory;
    private readonly string _settingsPath;

    private AppState _appState = new();
    private InboxFilter _currentFilter = InboxFilter.All;
    private List<InboxMessageItem> _allMessages = [];
    private HashSet<string> _knownMessageIds = [];
    private bool _isRefreshingInbox;
    private bool _isApplyingStateToUi;
    private bool _isExplicitExitRequested;
    private string _searchKeyword = string.Empty;
    private NotificationWindow? _activeNotificationWindow;
    private Forms.NotifyIcon? _trayIcon;

    public MainWindow()
    {
        InitializeComponent();
        ConfigureTrayIcon();

        _storageDirectory = ResolveStorageDirectory();
        _settingsPath = IoPath.Combine(_storageDirectory, "state.json");
        Directory.CreateDirectory(_storageDirectory);

        InboxList.ItemsSource = _visibleMessages;
        _inboxRefreshTimer.Tick += InboxRefreshTimer_Tick;
        LoadState();
        BindStateToUi();
        UpdateFilterButtonStates();
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateWindowChrome();
        UpdateAutoRefreshTimer();
        RenderMarkdownToDocument("### 选择一条消息查看详情\n\n右侧将按照 Markdown 方式显示正文。");
        EnsureAutoStartRegistered();
        _ = LoadDailyQuoteAsync();

        if (!string.IsNullOrWhiteSpace(_appState.SendKey))
        {
            await RefreshInboxAsync(forceLogin: false, isAutomatic: false);
        }
    }

    private async void InboxRefreshTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshInboxAsync(forceLogin: false, isAutomatic: true);
    }

    private void LoadState()
    {
        if (!File.Exists(_settingsPath))
        {
            _appState = AppState.CreateDefault();
            return;
        }

        try
        {
            var json = File.ReadAllText(_settingsPath, Encoding.UTF8);
            _appState = JsonSerializer.Deserialize<AppState>(json, _jsonOptions) ?? AppState.CreateDefault();
        }
        catch
        {
            _appState = AppState.CreateDefault();
        }
    }

    private void BindStateToUi()
    {
        _isApplyingStateToUi = true;
        SendKeyBox.Text = _appState.SendKey;
        AutoRefreshBox.IsChecked = _appState.AutoRefreshInbox;
        PopupNotificationsBox.IsChecked = _appState.PopupNotificationsEnabled;
        CloseToTrayBox.IsChecked = _appState.CloseToTrayEnabled;
        SearchBox.Text = _searchKeyword;
        HeaderStatusTextBlock.Text = string.IsNullOrWhiteSpace(_appState.LastInboxSyncLocal) ? "待登录" : "已缓存登录";
        StatusTextBlock.Text = "已载入本地配置。";
        InboxCountTextBlock.Text = "消息列表";
        InboxMetaTextBlock.Text = string.IsNullOrWhiteSpace(_appState.LastInboxSyncLocal)
            ? "尚未同步"
            : $"上次同步 {_appState.LastInboxSyncLocal}";
        DetailFooterTextBlock.Text = "未加载消息。";
        _isApplyingStateToUi = false;
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        _appState.AuthToken = string.Empty;
        await RefreshInboxAsync(forceLogin: true, isAutomatic: false);
    }

    private async void RefreshInbox_Click(object sender, RoutedEventArgs e)
    {
        await RefreshInboxAsync(forceLogin: false, isAutomatic: false);
    }

    private async Task RefreshInboxAsync(bool forceLogin, bool isAutomatic)
    {
        if (_isRefreshingInbox)
        {
            return;
        }

        SaveUiToState();
        if (string.IsNullOrWhiteSpace(_appState.SendKey))
        {
            SetStatus("请先填写 SendKey。", "待登录");
            return;
        }

        if (forceLogin)
        {
            _appState.AuthToken = string.Empty;
        }

        _isRefreshingInbox = true;
        Cursor = WpfCursors.Wait;
        SetStatus(isAutomatic ? "后台同步中..." : "正在登录并同步收件箱...", "同步中");

        try
        {
            var token = await EnsureTokenAsync(forceRefresh: forceLogin);
            var messages = await LoadInboxMessagesAsync(token);
            var previousId = (InboxList.SelectedItem as InboxMessageItem)?.UniqueId;
            var hadPreviousSync = _knownMessageIds.Count > 0;
            var allIncomingMessages = messages
                .Where(m => !_knownMessageIds.Contains(m.UniqueId))
                .ToList();
            var incomingMessages = allIncomingMessages.Take(3).ToList();

            var newIds = messages.Select(m => m.UniqueId).Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet();
            var incomingCount = allIncomingMessages.Count;

            _knownMessageIds = newIds;
            _allMessages = messages;
            ApplyInboxFilter();

            _appState.LastInboxSyncLocal = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            await SaveStateAsync();

            RestoreSelection(previousId);
            if (InboxList.SelectedItem is null && _visibleMessages.Count > 0)
            {
                InboxList.SelectedIndex = 0;
            }

            InboxMetaTextBlock.Text = incomingCount > 0
                ? $"刚同步到 {incomingCount} 条新消息，当前共 {_allMessages.Count} 条"
                : $"上次同步 {_appState.LastInboxSyncLocal}，当前共 {_allMessages.Count} 条";

            SetStatus(
                incomingCount > 0 ? $"同步完成，新增 {incomingCount} 条消息。" : "同步完成。",
                "已登录");

            if (hadPreviousSync && incomingCount > 0 && _appState.PopupNotificationsEnabled)
            {
                ShowTopRightNotification(incomingMessages, incomingCount);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"收件箱同步失败：{ex.Message}", "同步失败");
        }
        finally
        {
            Cursor = WpfCursors.Arrow;
            _isRefreshingInbox = false;
        }
    }

    private async Task<string> EnsureTokenAsync(bool forceRefresh)
    {
        if (!forceRefresh && !string.IsNullOrWhiteSpace(_appState.AuthToken))
        {
            return _appState.AuthToken;
        }

        if (string.IsNullOrWhiteSpace(_appState.SendKey))
        {
            throw new InvalidOperationException("SendKey 为空。");
        }

        var payload = new Dictionary<string, string> { ["sendkey"] = _appState.SendKey };
        using var response = await _httpClient.PostAsync(
            $"{InboxApiBase}/login/by/sendkey",
            new FormUrlEncodedContent(payload));

        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        EnsureApiSuccess(response, document.RootElement);

        var token = TryReadString(document.RootElement, "token")
                    ?? TryReadString(document.RootElement, "data", "token")
                    ?? string.Empty;
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("服务端未返回 token。");
        }

        _appState.AuthToken = token;
        await SaveStateAsync();
        return token;
    }

    private async Task<List<InboxMessageItem>> LoadInboxMessagesAsync(string token, bool hasRetried = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{InboxApiBase}/sc3/push/index");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

        using var response = await _httpClient.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);

        if (ShouldRefreshToken(response, document.RootElement) && !hasRetried)
        {
            _appState.AuthToken = string.Empty;
            await SaveStateAsync();
            var freshToken = await EnsureTokenAsync(forceRefresh: true);
            return await LoadInboxMessagesAsync(freshToken, hasRetried: true);
        }

        EnsureApiSuccess(response, document.RootElement);

        var arrayElement = FindFirstArray(document.RootElement, "pushes", "data", "list", "rows");
        if (!arrayElement.HasValue)
        {
            return [];
        }

        var messages = new List<InboxMessageItem>();
        foreach (var item in arrayElement.Value.EnumerateArray())
        {
            var message = ParseInboxMessage(item);
            message.IsRead = message.IsRead || _appState.ReadMessageIds.Contains(message.UniqueId);
            message.IsStarred = message.IsStarred || _appState.StarredMessageIds.Contains(message.UniqueId);
            messages.Add(message);
        }

        return messages
            .OrderByDescending(m => m.SortTime ?? DateTime.MinValue)
            .ThenByDescending(m => m.UniqueId)
            .ToList();
    }

    private void ApplyInboxFilter()
    {
        IEnumerable<InboxMessageItem> source = _allMessages;
        source = _currentFilter switch
        {
            InboxFilter.Unread => source.Where(m => !m.IsRead),
            InboxFilter.Starred => source.Where(m => m.IsStarred),
            _ => source
        };

        if (!string.IsNullOrWhiteSpace(_searchKeyword))
        {
            source = source.Where(m =>
                ContainsSearchText(m.Title, _searchKeyword) ||
                ContainsSearchText(m.Summary, _searchKeyword) ||
                ContainsSearchText(m.Body, _searchKeyword));
        }

        _visibleMessages.Clear();
        foreach (var item in source)
        {
            _visibleMessages.Add(item);
        }

        var label = _currentFilter switch
        {
            InboxFilter.Unread => "未读",
            InboxFilter.Starred => "加星",
            _ => "全部"
        };
        InboxCountTextBlock.Text = $"{label}消息 ({_visibleMessages.Count})";
        UpdateFilterButtonStates();
    }

    private static bool ContainsSearchText(string? source, string keyword)
    {
        return !string.IsNullOrWhiteSpace(source) &&
               source.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private async void InboxList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (InboxList.SelectedItem is not InboxMessageItem item)
        {
            return;
        }

        DetailTitleTextBlock.Text = item.Title;
        DetailMetaTextBlock.Text = $"{item.StateLabel} | {item.DisplayTime}";
        RenderMarkdownToDocument(string.IsNullOrWhiteSpace(item.Body) ? item.Summary : item.Body);

        if (!item.IsRead)
        {
            await UpdateReadStateAsync(item, isRead: true, statusText: "已打开消息，已标记为已读。");
            return;
        }

        DetailFooterTextBlock.Text = "已显示消息正文。";
    }

    private void FilterAll_Click(object sender, RoutedEventArgs e)
    {
        _currentFilter = InboxFilter.All;
        ApplyInboxFilter();
    }

    private void FilterUnread_Click(object sender, RoutedEventArgs e)
    {
        _currentFilter = InboxFilter.Unread;
        ApplyInboxFilter();
    }

    private void FilterStarred_Click(object sender, RoutedEventArgs e)
    {
        _currentFilter = InboxFilter.Starred;
        ApplyInboxFilter();
    }

    private async void ToggleReadSelected_Click(object sender, RoutedEventArgs e)
    {
        if (InboxList.SelectedItem is not InboxMessageItem item)
        {
            DetailFooterTextBlock.Text = "请先选择一条消息。";
            return;
        }

        await UpdateReadStateAsync(item, !item.IsRead, item.IsRead ? "已标记为未读。" : "已标记为已读。");
    }

    private async void ToggleStarSelected_Click(object sender, RoutedEventArgs e)
    {
        if (InboxList.SelectedItem is not InboxMessageItem item)
        {
            DetailFooterTextBlock.Text = "请先选择一条消息。";
            return;
        }

        item.IsStarred = !item.IsStarred;
        if (item.IsStarred)
        {
            _appState.StarredMessageIds.Add(item.UniqueId);
        }
        else
        {
            _appState.StarredMessageIds.Remove(item.UniqueId);
        }

        await SaveStateAsync();
        ApplyInboxFilter();
        RestoreSelection(item.UniqueId);
        DetailMetaTextBlock.Text = $"{item.StateLabel} | {item.DisplayTime}";
        DetailFooterTextBlock.Text = item.IsStarred ? "已加入本地加星。" : "已取消本地加星。";
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (InboxList.SelectedItem is not InboxMessageItem item)
        {
            DetailFooterTextBlock.Text = "请先选择一条消息。";
            return;
        }

        try
        {
            SetStatus("正在删除消息...", "处理中");
            var token = await EnsureTokenAsync(forceRefresh: false);
            await DeleteInboxMessageAsync(token, item);

            _allMessages.RemoveAll(m => m.UniqueId == item.UniqueId);
            _visibleMessages.Remove(item);
            _knownMessageIds.Remove(item.UniqueId);
            _appState.ReadMessageIds.Remove(item.UniqueId);
            _appState.StarredMessageIds.Remove(item.UniqueId);
            await SaveStateAsync();

            ApplyInboxFilter();
            if (_visibleMessages.Count > 0)
            {
                InboxList.SelectedIndex = 0;
            }
            else
            {
                DetailTitleTextBlock.Text = "选择一条消息查看详情";
                DetailMetaTextBlock.Text = "这里会显示当前消息的状态和时间。";
                RenderMarkdownToDocument("### 暂无消息\n\n当前筛选条件下已经没有可显示的消息。");
                DetailFooterTextBlock.Text = "消息已删除。";
            }

            InboxMetaTextBlock.Text = $"上次同步 {_appState.LastInboxSyncLocal}，当前共 {_allMessages.Count} 条";
            SetStatus("消息删除成功。", "已登录");
        }
        catch (Exception ex)
        {
            DetailFooterTextBlock.Text = $"删除失败：{ex.Message}";
            SetStatus($"消息删除失败：{ex.Message}", "删除失败");
        }
    }

    private async Task UpdateReadStateAsync(InboxMessageItem item, bool isRead, string statusText)
    {
        item.IsRead = isRead;
        if (isRead)
        {
            _appState.ReadMessageIds.Add(item.UniqueId);
        }
        else
        {
            _appState.ReadMessageIds.Remove(item.UniqueId);
        }

        await SaveStateAsync();
        ApplyInboxFilter();
        RestoreSelection(item.UniqueId);
        DetailMetaTextBlock.Text = $"{item.StateLabel} | {item.DisplayTime}";
        DetailFooterTextBlock.Text = statusText;
    }

    private async Task DeleteInboxMessageAsync(string token, InboxMessageItem item)
    {
        var identifiers = new[]
        {
            ("id", item.RawId),
            ("push_id", item.RawId),
            ("message_id", item.RawId),
            ("id", item.UniqueId)
        }
        .Where(p => !string.IsNullOrWhiteSpace(p.Item2))
        .Distinct()
        .ToList();

        Exception? lastError = null;
        foreach (var (key, value) in identifiers)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{InboxApiBase}/sc3/push/remove");
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
                request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { [key] = value });

                using var response = await _httpClient.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);

                if (ShouldRefreshToken(response, document.RootElement))
                {
                    _appState.AuthToken = string.Empty;
                    await SaveStateAsync();
                    var freshToken = await EnsureTokenAsync(forceRefresh: true);
                    await DeleteInboxMessageWithPayloadAsync(freshToken, key, value);
                    return;
                }

                EnsureApiSuccess(response, document.RootElement);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw lastError ?? new InvalidOperationException("服务端未接受删除请求。");
    }

    private async Task DeleteInboxMessageWithPayloadAsync(string token, string key, string value)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{InboxApiBase}/sc3/push/remove");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { [key] = value });

        using var response = await _httpClient.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        EnsureApiSuccess(response, document.RootElement);
    }

    private void RestoreSelection(string? uniqueId)
    {
        if (!string.IsNullOrWhiteSpace(uniqueId))
        {
            var selected = _visibleMessages.FirstOrDefault(m => m.UniqueId == uniqueId);
            if (selected is not null)
            {
                InboxList.SelectedItem = selected;
                return;
            }
        }

        if (_visibleMessages.Count > 0)
        {
            InboxList.SelectedIndex = 0;
        }
    }

    private async void AutoRefreshBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_isApplyingStateToUi)
        {
            return;
        }

        _appState.AutoRefreshInbox = AutoRefreshBox.IsChecked == true;
        UpdateAutoRefreshTimer();
        await SaveStateAsync();
    }

    private async void PopupNotificationsBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_isApplyingStateToUi)
        {
            return;
        }

        _appState.PopupNotificationsEnabled = PopupNotificationsBox.IsChecked == true;
        await SaveStateAsync();
    }

    private async void CloseToTrayBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (_isApplyingStateToUi)
        {
            return;
        }

        _appState.CloseToTrayEnabled = CloseToTrayBox.IsChecked == true;
        UpdateTrayIconVisibility();
        await SaveStateAsync();
    }

    private void SearchBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isApplyingStateToUi)
        {
            return;
        }

        _searchKeyword = SearchBox.Text.Trim();
        ApplyInboxFilter();
        if (_visibleMessages.Count > 0)
        {
            InboxList.SelectedIndex = 0;
        }
    }

    private void UpdateAutoRefreshTimer()
    {
        if (_appState.AutoRefreshInbox)
        {
            _inboxRefreshTimer.Start();
        }
        else
        {
            _inboxRefreshTimer.Stop();
        }
    }

    private void UpdateFilterButtonStates()
    {
        HighlightFilterButton(FilterAllButton, _currentFilter == InboxFilter.All);
        HighlightFilterButton(FilterUnreadButton, _currentFilter == InboxFilter.Unread);
        HighlightFilterButton(FilterStarredButton, _currentFilter == InboxFilter.Starred);
    }

    private static void HighlightFilterButton(WpfButton button, bool isActive)
    {
        button.Background = isActive
            ? new SolidColorBrush(WpfColor.FromArgb(0xFF, 0x20, 0x70, 0xFF))
            : new SolidColorBrush(WpfColor.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
        button.Foreground = isActive ? WpfBrushes.White : new SolidColorBrush(WpfColor.FromRgb(0x20, 0x43, 0x6A));
        button.BorderBrush = new SolidColorBrush(WpfColor.FromArgb(0xA0, 0xFF, 0xFF, 0xFF));
    }

    private void SendKeyBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        _appState.SendKey = SendKeyBox.Text.Trim();
        _appState.AuthToken = string.Empty;
        HeaderStatusTextBlock.Text = string.IsNullOrWhiteSpace(_appState.SendKey) ? "待登录" : "待重新登录";
    }

    private async void SendKeyBox_OnKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        _appState.AuthToken = string.Empty;
        await RefreshInboxAsync(forceLogin: true, isAutomatic: false);
    }

    private void SaveUiToState()
    {
        _appState.SendKey = SendKeyBox.Text.Trim();
        _appState.AutoRefreshInbox = AutoRefreshBox.IsChecked == true;
        _appState.PopupNotificationsEnabled = PopupNotificationsBox.IsChecked == true;
        _appState.CloseToTrayEnabled = CloseToTrayBox.IsChecked == true;
    }

    private async Task SaveStateAsync()
    {
        var json = JsonSerializer.Serialize(_appState, _jsonOptions);
        await File.WriteAllTextAsync(_settingsPath, json, Encoding.UTF8);
    }

    private void SetStatus(string text, string header)
    {
        StatusTextBlock.Text = text;
        HeaderStatusTextBlock.Text = header;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsPopup.IsOpen = !SettingsPopup.IsOpen;
    }

    private void ShowTopRightNotification(List<InboxMessageItem> incomingMessages, int incomingCount)
    {
        var title = incomingCount == 1 ? "收到 1 条新消息" : $"收到 {incomingCount} 条新消息";
        var body = incomingMessages.Count switch
        {
            0 => "收件箱已更新。",
            1 => incomingMessages[0].Title,
            _ => string.Join("\n", incomingMessages.Select(m => $"• {m.Title}"))
        };

        _activeNotificationWindow?.Close();
        _activeNotificationWindow = new NotificationWindow(title, body);
        _activeNotificationWindow.Show();
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            FindVisualParent<WpfButton>(source) is not null)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        DragMove();
    }

    private void ArticleHeader_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source &&
            (FindVisualParent<WpfButton>(source) is not null || FindVisualParent<WpfRichTextBox>(source) is not null))
        {
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private static T? FindVisualParent<T>(DependencyObject current) where T : DependencyObject
    {
        var parent = current;
        while (parent is not null)
        {
            if (parent is T typed)
            {
                return typed;
            }

            parent = VisualTreeHelper.GetParent(parent);
        }

        return null;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Window_OnStateChanged(object sender, EventArgs e)
    {
        UpdateWindowChrome();
    }

    private void UpdateWindowChrome()
    {
        if (WindowState == WindowState.Maximized)
        {
            RootBorder.Margin = new Thickness(8);
            RootBorder.CornerRadius = new CornerRadius(20);
            return;
        }

        RootBorder.Margin = new Thickness(14);
        RootBorder.CornerRadius = new CornerRadius(28);
    }

    private void ConfigureTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "Hurkinの收件箱",
            Visible = false
        };

        try
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
            {
                _trayIcon.Icon = Drawing.Icon.ExtractAssociatedIcon(processPath);
            }
        }
        catch
        {
            _trayIcon.Icon = Drawing.SystemIcons.Application;
        }

        if (_trayIcon.Icon is null)
        {
            _trayIcon.Icon = Drawing.SystemIcons.Application;
        }

        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        _trayIcon.BalloonTipTitle = "Hurkinの收件箱";
        _trayIcon.BalloonTipText = "客户端仍在后台运行。双击托盘图标即可恢复窗口。";

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开", null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(ExitFromTray));
        _trayIcon.ContextMenuStrip = menu;
    }

    private void UpdateTrayIconVisibility()
    {
        if (_trayIcon is null)
        {
            return;
        }

        _trayIcon.Visible = _appState.CloseToTrayEnabled && !IsVisible;
    }

    private void MinimizeToTray()
    {
        Hide();
        ShowInTaskbar = false;
        WindowState = WindowState.Normal;

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = true;
            _trayIcon.ShowBalloonTip(1800);
        }
    }

    private void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        Activate();
        WindowState = WindowState.Normal;

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
        }
    }

    private void ExitFromTray()
    {
        _isExplicitExitRequested = true;
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
        }

        Close();
    }

    private void EnsureAutoStartRegistered()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
            {
                return;
            }

            using var runKey = Registry.CurrentUser.CreateSubKey(AutoStartRegistryPath);
            if (runKey is null)
            {
                return;
            }

            var expectedValue = $"\"{processPath}\"";
            var currentValue = runKey.GetValue(AutoStartValueName) as string;
            if (!string.Equals(currentValue, expectedValue, StringComparison.OrdinalIgnoreCase))
            {
                runKey.SetValue(AutoStartValueName, expectedValue, RegistryValueKind.String);
            }
        }
        catch
        {
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isExplicitExitRequested || !_appState.CloseToTrayEnabled)
        {
            return;
        }

        e.Cancel = true;
        MinimizeToTray();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        MainWindow_Closing(this, e);
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        base.OnClosed(e);
    }

    private async Task LoadDailyQuoteAsync()
    {
        try
        {
            using var response = await _httpClient.GetAsync(HitokotoApi);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            var payload = JsonSerializer.Deserialize<HitokotoResponse>(json, _jsonOptions);
            var quote = payload?.Hitokoto?.Trim();
            if (string.IsNullOrWhiteSpace(quote))
            {
                HeaderQuoteTextBlock.Text = "风会记住每一条真正抵达的消息。";
                return;
            }

            var suffix = string.IsNullOrWhiteSpace(payload?.From)
                ? string.Empty
                : $" · {payload!.From}";
            HeaderQuoteTextBlock.Text = $"{quote}{suffix}";
        }
        catch
        {
            HeaderQuoteTextBlock.Text = "风会记住每一条真正抵达的消息。";
        }
    }

    private void RenderMarkdownToDocument(string markdown)
    {
        var normalizedMarkdown = string.IsNullOrWhiteSpace(markdown)
            ? "_暂无正文内容_"
            : NormalizeMessageMarkdown(markdown.Replace("\r\n", "\n", StringComparison.Ordinal));
        var parsed = Markdown.Parse(normalizedMarkdown, _markdownPipeline);
        var document = new FlowDocument
        {
            PagePadding = new Thickness(0),
            Background = WpfBrushes.Transparent,
            FontFamily = new WpfFontFamily("Segoe UI, Microsoft YaHei UI"),
            FontSize = 15,
            Foreground = new SolidColorBrush(WpfColor.FromRgb(0x1F, 0x30, 0x42)),
            LineHeight = 28
        };

        foreach (var block in parsed)
        {
            AddBlock(document.Blocks, block, 0);
        }

        if (document.Blocks.Count == 0)
        {
            document.Blocks.Add(new Paragraph(new Run("暂无正文内容。")));
        }

        DetailBodyRichTextBox.Document = document;
    }

    private void AddBlock(BlockCollection blocks, Markdig.Syntax.Block block, int depth)
    {
        switch (block)
        {
            case HeadingBlock heading:
                blocks.Add(BuildHeadingParagraph(heading));
                break;
            case ParagraphBlock paragraph:
                blocks.Add(BuildParagraph(paragraph.Inline, depth, isQuote: false));
                break;
            case QuoteBlock quote:
                AddQuoteBlock(blocks, quote, depth);
                break;
            case ListBlock list:
                AddListBlock(blocks, list, depth);
                break;
            case FencedCodeBlock fencedCode:
                blocks.Add(BuildCodeParagraph(fencedCode.Lines.ToString()));
                break;
            case CodeBlock codeBlock:
                blocks.Add(BuildCodeParagraph(codeBlock.Lines.ToString()));
                break;
            case ThematicBreakBlock:
                blocks.Add(new Paragraph(new Run("────────────────────────"))
                {
                    Margin = new Thickness(0, 8, 0, 12),
                    Foreground = new SolidColorBrush(WpfColor.FromRgb(0x9F, 0xB3, 0xCA))
                });
                break;
            default:
                if (block is LeafBlock leaf && leaf.Inline is not null)
                {
                    blocks.Add(BuildParagraph(leaf.Inline, depth, isQuote: false));
                }
                break;
        }
    }

    private Paragraph BuildHeadingParagraph(HeadingBlock heading)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 14, 0, 10)
        };
        AddInlines(paragraph.Inlines, heading.Inline);

        paragraph.FontWeight = FontWeights.Bold;
        paragraph.Foreground = new SolidColorBrush(WpfColor.FromRgb(0x16, 0x3B, 0x5A));
        paragraph.FontSize = heading.Level switch
        {
            1 => 28,
            2 => 24,
            3 => 20,
            _ => 18
        };
        return paragraph;
    }

    private Paragraph BuildParagraph(ContainerInline? inline, int depth, bool isQuote)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(depth * 18, 0, 0, 12)
        };
        if (isQuote)
        {
            paragraph.Margin = new Thickness(depth * 18 + 14, 2, 0, 12);
            paragraph.Foreground = new SolidColorBrush(WpfColor.FromRgb(0x4B, 0x62, 0x7B));
            paragraph.Background = new SolidColorBrush(WpfColor.FromRgb(0xEE, 0xF4, 0xFF));
        }

        AddInlines(paragraph.Inlines, inline);
        if (paragraph.Inlines.Count == 0)
        {
            paragraph.Inlines.Add(new Run(string.Empty));
        }

        return paragraph;
    }

    private Paragraph BuildCodeParagraph(string? code)
    {
        return new Paragraph(new Run((code ?? string.Empty).TrimEnd()))
        {
            Margin = new Thickness(0, 4, 0, 14),
            Padding = new Thickness(14, 12, 14, 12),
            Background = new SolidColorBrush(WpfColor.FromRgb(0x12, 0x22, 0x35)),
            Foreground = new SolidColorBrush(WpfColor.FromRgb(0xEE, 0xF5, 0xFF)),
            FontFamily = new WpfFontFamily("Consolas, Cascadia Code, Microsoft YaHei UI"),
            FontSize = 14
        };
    }

    private void AddQuoteBlock(BlockCollection blocks, QuoteBlock quote, int depth)
    {
        foreach (var child in quote)
        {
            if (child is ParagraphBlock paragraph)
            {
                blocks.Add(BuildParagraph(paragraph.Inline, depth + 1, isQuote: true));
                continue;
            }

            AddBlock(blocks, child, depth + 1);
        }
    }

    private void AddListBlock(BlockCollection blocks, ListBlock list, int depth)
    {
        var index = int.TryParse(list.OrderedStart, out var orderedStart) ? orderedStart : 1;
        foreach (var item in list)
        {
            if (item is not ListItemBlock listItem)
            {
                continue;
            }

            var bullet = list.IsOrdered ? $"{index}. " : "• ";
            var isFirstParagraph = true;
            foreach (var child in listItem)
            {
                if (child is ParagraphBlock paragraph)
                {
                    var block = BuildParagraph(paragraph.Inline, depth + 1, isQuote: false);
                    if (isFirstParagraph)
                    {
                        var bulletRun = new Run(bullet)
                        {
                            FontWeight = FontWeights.SemiBold,
                            Foreground = new SolidColorBrush(WpfColor.FromRgb(0x16, 0x3B, 0x5A))
                        };
                        if (block.Inlines.FirstInline is null)
                        {
                            block.Inlines.Add(bulletRun);
                        }
                        else
                        {
                            block.Inlines.InsertBefore(block.Inlines.FirstInline, bulletRun);
                        }
                        isFirstParagraph = false;
                    }
                    else
                    {
                        block.Margin = new Thickness((depth + 1) * 18 + 22, 0, 0, 12);
                    }

                    blocks.Add(block);
                    continue;
                }

                AddBlock(blocks, child, depth + 2);
            }

            if (list.IsOrdered)
            {
                index++;
            }
        }
    }

    private void AddInlines(InlineCollection inlines, ContainerInline? container)
    {
        if (container is null)
        {
            return;
        }

        var current = container.FirstChild;
        while (current is not null)
        {
            switch (current)
            {
                case LiteralInline literal:
                    inlines.Add(new Run(WebUtility.HtmlDecode(literal.Content.ToString())));
                    break;
                case LineBreakInline:
                    inlines.Add(new LineBreak());
                    break;
                case CodeInline codeInline:
                    inlines.Add(new Run(codeInline.Content)
                    {
                        FontFamily = new WpfFontFamily("Consolas, Cascadia Code, Microsoft YaHei UI"),
                        Background = new SolidColorBrush(WpfColor.FromRgb(0xEE, 0xF3, 0xFA))
                    });
                    break;
                case EmphasisInline emphasis:
                    var span = new Span();
                    if (emphasis.DelimiterCount >= 2)
                    {
                        span.FontWeight = FontWeights.Bold;
                    }
                    else
                    {
                        span.FontStyle = FontStyles.Italic;
                    }

                    AddInlines(span.Inlines, emphasis);
                    inlines.Add(span);
                    break;
                case LinkInline link when !link.IsImage:
                    var hyperlink = new Hyperlink
                    {
                        Foreground = new SolidColorBrush(WpfColor.FromRgb(0x22, 0x67, 0xD8))
                    };
                    var url = link.GetDynamicUrl != null ? link.GetDynamicUrl() ?? string.Empty : link.Url ?? string.Empty;
                    if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    {
                        hyperlink.NavigateUri = uri;
                        hyperlink.RequestNavigate += Hyperlink_RequestNavigate;
                    }

                    AddInlines(hyperlink.Inlines, link);
                    if (hyperlink.Inlines.Count == 0)
                    {
                        hyperlink.Inlines.Add(new Run(url));
                    }

                    inlines.Add(hyperlink);
                    break;
                case LinkInline image when image.IsImage:
                    var imageText = image.Url ?? image.GetDynamicUrl?.Invoke() ?? string.Empty;
                    AddImageInline(inlines, imageText);
                    break;
                case ContainerInline nested:
                    AddInlines(inlines, nested);
                    break;
            }

            current = current.NextSibling;
        }
    }

    private static string NormalizeMessageMarkdown(string markdown)
    {
        var normalized = Regex.Replace(
            markdown,
            @"^\[图片\]\s*(https?://\S+)\s*$",
            match => $"![]({match.Groups[1].Value})",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);

        return Regex.Replace(
            normalized,
            @"^(https?://\S+\.(?:png|jpe?g|gif|webp|bmp)(?:\?\S*)?)\s*$",
            match => $"![]({match.Groups[1].Value})",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);
    }

    private void AddImageInline(InlineCollection inlines, string imageUrl)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
        {
            inlines.Add(new Run($"[图片] {imageUrl}")
            {
                Foreground = new SolidColorBrush(WpfColor.FromRgb(0x6E, 0x86, 0xA3))
            });
            return;
        }

        try
        {
            using var response = _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            using var stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            using var memoryStream = new MemoryStream();
            stream.CopyTo(memoryStream);
            memoryStream.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 560;
            bitmap.StreamSource = memoryStream;
            bitmap.EndInit();
            bitmap.Freeze();

            var image = new System.Windows.Controls.Image
            {
                Source = bitmap,
                MaxWidth = 560,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 10, 0, 10)
            };

            inlines.Add(new LineBreak());
            inlines.Add(new InlineUIContainer(image));
            inlines.Add(new LineBreak());
        }
        catch
        {
            inlines.Add(new Run($"[图片] {imageUrl}")
            {
                Foreground = new SolidColorBrush(WpfColor.FromRgb(0x6E, 0x86, 0xA3))
            });
        }
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    private static InboxMessageItem ParseInboxMessage(JsonElement item)
    {
        var rawId = TryReadString(item, "id")
                    ?? TryReadString(item, "_id")
                    ?? Guid.NewGuid().ToString("N");
        var body = TryReadString(item, "desp")
                   ?? TryReadString(item, "description")
                   ?? string.Empty;
        var createdText = TryReadString(item, "created_at")
                          ?? TryReadString(item, "updated_at")
                          ?? string.Empty;
        var createdAt = TryParseDate(createdText);

        return new InboxMessageItem
        {
            UniqueId = rawId,
            RawId = rawId,
            Title = TryReadString(item, "title") ?? "(无标题)",
            Body = body,
            Summary = BuildSummary(body),
            IsRead = TryReadBoolean(item, "is_read") ?? false,
            IsStarred = TryReadBoolean(item, "is_starred") ?? TryReadBoolean(item, "is_star") ?? false,
            DisplayTime = FormatDisplayTime(createdText, createdAt),
            SortTime = createdAt
        };
    }

    private static string FormatDisplayTime(string? originalText, DateTime? parsed)
    {
        if (parsed.HasValue)
        {
            return parsed.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        if (string.IsNullOrWhiteSpace(originalText))
        {
            return string.Empty;
        }

        var normalized = originalText!.Replace('T', ' ').Trim();
        var offsetIndex = normalized.IndexOf('+', StringComparison.Ordinal);
        if (offsetIndex > 0)
        {
            normalized = normalized[..offsetIndex];
        }

        var dotIndex = normalized.IndexOf('.', StringComparison.Ordinal);
        if (dotIndex > 0)
        {
            normalized = normalized[..dotIndex];
        }

        return normalized;
    }

    private static JsonElement? FindFirstArray(JsonElement root, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.Array)
            {
                return property;
            }

            if (property.ValueKind == JsonValueKind.Object)
            {
                var nested = FindFirstArray(property, propertyNames);
                if (nested.HasValue)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static bool ShouldRefreshToken(HttpResponseMessage response, JsonElement root)
    {
        if ((int)response.StatusCode == 401)
        {
            return true;
        }

        var message = ParseApiMessage(root, string.Empty);
        return message.Contains("UNAUTHORIZED", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureApiSuccess(HttpResponseMessage response, JsonElement root)
    {
        if (response.IsSuccessStatusCode && !LooksLikeApiError(root))
        {
            return;
        }

        throw new InvalidOperationException(ParseFriendlyApiMessage(root, $"HTTP {(int)response.StatusCode}"));
    }

    private static bool LooksLikeApiError(JsonElement root)
    {
        var code = TryReadInt(root, "code");
        if (code.HasValue && code.Value != 0 && code.Value != 200)
        {
            return true;
        }

        var message = ParseApiMessage(root, string.Empty);
        return !string.IsNullOrWhiteSpace(message) &&
               (message.Contains("AUTH", StringComparison.OrdinalIgnoreCase)
                || message.Contains("error", StringComparison.OrdinalIgnoreCase)
                || message.Contains("failed", StringComparison.OrdinalIgnoreCase)
                || message.Contains("invalid", StringComparison.OrdinalIgnoreCase)
                || message.Contains("失败", StringComparison.OrdinalIgnoreCase));
    }

    private static string ParseApiMessage(JsonElement root, string fallback)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("errors", out var errors) &&
            errors.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in errors.EnumerateArray())
            {
                var nested = TryReadString(item, "message");
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return TryReadString(root, "message")
               ?? TryReadString(root, "info")
               ?? TryReadString(root, "error")
               ?? fallback;
    }

    private static string ParseFriendlyApiMessage(JsonElement root, string fallback)
    {
        var raw = ParseApiMessage(root, fallback);
        if (raw.Contains("invalid sendkey", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("错误的sendkey", StringComparison.OrdinalIgnoreCase))
        {
            return "当前 SendKey 无法登录 Server3 收件箱，请确认它是账号主 SendKey。";
        }

        return raw;
    }

    private static string BuildSummary(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

        return normalized.Length > 110 ? $"{normalized[..110]}..." : normalized;
    }

    private static string? TryReadString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var part in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
            {
                return null;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.ToString(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null
        };
    }

    private static int? TryReadInt(JsonElement element, params string[] path)
    {
        var raw = TryReadString(element, path);
        return int.TryParse(raw, out var value) ? value : null;
    }

    private static bool? TryReadBoolean(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var part in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
            {
                return null;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => current.TryGetInt32(out var number) ? number != 0 : null,
            JsonValueKind.String => current.GetString() switch
            {
                "1" => true,
                "0" => false,
                var text when bool.TryParse(text, out var parsed) => parsed,
                _ => null
            },
            _ => null
        };
    }

    private static DateTime? TryParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
        {
            return parsed;
        }

        if (long.TryParse(value, out var unix))
        {
            if (unix > 1000000000000)
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(unix).LocalDateTime;
            }

            if (unix > 1000000000)
            {
                return DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
            }
        }

        return null;
    }

    private static string ResolveStorageDirectory()
    {
        var candidates = new List<string>();

        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                candidates.Add(IoPath.Combine(localAppData, "ServerChanDesktop"));
            }
        }
        catch
        {
        }

        candidates.Add(IoPath.Combine(AppContext.BaseDirectory, "data"));

        foreach (var candidate in candidates)
        {
            try
            {
                Directory.CreateDirectory(candidate);
                var probe = IoPath.Combine(candidate, ".probe");
                File.WriteAllText(probe, "ok", Encoding.UTF8);
                File.Delete(probe);
                return candidate;
            }
            catch
            {
            }
        }

        throw new InvalidOperationException("无法创建本地存储目录。");
    }
}

public sealed class AppState
{
    [JsonPropertyName("sendKey")]
    public string SendKey { get; set; } = string.Empty;

    [JsonPropertyName("authToken")]
    public string AuthToken { get; set; } = string.Empty;

    [JsonPropertyName("autoRefreshInbox")]
    public bool AutoRefreshInbox { get; set; } = true;

    [JsonPropertyName("lastInboxSyncLocal")]
    public string LastInboxSyncLocal { get; set; } = string.Empty;

    [JsonPropertyName("popupNotificationsEnabled")]
    public bool PopupNotificationsEnabled { get; set; } = true;

    [JsonPropertyName("closeToTrayEnabled")]
    public bool CloseToTrayEnabled { get; set; } = true;

    [JsonPropertyName("readMessageIds")]
    public HashSet<string> ReadMessageIds { get; set; } = [];

    [JsonPropertyName("starredMessageIds")]
    public HashSet<string> StarredMessageIds { get; set; } = [];

    public static AppState CreateDefault() => new();
}

public sealed class HitokotoResponse
{
    [JsonPropertyName("hitokoto")]
    public string? Hitokoto { get; set; }

    [JsonPropertyName("from")]
    public string? From { get; set; }
}

public sealed class InboxMessageItem
{
    public string UniqueId { get; set; } = string.Empty;

    public string RawId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public bool IsRead { get; set; }

    public bool IsStarred { get; set; }

    public string DisplayTime { get; set; } = string.Empty;

    public DateTime? SortTime { get; set; }

    public string StateLabel => IsStarred ? "加星" : IsRead ? "已读" : "未读";
}

public enum InboxFilter
{
    All,
    Unread,
    Starred
}
