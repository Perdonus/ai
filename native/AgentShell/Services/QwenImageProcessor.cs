namespace AgentShell.Services;

/// <summary>
/// Mirrors the official Qwen-VL <c>smart_resize</c> helper. Screenshots are resized to a
/// multiple of <see cref="Factor"/> so the vision tower sees a supported resolution, and the
/// total pixel count is clamped so prefill stays affordable on a small GPU.
/// </summary>
public static class QwenImageProcessor
{
    public const int Factor = 32;

    public const int DefaultMinPixels = 32 * 32 * 4;

    public const int DefaultMaxPixels = 1280 * 1024;

    public static (int Width, int Height) SmartResize(
        int width,
        int height,
        int factor = Factor,
        int minPixels = DefaultMinPixels,
        int maxPixels = DefaultMaxPixels)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
        }

        var hBar = Math.Max(factor, (int)Math.Round((double)height / factor) * factor);
        var wBar = Math.Max(factor, (int)Math.Round((double)width / factor) * factor);

        if ((long)hBar * wBar > maxPixels)
        {
            var beta = Math.Sqrt((double)height * width / maxPixels);
            hBar = Math.Max(factor, (int)Math.Floor(height / beta / factor) * factor);
            wBar = Math.Max(factor, (int)Math.Floor(width / beta / factor) * factor);
        }
        else if ((long)hBar * wBar < minPixels)
        {
            var beta = Math.Sqrt((double)minPixels / ((double)height * width));
            hBar = Math.Max(factor, (int)Math.Ceiling(height * beta / factor) * factor);
            wBar = Math.Max(factor, (int)Math.Ceiling(width * beta / factor) * factor);
        }

        return (wBar, hBar);
    }
}
