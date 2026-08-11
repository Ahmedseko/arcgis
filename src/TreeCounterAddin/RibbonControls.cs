using System.Windows;
using ArcGIS.Desktop.Framework.Contracts;

namespace TreeCounterAddin
{
    // Ribbon-level shortcuts and status readouts that mirror the dockpane ViewModel - the
    // dockpane stays the single source of truth for settings; these just give quick
    // access/at-a-glance status without opening the panel.

    internal class TreeCounterDetectShortcutButton : Button
    {
        public TreeCounterDetectShortcutButton()
        {
            TreeCounterDockpaneViewModel.RibbonStateChanged += Refresh;
            Refresh();
        }

        protected override void OnClick()
        {
            TreeCounterDockpaneViewModel.Show();
            var vm = TreeCounterDockpaneViewModel.Instance;
            if (vm?.RunDetectionCommand.CanExecute(null) == true)
                vm.RunDetectionCommand.Execute(null);
        }

        protected override void OnUpdate() => Refresh();

        private void Refresh() =>
            Enabled = TreeCounterDockpaneViewModel.Instance?.RunDetectionCommand.CanExecute(null) ?? true;
    }

    internal class TreeCounterCancelShortcutButton : Button
    {
        public TreeCounterCancelShortcutButton()
        {
            TreeCounterDockpaneViewModel.RibbonStateChanged += Refresh;
            Refresh();
        }

        protected override void OnClick()
        {
            var vm = TreeCounterDockpaneViewModel.Instance;
            if (vm?.CancelCommand.CanExecute(null) == true)
                vm.CancelCommand.Execute(null);
        }

        protected override void OnUpdate() => Refresh();

        private void Refresh() =>
            Enabled = TreeCounterDockpaneViewModel.Instance?.CancelCommand.CanExecute(null) ?? false;
    }

    internal class TreeCounterAboutButton : Button
    {
        protected override void OnClick()
        {
            MessageBox.Show(
                $"Forestry Toolkit {TreeCounterDockpaneViewModel.AppVersion}\n\n" +
                "Tree/oil palm detection, land clearing detection, road/trail extraction, " +
                "change comparison, biomass & carbon estimation, cruising data import, and " +
                "more - open the panel via the Forestry Toolkit button for the full toolset.\n\n" +
                "See the panel's own Help and About tabs for a detailed how-to-use guide " +
                "(English/Indonesian) and full technology/developer credits.\n\n" +
                "Developer: Ahmed Seko",
                "About Forestry Toolkit", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    // ArcGIS Pro has no dedicated read-only "label" ribbon control, so a disabled Button
    // whose Caption mirrors live ViewModel state is the standard stand-in pattern. Driven
    // by TreeCounterDockpaneViewModel.RibbonStateChanged (a static event) rather than
    // Button.OnUpdate() alone - OnUpdate's ribbon-refresh timing isn't reliably triggered
    // just by opening the dockpane, which left these stuck on their placeholder text.
    internal abstract class RibbonStatusLabel : Button
    {
        protected RibbonStatusLabel()
        {
            Enabled = false;
            TreeCounterDockpaneViewModel.RibbonStateChanged += Refresh;
            Refresh();
        }

        protected override void OnUpdate() => Refresh();

        private void Refresh() => Caption = ComputeCaption(TreeCounterDockpaneViewModel.Instance);

        protected abstract string ComputeCaption(TreeCounterDockpaneViewModel vm);
    }

    internal class TreeCounterAiStatusLabel : RibbonStatusLabel
    {
        protected override string ComputeCaption(TreeCounterDockpaneViewModel vm) =>
            vm == null ? "AI: -" : $"AI: {vm.SelectedProvider} / {vm.SelectedModel}";
    }

    internal class TreeCounterLastResultLabel : RibbonStatusLabel
    {
        protected override string ComputeCaption(TreeCounterDockpaneViewModel vm) =>
            vm == null || vm.LastTreeCount < 0
                ? "Last: -"
                : $"Last: {vm.LastTreeCount} trees / {vm.LastAreaHa:F1} ha";
    }
}
