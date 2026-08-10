namespace FlowX.Benchmarks;

/// <summary>
/// The performance budgets from
/// <a href="../../docs/14-Performance.md">14-Performance</a>, as code.
/// </summary>
/// <remarks>
/// <para>
/// Duplicated from the document deliberately: a budget that lives only in prose is
/// a budget nothing can fail against.
/// </para>
/// <para>
/// <strong>This file said a script read it, and none did.</strong> The claim was that
/// "the budget-checking script reads these values from the benchmark output";
/// <c>scripts/check-benchmark-budgets.py</c> reads <c>docs/benchmarks/baseline.json</c>
/// and has never referenced this table, and a search for <c>Budgets.</c> across the
/// repository returned nothing at all. Three budgets stated in percentiles sat in a file
/// with no reader, and the two written as p99 had never been measured by anything —
/// BenchmarkDotNet computes its statistics over iterations, and an iteration is a mean.
/// </para>
/// <para>
/// <c>LatencyPercentiles</c> now measures them per invocation and prints the distribution
/// beside these numbers: <c>dotnet run -c Release --project tests/FlowX.Benchmarks --
/// percentiles</c>. It prints and does not fail, because a shared runner cannot resolve a
/// tail — so these are still not gated, and that is now a statement about the hardware
/// rather than about the harness.
/// </para>
/// <para>
/// Only the budgets that are <em>measurable today</em> appear here. The rest are
/// listed as unmeasurable with the work package that makes them real, so nobody has
/// to guess whether a missing number means "passing" or "never ran".
/// </para>
/// </remarks>
public static class Budgets
{
    /// <summary>B3 — capability dispatch, p99. The floor the generator must approach.</summary>
    public const double CapabilityDispatchP99Nanoseconds = 150;

    /// <summary>B1 — 4-step ephemeral flow overhead, p50.</summary>
    public const double FlowOverheadP50Microseconds = 1.5;

    /// <summary>B1 — 4-step ephemeral flow overhead, p99. The kill criterion for P0.</summary>
    public const double FlowOverheadP99Microseconds = 5.0;

    /// <summary>
    /// The tolerance a benchmark may drift from its committed baseline before CI
    /// fails. Five per cent is tight enough to catch a real regression and loose
    /// enough to survive a noisy shared runner.
    /// </summary>
    public const double RegressionTolerancePercent = 5.0;
}
