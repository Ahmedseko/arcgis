using System;
using TreeCounterAddin;

// Plain self-check, no test framework - run with `dotnet run` from this folder. Exits
// non-zero if any check fails, so it's usable in a script/CI step later without needing
// ArcGIS Pro installed (these two math classes have zero ArcGIS dependency).
var failures = 0;

void Check(string name, bool condition)
{
    Console.WriteLine((condition ? "PASS  " : "FAIL  ") + name);
    if (!condition) failures++;
}

bool Close(double a, double b, double tolerance) => Math.Abs(a - b) <= tolerance;

// --- SliverMath.Thinness ---
// A circle's thinness is exactly 1 regardless of radius (4*pi*A / P^2 with A=pi*r^2, P=2*pi*r).
var circleArea = Math.PI * 10 * 10;
var circlePerimeter = 2 * Math.PI * 10;
Check("Thinness(circle) ~= 1.0", Close(SliverMath.Thinness(circleArea, circlePerimeter), 1.0, 1e-9));

// A long thin rectangle (100 x 1) should read as clearly non-circular.
var thinRectThinness = SliverMath.Thinness(area: 100, perimeter: 2 * (100 + 1));
Check("Thinness(100x1 rect) < 0.1", thinRectThinness < 0.1);

// Degenerate ring (zero/negative perimeter) is treated as maximally thin, not a divide-by-zero.
Check("Thinness(perimeter<=0) == 0", SliverMath.Thinness(area: 5, perimeter: 0) == 0);

// --- SliverMath.Median ---
Check("Median(odd count) == middle value", SliverMath.Median(new[] { 3.0, 1.0, 2.0 }) == 2.0);
Check("Median(even count) == average of middle two", SliverMath.Median(new[] { 1.0, 2.0, 3.0, 4.0 }) == 2.5);
Check("Median(single value) == that value", SliverMath.Median(new[] { 7.0 }) == 7.0);

// --- BiomassMath.Estimate ---
// Hand-calculated expectation: AGB = 10*600*1.5 = 9000; total = 9000*1.37 = 12330;
// carbon = 12330*0.47 = 5795.1; CO2e = 5795.1*3.667 ~= 21250.63.
var (biomassKg, carbonKg, co2eKg) = BiomassMath.Estimate(
    totalVolumeM3: 10, woodDensityKgM3: 600, biomassExpansionFactor: 1.5,
    rootShootRatio: 0.37, carbonFraction: 0.47);
Check("Biomass(10 m3 @ defaults) == 12330 kg", Close(biomassKg, 12330, 0.01));
Check("Carbon(10 m3 @ defaults) == 5795.1 kg", Close(carbonKg, 5795.1, 0.01));
Check("CO2e(10 m3 @ defaults) ~= 21250.63 kg", Close(co2eKg, 21250.6317, 0.01));

// Zero volume must not produce NaN/negative output from any of the multipliers.
var (zeroBiomass, zeroCarbon, zeroCo2e) = BiomassMath.Estimate(0, 600, 1.5, 0.37, 0.47);
Check("Biomass(0 m3) == 0", zeroBiomass == 0 && zeroCarbon == 0 && zeroCo2e == 0);

Console.WriteLine();
Console.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) FAILED.");
return failures == 0 ? 0 : 1;
