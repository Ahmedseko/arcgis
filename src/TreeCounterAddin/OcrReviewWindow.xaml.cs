using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace TreeCounterAddin
{
    // One row per scanned photo - plain auto-properties are enough here since the DataGrid
    // only needs edit-commit-then-read-back, not live two-way notification elsewhere.
    public class OcrRow
    {
        public string FileName { get; set; }
        public string PhotoPath { get; set; }
        public bool Include { get; set; }
        // X/Y are either UTM easting/northing (meters) or longitude/latitude (degrees),
        // depending on Wkid - see OcrCoordinateReader.Result.
        public double? X { get; set; }
        public double? Y { get; set; }
        public int? Wkid { get; set; }
        public string RawText { get; set; }
    }

    public partial class OcrReviewWindow : Window
    {
        private readonly List<OcrRow> _rows;

        // Populated only after the user clicks "Create Points" - null/empty otherwise
        // (including on Cancel), so the caller can tell "nothing confirmed" from
        // "confirmed zero rows" without inspecting DialogResult separately.
        public List<OcrRow> ConfirmedRows { get; private set; } = new();

        public OcrReviewWindow(List<OcrRow> rows)
        {
            InitializeComponent();
            _rows = rows;
            Grid.ItemsSource = _rows;
        }

        private void CreatePoints_Click(object sender, RoutedEventArgs e)
        {
            Grid.CommitEdit(DataGridEditingUnit.Row, true);

            ConfirmedRows = _rows.Where(r => r.Include && r.X.HasValue && r.Y.HasValue && r.Wkid.HasValue).ToList();
            if (ConfirmedRows.Count == 0)
            {
                MessageBox.Show("No rows are checked with X, Y, and a coordinate system filled in.",
                    "Nothing to create", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
            Close();
        }
    }
}
