namespace NeoAdmin;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(
            UnhandledExceptionMode.ThrowException);

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            CrashLog.Write(
                "Unhandled AppDomain exception.",
                args.ExceptionObject as Exception);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write(
                "Unobserved task exception.",
                args.Exception);
            args.SetObserved();
        };

        CrashLog.Write("Application starting.");

        try
        {
            string configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            AppConfig config = AppConfig.Load(configPath);
            Application.Run(new MainForm(config));
            CrashLog.Write("Application stopped normally.");
        }
        catch (Exception exception)
        {
            CrashLog.Write(
                "Application terminated after an unhandled exception.",
                exception);

            MessageBox.Show(
                $"{exception.Message}\n\nDetails were written to:\n{CrashLog.LogPath}",
                "NEO ADMIN could not start",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
