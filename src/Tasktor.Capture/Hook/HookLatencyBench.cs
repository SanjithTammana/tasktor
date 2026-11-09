using System.Diagnostics;

namespace Tasktor.Capture.Hook;

/// <summary>
/// C19 acceptance 3 (hook p99.9 ≤ 50 µs at 1000 Hz mouse input): times the work the mouse hook callback does per event,
/// decode (<see cref="HookInput.TryMouse"/>) plus the ring write, on synthetic input. Coordinates are a synthetic pattern,
/// never real pointer positions; only timings come out.
/// </summary>
public static class HookLatencyBench
{
    public const double BudgetUs = 50;

    public sealed record Result(int Samples, double P50Us, double P99Us, double P999Us, double MaxUs)
    {
        public bool WithinBudget => P999Us <= BudgetUs;

        public override string ToString() =>
            FormattableString.Invariant($"samples {Samples}: p50 {P50Us:0.000} µs, p99 {P99Us:0.000} µs, p99.9 {P999Us:0.000} µs, max {MaxUs:0.000} µs (budget p99.9 ≤ {BudgetUs} µs)");
    }

    /// <param name="hz">Events a second (1000 for the acceptance run); 0 runs them back to back.</param>
    public static Result Run(IHookInputSink sink, int samples, int hz)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentOutOfRangeException.ThrowIfLessThan(samples, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(hz);

        // Timed on a thread at the hook thread's own priority, so the tail measures what the real callback would see.
        Result? result = null;
        var thread = new Thread(() => result = Measure(sink, samples, hz)) { IsBackground = true, Priority = LowLevelInputHook.HookThreadPriority };
        thread.Start();
        thread.Join();
        return result!;
    }

    private static Result Measure(IHookInputSink sink, int samples, int hz)
    {
        for (var i = 0; i < 1000; i++) Body(sink, i); // warm up (tiered JIT) before timing

        var ticks = new long[samples];
        var period = hz == 0 ? 0 : Stopwatch.Frequency / hz;
        var next = Stopwatch.GetTimestamp();
        for (var i = 0; i < samples; i++)
        {
            if (period > 0)
            {
                next += period;
                while (Stopwatch.GetTimestamp() < next) Thread.SpinWait(8);
            }

            var start = Stopwatch.GetTimestamp();
            Body(sink, i);
            ticks[i] = Stopwatch.GetTimestamp() - start;
        }

        Array.Sort(ticks);
        double Us(long t) => t * 1_000_000.0 / Stopwatch.Frequency;
        double At(double p) => Us(ticks[Math.Clamp((int)Math.Ceiling(p * samples) - 1, 0, samples - 1)]);
        return new Result(samples, At(0.5), At(0.99), At(0.999), Us(ticks[^1]));
    }

    // Exactly the callback's per-event work, minus the native transition and CallNextHookEx.
    private static void Body(IHookInputSink sink, int i)
    {
        var message = (i & 15) == 0 ? 0x0201u : 0x0200u;
        if (HookInput.TryMouse(message, i & 1023, (i >> 10) & 1023, 0, 0, unchecked((uint)i), out var item)) sink.Write(in item);
    }
}
