using System.Windows.Forms;

namespace DeskMemoryDisposableWindow;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var title = args.Length == 0 ? "Desk Memory Disposable Window" : string.Join(" ", args);
        Application.Run(new Form
        {
            Text = title,
            Width = 760,
            Height = 520,
            StartPosition = FormStartPosition.CenterScreen
        });
    }
}
