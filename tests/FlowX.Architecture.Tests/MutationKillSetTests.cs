using System.Text.Json;
using Shouldly;
using Xunit;

namespace FlowX.Architecture.Tests;

/// <summary>
/// The register of mutations that prove this repository's gates can fail.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why a register and not a mutation-testing tool.</strong> Under xUnit v3 every test
/// project builds as its own executable, and <c>dotnet-stryker</c> 4.16.0 does not carry
/// mutant activation across that boundary: it injects mutants, the adapter runs all the
/// tests, no mutant is ever active, and the score is 0.00 % by construction. Scoped to one
/// file it reported 16 of 16 mutants surviving while the same mutation made by hand failed
/// five tests. The <c>dotnettest</c> runner exits 1 after startup with no diagnostic. A tool
/// whose number is produced by its own defect is worse than no tool, because the number gets
/// read.
/// </para>
/// <para>
/// <strong>What this register is instead.</strong> Narrower and true. It does not score every
/// mutable line; it records, for the gates that carry the architecture rules, a mutation that
/// <em>compiles</em> and the tests it kills. The quality workflow applies each patch, builds,
/// runs the named tests and requires them to fail. A gate that has never been shown to fail
/// is indistinguishable from a gate that cannot see, and this is the difference written down.
/// </para>
/// <para>
/// <strong>This test is the cheap half.</strong> Applying a patch and rebuilding costs minutes,
/// so that runs in the quality workflow. What runs here, in the ordinary suite, is everything
/// that can go wrong with the register without a build: a patch that no longer applies, a test
/// filter naming a test that has been renamed away, an entry with no reason. Each of those
/// turns the expensive job into a red that looks like a real finding, and each is findable in
/// milliseconds.
/// </para>
/// </remarks>
public sealed class MutationKillSetTests
{
    private static readonly DirectoryInfo Mutations =
        new(Path.Combine(RepositoryLayout.Root.FullName, "tests", "mutations"));

    /// <summary>
    /// Every entry names a patch that still applies and a test that still exists.
    /// </summary>
    /// <remarks>
    /// The applies-cleanly half is <c>git apply --check</c>, which answers the question
    /// without touching the tree. A patch goes stale the first time somebody edits the lines
    /// around the mutation, and a stale patch fails the expensive job with a merge error that
    /// reads nothing like "this gate stopped being proven".
    /// </remarks>
    [Fact]
    public void EveryRecordedMutationStillApplies()
    {
        var entries = Register();

        entries.Count.ShouldBeGreaterThan(
            0,
            "The mutation register is empty, so the quality workflow proves nothing and says "
            + "so to nobody.");

        var problems = new List<string>();

        foreach (var entry in entries)
        {
            var patch = new FileInfo(Path.Combine(Mutations.FullName, entry.Patch));

            if (!patch.Exists)
            {
                problems.Add($"{entry.Patch}: the register names a patch that is not there.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.Kills))
            {
                problems.Add($"{entry.Patch}: names no test, so applying it proves nothing.");
            }

            if (string.IsNullOrWhiteSpace(entry.Why))
            {
                problems.Add(
                    $"{entry.Patch}: records no reason. A mutation whose point nobody wrote "
                    + "down is one the next reader deletes.");
            }

            var (exit, output) = Git($"apply --check --verbose \"{patch.FullName}\"");

            if (exit != 0)
            {
                problems.Add(
                    $"{entry.Patch}: no longer applies to this tree. The code around the "
                    + $"mutation moved and the proof went stale with it. {output.Trim()}");
            }
        }

        problems.ShouldBeEmpty(
            "The mutation register does not describe this tree. Until it does, the gates it "
            + "covers are unproven and the quality workflow will fail for the wrong reason:"
            + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// Every test the register expects to kill is a test this project declares.
    /// </summary>
    /// <remarks>
    /// A filter that matches nothing makes <c>dotnet test</c> exit non-zero for having run no
    /// tests, which the expensive job would read as a kill. That is the one failure mode of
    /// this whole arrangement that produces a green light from an empty run, so it is checked
    /// against the source rather than against a run.
    /// </remarks>
    [Fact]
    public void EveryExpectedKillNamesATestThatExists()
    {
        var declared = SourceSurvey
            .SourceFiles("tests")
            .Where(file => file.Directory?.Name == "FlowX.Architecture.Tests")
            .SelectMany(file => File.ReadAllText(file.FullName).Split('\n'))
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("public void ", StringComparison.Ordinal))
            .Select(line => line["public void ".Length..].Split('(')[0])
            .ToHashSet(StringComparer.Ordinal);

        declared.Count.ShouldBeGreaterThan(
            0,
            "No test method was found in this project's own source, so this check has no "
            + "names to compare the register against.");

        var missing = Register()
            .Select(entry => entry.Kills.Split('~')[^1])
            .Select(name => name.Split('.')[^1])
            .Where(name => !declared.Contains(name))
            .ToList();

        missing.ShouldBeEmpty(
            "The register expects a test that no longer exists. Its filter matches nothing, "
            + "and a `dotnet test` run that matched nothing exits non-zero — which the "
            + "quality workflow would read as the mutation having been killed: "
            + string.Join(", ", missing));
    }

    private static List<Entry> Register()
    {
        var file = new FileInfo(Path.Combine(Mutations.FullName, "register.json"));

        file.Exists.ShouldBeTrue(
            $"{file.FullName} is the record of which gates have been proven able to fail. "
            + "Without it the quality workflow has nothing to run.");

        using var document = JsonDocument.Parse(File.ReadAllText(file.FullName));

        return document.RootElement.GetProperty("mutations")
            .EnumerateArray()
            .Select(entry => new Entry(
                entry.GetProperty("patch").GetString() ?? string.Empty,
                entry.GetProperty("kills").GetString() ?? string.Empty,
                entry.TryGetProperty("why", out var why) ? why.GetString() ?? string.Empty : string.Empty))
            .ToList();
    }

    private static (int Exit, string Output) Git(string arguments)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = RepositoryLayout.Root.FullName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;

        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();

        return (process.ExitCode, output);
    }

    private readonly record struct Entry(string Patch, string Kills, string Why);
}
