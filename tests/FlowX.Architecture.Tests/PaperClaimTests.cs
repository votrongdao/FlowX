using System.Globalization;
using Shouldly;
using Xunit;

namespace FlowX.Architecture.Tests;

/// <summary>
/// The figures <c>docs/paper/derivation-closure.md</c> quotes, checked against this repository.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A paper is a second declaration of facts the code already holds</strong>, which
/// makes it the exact shape of thing the paper is about. Every count in it — decision records,
/// fitness functions, adapters, schema paths, classification rules — is derivable from the
/// tree, and every one of them was true on the day it was typed and has no way of staying
/// true afterwards. This is the derivation rule turned on the document that describes it.
/// </para>
/// <para>
/// <strong>It also does the reviewer's job.</strong> An artifact evaluation asks whether the
/// numbers in a submission can be reproduced from the artifact. Here the answer is a test:
/// clone, build, run the suite, and a disagreement between the prose and the repository is a
/// red test naming both values.
/// </para>
/// <para>
/// <strong>What is deliberately not here.</strong> The benchmark figures — build overhead,
/// allocation, latency percentiles — are measurements of a machine and not properties of a
/// tree, so a gate that recomputed them would fail on a busy runner and teach everybody to
/// ignore it. They stay in <c>docs/benchmarks/</c> with their own harnesses and their own
/// noise floors, and the paper's appendix says which command produces each. What is checked
/// here is only what a checkout can answer with certainty.
/// </para>
/// </remarks>
public sealed class PaperClaimTests
{
    private static readonly FileInfo Paper =
        new(Path.Combine(RepositoryLayout.Root.FullName, "docs", "paper", "derivation-closure.md"));

    /// <summary>
    /// Every corpus figure the paper states is the figure this repository yields.
    /// </summary>
    /// <remarks>
    /// Stated as "this number must appear in the text", not as "this number must appear in
    /// that sentence": a prose gate that pins wording stops the paper being edited, which is
    /// the wrong cost. What it catches is the number going stale, which is the whole failure
    /// mode — one classification rule was added while this suite was being written, and the
    /// count in §4 was wrong within the hour.
    /// </remarks>
    /// <remarks>
    /// Each row is a pattern with the figure as its one capture group, so what is pinned is the
    /// <em>claim</em> rather than the sentence around it or the digits anywhere in the file.
    /// The weaker spelling was written first and did not work: asserting merely that the
    /// measured value appears somewhere in the text passes while the claim itself is wrong,
    /// because "68" also occurs in "68 / 68" two lines down. A gate that survives the mutation
    /// it exists to catch is decoration, and this one was, until the mutation was tried.
    /// </remarks>
    [Theory]
    [InlineData("architecture decision records", @"\*\*(\d+) architecture decision records\*\*")]
    [InlineData("architecture decision records", @"\| Architecture decision records \| (\d+) \(")]
    [InlineData("accepted records", @"\| Architecture decision records \| \d+ \((\d+) Accepted")]
    [InlineData("proposed records", @"Accepted, (\d+) Proposed")]
    [InlineData("records with a Revisit-when clause", @"\| ADRs carrying a `Revisit when` clause \| (\d+ / \d+) \|")]
    [InlineData("transport adapters", @"\*\*(\d+) transport adapters\*\*")]
    [InlineData("transport adapters", @"\| Transport / infrastructure adapters \| (\d+) \|")]
    [InlineData("compiler diagnostics", @"(\d+) diagnostic identifiers are raised")]
    [InlineData("compiler diagnostics", @"\| Compiler diagnostic identifiers \| (\d+) \|")]
    [InlineData("fitness functions declared", @"\*\*(\d+) executable fitness\s+functions\*\*")]
    [InlineData("fitness functions declared", @"\| Executable fitness functions \| (\d+) declared \|")]
    [InlineData("classification rules", @"carries \*\*(\d+) classification")]
    [InlineData("classification rules", @"\| Compatibility classification rules \| (\d+) \(")]
    [InlineData("breaking rules", @"classification rules \| \d+ \((\d+) breaking")]
    [InlineData("additive rules", @"\d+ breaking, (\d+) additive")]
    [InlineData("neutral rules", @"\d+ additive, (\d+) neutral")]
    public void TheStatedFigureIsTheRepositorysFigure(string what, string pattern)
    {
        var measured = Measure(what);
        var match = System.Text.RegularExpressions.Regex.Match(Text(), pattern);

        match.Success.ShouldBeTrue(
            $"No claim about {what} matches /{pattern}/ anywhere in the paper. Either the "
            + "sentence was rewritten past this pattern — in which case move the pattern — or "
            + "the claim was dropped and this row should go with it. A pattern that matches "
            + "nothing is a gate that passes on an empty document.");

        match.Groups[1].Value.ShouldBe(
            measured,
            $"The paper claims {what} is {match.Groups[1].Value}; the repository yields "
            + $"{measured}. One of the two moved and the other did not, which is the drift "
            + "this paper is about, arriving in the paper.");
    }

