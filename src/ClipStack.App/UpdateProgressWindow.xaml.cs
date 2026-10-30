using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using ClipStack.Core.Logging;

namespace ClipStack.App;

/// <summary>
/// Progress for the update download. The package is 60-70 MB, and on a slow line or through a proxy
/// that takes minutes; without this window the app looks hung and the user clicks "检查更新…" again,
/// which only runs into Velopack's update lock and reports a bogus download failure.
/// </summary>
public partial class UpdateProgressWindow : Window
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly long _totalBytes;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _lastSample;
    private long _lastBytes;
    private string _speed = "…";

    public UpdateProgressWindow(string version, long totalBytes)
    {
        InitializeComponent();
        ThemeManager.Attach(this);

        _totalBytes = totalBytes;
        Headline.Text = $"正在下载 ClipStack {version}";
        Detail.Text = totalBytes > 0
            ? $"0 / {Megabytes(totalBytes)} MB"
            : "准备下载…";
    }

    /// <summary>Cancel token for the download; closing the window also cancels.</summary>
    public CancellationToken Token => _cancellation.Token;

    /// <summary>Called from the download thread; marshals to the UI thread itself.</summary>
    public void Report(int percent)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action<int>(Report), percent);
            return;
        }

        // A restarted attempt reports from zero again; start the speed sample over with it.
        if (percent < Bar.Value)
        {
            _clock.Restart();
            _lastSample = TimeSpan.Zero;
            _lastBytes = 0;
            _speed = "…";
        }

        Bar.Value = Math.Clamp(percent, 0, 100);

        if (_totalBytes <= 0)
        {
            Detail.Text = $"{percent}%";
            return;
        }

        var done = _totalBytes * Math.Clamp(percent, 0, 100) / 100;
        Detail.Text = $"{Megabytes(done)} / {Megabytes(_totalBytes)} MB · {Speed(done)}";
    }

    private string Speed(long done)
    {
        var elapsed = _clock.Elapsed;
        var span = elapsed - _lastSample;
        if (span.TotalSeconds < 1)
        {
            return _speed;
        }

        var rate = (done - _lastBytes) / span.TotalSeconds;
        _lastSample = elapsed;
        _lastBytes = done;
        _speed = rate > 0 ? $"{Megabytes((long)rate)} MB/s" : "已暂停";
        return _speed;
    }

    private static string Megabytes(long bytes) => (bytes / 1024d / 1024d).ToString("0.0");

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        Detail.Text = "正在取消…";
        Log.Info("Update download cancelled by the user");
        _cancellation.Cancel();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        // Closing the window means the same thing as pressing 取消.
        _cancellation.Cancel();
    }
}
