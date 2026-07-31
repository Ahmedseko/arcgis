using System;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace TreeCounterAddin
{
    // The "option 3" custom photo card - a floating WPF control placed over the map via
    // MapViewOverlayControl (see PhotoPopupMapTool), since ArcGIS Pro's built-in popup only
    // shows a hierarchical field-list dock pane, not an inline photo, for this add-in's point
    // layers (confirmed by direct testing - see conversation notes).
    public partial class PopupCardControl : UserControl
    {
        public event EventHandler Closed;

        public PopupCardControl()
        {
            InitializeComponent();
        }

        public void SetContent(string title, string subtitle, byte[] imageBytes)
        {
            TitleText.Text = title;
            SubtitleText.Text = subtitle;

            using var ms = new MemoryStream(imageBytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
            PhotoImage.Source = bitmap;
        }

        private void CloseButton_Click(object sender, System.Windows.RoutedEventArgs e) =>
            Closed?.Invoke(this, EventArgs.Empty);
    }
}
