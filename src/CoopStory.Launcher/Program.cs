namespace CoopStory.Launcher;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--wait-for" && int.TryParse(args[1], out var previousPid))
        {
            try
            {
                using var previous = System.Diagnostics.Process.GetProcessById(previousPid);
                if (!previous.WaitForExit(30000)) return;
            }
            catch (ArgumentException) { }
        }
        ApplicationConfiguration.Initialize();
        using var singleInstance = LauncherSingleInstance.TryAcquire();
        if (singleInstance is null)
        {
            MessageBox.Show(
                "RDR2 Coop Story Launcher is already running for this user.\n\n" +
                "Return to the existing launcher window.",
                "Launcher already running",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        var paths = LauncherPaths.CreateDefault();
        using var logger = new LauncherLogger(paths.LauncherLogPath);
        Application.ThreadException += (_, args) =>
        {
            logger.Error("ui.thread_exception", args.Exception);
            MessageBox.Show(
                $"Unexpected error: {args.Exception.Message}\n\n" +
                $"Details were saved to:\n{paths.LogDirectory}",
                "RDR2 Coop Story",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        };

        try
        {
            var services = LauncherServices.Create(paths, logger);
            Application.Run(new MainForm(services));
        }
        catch (Exception exception)
        {
            logger.Error("launcher.fatal", exception);
            MessageBox.Show(
                $"The launcher cannot start: {exception.Message}\n\n" +
                $"Details were saved to:\n{paths.LogDirectory}",
                "RDR2 Coop Story",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
