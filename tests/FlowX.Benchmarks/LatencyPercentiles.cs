using System.Diagnostics;
using FlowX.Runtime;

namespace FlowX.Benchmarks;

/// <summary>
/// The p50 and p99 that <see cref="Budgets"/> states, measured per invocation.
/// </summary>
/// <remarks>
/// <para>
/// <strong>BenchmarkDotNet cannot answer this, and it was being asked to.</strong> Its
/// statistics are computed over <em>iterations</em>, and an iteration is the mean of many
/// invocations — so a percentile of those is a percentile of averages, which is not a latency
/// distribution and is not what a budget written as "p99 ≤ 5 µs" means. The harness also ran
/// ten iterations and emitted no percentile column at all, so nothing in this repository had
/// ever produced the two numbers <see cref="Budgets"/> is written in.
/// </para>
/// <para>
/// <strong>What this measures instead.</strong> One timestamp either side of one
/// <c>ExecuteAsync</c>, recorded for every invocation, sorted, and read at the ranks that
/// matter. That is a real distribution, and it is the only shape that can answer a tail
/// question.
/// </para>
/// <para>
/// <strong>The timer is measured too, and reported rather than subtracted.</strong> At a mean
/// near 160 ns, two <see cref="Stopwatch.GetTimestamp"/> calls are not free relative to the
/// thing being timed. Subtracting an average from a distribution would flatter the tail — the
/// overhead has a tail of its own — so the empty-loop distribution is printed beside the real
/// one and the reader subtracts nothing. A p99 that is only interesting after an adjustment is
/// not a p99 anybody should cite.
/// </para>
/// <para>
/// <strong>Not gated, and deliberately.</strong> A shared CI runner cannot separate this
/// signal from its own scheduling: <c>docs/benchmarks/baseline.json</c> records two runs of one
/// commit disagreeing by 159 % on absolute time. This prints, it does not fail. Run it on
/// isolated hardware before quoting the number anywhere.
/// </para>
/// </remarks>
public static class LatencyPercentiles
{
    /// <summary>Invocations per arm. Enough that the 99.9th percentile has a thousand samples.</summary>
    private const int Samples = 1_000_000;

    /// <summary>Invocations discarded before recording, so tiered JIT has settled.</summary>
    private const int Warmup = 100_000;

    /// <summary>Measures the four-step flow and prints its latency distribution.</summary>
    public static async Task RunAsync()
    {
        var engine = new FlowEngine(new FixedClock());
        var dispatcher = new NullDispatcher();
        var invocation = new FlowInvocation("perc-corr", "perc-idem", "perc-tenant");

        var validate = CapabilityDescriptor.Create("order.validate", "1.0.0", true);
        var reserve = CapabilityDescriptor.Create("inventory.reserve", "1.0.0", true, "inventory-ledger");
        var release = CapabilityDescriptor.Create("inventory.release", "1.0.0", true, "inventory-ledger");
        var capture = CapabilityDescriptor.Create("payment.capture", "2.1.0", false, "payment-gateway");
        var refund = CapabilityDescriptor.Create("payment.refund", "2.1.0", true, "payment-gateway");

        var query = ExecutionPlan.Create(
            FlowDescriptor.Create("order.get", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
            StepGraph.Create([
                StepNode.ForCapability(0, validate),
                StepNode.ForCapability(1, validate),
                StepNode.ForCapability(2, validate),
                StepNode.ForEmit(3, "order.read"),
            ]));

        var saga = ExecutionPlan.Create(
            FlowDescriptor.Create("order.place", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30)),
            StepGraph.Create([
                StepNode.ForCapability(0, validate),
                StepNode.ForCapability(1, reserve, release),
                StepNode.ForCapability(2, capture, refund),
                StepNode.ForEmit(3, "order.placed"),
            ]));

        Console.WriteLine($"Runtime      : {Environment.Version}, {Environment.ProcessorCount} logical cores");
        Console.WriteLine($"Samples      : {Samples:N0} per arm, after {Warmup:N0} discarded");
        Console.WriteLine($"Timer        : {(Stopwatch.IsHighResolution ? "high resolution" : "LOW RESOLUTION")}, "
                          + $"{Stopwatch.Frequency:N0} ticks/s");
        Console.WriteLine();

        Report("empty loop (the timer itself)", MeasureTimerOverhead());
        Report("4-step flow, no compensation", await MeasureAsync(engine, query, dispatcher, invocation));
        Report("4-step saga, success path", await MeasureAsync(engine, saga, dispatcher, invocation));

        Console.WriteLine();
        Console.WriteLine($"Budget p50   : {Budgets.FlowOverheadP50Microseconds} µs");
        Console.WriteLine($"Budget p99   : {Budgets.FlowOverheadP99Microseconds} µs");
        Console.WriteLine();
        Console.WriteLine("Printed, not gated. See the remarks in LatencyPercentiles.cs for why, and do not");
        Console.WriteLine("quote a figure measured on a shared runner.");
    }

