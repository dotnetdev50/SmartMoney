using System;
using System.Collections.Generic;
using System.Linq;
using SmartMoney.Domain.Entities;

namespace SmartMoney.Application.Services;

public class NormalizationService
{
    private const int RollingWindow = 20;
    private const double MaxZScore = 3.0;

    // Tolerance used to avoid exact floating-point equality checks
    private const double StdDevTolerance = 1e-9;

    public double Normalize(List<double> historicalValues, double todayValue)
    {
        if (historicalValues.Count < RollingWindow)
            return 0; // not enough data yet

        var window = historicalValues
            .TakeLast(RollingWindow)
            .ToList();

        var mean = window.Average();
        var stdDev = CalculateStdDev(window, mean);

        // Use a tolerance when comparing floating-point values instead of exact equality
        if (Math.Abs(stdDev) < StdDevTolerance)
            return 0;

        var z = (todayValue - mean) / stdDev;

        // Clamp extreme spikes (war, crash etc)
        z = Math.Max(-MaxZScore, Math.Min(MaxZScore, z));

        // Scale to -100 to +100
        return (z / MaxZScore) * 100;
    }

    private double CalculateStdDev(List<double> values, double mean)
    {
        var variance = values.Sum(v => Math.Pow(v - mean, 2)) / values.Count;
        return Math.Sqrt(variance);
    }
}