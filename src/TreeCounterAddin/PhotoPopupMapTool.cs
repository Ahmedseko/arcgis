using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using ArcGIS.Desktop.Mapping;
using ArcGIS.Desktop.Mapping.Events;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Input;

namespace TreeCounterAddin
{
    // Ribbon-activated map tool for the custom photo "card" popup (see CLAUDE.md/conversation
    // history: ArcGIS Pro's built-in popup only shows a hierarchical field-list dock pane, not
    // an inline photo, for this add-in's point layers - confirmed by direct testing). Only
    // shows a card when the clicked feature actually has a photo attachment; everything else
    // is silently ignored, since this tool exists specifically for the photo card and isn't a
    // general-purpose identify replacement (use the default Explore tool for that).
    //
    // Derives from ArcGIS.Desktop.Mapping.MapTool - the documented Esri SDK base for
    // interactive map click tools (see Esri's own ProGuide-MapView-Interaction and the
    // MapToolWithOverlayControl/ShowCoordinatesTool.cs sample, which this follows almost
    // verbatim). An earlier version of this file derived from the more generic
    // ArcGIS.Desktop.Framework.Contracts.Tool instead (based on a flawed local metadata scan
    // that seemed to show no MapTool type in the installed SDK) - that base doesn't properly
    // integrate with MapView's tool-switching/navigation state machine, which is what caused
    // the map to become unresponsive (couldn't pan, couldn't switch tools, double-click forced
    // a zoom) once this tool was activated.
    internal class PhotoPopupMapTool : MapTool
    {
        private static readonly Regex FieldToken = new(@"\{([^}]+)\}", RegexOptions.Compiled);

        // IMapViewOverlayControl's InitialXRatio/InitialYRatio (and every other position
        // property) are get-only, even on the interface itself - confirmed via reflection.
        // There's no way to move an existing overlay control, so "anchoring" the card to its
        // map point means removing and re-adding a new MapViewOverlayControl at a freshly
        // computed ratio every time the camera changes. _anchorMapPoint/_cardControl are kept
        // around specifically so MapViewCameraChangedEvent's handler can do that recompute.
        private IMapViewOverlayControl _currentOverlay;
        private PopupCardControl _cardControl;
        private MapPoint _anchorMapPoint;

        public PhotoPopupMapTool()
        {
            IsSketchTool = false;
            MapViewCameraChangedEvent.Subscribe(OnCameraChanged);
        }

        protected override void OnToolMouseDown(MapViewMouseButtonEventArgs e)
        {
            // Signals to the framework that this tool (not default map navigation) handles
            // the click - HandleMouseDownAsync only runs when this is set.
            if (e.ChangedButton == MouseButton.Left)
                e.Handled = true;
        }

        protected override Task HandleMouseDownAsync(MapViewMouseButtonEventArgs e)
        {
            return QueuedTask.Run(() =>
            {
                var mapView = MapView.Active;
                if (mapView == null) return;

                // A bare click point has zero area, so a small pixel tolerance box is
                // converted to map units the same way Esri's own identify samples do.
                const int tolerancePixels = 4;
                var corner1 = mapView.ClientToMap(new System.Windows.Point(e.ClientPoint.X - tolerancePixels, e.ClientPoint.Y - tolerancePixels));
                var corner2 = mapView.ClientToMap(new System.Windows.Point(e.ClientPoint.X + tolerancePixels, e.ClientPoint.Y + tolerancePixels));
                var envelope = EnvelopeBuilderEx.CreateEnvelope(corner1, corner2, mapView.Map.SpatialReference);

                var found = false;
                // GetFeatures returns a SelectionSet - ToDictionary() flattens it to the
                // MapMember/objectID pairs actually needed here.
                foreach (var kvp in mapView.GetFeatures(envelope, true, false).ToDictionary())
                {
                    if (kvp.Key is not FeatureLayer layer) continue;
                    foreach (var oid in kvp.Value)
                    {
                        if (TryBuildCard(layer, oid, out var title, out var subtitle, out var imageBytes, out var anchorPoint))
                        {
                            ShowCard(mapView, title, subtitle, imageBytes, anchorPoint);
                            found = true;
                            break;
                        }
                    }
                    if (found) break;
                }

                if (TreeCounterDockpaneViewModel.Instance is { } vm)
                    vm.StatusText = found ? "Photo Popup: card shown." : "Photo Popup: no photo attachment found at that location.";
            });
        }

