using System.Globalization;

namespace MiniRover.Client;

/// <summary>
/// The car's settings as it reported them ("key=value" lines: every setting plus read-only "info.*" keys).
/// The car clamps values to safe ranges, so this is what it actually stored, not what was asked for.
/// </summary>
public sealed class CarSettings
{
    readonly Dictionary<string, string> _values;

    CarSettings(Dictionary<string, string> values) => _values = values;

    public static CarSettings Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in (text ?? "").Split('\n'))
        {
            int eq = line.IndexOf('=');
            if (eq > 0) values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        return new CarSettings(values);
    }

    public IReadOnlyDictionary<string, string> Values => _values;

    public string Get(string key, string fallback = "") => _values.TryGetValue(key, out var v) ? v : fallback;

    public double GetDouble(string key, double fallback) =>
        double.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;

    public bool GetBool(string key, bool fallback = false) => _values.TryGetValue(key, out var v) ? v != "0" && v.Length > 0 : fallback;

    /// <summary>"key=value" with an invariant-culture number (the car parses with '.' as the decimal point).</summary>
    public static string Format(string key, double value) => key + "=" + value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// The battery coefficient that makes the car read <paramref name="measuredVolts"/> (from a multimeter) when it
    /// currently reports <paramref name="reportedVolts"/>. The reading is linear in the coefficient.
    /// </summary>
    public static double CalibratedBatteryCoefficient(double currentCoefficient, double reportedVolts, double measuredVolts)
    {
        if (reportedVolts <= 0.5 || measuredVolts <= 0.5) throw new ArgumentException("Both voltages must be real pack voltages.");
        return currentCoefficient * measuredVolts / reportedVolts;
    }
}
