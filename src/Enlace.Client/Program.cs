using Enlace.Windows;

namespace Enlace.Client;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        AppPaths.CleanClipboardFolder();
        Application.Run(new ConnectForm());
    }
}
