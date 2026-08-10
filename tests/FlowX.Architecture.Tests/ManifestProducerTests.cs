using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace FlowX.Architecture.Tests;

/// <summary>
/// The rule that a field in the published schema has something that writes it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why absence is the dangerous direction.</strong> The manifest schema is
/// <c>additionalProperties: false</c> at every level, so every field in it is a promise about
/// what a consumer will be told. A field nothing produces breaks that promise silently: the
/// document does not say "unavailable", it says nothing, and a reader takes nothing to mean
/// <em>this application has none</em>. "This application has no owner recorded" and "the
/// compiler never looked for one" are opposite facts reported by identical bytes.
/// </para>
/// <para>
/// <strong>Why a gate and not a review.</strong>
/// <a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0017-manifest-v1-freeze-criteria.md">ADR-0017</a>
/// §1 did this audit by hand and wrote down what it found. Recomputing it here finds paths
/// that audit missed — the whole top-level <c>policies</c> catalogue among them, five paths
/// whose leaf name <em>is</em> written, but at a different path, which is exactly the mistake
/// a human comparing two lists makes and a set difference does not.
/// </para>
/// <para>
/// <strong>What the register is for.</strong> The debt is real and most of it is not closable
/// in one commit — several rows need a DSL addition, and one needs JSON Schema generation
/// that does not exist. So the rule is not "no unproduced field"; it is <em>no unproduced
/// field nobody wrote down</em>. Adding a field to the schema without a producer fails until
/// a row with a reason exists; writing a producer fails until the row is removed. The set can
/// shrink and cannot silently grow.
/// </para>
/// </remarks>
public sealed partial class ManifestProducerTests
{
    /// <summary>Pulls a manifest out of the C# constant the generator emits it into.</summary>
    [GeneratedRegex(@"public const string Json = @""(?<json>.*)"";", RegexOptions.Singleline)]
    private static partial Regex EmittedJson();

