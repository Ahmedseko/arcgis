using System.Collections.Generic;
using System.Linq;

namespace TreeCounterAddin
{
    // Pure geometry/statistics math used by sliver polygon detection - deliberately free of
    // any ArcGIS reference so it can be exercised by a plain console project without ArcGIS
    // Pro installed (see src/ForestryToolkit.MathTests).
    internal static class SliverMath
    {
        // 4*pi*Area / Perimeter^2 - 1.0 for a circle, approaching 0 as a shape thins out or
        // gets more elongated. Perimeter <= 0 is a degenerate ring, treated as maximally thin.
        public static double Thinness(double area, double perimeter) =>
            perimeter <= 0 ? 0 : 4 * System.Math.PI * area / (perimeter * perimeter);

        public static double Median(IEnumerable<double> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            var mid = sorted.Count / 2;
            return sorted.Count % 2 == 0 ? (sorted[mid - 1] + sorted[mid]) / 2.0 : sorted[mid];
        }
    }
}
