using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Benzene.Test.Docs;

/// <summary>
/// One compilable program assembled from a documentation page: every fenced C# block in that page
/// tagged with the same unit name, in document order.
/// </summary>
public class DocSnippet
{
    public DocSnippet(string docPath, string unit, string sdk, IReadOnlyList<string> blocks)
    {
        DocPath = docPath;
        Unit = unit;
        Sdk = sdk;
        Blocks = blocks;
    }

    /// <summary>The doc's path relative to the repo root, e.g. <c>docs/getting-started-aws.md</c>.</summary>
    public string DocPath { get; }

    /// <summary>The unit name from the directive, unique within the doc.</summary>
    public string Unit { get; }

    /// <summary>
    /// The project SDK the guide tells the reader to create — <c>lib</c> (default) or <c>web</c>.
    /// It decides which implicit usings are in scope, so it must match what the guide's
    /// <c>dotnet new</c> step actually produces.
    /// </summary>
    public string Sdk { get; }

    /// <summary>The fenced blocks belonging to this unit, in document order.</summary>
    public IReadOnlyList<string> Blocks { get; }

    public override string ToString() => $"{DocPath} [{Unit}]";
}

/// <summary>
/// Finds and compiles the documentation snippets that are meant to be real, complete code.
/// </summary>
/// <remarks>
/// <para>
/// A quickstart that does not compile is the most expensive documentation bug there is — the reader
/// has no prior model to notice it against, so they assume the mistake is theirs. Every
/// getting-started guide in this repo omitted <c>.AddBenzene()</c> and therefore threw on the first
/// message, for as long as nothing checked.
/// </para>
/// <para>
/// <strong>Opt-in by directive, not by scanning every fence.</strong> Most C# blocks in the docs are
/// deliberate fragments — one <c>Use*</c> call, a middleware body, a line showing an option. Trying to
/// compile those would produce noise that trains people to ignore the check. A block only enters a
/// compilation when the line before it says:
/// </para>
/// <code>
/// &lt;!-- compile: quickstart --&gt;
/// </code>
/// <para>
/// Blocks sharing a unit name within one page are concatenated in document order, so a guide that
/// builds its program across several steps (handler, then StartUp, then entry point) is checked as
/// the single program the reader ends up with.
/// </para>
/// <para>
/// <strong>This does not prove the snippet is correct, only that it builds.</strong> Compilation
/// would not have caught the missing <c>.AddBenzene()</c> on its own — that is a runtime resolve
/// failure. What catches it is a unit that is also exercised by a test (see
/// <c>QuickstartSnippetsTest</c>), which is why the AWS quickstart's unit is built *and* run.
/// </para>
/// </remarks>
public static class DocSnippetCompiler
{
    private static readonly Regex Directive = new(
        @"^<!--\s*compile:\s*(?<unit>[A-Za-z0-9._-]+)(\s+sdk=(?<sdk>lib|web|worker))?\s*-->\s*$", RegexOptions.Compiled);
    private static readonly Regex FenceOpen = new(@"^\s*```\s*(csharp|cs|c#)\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FenceClose = new(@"^\s*```\s*$", RegexOptions.Compiled);

    // What `dotnet new classlib` gives a net10.0 project. The docs are written against that project
    // shape, so a snippet using Directory or Task without naming System.IO/System.Threading.Tasks is
    // correct as printed — the check has to model the reader's project, not a bare compilation.
    private const string LibraryImplicitUsings = """
        global using global::System;
        global using global::System.Collections.Generic;
        global using global::System.IO;
        global using global::System.Linq;
        global using global::System.Net.Http;
        global using global::System.Threading;
        global using global::System.Threading.Tasks;
        """;

    // Microsoft.NET.Sdk.Web adds these on top. Kept separate rather than merged into one permissive
    // set: a guide whose reader runs `dotnet new classlib` must not be allowed to omit a using that
    // only a web project would have supplied. The doc says which it is (`sdk=web`).
    private const string WebImplicitUsings = """
        global using global::System.Net.Http.Json;
        global using global::Microsoft.AspNetCore.Builder;
        global using global::Microsoft.AspNetCore.Hosting;
        global using global::Microsoft.AspNetCore.Http;
        global using global::Microsoft.AspNetCore.Routing;
        global using global::Microsoft.Extensions.Configuration;
        global using global::Microsoft.Extensions.DependencyInjection;
        global using global::Microsoft.Extensions.Hosting;
        global using global::Microsoft.Extensions.Logging;
        """;

    // `dotnet new worker`: plain Microsoft.NET.Sdk, but Microsoft.Extensions.Hosting contributes
    // these four through its build props, which is why the worker template's Program.cs can call
    // Host.CreateDefaultBuilder with no using at all.
    private const string WorkerImplicitUsings = """
        global using global::Microsoft.Extensions.Configuration;
        global using global::Microsoft.Extensions.DependencyInjection;
        global using global::Microsoft.Extensions.Hosting;
        global using global::Microsoft.Extensions.Logging;
        """;

    /// <summary>
    /// The repo root, found by walking up from the test binaries until the docs tree appears.
    /// </summary>
    public static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "docs")) &&
                Directory.Exists(Path.Combine(directory.FullName, "src")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Could not locate the repo root above {AppContext.BaseDirectory}");
    }

    /// <summary>
    /// Every tagged snippet across the docs tree, one entry per (page, unit).
    /// </summary>
    public static IReadOnlyList<DocSnippet> Discover()
    {
        var root = RepoRoot();

        return Directory
            .EnumerateFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories)
            .OrderBy(x => x, StringComparer.Ordinal)
            .SelectMany(path => Parse(path, Path.GetRelativePath(root, path).Replace('\\', '/')))
            .ToArray();
    }

    private static IEnumerable<DocSnippet> Parse(string path, string relativePath)
    {
        var lines = File.ReadAllLines(path);
        var units = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var sdks = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var directive = Directive.Match(lines[i]);
            if (!directive.Success)
            {
                continue;
            }

            // The fence must open on the next non-blank line, or the directive is pointing at nothing
            // and would silently cover no code at all.
            var fence = i + 1;
            while (fence < lines.Length && lines[fence].Trim().Length == 0)
            {
                fence++;
            }

            if (fence >= lines.Length || !FenceOpen.IsMatch(lines[fence]))
            {
                throw new InvalidOperationException(
                    $"{relativePath}:{i + 1} has a compile directive that is not followed by a ```csharp block.");
            }