    private static async Task<double[]> MeasureAsync(
        FlowEngine engine, ExecutionPlan plan, IStepDispatcher dispatcher, FlowInvocation invocation)
    {
        for (var i = 0; i < Warmup; i++)
        {
            _ = await engine.ExecuteAsync(plan, dispatcher, invocation).ConfigureAwait(false);
        }

        var ticks = new double[Samples];
        var perTick = 1_000_000_000.0 / Stopwatch.Frequency;

        for (var i = 0; i < Samples; i++)
        {
            var start = Stopwatch.GetTimestamp();
            _ = await engine.ExecuteAsync(plan, dispatcher, invocation).ConfigureAwait(false);
            ticks[i] = (Stopwatch.GetTimestamp() - start) * perTick;
        }

        return ticks;
    }

    private static double[] MeasureTimerOverhead()
    {
        var ticks = new double[Samples];
        var perTick = 1_000_000_000.0 / Stopwatch.Frequency;

        for (var i = 0; i < Warmup; i++)
        {
            _ = Stopwatch.GetTimestamp();
        }

        for (var i = 0; i < Samples; i++)
        {
            var start = Stopwatch.GetTimestamp();
            ticks[i] = (Stopwatch.GetTimestamp() - start) * perTick;
        }

        return ticks;
    }

    private static void Report(string name, double[] samples)
    {
        Array.Sort(samples);

        Console.WriteLine($"{name}");
        Console.WriteLine(
            $"  p50 {Rank(samples, 50),9:N1} ns   p90 {Rank(samples, 90),9:N1} ns   "
            + $"p99 {Rank(samples, 99),9:N1} ns   p99.9 {Rank(samples, 99.9),9:N1} ns   "
            + $"max {samples[^1],11:N1} ns");
    }

    /// <summary>The nearest-rank percentile, which needs no interpolation to defend.</summary>
    private static double Rank(double[] sorted, double percentile)
    {
        var rank = (int)Math.Ceiling(percentile / 100 * sorted.Length);

        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
    }

    private sealed class NullDispatcher : IStepDispatcher
    {
        public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Success);

        public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct)
            => ValueTask.FromResult(StepOutcome.Success);

        public bool Evaluate(int stepIndex, FlowContext ctx)
            => throw new NotSupportedException("This double runs plans with no branch step.");

        public IterationSource BeginIteration(int stepIndex, FlowContext ctx)
            => throw new NotSupportedException("This dispatcher has no iteration to begin.");

        public FlowContext EnterIteration(int stepIndex, in IterationSource source, int iteration, FlowContext ctx)
            => throw new NotSupportedException("This dispatcher has no iteration to enter.");

        public int Select(int stepIndex, FlowContext ctx)
            => throw new NotSupportedException("This double runs plans with no switch step.");
    }
}
