using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Framework;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace TreeCounterAddin
{
    // A user's own way to flag which of many layers in a cluttered Contents pane they
    // actually use often, without touching the layers themselves - see chat: renaming a
    // layer (e.g. a "⭐ " prefix) was the first idea, rejected because a layer's Name also
    // drives its legend text in a layout, so favoriting something would silently change
    // what prints on a map layout. This list lives entirely in this add-in (FavoritesStore,
    // keyed by project) instead - a layer is never modified just by being favorited.
    internal partial class TreeCounterDockpaneViewModel
    {
        // One row in the Favorites list - its own small notifier rather than a plain
        // string, so the visibility CheckBox can two-way-bind and reflect toggles made
        // from ArcGIS Pro's own Contents pane too (re-synced every RefreshRasterLayersAsync,
        // same as every other layer list here).
        public class FavoriteLayerItem : INotifyPropertyChanged
        {
            public string Name { get; }

            private bool _isVisible;
            public bool IsVisible
            {
                get => _isVisible;
                set
                {
                    if (_isVisible == value) return;
                    _isVisible = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisible)));
                }
            }

            public FavoriteLayerItem(string name, bool isVisible)
            {
                Name = name;
                _isVisible = isVisible;
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }

        // All layers in the active map, any type - separate from RasterLayers/PolygonLayers/
        // etc. (which are filtered by geometry type for feature-specific pickers) since a
        // favorite can be any kind of layer.
        public ObservableCollection<string> AllLayerNames { get; } = new();

        // Substring filter over AllLayerNames for the "pick a layer to favorite" combo -
        // added after a real map turned out to have enough layers that scrolling the full
        // list to find one got tedious (a plain editable ComboBox's built-in text search
        // only jumps to the first *prefix* match, it doesn't narrow the dropdown itself).
        private string _layerSearchText = "";
        public string LayerSearchText
        {
            get => _layerSearchText;
            set
            {
                if (SetProperty(ref _layerSearchText, value))
                    RefreshFilteredLayerNames();
            }
        }

        public ObservableCollection<string> FilteredLayerNames { get; } = new();

        private void RefreshFilteredLayerNames()
        {
            FilteredLayerNames.Clear();
            var matches = string.IsNullOrWhiteSpace(LayerSearchText)
                ? AllLayerNames
                : AllLayerNames.Where(n => n.Contains(LayerSearchText, StringComparison.OrdinalIgnoreCase));
            foreach (var name in matches)
                FilteredLayerNames.Add(name);
        }

        private string _selectedLayerToFavorite;
        public string SelectedLayerToFavorite
        {
            get => _selectedLayerToFavorite;
            set => SetProperty(ref _selectedLayerToFavorite, value);
        }

        public ObservableCollection<FavoriteLayerItem> FavoriteLayers { get; } = new();

        private readonly HashSet<string> _favoriteNames = new();
        private string _favoritesLoadedForProjectUri;

        public ICommand AddFavoriteCommand => new RelayCommand(
            () => SetFavorite(SelectedLayerToFavorite, true),
            () => SelectedLayerToFavorite != null && !_favoriteNames.Contains(SelectedLayerToFavorite));

        public ICommand RemoveFavoriteCommand => new RelayCommand(
            (param) => SetFavorite((param as FavoriteLayerItem)?.Name, false), () => true);

        public ICommand ToggleFavoriteVisibilityCommand => new RelayCommand(
            async (param) =>
            {
                if (param is not FavoriteLayerItem item) return;
                var newVisible = !item.IsVisible;
                var applied = await QueuedTask.Run(() =>
                {
                    var layer = MapView.Active?.Map.GetLayersAsFlattenedList()
                        .FirstOrDefault(l => l.Name == item.Name);
                    if (layer == null) return false;
                    layer.SetVisibility(newVisible);
                    return true;
                });
                if (applied) item.IsVisible = newVisible;
            }, () => true);

        private void SetFavorite(string name, bool favorite)
        {
            if (name == null) return;
            if (favorite) _favoriteNames.Add(name);
            else _favoriteNames.Remove(name);

            var project = Project.Current;
            if (project != null) FavoritesStore.Save(project.URI, _favoriteNames);

            RebuildFavoriteLayers();
        }

        // Rebuilds FavoriteLayers from _favoriteNames + AllLayerNames (dropping any favorite
        // whose layer no longer exists in the map, same "prune what's gone" pattern every
        // other Selected* layer property already follows in RefreshRasterLayersAsync) using
        // each layer's current visibility. Cheap - only runs over already-in-memory lists,
        // no new map scan.
        private void RebuildFavoriteLayers(Dictionary<string, bool> visibilityByName = null)
        {
            visibilityByName ??= FavoriteLayers.ToDictionary(f => f.Name, f => f.IsVisible);
            _favoriteNames.RemoveWhere(n => !AllLayerNames.Contains(n));

            FavoriteLayers.Clear();
            foreach (var name in _favoriteNames.OrderBy(n => n))
                FavoriteLayers.Add(new FavoriteLayerItem(name, visibilityByName.TryGetValue(name, out var v) && v));
        }

        // Called from RefreshRasterLayersAsync with this scan's already-fetched layer
        // name/visibility pairs - avoids a second map traversal just for favorites.
        private void SyncFavorites(List<(string Name, bool IsVisible)> allLayers)
        {
            AllLayerNames.Clear();
            foreach (var (name, _) in allLayers)
                AllLayerNames.Add(name);
            RefreshFilteredLayerNames();

            var project = Project.Current;
            if (project != null && project.URI != _favoritesLoadedForProjectUri)
            {
                // First sync since this ViewModel was constructed, or the user switched to a
                // different project without restarting ArcGIS Pro - (re)load whatever was
                // saved for *this* project, discarding any other project's favorites still
                // sitting in memory.
                _favoriteNames.Clear();
                foreach (var name in FavoritesStore.Load(project.URI))
                    _favoriteNames.Add(name);
                _favoritesLoadedForProjectUri = project.URI;
            }

            var visibilityByName = allLayers.ToDictionary(l => l.Name, l => l.IsVisible);
            RebuildFavoriteLayers(visibilityByName);
        }
    }
}