    /// <summary>
    /// Every schema field path is either produced by a build or written down as debt.
    /// </summary>
    /// <remarks>
    /// Both directions are checked, and the second is the one that keeps the register honest:
    /// a row for a field that is now produced would let the register grow into a list of
    /// excuses nobody rereads. Closing a field is supposed to cost one deletion here.
    /// </remarks>
    [Fact]
    public void EverySchemaFieldIsProducedOrRecordedAsDebt()
    {
        var declared = SchemaPaths();
        var produced = ProducedPaths();
        var recorded = Register();

        declared.Count.ShouldBeGreaterThan(
            0,
            "No field path was read out of the manifest schema, so this gate compared two "
            + "empty sets and would pass against any schema at all.");

        produced.Count.ShouldBeGreaterThan(
            0,
            "No emitted manifest was found under obj/generated. Build the solution before "
            + "running the architecture gates: with nothing produced, every field looks like "
            + "debt and the register looks complete by accident.");

        var problems = new List<string>();

        problems.AddRange(declared
            .Where(path => !produced.Contains(path) && !recorded.ContainsKey(path))
            .Select(path =>
                $"'{path}' is declared in the schema and no build produces it, and it is not "
                + "in schemas/unproduced-fields.json. A consumer reading it absent is told "
                + "the application has none, which is not what is true."));

        problems.AddRange(recorded.Keys
            .Where(produced.Contains)
            .Select(path =>
                $"'{path}' is recorded as unproduced and a build now produces it. Delete the "
                + "row: a register that keeps rows after they are closed stops being read."));

        problems.AddRange(recorded.Keys
            .Where(path => !declared.Contains(path))
            .Select(path =>
                $"'{path}' is recorded as unproduced and the schema no longer declares it. "
                + "Delete the row; there is no promise left to break."));

        problems.AddRange(recorded
            .Where(entry => string.IsNullOrWhiteSpace(entry.Value))
            .Select(entry =>
                $"'{entry.Key}' is recorded with no reason. A debt nobody stated the shape of "
                + "is one the next reader either closes wrongly or leaves forever."));

        problems.ShouldBeEmpty(
            "The published schema and what this repository actually writes have diverged "
            + "(ADR-0005, ADR-0017, quality goal Q3):"
            + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    /// <summary>Every field path the schema declares, following <c>$ref</c> and arrays.</summary>
    /// <remarks>
    /// Guarded against the schema's own recursion — a step contains branches which contain
    /// steps — by refusing to re-enter a definition already on the path. Without the guard
    /// this walk does not terminate, which is a thing worth knowing before somebody tries to
    /// write the same loop somewhere else.
    /// </remarks>
    private static HashSet<string> SchemaPaths()
    {
        var file = new FileInfo(Path.Combine(
            RepositoryLayout.Root.FullName, "schemas", "flowx.manifest.schema.json"));

        file.Exists.ShouldBeTrue($"{file.FullName} is the contract this gate reads.");

        using var document = JsonDocument.Parse(File.ReadAllText(file.FullName));
        var root = document.RootElement;
        var definitions = root.TryGetProperty("$defs", out var defs) ? defs : default;
        var paths = new HashSet<string>(StringComparer.Ordinal);

        Walk(root, string.Empty, Array.Empty<string>());

        return paths;

        void Walk(JsonElement node, string prefix, string[] seen)
        {
            if (node.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (node.TryGetProperty("$ref", out var reference))
            {
                var name = reference.GetString()?.Split('/')[^1];

                if (name is not null
                    && definitions.ValueKind == JsonValueKind.Object
                    && definitions.TryGetProperty(name, out var definition)
                    && Array.IndexOf(seen, name) < 0)
                {
                    Walk(definition, prefix, [.. seen, name]);
                }

                return;
            }

            if (node.TryGetProperty("items", out var items))
            {
                Walk(items, prefix, seen);
                return;
            }

            if (!node.TryGetProperty("properties", out var properties))
            {
                return;
            }

            foreach (var property in properties.EnumerateObject())
            {
                var path = prefix.Length == 0 ? property.Name : prefix + "." + property.Name;

                paths.Add(path);
                Walk(property.Value, path, seen);
            }
        }
    }

    /// <summary>Every path present in any manifest a build in this repository emitted.</summary>
    /// <remarks>
    /// The union across every project, not one sample: a field only one application declares
    /// is still produced, and asking a single manifest would report it as debt. Read from
    /// <c>obj/generated</c> rather than from a committed copy, for
    /// <c>PublishedContractTests</c>'s reason — that file is the artifact, and a committed
    /// copy is a claim about a build rather than the build.
    /// </remarks>
    private static HashSet<string> ProducedPaths()
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in RepositoryLayout.Root
                     .EnumerateFiles("FlowXManifest.g.cs", SearchOption.AllDirectories)
                     .Where(file => file.FullName.Replace('\\', '/').Contains("/obj/", StringComparison.Ordinal)))
        {
            var match = EmittedJson().Match(File.ReadAllText(file.FullName));

            if (!match.Success)
            {
                continue;
            }

            using var document = JsonDocument.Parse(
                match.Groups["json"].Value.Replace("\"\"", "\"", StringComparison.Ordinal));

            Walk(document.RootElement, string.Empty);
        }

        return paths;

        void Walk(JsonElement node, string prefix)
        {
            switch (node.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in node.EnumerateObject())
                    {
                        var path = prefix.Length == 0 ? property.Name : prefix + "." + property.Name;

                        paths.Add(path);
                        Walk(property.Value, path);
                    }

                    break;

                case JsonValueKind.Array:
                    foreach (var item in node.EnumerateArray())
                    {
                        Walk(item, prefix);
                    }

                    break;

                default:
                    break;
            }
        }
    }

    private static Dictionary<string, string> Register()
    {
        var file = new FileInfo(Path.Combine(
            RepositoryLayout.Root.FullName, "schemas", "unproduced-fields.json"));

        file.Exists.ShouldBeTrue(
            $"{file.FullName} is the record of which promises the schema makes and no build "
            + "keeps. Without it this gate has nothing to compare the difference against.");

        using var document = JsonDocument.Parse(File.ReadAllText(file.FullName));

        return document.RootElement.GetProperty("unproduced")
            .EnumerateArray()
            .ToDictionary(
                entry => entry.GetProperty("path").GetString() ?? string.Empty,
                entry => entry.TryGetProperty("why", out var why) ? why.GetString() ?? string.Empty : string.Empty,
                StringComparer.Ordinal);
    }

}
