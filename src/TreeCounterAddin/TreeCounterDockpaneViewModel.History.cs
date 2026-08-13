using ArcGIS.Desktop.Framework;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Windows.Input;

namespace TreeCounterAddin
{
    // A running log of what ran, when, and its result - across every feature in the panel,
    // in one place. Every feature already follows the same "XxxStatus" property naming
    // convention (LandClearingStatus, RoadExtractionStatus, SliverStatus, ...) for its own
    // status line, so this hooks the ViewModel's existing PropertyChanged event (already
    // subscribed once in the constructor for RibbonStateChanged) instead of touching each
    // feature's own command method individually - zero changes needed anywhere else.
    // ponytail: logs every status change, not just "final" ones (a multi-stage operation's
    // "Scanning..." / "Vectorizing..." / "Done: ..." messages all get their own entries) -
    // distinguishing "in progress" from "final" per feature would need per-feature knowledge
    // this generic hook deliberately doesn't have. Reads fine in practice as a blow-by-blow
    // trail, not just noise. No one-click re-run yet either - each feature's own parameters
    // aren't captured here, only the status text; add if this turns out to not be enough.
    internal partial class TreeCounterDockpaneViewModel
    {
        public class HistoryEntry
        {
            public DateTime Timestamp { get; }
            public string Feature { get; }
            public string Message { get; }
            public string Display => $"{Timestamp:HH:mm:ss}  [{Feature}]  {Message}";

            public HistoryEntry(string feature, string message)
            {
                Timestamp = DateTime.Now;
                Feature = feature;
                Message = message;
            }
        }

        private const int MaxHistoryEntries = 200;

        public ObservableCollection<HistoryEntry> RunHistory { get; } = new();

        public ICommand ClearHistoryCommand => new RelayCommand(() => RunHistory.Clear());

        private void RecordHistory(string propertyName)
        {
            if (propertyName == null || !propertyName.EndsWith("Status", StringComparison.Ordinal))
                return;
            // Reflection here (not a giant switch over every *Status property) is what keeps
            // this generic - a new feature's own FooStatus property is picked up automatically
            // the moment it's added, no edit to this file required.
            var value = GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(this) as string;
            if (string.IsNullOrEmpty(value)) return;

            var feature = SplitCamelCase(propertyName.Substring(0, propertyName.Length - "Status".Length));
            RunHistory.Insert(0, new HistoryEntry(feature, value));
            while (RunHistory.Count > MaxHistoryEntries)
                RunHistory.RemoveAt(RunHistory.Count - 1);
        }

        // "LandClearing" -> "Land Clearing" - just for readability in the log, not used
        // anywhere functional.
        private static string SplitCamelCase(string s)
        {
            if (string.IsNullOrEmpty(s)) return "General";
            var sb = new StringBuilder();
            foreach (var c in s)
            {
                if (sb.Length > 0 && char.IsUpper(c)) sb.Append(' ');
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