            var body = new StringBuilder();
            var line = fence + 1;
            for (; line < lines.Length && !FenceClose.IsMatch(lines[line]); line++)
            {
                body.AppendLine(lines[line]);
            }

            var unit = directive.Groups["unit"].Value;
            if (!units.TryGetValue(unit, out var blocks))
            {
                units[unit] = blocks = new List<string>();
                sdks[unit] = "lib";
                order.Add(unit);
            }

            // Any block in a unit may carry the sdk, since it is a property of the project all the
            // blocks live in, not of one block.
            if (directive.Groups["sdk"].Success)
            {
                sdks[unit] = directive.Groups["sdk"].Value;
            }

            blocks.Add(body.ToString());
            i = line;
        }

        return order.Select(unit => new DocSnippet(relativePath, unit, sdks[unit], units[unit]));
    }

    /// <summary>
    /// Compiles a snippet against the same assemblies this test project references, and returns the
    /// errors. An empty result means it builds.
    /// </summary>
    /// <remarks>
    /// Each block becomes its own syntax tree rather than being concatenated, because that is what it
    /// is: a guide that spans several steps is telling the reader to write several files, each with
    /// its own using directives and possibly its own file-scoped namespace. Gluing them together
    /// would fail on things the reader's project would not — a second set of usings after the first
    /// declaration, or a Program.cs of top-level statements following a namespaced class.
    /// </remarks>
    public static IReadOnlyList<Diagnostic> Compile(DocSnippet snippet) =>
        Build(snippet).GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error).ToArray();

    /// <summary>
    /// Compiles a snippet and loads it, so a test can run the code the docs actually print.
    /// </summary>
    /// <remarks>
    /// Compiling proves less than it looks. The bug this whole check exists for — a quickstart with no
    /// <c>.AddBenzene()</c> — compiles perfectly and fails on the first message. Only running it
    /// catches that, which is why the AWS quickstart is executed rather than merely built.
    /// </remarks>
    public static Assembly Emit(DocSnippet snippet)
    {
        using var stream = new MemoryStream();
        var result = Build(snippet).Emit(stream);

        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"{snippet} failed to emit:{Environment.NewLine}" +
                string.Join(Environment.NewLine, result.Diagnostics.Where(x => x.Severity == DiagnosticSeverity.Error)));
        }

        return Assembly.Load(stream.ToArray());
    }

    private static CSharpCompilation Build(DocSnippet snippet)
    {
        var implicitUsings = LibraryImplicitUsings + Environment.NewLine + snippet.Sdk switch
        {
            "web" => WebImplicitUsings,
            "worker" => WorkerImplicitUsings,
            _ => string.Empty
        };

        var trees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(implicitUsings) };
        trees.AddRange(snippet.Blocks.Select((block, i) =>
            CSharpSyntaxTree.ParseText(block, path: $"{snippet.DocPath} [{snippet.Unit}] block {i + 1}")));

        // A guide whose last step is a Program.cs of top-level statements is an executable; one that
        // only ever declares types (a Lambda assembly, an Azure Functions assembly) is a library, and
        // asking it for an entry point would fail it for a reason the reader would never hit.
        var hasEntryPoint = trees.Any(tree => tree.GetRoot().ChildNodes()
            .Any(node => node is GlobalStatementSyntax));

        // The assembly name is unique per doc+unit so two guides that both declare a StartUp can be
        // loaded into the same test process without colliding.
        return CSharpCompilation.Create(
            $"DocSnippet_{snippet.DocPath.Replace('/', '_').Replace(".md", string.Empty)}_{snippet.Unit}",
            trees,
            ReferenceAssemblies(),
            new CSharpCompilationOptions(
                hasEntryPoint ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Disable));
    }

    // TRUSTED_PLATFORM_ASSEMBLIES rather than the loaded assemblies: a project reference whose types
    // the test process has not touched yet is not loaded, and a snippet is exactly the thing likely to
    // be the first to touch it.
    private static IReadOnlyList<MetadataReference> ReferenceAssemblies() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Where(File.Exists)
        .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
        .ToArray();
}
