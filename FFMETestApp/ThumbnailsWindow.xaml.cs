using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IOPath = System.IO.Path;

namespace FFMETestApp;

// Non-blocking dialog that shows a scrollable grid of video thumbnails.
// Thumbnails are captured sequentially — one at a time — to avoid saturating the decoder.
// Each card is clickable; clicking raises VideoSelected so the caller can load that file.
public partial class ThumbnailsWindow : Window
{
    // Height in pixels of every thumbnail image. Width is derived from each video's aspect ratio.
    private const int ThumbnailHeight = 128;

    // Position within each video (in seconds) at which the thumbnail frame is captured.
    // 1 second is used to skip past opening black frames that many clips start with.
    private const double SeekSeconds = 1.0;

    private readonly string[] _videoFiles;

    // Raised when the user clicks a thumbnail card. Passes the full file path of the chosen video.
    public event Action<string>? VideoSelected;

    public ThumbnailsWindow(string[] videoFiles)
    {
        _videoFiles = videoFiles;
        InitializeComponent();

        // ContentRendered fires after the window is visible on screen, ensuring the WrapPanel
        // has its layout before we start adding cards to it.
        ContentRendered += OnContentRendered;
    }

    // Captures thumbnails one by one and adds each card to the panel as it arrives.
    // Running captures sequentially (rather than in parallel) avoids spinning up dozens of
    // FFmpeg decoder instances simultaneously, which would saturate CPU and I/O.
    private async void OnContentRendered(object? sender, EventArgs e)
    {
        for (int i = 0; i < _videoFiles.Length; i++)
        {
            string path = _videoFiles[i];

            // Update the status bar so the user can see progress during a long capture run.
            StatusText.Text = $"Loading thumbnail {i + 1} of {_videoFiles.Length}: {IOPath.GetFileName(path)}";

            // CaptureFrame does blocking FFmpeg I/O and decoding; run it on a thread-pool thread
            // so the UI thread stays responsive. The frozen BitmapSource it returns is safe to
            // hand back to the UI thread without any further marshalling.
            var bitmap = await Task.Run(() => ThumbnailHelper.CaptureFrame(path, SeekSeconds, ThumbnailHeight));

            // Add the completed card to the WrapPanel immediately — the user sees thumbnails
            // appear one at a time rather than waiting for all captures to finish.
            ThumbnailsPanel.Children.Add(BuildThumbnailItem(path, bitmap, VideoSelected));
        }

        StatusText.Text = $"{_videoFiles.Length} thumbnail{(_videoFiles.Length == 1 ? "" : "s")} — captured at {SeekSeconds:0.#}s";
    }

    // Builds one thumbnail card: an image (or grey placeholder) above a filename label,
    // wrapped in a bordered card that highlights on hover and fires onSelected on click.
    private static Border BuildThumbnailItem(string filePath, BitmapSource? bitmap, Action<string>? onSelected)
    {
        var img = new Image
        {
            Height = ThumbnailHeight,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Top,
        };

        if (bitmap != null)
        {
            img.Source = bitmap;
            // Set the image width to match the actual pixel width of the captured frame
            // so the card is sized to the video's true aspect ratio.
            img.Width = bitmap.PixelWidth;
        }
        else
        {
            // CaptureFrame returned null (unreadable file, no video stream, etc.).
            // Fall back to a 16:9 placeholder so the layout stays consistent.
            img.Width = (int)(ThumbnailHeight * 16.0 / 9);
            img.Source = null;
        }

        // Filename label beneath the image. MaxWidth matches the image so long names wrap
        // rather than widening the card beyond the thumbnail.
        var label = new TextBlock
        {
            Text = IOPath.GetFileNameWithoutExtension(filePath),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = img.Width,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(2, 4, 2, 0),
            FontSize = 11,
        };

        // Grey placeholder shown instead of the image when capture failed.
        var placeholder = new Border
        {
            Width = img.Width,
            Height = ThumbnailHeight,
            Background = Brushes.DimGray,
            Child = new TextBlock
            {
                Text = "No preview",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.LightGray,
            },
        };

        // Stack the image (or placeholder) above the filename label.
        var content = new StackPanel { Orientation = Orientation.Vertical };
        content.Children.Add(bitmap != null ? (UIElement)img : placeholder);
        content.Children.Add(label);

        // Outer card border — gives each thumbnail a visible boundary and acts as the
        // hit-test surface for hover highlighting and click events.
        var card = new Border
        {
            Margin = new Thickness(5),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            Child = content,
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        if (onSelected != null)
        {
            // Highlight the border on hover to give clear visual feedback that the card is clickable.
            card.MouseEnter += (_, _) => card.BorderBrush = Brushes.DodgerBlue;
            card.MouseLeave += (_, _) => card.BorderBrush = Brushes.Gray;

            // MouseLeftButtonUp (rather than Click) is used because Border doesn't have a Click event.
            card.MouseLeftButtonUp += (_, _) => onSelected(filePath);
        }

        return card;
    }
}