        // Runs on the MCT (called from HandleMouseDownAsync's QueuedTask.Run).
        private static bool TryBuildCard(FeatureLayer layer, long oid, out string title, out string subtitle, out byte[] imageBytes, out MapPoint anchorPoint)
        {
            title = layer.Name;
            subtitle = layer.Name;
            imageBytes = null;
            anchorPoint = null;

            using var table = layer.GetTable();
            if (table == null) return false;

            // Attachments must be checked for before GetAttachments() is called - calling it
            // on a table that was never attachment-enabled (e.g. a fishnet/polygon layer that
            // happens to be near the click) throws GeodatabaseAttachmentNotEnabledException
            // rather than just returning empty.
            if (!table.IsAttachmentEnabled()) return false;

            var queryFilter = new QueryFilter { ObjectIDs = new List<long> { oid } };
            using var cursor = table.Search(queryFilter, false);
            if (!cursor.MoveNext()) return false;
            using var row = cursor.Current;

            var photo = row.GetAttachments(null, false).FirstOrDefault();
            if (photo == null) return false;

            using (var stream = photo.GetData())
                imageBytes = stream?.ToArray();
            if (imageBytes == null || imageBytes.Length == 0) return false;

            anchorPoint = (row as Feature)?.GetShape() as MapPoint;
            if (anchorPoint == null) return false;

            // Reuses the same popup-title expression already set by ImportPhotosAsync /
            // ScanPhotosForCoordinatesAsync / ImportExcelAsync (see SetPopupTitle in
            // TreeCounterDockpaneViewModel.cs) rather than a second, separate title field.
            if (layer.GetDefinition() is ArcGIS.Core.CIM.CIMFeatureLayer def && !string.IsNullOrEmpty(def.PopupInfo?.Title))
            {
                title = FieldToken.Replace(def.PopupInfo.Title, m =>
                {
                    try { return Convert.ToString(row[m.Groups[1].Value]) ?? ""; }
                    catch { return ""; }
                });
            }

            return true;
        }

        private void ShowCard(MapView mapView, string title, string subtitle, byte[] imageBytes, MapPoint anchorPoint)
        {
            // Compute the initial screen position on the same MCT thread that already has
            // the map point, then hand off to the UI thread just to build/add the control.
            var clientPoint = mapView.MapToClient(anchorPoint);
            var viewSize = mapView.GetViewSize();
            var xRatio = Clamp01(clientPoint.X / viewSize.Width);
            var yRatio = Clamp01(clientPoint.Y / viewSize.Height);

            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                RemoveCurrentOverlay(mapView);

                _cardControl = new PopupCardControl();
                _cardControl.SetContent(title, subtitle, imageBytes);
                _cardControl.Closed += (_, __) =>
                {
                    RemoveCurrentOverlay(mapView);
                    _anchorMapPoint = null;
                };

                _anchorMapPoint = anchorPoint;
                AddOverlayAt(mapView, xRatio, yRatio);
            });
        }

        // Fires on every pan/zoom/rotate - re-anchors the card to its map point by removing
        // and re-adding the overlay control (see the field comment above for why: there's no
        // "move" API for an overlay control already on the view).
        private void OnCameraChanged(MapViewCameraChangedEventArgs args)
        {
            if (_cardControl == null || _anchorMapPoint == null) return;
            var mapView = MapView.Active;
            if (mapView == null) return;

            _ = QueuedTask.Run(() =>
            {
                var clientPoint = mapView.MapToClient(_anchorMapPoint);
                var viewSize = mapView.GetViewSize();
                var xRatio = clientPoint.X / viewSize.Width;
                var yRatio = clientPoint.Y / viewSize.Height;

                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (_cardControl == null) return;

                    // The point panned out of view - hide the card rather than clamp it to
                    // an edge, since that would misleadingly suggest the point is still there.
                    if (xRatio < 0 || xRatio > 1 || yRatio < 0 || yRatio > 1)
                    {
                        RemoveCurrentOverlay(mapView);
                        return;
                    }

                    AddOverlayAt(mapView, Clamp01(xRatio), Clamp01(yRatio));
                });
            });
        }

        private void AddOverlayAt(MapView mapView, double xRatio, double yRatio)
        {
            if (_currentOverlay != null)
                mapView.RemoveOverlayControl(_currentOverlay);

            _currentOverlay = new MapViewOverlayControl(_cardControl, canMove: true, canResizeHorizontally: false,
                canResizeVertically: false, OverlayControlRelativePosition.TopLeft, xRatio, yRatio);
            mapView.AddOverlayControl(_currentOverlay);
        }

        private void RemoveCurrentOverlay(MapView mapView)
        {
            if (_currentOverlay == null) return;
            mapView.RemoveOverlayControl(_currentOverlay);
            _currentOverlay = null;
            _cardControl = null;
        }

        private static double Clamp01(double value) => value < 0 ? 0 : value > 1 ? 1 : value;
    }
}
