namespace DiskSpace;

/// <summary>The application icon (app.ico, embedded as a resource).</summary>
internal static class AppIcon
{
    private static readonly Lazy<Icon?> icon = new(() =>
    {
        using Stream? s = typeof(AppIcon).Assembly.GetManifestResourceStream("DiskSpace.app.ico");
        return s is null ? null : new Icon(s);
    });

    public static Icon? Value => icon.Value;
}
