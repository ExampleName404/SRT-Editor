using System.Windows;

namespace SrtEditor.Tests;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        (string Name, Action Run)[] suites =
        [
            ("SRT files and history", SrtDataTests.Run),
            ("Timing input and edit refresh", EditorTests.Run),
            ("Audio loading", AudioLoadTests.Run),
            ("Audio keyboard and seek input", AudioUiTests.Run)
        ];
        int failed = 0;
        foreach (var suite in suites)
        {
            try
            {
                suite.Run();
                Console.WriteLine($"PASS {suite.Name}");
            }
            catch (Exception exception)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {suite.Name}: {exception}");
            }
        }

        app.Shutdown();
        return failed == 0 ? 0 : 1;
    }
}

internal static class Check
{
    public static void True(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
