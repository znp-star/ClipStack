using Velopack;

namespace ClipStack.App;

/// <summary>
/// Entry point. Velopack's install / update / uninstall hooks arrive as command-line flags and must
/// be handled before any window, resource dictionary or single-instance check exists, which is
/// earlier than WPF's generated <c>Main</c> allows.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
