using System;
using System.Globalization;
using System.Windows.Data;

namespace TreeCounterAddin
{
    // Disables the Color Reference Sampler's raster/category pickers while a sampling
    // session is active (IsColorSampling) - a real report (2026-08-16) asked for one
    // session to always be one category, to avoid an output feature class silently mixing
    // categories; locking the pickers enforces that instead of just documenting it, and
    // also stops the raster from being swapped out from under the running pixel-sample
    // worker process (which was started against one specific raster path).
    public class InverseBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is bool b && !b;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is bool b && !b;
    }
}
