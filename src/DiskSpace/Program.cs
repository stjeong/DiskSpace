namespace DiskSpace;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Updater.CleanupAfterUpdate(); // removes the previous exe left by a self-update

        string? path = args.Length > 0 ? args[0] : null;
        if (string.IsNullOrWhiteSpace(path))
        {
            using var dlg = new DriveSelectForm();
            if (dlg.ShowDialog() != DialogResult.OK || dlg.SelectedPath is null)
                return;
            path = dlg.SelectedPath;
        }

        Application.Run(new MainForm(path));
    }
}
