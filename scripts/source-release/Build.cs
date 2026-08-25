using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;

// Stages the source files that the allowed projects need.
class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.Stage);

    [Parameter("Repository root to stage from (absolute).")]
    readonly AbsolutePath RepoRoot;

    [Parameter("Allow-list file: one customer-facing csproj per line, relative to --repo-root " +
               "or absolute. Blank lines and '#' comments are ignored.")]
    readonly string AllowList;

    [Parameter("Output staging directory (created if missing).")]
    readonly AbsolutePath StagingDir;

    [Parameter("Optional .slnx filename at the repo root to mirror. When unset, the first .slnx " +
               "at the root (filesystem order) is used.")]
    readonly string SolutionFile;

    static readonly string[] ItemTypes =
    [
        "Compile", "None", "Content", "EmbeddedResource",
        "AvaloniaResource", "AvaloniaXaml", "AdditionalFiles"
    ];

    Target Stage => _ => _
        .Executes(() =>
        {
            if (RepoRoot is null)
                Fail("--repo-root is required.");
            if (StagingDir is null)
                Fail("--staging-dir is required.");
            if (string.IsNullOrWhiteSpace(AllowList))
                Fail("--allow-list is required.");

            var repoRoot = (AbsolutePath)Canonicalize(RepoRoot);
            if (!Directory.Exists(repoRoot))
                Fail($"Repository root not found at '{repoRoot}'.");

            var allowListPath = Path.IsPathRooted(AllowList) ? AllowList : Path.Combine(repoRoot, AllowList);
            if (!File.Exists(allowListPath))
                Fail($"Allow-list file not found at '{AllowList}' (resolved to '{allowListPath}').");

            var stagingDir = (AbsolutePath)Path.GetFullPath(StagingDir);
            Directory.CreateDirectory(stagingDir);

            Log.Information("Repo root:    {RepoRoot}", repoRoot);
            Log.Information("Allow list:   {AllowList}", allowListPath);
            Log.Information("Staging dir:  {StagingDir}", stagingDir);

            var projects = ReadAllowList(allowListPath, repoRoot);
            Log.Information("Customer-facing projects ({Count}):", projects.Count);
            foreach (var p in projects)
                Log.Information("  {Project}", p);

            var tempDir = (AbsolutePath)Path.Combine(Path.GetTempPath(), "source-release-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                var candidates = new List<string>();
                foreach (var csproj in projects)
                {
                    Log.Information("Enumerating {Project}...", csproj);
                    foreach (var tfm in ResolveTargetFrameworks(repoRoot, csproj, tempDir))
                    {
                        Log.Information("  - {Project}  (TargetFramework={Tfm})", csproj, tfm);
                        Enumerate(repoRoot, csproj, tfm, tempDir, candidates);
                    }
                }

                var staged = FilterToRepoRelative(candidates, repoRoot);
                Log.Information("Collected {Count} repo-relative source files.", staged.Count);

                Log.Information("Staging files into {StagingDir}...", stagingDir);
                foreach (var rel in staged)
                    CopyPreservingMode(repoRoot / rel, stagingDir / rel);

                WriteCustomerSolution(repoRoot, stagingDir, projects);
            }
            finally
            {
                Directory.Delete(tempDir, recursive: true);
            }
        });

    List<string> ReadAllowList(string allowListPath, AbsolutePath repoRoot)
    {
        var projects = new List<string>();
        foreach (var raw in File.ReadLines(allowListPath))
        {
            var line = raw;
            var hash = line.IndexOf('#');
            if (hash >= 0)
                line = line[..hash];
            line = line.Trim();
            if (line.Length == 0)
                continue;
            if (!File.Exists(Path.Combine(repoRoot, line)))
                Fail($"Project does not exist: {line}");
            projects.Add(line);
        }

        if (projects.Count == 0)
            Fail("Allow-list is empty.");
        return projects;
    }

    IEnumerable<string> ResolveTargetFrameworks(AbsolutePath repoRoot, string csproj, AbsolutePath tempDir)
    {
        var root = RunGet(repoRoot, csproj, tfm: null, tempDir,
            getItems: [], getProperties: ["TargetFrameworks", "TargetFramework"]);
        var props = GetObject(root, "Properties");
        var multi = GetString(props, "TargetFrameworks");
        var single = GetString(props, "TargetFramework");
        var list = string.IsNullOrWhiteSpace(multi) ? single : multi;
        var tfms = list
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (tfms.Count == 0)
            Fail($"Project {csproj} defines neither TargetFramework nor TargetFrameworks.");
        return tfms;
    }

    // MSBuild get mode supplies project items and properties. Preprocessing supplies imported files.
    void Enumerate(AbsolutePath repoRoot, string csproj, string tfm, AbsolutePath tempDir, List<string> candidates)
    {
        var root = RunGet(repoRoot, csproj, tfm, tempDir,
            getItems: ItemTypes,
            getProperties: ["MSBuildProjectFullPath", "AssemblyOriginatorKeyFile"]);

        var items = GetObject(root, "Items");
        if (items.ValueKind == JsonValueKind.Object)
        {
            foreach (var itemType in items.EnumerateObject())
            {
                foreach (var item in itemType.Value.EnumerateArray())
                {
                    var full = GetString(item, "FullPath");
                    candidates.Add(string.IsNullOrEmpty(full) ? GetString(item, "Identity") : full);
                }
            }
        }

        var props = GetObject(root, "Properties");
        candidates.Add(GetString(props, "MSBuildProjectFullPath"));
        var snk = GetString(props, "AssemblyOriginatorKeyFile");
        if (!string.IsNullOrEmpty(snk))
            candidates.Add(snk);

        // The -pp output lists imported files in its first comment block.
        var ppFile = tempDir / $"pp-{Guid.NewGuid():N}.xml";
        RunDotnet(repoRoot, "msbuild", csproj, $"-pp:{ppFile}", $"-p:TargetFramework={tfm}", "-nologo");
        foreach (var raw in File.ReadLines(ppFile))
        {
            var line = raw.Trim();
            if (line.StartsWith('/') && (line.EndsWith(".props", StringComparison.Ordinal)
                                         || line.EndsWith(".targets", StringComparison.Ordinal)))
                candidates.Add(line);
        }
    }

    // Resolve all symlinks before the repository containment check.
    SortedSet<string> FilterToRepoRelative(IEnumerable<string> candidates, AbsolutePath repoRoot)
    {
        var prefix = repoRoot + Path.DirectorySeparatorChar.ToString();
        var staged = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var raw in candidates)
        {
            if (string.IsNullOrEmpty(raw))
                continue;
            var abs = Path.IsPathRooted(raw) ? raw : Path.Combine(repoRoot, raw);
            var real = Canonicalize(abs);
            if (!real.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            if (!File.Exists(real))
            {
                Log.Warning("Skipping enumerated file that does not exist: {File}", real);
                continue;
            }
            staged.Add(Path.GetRelativePath(repoRoot, real));
        }
        return staged;
    }

    void WriteCustomerSolution(AbsolutePath repoRoot, AbsolutePath stagingDir, IReadOnlyList<string> projects)
    {
        AbsolutePath origSlnx;
        if (!string.IsNullOrWhiteSpace(SolutionFile))
        {
            origSlnx = repoRoot / SolutionFile;
            if (!File.Exists(origSlnx))
                Fail($"Specified solution file not found at {origSlnx}.");
        }
        else
        {
            origSlnx = repoRoot.GlobFiles("*.slnx").FirstOrDefault()
                       ?? FailReturn<AbsolutePath>($"No .slnx found at {repoRoot}.");
        }

        var customerSlnx = stagingDir / origSlnx.Name;
        var sb = new StringBuilder();
        sb.AppendLine("<Solution>");
        foreach (var csproj in projects)
            sb.AppendLine($"  <Project Path=\"{csproj.Replace('\\', '/')}\" />");
        sb.AppendLine("</Solution>");
        File.WriteAllText(customerSlnx, sb.ToString());

        Log.Information("Wrote {Slnx}:\n{Content}", customerSlnx, sb.ToString().TrimEnd());
    }

    // Keep MSBuild log output separate from the JSON result.
    JsonElement RunGet(AbsolutePath workingDir, string csproj, string? tfm, AbsolutePath tempDir,
        string[] getItems, string[] getProperties)
    {
        var outFile = tempDir / $"get-{Guid.NewGuid():N}.json";
        var args = new List<string> { "msbuild", csproj, "-nologo" };
        if (getItems.Length > 0)
            args.Add("-getItem:" + string.Join(',', getItems));
        if (getProperties.Length > 0)
            args.Add("-getProperty:" + string.Join(',', getProperties));
        if (tfm is not null)
            args.Add($"-p:TargetFramework={tfm}");
        args.Add($"-getResultOutputFile:{outFile}");

        RunDotnet(workingDir, args.ToArray());
        using var doc = JsonDocument.Parse(File.ReadAllText(outFile));
        return doc.RootElement.Clone();
    }

    static void RunDotnet(AbsolutePath workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start dotnet.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            Log.Error("dotnet {Args}\n{Stdout}\n{Stderr}",
                string.Join(' ', args), stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
            Fail($"`dotnet {string.Join(' ', args)}` failed with exit code {process.ExitCode}.");
        }
    }

    static void CopyPreservingMode(AbsolutePath src, AbsolutePath dst)
    {
        Directory.CreateDirectory(dst.Parent);
        File.Copy(src, dst, overwrite: true);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(dst, File.GetUnixFileMode(src));
    }

    // On Unix, realpath returns the same physical path format as MSBuild.
    [DllImport("libc", SetLastError = true, EntryPoint = "realpath")]
    static extern IntPtr Realpath(string path, IntPtr resolved);

    static string Canonicalize(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            var ptr = Realpath(path, IntPtr.Zero);
            if (ptr != IntPtr.Zero)
            {
                try
                {
                    return Marshal.PtrToStringUTF8(ptr) ?? Path.GetFullPath(path);
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
        }
        return Path.GetFullPath(path);
    }

    static JsonElement GetObject(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value)
            ? value
            : default;

    static string GetString(JsonElement obj, string name)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value))
            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
        return "";
    }

    [DoesNotReturn]
    static void Fail(string message)
    {
        // GitHub Actions reads this workflow command as an error annotation.
        Console.WriteLine($"::error::{message}");
        throw new Exception(message);
    }

    [DoesNotReturn]
    static T FailReturn<T>(string message)
    {
        Fail(message);
        return default!;
    }
}
