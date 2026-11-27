namespace DiskSpace;

/// <summary>
/// Converts sizes designed at 96 DPI (100%) to device pixels. All forms use AutoScaleMode.None and
/// re-apply their sizes from these 96-DPI constants on every DPI change, so repeated changes never drift.
/// </summary>
internal static class Dpi
{
    public const int Default = 96;

    public static int Scale(int value, int dpi) => (int)Math.Round(value * dpi / (double)Default);

    public static float Scale(float value, int dpi) => value * dpi / Default;

    public static Size Scale(Size size, int dpi) => new(Scale(size.Width, dpi), Scale(size.Height, dpi));

    public static Padding Scale(Padding p, int dpi) =>
        new(Scale(p.Left, dpi), Scale(p.Top, dpi), Scale(p.Right, dpi), Scale(p.Bottom, dpi));
}
