using System.Windows;
using System.Windows.Threading;

namespace ServerChanDesktop;

public partial class NotificationWindow : Window
{
    private readonly DispatcherTimer _closeTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    public NotificationWindow(string title, string body)
    {
        InitializeComponent();
        TitleTextBlock.Text = title;
        BodyTextBlock.Text = body;

        Loaded += NotificationWindow_Loaded;
        _closeTimer.Tick += CloseTimer_Tick;
        _closeTimer.Start();
    }

    private void NotificationWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 20;
        Top = workArea.Top + 20;
    }

    private void CloseTimer_Tick(object? sender, EventArgs e)
    {
        _closeTimer.Stop();
        Close();
    }
}
