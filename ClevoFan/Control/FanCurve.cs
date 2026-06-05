namespace ClevoFan.Control;

/// <summary>
/// A monotonic temperature-&gt;duty curve with hysteresis to stop the fan hunting around a threshold.
/// Points are (tempC, dutyPercent), sorted ascending by temp. Linear interpolation between points;
/// clamped to the first/last point outside the range.
/// </summary>
public sealed class FanCurve
{
    private readonly (int temp, int duty)[] _points;
    private readonly int _hysteresisC;
    private int _lastTempApplied = int.MinValue;
    private int _lastDuty;

    public FanCurve(IEnumerable<(int temp, int duty)> points, int hysteresisC = 5)
    {
        _points = points.OrderBy(p => p.temp).ToArray();
        if (_points.Length == 0) throw new ArgumentException("Curve needs at least one point.");
        _hysteresisC = Math.Max(0, hysteresisC);
    }

    /// <summary>The default "Quiet" curve — keeps the CPU fan calm at idle/light load on an i7-11800H,
    /// then ramps assertively once it actually gets warm. Tune freely in config.</summary>
    public static FanCurve Quiet() => new(new[]
    {
        (45, 20), // floor: keep some airflow, avoid heat-soak, but near-silent
        (55, 25),
        (65, 33),
        (72, 45),
        (80, 60),
        (87, 80),
        (92, 100),
    });

    /// <summary>Duty for a given temperature, with hysteresis applied vs. the last decision.</summary>
    public int DutyFor(int tempC)
    {
        int target = Interpolate(tempC);

        // Hysteresis: only change the applied duty if the temperature moved past the last
        // applied point by more than _hysteresisC, OR the target is higher (ramp up promptly,
        // ramp down lazily).
        if (_lastTempApplied != int.MinValue)
        {
            if (target > _lastDuty)
            {
                // ramp up immediately
            }
            else if (tempC > _lastTempApplied - _hysteresisC)
            {
                // within the deadband on the way down: hold previous duty
                return _lastDuty;
            }
        }

        _lastTempApplied = tempC;
        _lastDuty = target;
        return target;
    }

    private int Interpolate(int tempC)
    {
        if (tempC <= _points[0].temp) return _points[0].duty;
        if (tempC >= _points[^1].temp) return _points[^1].duty;

        for (int i = 1; i < _points.Length; i++)
        {
            var (t1, d1) = _points[i - 1];
            var (t2, d2) = _points[i];
            if (tempC <= t2)
            {
                double frac = (double)(tempC - t1) / (t2 - t1);
                return (int)Math.Round(d1 + frac * (d2 - d1));
            }
        }
        return _points[^1].duty;
    }

    public override string ToString() => string.Join("  ", _points.Select(p => $"{p.temp}C->{p.duty}%"));
}
