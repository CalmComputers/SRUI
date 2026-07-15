namespace Srui.Audio;

/// <summary>Device data-callback timing counters, read via
/// <see cref="SoundManager.GetCallbackStats"/>. Each callback must
/// produce one period of audio within <see cref="BudgetNs"/>; taking
/// longer is a buffer underrun.</summary>
/// <param name="Callbacks">Callbacks observed since start (or last reset).</param>
/// <param name="Overruns">Callbacks that exceeded their budget.</param>
/// <param name="MaxNs">Slowest callback observed, in nanoseconds.</param>
/// <param name="BudgetNs">Most recent per-callback budget, in nanoseconds.</param>
public readonly record struct CallbackStats(
    ulong Callbacks, ulong Overruns, ulong MaxNs, ulong BudgetNs)
{
    /// <summary>Fraction of callbacks that missed their deadline, 0..1.</summary>
    public double OverrunRate => Callbacks == 0 ? 0 : (double)Overruns / Callbacks;

    /// <summary>Worst callback duration as a fraction of the budget;
    /// above 1.0 means at least one underrun occurred.</summary>
    public double WorstLoad => BudgetNs == 0 ? 0 : (double)MaxNs / BudgetNs;
}