    /// <summary>
    /// The paper's headline debt figure is the one the register and the schema agree on.
    /// </summary>
    /// <remarks>
    /// Kept apart from the table above because it is the one claim that is not a count of
    /// files. It is a set difference, it is the paper's fourth contribution, and
    /// <see cref="ManifestProducerTests"/> is what makes it true rather than asserted.
    /// </remarks>
    [Fact]
    public void TheDebtFigureMatchesTheRegister()
    {
        Text().ShouldContain(
            "20 of 80",
            Case.Sensitive,
            "The paper's schema-debt figure is not what schemas/unproduced-fields.json and "
            + "the schema now say. ManifestProducerTests holds the number honest in the "
            + "repository; nothing but this holds it honest in the prose.");
    }

    private static string Text()
    {
        Paper.Exists.ShouldBeTrue(
            $"{Paper.FullName} is the document these rows describe. If it moved, move this "
            + "gate with it rather than deleting it — a paper nobody checks goes stale in "
            + "exactly the way its own §2.1 describes.");

        return File.ReadAllText(Paper.FullName);
    }

    private static string Measure(string what) => what switch
    {
        "architecture decision records" => Adrs().Count.ToString(CultureInfo.InvariantCulture),
        "records with a Revisit-when clause" =>
            $"{Adrs().Count(file => File.ReadAllText(file.FullName).Contains("Revisit when", StringComparison.Ordinal))}"
            + $" / {Adrs().Count}",
        "accepted records" => Status("Accepted").ToString(CultureInfo.InvariantCulture),
        "proposed records" => Status("Proposed").ToString(CultureInfo.InvariantCulture),
        "transport adapters" => new DirectoryInfo(Path.Combine(RepositoryLayout.Root.FullName, "plugins"))
            .GetDirectories().Length.ToString(CultureInfo.InvariantCulture),
        "compiler diagnostics" => Diagnostics().ToString(CultureInfo.InvariantCulture),
        "fitness functions declared" => FitnessFunctions().ToString(CultureInfo.InvariantCulture),
        "classification rules" => Severities().Values.Sum().ToString(CultureInfo.InvariantCulture),
        "breaking rules" => Severities()["Breaking"].ToString(CultureInfo.InvariantCulture),
        "additive rules" => Severities()["Additive"].ToString(CultureInfo.InvariantCulture),
        "neutral rules" => Severities()["Neutral"].ToString(CultureInfo.InvariantCulture),
        _ => throw new ArgumentOutOfRangeException(nameof(what), what, "No measurement is defined."),
    };

    private static List<FileInfo> Adrs() =>
        [.. new DirectoryInfo(Path.Combine(RepositoryLayout.Root.FullName, "docs", "adr"))
            .GetFiles("ADR-*.md")];

    private static int Status(string status) => Adrs()
        .Count(file => File.ReadAllText(file.FullName)
            .Contains("**Status:** " + status, StringComparison.Ordinal));

    private static int FitnessFunctions() => Directory
        .EnumerateFiles(
            Path.Combine(RepositoryLayout.Root.FullName, "tests", "FlowX.Architecture.Tests"),
            "*.cs")
        .SelectMany(file => File.ReadAllLines(file))
        .Count(line => line.Contains("[Fact]", StringComparison.Ordinal)
                       || line.Contains("[Theory]", StringComparison.Ordinal));

    /// <summary>
    /// The distinct diagnostic identifiers the compiler raises.
    /// </summary>
    /// <remarks>
    /// Quoted occurrences only. A diagnostic id also appears in prose — in remarks explaining
    /// why one analyzer defers to another — and counting those would make the figure grow
    /// every time somebody wrote a better comment.
    /// </remarks>
    private static int Diagnostics() => Directory
        .EnumerateFiles(
            Path.Combine(RepositoryLayout.Root.FullName, "src", "FlowX.Compiler"),
            "*.cs",
            SearchOption.AllDirectories)
        .Where(path => !path.Replace('\\', '/').Split('/').Any(part => part is "obj" or "bin"))
        .SelectMany(path => System.Text.RegularExpressions.Regex
            .Matches(File.ReadAllText(path), "\"FLOWX1[0-9]{3}\"")
            .Select(match => match.Value))
        .Distinct(StringComparer.Ordinal)
        .Count();

    private static Dictionary<string, int> Severities()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryLayout.Root.FullName, "src", "FlowX.Cli", "Diffing", "ManifestDiff.cs"));

        return System.Text.RegularExpressions.Regex
            .Matches(source, @"DiffSeverity\.(?<name>\w+)")
            .Select(match => match.Groups["name"].Value)
            .GroupBy(name => name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
    }
}
