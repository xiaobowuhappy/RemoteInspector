namespace RemoteInspector.Core;

public static class ImageCoordinates
{
    // WPF uniform image content is centered within its available rectangle. Return Unity bottom-left coordinates.
    public static bool TryNormalize(double width, double height, double pixelsWide, double pixelsHigh, double x, double y, out float u, out float v)
    {
        u = v = 0;
        if (width <= 0 || height <= 0 || pixelsWide <= 0 || pixelsHigh <= 0) return false;
        double scale = Math.Min(width / pixelsWide, height / pixelsHigh);
        double w = pixelsWide * scale, h = pixelsHigh * scale;
        x -= (width - w) / 2; y -= (height - h) / 2;
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x >= w || y >= h) return false;
        u = (float)(x / w); v = (float)(1 - y / h); return true;
    }
}
