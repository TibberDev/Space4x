using System.Globalization;
using Pulsar4X.Api;

namespace Pulsar4X.UI.Core;

/// <summary>Number formatting shared by the screens. Wraps upstream's <see cref="Stringify"/> so units
/// read the same as in the ImGui client, and pins the culture so tests and screenshots are stable.</summary>
public static class Fmt
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public static string Int(long value) => value.ToString("N0", C);
    public static string Int(double value) => value.ToString("N0", C);
    public static string Percent(double fraction) => (fraction * 100).ToString("0", C) + "%";
    public static string Percent1(double fraction) => (fraction * 100).ToString("0.#", C) + "%";
    public static string Credits(decimal amount) => amount.ToString("N0", C) + " cr";

    /// <summary>"341 M", "50 k" — for big counts in tight table cells.</summary>
    public static string Quantity(double value) => Stringify.Quantity(value, "0.##");

    public static string Power(double kW) => Stringify.Power(kW, "0.##");
    public static string Mass(double kg) => Stringify.Mass(kg, "0.##");
    public static string Volume(double m3) => Stringify.Volume(m3, "0.##");
    public static string Points(double points) => points.ToString("N0", C) + " pts";
    public static string PointsPerDay(double points) => points.ToString("N0", C) + " pts/day";
}
