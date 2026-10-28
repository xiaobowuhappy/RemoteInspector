using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RemoteInspector.Desktop;

public partial class MainWindow
{
    private bool fitImage = true;
    private bool updatingImageZoom;
    private int imageZoomVersion;
    private void FitImage()
    {
        fitImage = true;
        if (PreviewImage.Source is not BitmapSource bitmap || ImageViewport.ActualWidth <= 24 || ImageViewport.ActualHeight <= 24) return;
        // Reserve scrollbar space so fitting cannot oscillate between showing and hiding scrollbars.
        double scale = Math.Min(1, Math.Min((ImageViewport.ActualWidth - 24) / bitmap.PixelWidth, (ImageViewport.ActualHeight - 24) / bitmap.PixelHeight));
        SetImageZoom(Math.Clamp(scale * 100, ImageZoom.Minimum, ImageZoom.Maximum), false);
    }
    private void SetImageZoom(double percent, bool retainCenter)
    {
        if (PreviewImage.Source is not BitmapSource bitmap) return;
        double oldWidth = double.IsFinite(PreviewImage.Width) ? PreviewImage.Width : bitmap.PixelWidth;
        double oldHeight = double.IsFinite(PreviewImage.Height) ? PreviewImage.Height : bitmap.PixelHeight;
        double viewportWidth = ImageViewport.ViewportWidth, viewportHeight = ImageViewport.ViewportHeight;
        double centerX = oldWidth > 0 ? (ImageViewport.HorizontalOffset + viewportWidth / 2 - Math.Max(0, (viewportWidth - oldWidth) / 2)) / oldWidth : .5;
        double centerY = oldHeight > 0 ? (ImageViewport.VerticalOffset + viewportHeight / 2 - Math.Max(0, (viewportHeight - oldHeight) / 2)) / oldHeight : .5;
        updatingImageZoom = true;
        try { ImageZoom.Value = percent; } finally { updatingImageZoom = false; }
        ImageZoomLabel.Text = percent.ToString("0") + "%";
        PreviewImage.Width = bitmap.PixelWidth * percent / 100;
        PreviewImage.Height = bitmap.PixelHeight * percent / 100;
        int version = ++imageZoomVersion;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (version != imageZoomVersion || closing) return;
            ImageViewport.UpdateLayout();
            ImageViewport.ScrollToHorizontalOffset(retainCenter ? centerX * PreviewImage.Width - ImageViewport.ViewportWidth / 2 : 0);
            ImageViewport.ScrollToVerticalOffset(retainCenter ? centerY * PreviewImage.Height - ImageViewport.ViewportHeight / 2 : 0);
        }));
    }
    private void ImageZoom_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (updatingImageZoom || PreviewImage == null || ImageViewport == null || PreviewImage.Source == null) return;
        fitImage = false; SetImageZoom(e.NewValue, true);
    }
    private void FitImage_Click(object sender, RoutedEventArgs e) => FitImage();
    private void ActualImage_Click(object sender, RoutedEventArgs e) { fitImage = false; SetImageZoom(100, true); }
    private void ImageViewport_SizeChanged(object sender, SizeChangedEventArgs e) { if (fitImage && PreviewImage != null) FitImage(); }
    private void Image_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || PreviewImage.Source == null) return;
        fitImage = false; SetImageZoom(Math.Clamp(ImageZoom.Value + (e.Delta > 0 ? 10 : -10), ImageZoom.Minimum, ImageZoom.Maximum), true); e.Handled = true;
    }
}
