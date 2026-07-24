using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using static Serilog.Log;

namespace NukeExtensions;

// Generates one CycloneDX SBOM per published NuGet package, using the already-restored
// solution (obj/project.assets.json) so component versions are the ones actually resolved
// into the build, rather than the floating PackageReference ranges GitHub's own dependency
// graph reports. Kept in sync with Avalonia's nukebuild/SbomGenerator.cs (which additionally
// reassembles Numerge merge groups); changes here should be mirrored there and vice versa.
//
// The Generate overload taking only a packages directory assumes each shipped .nupkg is the
// pack output of the identically-named source project, which holds for the component
// repositories that consume build-common. A repository that merges several projects' packed
// output into one shipped package should call GenerateForPackage directly with the full list
// of constituent projects, so the merged-away projects' dependencies still appear in the SBOM.
//
// GenerateForVsix does the same for a Visual Studio extension (.vsix), which is also an OPC
// package: the constituent scan, shipped-binary content check and embed machinery are shared,
// and only the manifest it reads (extension.vsixmanifest vs .nuspec) differs.
public static class SbomGenerator
{
    // The version parameter is optional: builds whose nuke script computes the package version
    // (and passes the same value to pack) supply it; builds where MSBuild computes the version
    // (e.g. from a shared props file plus a CI suffix) omit it, and each package's version is
    // read back from its own .nuspec - which by definition matches what was shipped.
    public static void Generate(
        Tool cycloneDx,
        AbsolutePath rootDirectory,
        AbsolutePath packagesDirectory,
        AbsolutePath outputDirectory,
        string? version = null)
    {
        outputDirectory.CreateOrCleanDirectory();

        var nupkgs = packagesDirectory.GlobFiles("*.nupkg");
        if (nupkgs.Count == 0)
        {
            // Silently producing zero SBOMs would look like success while shipping packages
            // without CRA evidence; a build that generates SBOMs must have packages to scan.
            throw new InvalidOperationException(
                $"SBOM: no .nupkg files found in {packagesDirectory} - was the SBOM target run before packing?");
        }

        foreach (var nupkg in nupkgs)
        {
            var meta = ReadNuspecMetadata((string)nupkg);
            GenerateForPackage(cycloneDx, rootDirectory, nupkg, outputDirectory, version ?? meta.Version, meta.Id,
                new[] { meta.Id });
        }
    }

    // projectSearchDirs: root-relative directories searched (recursively) for the constituent
    // projects' .csproj files; defaults to the src/packages layout the component repositories
    // use. Repositories with different layouts (e.g. Xpf's fork/ and tools/ trees) pass their own.
    //
    // additionalProductNames: binary names (assembly simple name or filename without extension)
    // that are first-party even though they match neither a constituent project id nor the
    // Avalonia.*/AvaloniaUI.* prefixes - e.g. a native library whose name differs from any
    // project (wpfgfx_xpf) - so the package-content scan records them as manufacturer-supplied
    // instead of flagging them as unaccounted third-party binaries.
    //
    // baseIntermediateOutputPath: forwarded to cyclonedx-dotnet as -biop for repositories whose
    // projects restore into <base>/obj/<ProjectName>/project.assets.json (the Arcade layout)
    // instead of <projectDir>/obj. Projects not found there fall back to cyclonedx-dotnet's
    // msbuild evaluation of ProjectAssetsFile, so a mixed-layout repository still resolves every
    // constituent correctly.
    public static void GenerateForPackage(Tool cycloneDx, AbsolutePath rootDirectory, AbsolutePath? packagePath,
        AbsolutePath outputDirectory, string version, string packageId, IReadOnlyList<string> constituentProjectIds,
        IReadOnlyList<string>? projectSearchDirs = null,
        IReadOnlyList<string>? additionalProductNames = null,
        AbsolutePath? baseIntermediateOutputPath = null)
    {
        var scan = ScanConstituentProjects(cycloneDx, rootDirectory, outputDirectory, version, packageId,
            constituentProjectIds, projectSearchDirs, baseIntermediateOutputPath);
        if (scan is null)
        {
            Warning($"SBOM: no source projects could be scanned for '{packageId}', no SBOM was generated for it.");
            return;
        }

        // The final .nupkg carries the authoritative publisher/license/repository metadata and the
        // actual shipped binaries; use it to flesh out the thin root component cyclonedx-dotnet
        // emits and to verify nothing ships that the dependency scan didn't already account for.
        PackageMetadata? meta = null;
        if (packagePath is not null)
            meta = ReadNuspecMetadata((string)packagePath);
        else
            Warning($"SBOM: couldn't find the built .nupkg for '{packageId}' - root metadata and package-content verification were skipped.");

        FinalizeAndEmit(scan.Value, meta, packagePath, outputDirectory, version, packageId,
            constituentProjectIds, additionalProductNames);
    }

    // Generates the SBOM for a Visual Studio extension (.vsix) and embeds it at the same
    // _manifest/cyclonedx/bom.cdx.json path used for .nupkgs. A VSIX is an OPC package like a
    // .nupkg, so the shipped-binary scan and the embed/content-type machinery are shared; the only
    // real differences are that its authoritative metadata lives in extension.vsixmanifest rather
    // than a .nuspec (there's no packed .nuspec inside a VSIX), and its root component is an
    // application rather than a library. Id and version are read from the manifest - they are what
    // was actually built into the shipped VSIX - so callers supply only the constituent projects.
    //
    // Because a VSIX IL-merges/bundles many projects into one deliverable, constituentProjectIds
    // must list every source project whose packed output ends up inside it, exactly as for a merged
    // .nupkg, so the merged-away projects' dependencies still appear in the SBOM. The primary
    // extension assembly is one of those constituents (the root component is the VSIX bundle, not
    // any single assembly), so it is recorded as a bundled component rather than as the root.
    public static void GenerateForVsix(Tool cycloneDx, AbsolutePath rootDirectory, AbsolutePath vsixPath,
        AbsolutePath outputDirectory, IReadOnlyList<string> constituentProjectIds,
        IReadOnlyList<string>? projectSearchDirs = null,
        IReadOnlyList<string>? additionalProductNames = null,
        AbsolutePath? baseIntermediateOutputPath = null)
    {
        var meta = ReadVsixManifestMetadata((string)vsixPath);
        var packageId = meta.Id;
        var version = meta.Version;

        var scan = ScanConstituentProjects(cycloneDx, rootDirectory, outputDirectory, version, packageId,
            constituentProjectIds, projectSearchDirs, baseIntermediateOutputPath);
        if (scan is null)
        {
            Warning($"SBOM: no source projects could be scanned for '{packageId}', no SBOM was generated for it.");
            return;
        }

        FinalizeAndEmit(scan.Value, meta, vsixPath, outputDirectory, version, packageId,
            constituentProjectIds, additionalProductNames);
    }

    // The merged CycloneDX document from scanning every constituent project, along with the
    // dedup/graph state the finalize phase needs to keep extending it consistently.
    readonly record struct ConstituentScan(
        JsonObject Merged, HashSet<string> SeenComponentKeys, List<AbsolutePath> ScannedProjectDirs);

    // Runs cyclonedx-dotnet over each constituent project against the already-restored solution and
    // merges the results into one document whose single root component is shared by every
    // constituent (via -sn/-sv). Package-format agnostic: it only reads source projects, so it's
    // identical for a .nupkg and a .vsix. Returns null if not one constituent could be located.
    static ConstituentScan? ScanConstituentProjects(Tool cycloneDx, AbsolutePath rootDirectory,
        AbsolutePath outputDirectory, string version, string packageId,
        IReadOnlyList<string> constituentProjectIds, IReadOnlyList<string>? projectSearchDirs,
        AbsolutePath? baseIntermediateOutputPath)
    {
        JsonObject? merged = null;
        var seenComponentKeys = new HashSet<string>();
        var scannedProjectDirs = new List<AbsolutePath>();

        var searchDirs = projectSearchDirs ?? new[] { "src", "packages" };

        foreach (var projectId in constituentProjectIds)
        {
            var project = searchDirs
                .SelectMany(dir => rootDirectory.GlobFiles($"{dir}/**/{projectId}.csproj"))
                .FirstOrDefault();
            if (project is null)
            {
                Warning($"SBOM: couldn't locate source project for '{projectId}', skipping it in the SBOM for '{packageId}'.");
                continue;
            }
            scannedProjectDirs.Add(project.Parent);

            var tempBom = outputDirectory / $"_{projectId}.tmp.json";
            // Two literal argument strings rather than a spliced-in fragment: Tool's argument
            // handler re-quotes interpolated values, so a pre-built " -biop ..." string would be
            // passed through as a single mangled argument.
            if (baseIntermediateOutputPath is null)
                cycloneDx(
                    $"\"{project}\" -o \"{outputDirectory}\" -fn \"{tempBom.Name}\" -F Json -dpr -ed -sn \"{packageId}\" -sv \"{version}\"",
                    workingDirectory: rootDirectory);
            else
                cycloneDx(
                    $"\"{project}\" -o \"{outputDirectory}\" -fn \"{tempBom.Name}\" -F Json -dpr -ed -sn \"{packageId}\" -sv \"{version}\" -biop \"{baseIntermediateOutputPath}\"",
                    workingDirectory: rootDirectory);

            var doc = JsonNode.Parse(File.ReadAllText(tempBom))!.AsObject();
            File.Delete(tempBom);

            var components = doc["components"]?.AsArray() ?? new JsonArray();
            if (merged is null)
            {
                merged = doc;
                foreach (var component in components)
                    seenComponentKeys.Add(ComponentKey(component));
            }
            else
            {
                var target = merged["components"]?.AsArray() ?? (JsonArray)(merged["components"] = new JsonArray());
                foreach (var component in components)
                {
                    if (seenComponentKeys.Add(ComponentKey(component)))
                        target.Add(component!.DeepClone());
                }

                // Every constituent project's own -sn/-sv override makes its root component (and
                // therefore its dependency-graph "ref") identical to the final package's, so merging
                // by ref correctly unions all constituents' dependsOn edges onto that shared root
                // instead of silently keeping only the first project's edges.
                MergeDependencyGraph(merged, doc["dependencies"]?.AsArray() ?? new JsonArray());
            }
        }

        return merged is null ? null : new ConstituentScan(merged, seenComponentKeys, scannedProjectDirs);
    }

    // Enriches the scanned dependency graph with everything only the shipped package can tell us -
    // bundled webapp dependencies, authoritative root metadata, declared package dependencies and
    // the actual shipped binaries - then writes the standalone SBOM and embeds a copy in the
    // package. Shared by the .nupkg and .vsix paths; the only per-format input is `meta` (read from
    // a .nuspec or an extension.vsixmanifest) and the package itself.
    static void FinalizeAndEmit(ConstituentScan scan, PackageMetadata? meta, AbsolutePath? packagePath,
        AbsolutePath outputDirectory, string version, string packageId,
        IReadOnlyList<string> constituentProjectIds, IReadOnlyList<string>? additionalProductNames)
    {
        var merged = scan.Merged;
        var seenComponentKeys = scan.SeenComponentKeys;

        // cyclonedx-dotnet only sees the MSBuild/NuGet graph. Some projects also bundle a
        // Bun/npm-built webapp directly into their published package - scan those separately
        // so their shipped JS dependencies aren't silently absent from the SBOM.
        var rootRef = merged["metadata"]?["component"]?["bom-ref"]?.GetValue<string>();
        foreach (var projectDir in scan.ScannedProjectDirs)
            AddNpmComponents(merged, seenComponentKeys, projectDir, rootRef);

        if (meta is not null && packagePath is not null)
        {
            EnrichRootComponent(merged, meta);
            AddDeclaredDependencyComponents(merged, seenComponentKeys, meta, rootRef);
            var productNames = additionalProductNames is null
                ? constituentProjectIds
                : constituentProjectIds.Concat(additionalProductNames).ToList();
            AddPackageContentComponents(merged, seenComponentKeys, (string)packagePath, packageId,
                productNames, meta);
        }

        // cyclonedx-dotnet leaves dependsOn edges pointing at packages it excluded as dev
        // dependencies (e.g. analyzers stripped by -ed), which dangle once the component is gone
        // (upstream bug CycloneDX/cyclonedx-dotnet#761, still reproducing in 6.2.0). Drop those so
        // the graph only references components actually present in the SBOM.
        PruneDanglingDependencyEdges(merged);

        var sbomJson = merged.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(outputDirectory / $"{packageId}.{version}.cdx.json", sbomJson);

        // Embed the SBOM inside the shipped package so it travels with it, in addition to the
        // standalone copy written above (which CI publishes as the SBOM artifact) - belt and
        // suspenders: consumers who only ever see the package still get its bill of materials.
        if (packagePath is not null)
            EmbedSbomInPackage(packagePath, sbomJson);
    }

    // The path inside the .nupkg where the CycloneDX SBOM is embedded. Mirrors the _manifest/
    // layout Microsoft.Sbom.Targets uses for its SPDX manifest, but keeps CycloneDX's recognised
    // *.cdx.json filename so tools that scan for that pattern still find it once unpacked.
    const string EmbeddedSbomEntryPath = "_manifest/cyclonedx/bom.cdx.json";

    // Adds the generated SBOM as a new part inside the shipped package. Must run before the package
    // is signed - a signature covers the whole archive, so adding a part afterwards would invalidate
    // it. That holds here: .nupkgs are signed server-side by nuget.org on push, and a .vsix has its
    // SBOM embedded during the merge/obfuscate repack, before any signing step - both after this.
    static void EmbedSbomInPackage(AbsolutePath packagePath, string sbomJson)
    {
        using var file = File.Open(packagePath, FileMode.Open, FileAccess.ReadWrite);
        using var zip = new ZipArchive(file, ZipArchiveMode.Update);

        // A NuGet signature is a .signature.p7s part; an OPC/VSIX signature lives under
        // package/services/digital-signature/. Either means the archive is already signed, so
        // adding a part now would break that signature - leave it alone.
        var isSigned = zip.Entries.Any(e =>
            e.FullName.EndsWith(".signature.p7s", StringComparison.OrdinalIgnoreCase)
            || e.FullName.StartsWith("package/services/digital-signature/", StringComparison.OrdinalIgnoreCase));
        if (isSigned)
        {
            Warning($"SBOM: '{packagePath.Name}' is already signed - skipping embed so its signature stays valid.");
            return;
        }

        // Re-embedding (e.g. a re-run over the same output) should replace, not stack duplicates.
        zip.GetEntry(EmbeddedSbomEntryPath)?.Delete();
        using (var entryStream = zip.CreateEntry(EmbeddedSbomEntryPath).Open())
        using (var writer = new StreamWriter(entryStream))
            writer.Write(sbomJson);

        EnsureJsonContentTypeRegistered(zip);
    }

    // A .nupkg is an OPC package: every part's extension must be declared in [Content_Types].xml or
    // strict OPC readers - including NuGet's own signature verification - reject the package. The
    // SBOM is a .json part, so register that extension before (or as) we add it.
    static void EnsureJsonContentTypeRegistered(ZipArchive zip)
    {
        const string contentTypesEntryName = "[Content_Types].xml";
        XNamespace ns = "http://schemas.openxmlformats.org/package/2006/content-types";

        var entry = zip.GetEntry(contentTypesEntryName);
        if (entry is null)
            return; // not a well-formed OPC package; don't fabricate one

        XDocument doc;
        using (var read = entry.Open())
            doc = XDocument.Load(read);

        var alreadyRegistered = doc.Root!.Elements(ns + "Default")
            .Any(d => string.Equals((string?)d.Attribute("Extension"), "json", StringComparison.OrdinalIgnoreCase));
        if (alreadyRegistered)
            return;

        doc.Root.Add(new XElement(ns + "Default",
            new XAttribute("Extension", "json"),
            new XAttribute("ContentType", "application/json")));

        entry.Delete();
        using var write = zip.CreateEntry(contentTypesEntryName).Open();
        doc.Save(write);
    }

    static void PruneDanglingDependencyEdges(JsonObject merged)
    {
        var deps = merged["dependencies"]?.AsArray();
        if (deps is null)
            return;

        var known = new HashSet<string>();
        var rootRef = merged["metadata"]?["component"]?["bom-ref"]?.GetValue<string>();
        if (rootRef is not null)
            known.Add(rootRef);
        foreach (var component in merged["components"]?.AsArray() ?? new JsonArray())
        {
            if (component?["bom-ref"]?.GetValue<string>() is { } bomRef)
                known.Add(bomRef);
            if (component?["purl"]?.GetValue<string>() is { } purl)
                known.Add(purl);
        }

        foreach (var node in deps.OfType<JsonObject>())
        {
            var dependsOn = node["dependsOn"]?.AsArray();
            if (dependsOn is null)
                continue;
            var kept = new JsonArray();
            foreach (var edge in dependsOn)
                if (known.Contains(edge!.GetValue<string>()))
                    kept.Add(edge.GetValue<string>());
            node["dependsOn"] = kept;
        }
    }

    static void MergeDependencyGraph(JsonObject target, JsonArray incoming)
    {
        var targetDeps = target["dependencies"]?.AsArray() ?? (JsonArray)(target["dependencies"] = new JsonArray());
        var byRef = targetDeps.OfType<JsonObject>().ToDictionary(d => d["ref"]!.GetValue<string>());

        foreach (var node in incoming.OfType<JsonObject>())
        {
            var nodeRef = node["ref"]!.GetValue<string>();
            var dependsOn = node["dependsOn"]?.AsArray().Select(x => x!.GetValue<string>()) ?? Enumerable.Empty<string>();

            if (!byRef.TryGetValue(nodeRef, out var existing))
            {
                existing = node.DeepClone().AsObject();
                targetDeps.Add(existing);
                byRef[nodeRef] = existing;
            }

            var existingDependsOn = existing["dependsOn"]?.AsArray() ?? (JsonArray)(existing["dependsOn"] = new JsonArray());
            var seen = existingDependsOn.Select(x => x!.GetValue<string>()).ToHashSet();
            foreach (var dep in dependsOn)
                if (seen.Add(dep))
                    existingDependsOn.Add(dep);
        }
    }

    // Scans <projectDir>/**/webapp/package.json for production "dependencies" (deliberately
    // ignoring devDependencies, which never ship) and adds them - plus their transitive
    // dependencies, walked through the installed node_modules - as fully-formed components:
    // bom-ref, resolved version, license, and dependency-graph edges, matching the shape of the
    // NuGet components cyclonedx-dotnet emits so npm packages aren't second-class SBOM entries.
    // Versions come from the actually-installed node_modules (same rationale as reading
    // project.assets.json rather than trusting floating ranges).
    static void AddNpmComponents(JsonObject merged, HashSet<string> seenComponentKeys, AbsolutePath projectDir,
        string? rootRef)
    {
        foreach (string packageJsonPath in projectDir.GlobFiles("**/webapp/package.json"))
        {
            var packageJson = JsonNode.Parse(File.ReadAllText(packageJsonPath))!.AsObject();
            var nodeModules = ((AbsolutePath)packageJsonPath).Parent / "node_modules";
            var dependencies = packageJson["dependencies"]?.AsObject() ?? new JsonObject();

            // The webapp is bundled into the shipped package, so its direct production
            // dependencies are direct dependencies of the final NuGet package.
            foreach (var (name, rangeNode) in dependencies)
            {
                if (IsTypeOnlyPackage(name))
                    continue;
                var purl = AddNpmComponentTree(merged, seenComponentKeys, nodeModules, name,
                    rangeNode!.GetValue<string>(), nodeModules);
                if (rootRef is not null)
                    AddDependsOn(merged, rootRef, purl);
            }
        }
    }

    // Adds the component for (name, range) and, recursively, everything it depends on, returning
    // its purl. The component/graph node/subtree are materialised only the first time a purl is
    // seen (which also breaks any dependency cycles); repeat encounters just return the purl so
    // the caller can still record its own edge to it.
    static string AddNpmComponentTree(JsonObject merged, HashSet<string> seenComponentKeys,
        AbsolutePath topLevelNodeModules, string name, string declaredRange, AbsolutePath parentNodeModules)
    {
        var (purl, componentVersion, installedDir) =
            ResolveNpmComponent(name, declaredRange, parentNodeModules, topLevelNodeModules);

        if (!seenComponentKeys.Add(purl))
            return purl;

        var installed = installedDir is not null && File.Exists(installedDir / "package.json")
            ? JsonNode.Parse(File.ReadAllText(installedDir / "package.json"))!.AsObject()
            : null;

        var component = new JsonObject
        {
            ["type"] = "library",
            ["bom-ref"] = purl,
            ["name"] = name,
            ["version"] = componentVersion,
            ["purl"] = purl
        };
        // No hashes: npm's verifiable hashes live in the (binary bun) lockfile, not the installed
        // tree, and a hash of the unpacked directory wouldn't be checkable against a registry.
        var licenses = installed is null ? null : BuildLicenses(installed);
        if (licenses is not null)
            component["licenses"] = licenses;

        var target = merged["components"]?.AsArray() ?? (JsonArray)(merged["components"] = new JsonArray());
        target.Add(component);

        var node = new JsonObject { ["ref"] = purl, ["dependsOn"] = new JsonArray() };
        (merged["dependencies"]?.AsArray() ?? (JsonArray)(merged["dependencies"] = new JsonArray())).Add(node);

        var childDeps = installed?["dependencies"]?.AsObject() ?? new JsonObject();
        var childNodeModules = installedDir is not null ? installedDir / "node_modules" : parentNodeModules;
        var dependsOn = node["dependsOn"]!.AsArray();
        foreach (var (childName, childRange) in childDeps)
        {
            if (IsTypeOnlyPackage(childName))
                continue;
            var childPurl = AddNpmComponentTree(merged, seenComponentKeys, topLevelNodeModules, childName,
                childRange!.GetValue<string>(), childNodeModules);
            dependsOn.Add(childPurl);
        }

        return purl;
    }

    // @types/* packages are TypeScript declaration stubs: esbuild strips them at build time, so
    // they're never part of the shipped bytes and don't belong in a scope-of-delivery SBOM.
    static bool IsTypeOnlyPackage(string name) => name.StartsWith("@types/", StringComparison.Ordinal);

    static void AddDependsOn(JsonObject merged, string fromRef, string toPurl)
    {
        var deps = merged["dependencies"]?.AsArray() ?? (JsonArray)(merged["dependencies"] = new JsonArray());
        var node = deps.OfType<JsonObject>().FirstOrDefault(d => d["ref"]?.GetValue<string>() == fromRef);
        if (node is null)
        {
            node = new JsonObject { ["ref"] = fromRef, ["dependsOn"] = new JsonArray() };
            deps.Add(node);
        }

        var dependsOn = node["dependsOn"]?.AsArray() ?? (JsonArray)(node["dependsOn"] = new JsonArray());
        if (!dependsOn.Any(x => x!.GetValue<string>() == toPurl))
            dependsOn.Add(toPurl);
    }

    static JsonArray? BuildLicenses(JsonObject installedPackageJson)
    {
        // Modern npm: "license" is an SPDX id or expression. Legacy: "license"/"licenses" objects.
        if (installedPackageJson["license"] is JsonValue licenseValue && licenseValue.TryGetValue(out string? spdx))
        {
            var licenses = SpdxToLicenses(spdx);
            if (licenses is not null)
                return licenses;
        }

        var legacy = (installedPackageJson["license"] as JsonObject)?["type"]?.GetValue<string>()
            ?? (installedPackageJson["licenses"] as JsonArray)?.OfType<JsonObject>()
                .FirstOrDefault()?["type"]?.GetValue<string>();
        return legacy is null
            ? null
            : new JsonArray(new JsonObject { ["license"] = new JsonObject { ["name"] = legacy } });
    }

    // Turns an SPDX string into a CycloneDX licenses array: a single license id becomes a
    // {license:{id}} entry, a compound SPDX expression becomes an {expression} entry.
    static JsonArray? SpdxToLicenses(string? spdx)
    {
        if (string.IsNullOrWhiteSpace(spdx))
            return null;
        var isExpression = spdx.IndexOf(" OR ", StringComparison.Ordinal) >= 0
            || spdx.IndexOf(" AND ", StringComparison.Ordinal) >= 0
            || spdx.IndexOf(" WITH ", StringComparison.Ordinal) >= 0;
        return new JsonArray(isExpression
            ? new JsonObject { ["expression"] = spdx }
            : new JsonObject { ["license"] = new JsonObject { ["id"] = spdx } });
    }

    static (string Purl, string Version, AbsolutePath? InstalledDir) ResolveNpmComponent(
        string name, string declaredRange, AbsolutePath parentNodeModules, AbsolutePath topLevelNodeModules)
    {
        // npm/bun hoist most packages to the top level but may nest a conflicting version under
        // the depending package, so prefer the nested copy and fall back to the hoisted one.
        var installedDir = new[] { parentNodeModules / name, topLevelNodeModules / name }
            .FirstOrDefault(d => File.Exists(d / "package.json"));

        if (declaredRange.StartsWith("github:") || declaredRange.StartsWith("git") || declaredRange.Contains("://"))
        {
            if (TryParseGitHubDependency(declaredRange, out var owner, out var repo, out var reference))
                return ($"pkg:github/{owner}/{repo}@{reference}", reference, installedDir);

            // A non-GitHub git/URL specifier (GitLab/Bitbucket, a raw tarball URL, ...). We have no
            // provider-specific purl for it, so degrade to a generic component - preferring the
            // version installed on disk - rather than throwing and failing the whole release over a
            // single dependency we can't classify precisely.
            var resolvedVersion = installedDir is not null
                ? JsonNode.Parse(File.ReadAllText(installedDir / "package.json"))!["version"]?.GetValue<string>()
                : null;
            resolvedVersion ??= declaredRange;
            Warning($"SBOM: npm dependency '{name}' uses an unrecognised git/URL specifier '{declaredRange}' - recording it as a generic component with version '{resolvedVersion}'.");
            return ($"pkg:generic/{EncodeNpmName(name)}@{resolvedVersion}", resolvedVersion, installedDir);
        }

        if (installedDir is null)
        {
            Warning($"SBOM: npm dependency '{name}' isn't installed near {parentNodeModules} - recording its declared range '{declaredRange}' instead of a resolved version.");
            return ($"pkg:npm/{EncodeNpmName(name)}@{declaredRange}", declaredRange, null);
        }

        var installedVersion = JsonNode.Parse(File.ReadAllText(installedDir / "package.json"))!["version"]?.GetValue<string>();
        if (installedVersion is null)
        {
            // package.json without a "version" is valid for private packages; don't let it NRE.
            Warning($"SBOM: npm dependency '{name}' installed at {installedDir} has no version in its package.json - recording its declared range '{declaredRange}' instead.");
            installedVersion = declaredRange;
        }
        return ($"pkg:npm/{EncodeNpmName(name)}@{installedVersion}", installedVersion, installedDir);
    }

    static string EncodeNpmName(string name) => name.StartsWith("@") ? $"%40{name[1..]}" : name;

    static bool TryParseGitHubDependency(string spec, out string owner, out string repo, out string reference)
    {
        owner = repo = "";
        var hashIndex = spec.IndexOf('#');
        reference = hashIndex >= 0 ? spec[(hashIndex + 1)..] : "HEAD";
        var withoutRef = hashIndex >= 0 ? spec[..hashIndex] : spec;

        var match = Regex.Match(withoutRef, @"github(?:\.com)?[:/]+([^/]+)/([^/#]+?)(?:\.git)?$");
        if (!match.Success)
            return false;
        owner = match.Groups[1].Value;
        repo = match.Groups[2].Value;
        return true;
    }

    // Prefer purl (or bom-ref) as the identity; both encode version. Fall back to name+version so
    // distinct versions of the same package aren't collapsed into one, and to a fresh GUID when
    // there's nothing identifiable to key on (treating it as unique rather than deduping blindly).
    static string ComponentKey(JsonNode? component) =>
        component?["purl"]?.GetValue<string>()
        ?? component?["bom-ref"]?.GetValue<string>()
        ?? (component?["name"]?.GetValue<string>() is { } name
            ? $"{name}@{component?["version"]?.GetValue<string>()}"
            : Guid.NewGuid().ToString());

    // Fills in the root component with the publisher, licence, description and repository details
    // from the shipped package's manifest (.nuspec or extension.vsixmanifest) - cyclonedx-dotnet
    // only emits type/name/version, which is far short of the manufacturer/provenance information a
    // CRA-facing SBOM is expected to carry.
    static void EnrichRootComponent(JsonObject merged, PackageMetadata meta)
    {
        var component = merged["metadata"]?["component"]?.AsObject();
        if (component is null)
            return;

        // Libraries (.nupkg) vs application/extension (.vsix); cyclonedx-dotnet's default is
        // "application" regardless, so the manifest decides.
        component["type"] = meta.ComponentType;
        // The manifest is authoritative for the shipped version. A build-supplied version that
        // disagrees means the packages predate the current restore, so the dependency data just
        // scanned may not describe what's actually inside them - fail rather than record it as
        // CRA evidence. Can't trigger in normal flows: builds pass the same version to pack and
        // to the SBOM scan within one run, and the .vsix/version-less .nupkg paths read the
        // version from the manifest itself.
        var scannedVersion = component["version"]?.GetValue<string>();
        if (scannedVersion is not null && scannedVersion != meta.Version)
            throw new InvalidOperationException(
                $"SBOM: '{meta.Id}' was scanned as version '{scannedVersion}' but the shipped package is '{meta.Version}' - are the packages stale?");
        component["version"] = meta.Version;
        component["purl"] = meta.Purl;
        if (meta.Description is not null)
            component["description"] = meta.Description;
        if (meta.Copyright is not null)
            component["copyright"] = meta.Copyright;

        JsonObject? supplier = null;
        if (meta.Authors is not null)
        {
            component["publisher"] = meta.Authors;
            component["author"] = meta.Authors;
            supplier = new JsonObject { ["name"] = meta.Authors };
            if (meta.ProjectUrl is not null)
                supplier["url"] = new JsonArray(meta.ProjectUrl);
            component["supplier"] = supplier;
        }

        var licenses = SpdxToLicenses(meta.LicenseExpression ?? meta.LicenseId);
        // A file license has no SPDX id; record it by name rather than emitting an invalid id.
        if (licenses is null && meta.LicenseFile is not null)
            licenses = new JsonArray(new JsonObject
                { ["license"] = new JsonObject { ["name"] = Path.GetFileName(meta.LicenseFile) } });
        if (licenses is not null)
            component["licenses"] = licenses;

        var externalReferences = new JsonArray();
        if (meta.ProjectUrl is not null)
            externalReferences.Add(new JsonObject { ["url"] = meta.ProjectUrl, ["type"] = "website" });
        if (meta.RepositoryUrl is not null)
            externalReferences.Add(new JsonObject { ["url"] = meta.RepositoryUrl, ["type"] = "vcs" });
        if (externalReferences.Count > 0)
            component["externalReferences"] = externalReferences;

        // Also record the manufacturer at the document level (the entity that supplied the BOM).
        if (supplier is not null && merged["metadata"] is JsonObject metadata)
            metadata["supplier"] = supplier.DeepClone();
    }

    // Cross-checks the SBOM against the shipped package's declared <dependencies>: everything
    // declared there is restored on the consumer's machine, so it's part of the delivered
    // dependency graph by definition. cyclonedx-dotnet's -ed flag drops packages referenced with
    // PrivateAssets as dev dependencies, but PrivateAssets only controls which *assets* flow to
    // consumers - a dependency that still appears in the nuspec (e.g. one kept for its
    // buildTransitive targets, like AvaloniaUI.Licensing) is delivered regardless, so re-add any
    // the scan excluded. A VSIX declares no such dependencies (meta.Dependencies is empty), so this
    // is a no-op for it - everything it delivers is already accounted for by the content scan.
    static void AddDeclaredDependencyComponents(JsonObject merged, HashSet<string> seenComponentKeys,
        PackageMetadata meta, string? rootRef)
    {
        var target = merged["components"]?.AsArray() ?? (JsonArray)(merged["components"] = new JsonArray());
        var representedNames = target
            .Select(c => c?["name"]?.GetValue<string>())
            .Where(n => n is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

        foreach (var (id, version) in meta.Dependencies)
        {
            // NuGet pack writes the resolved package version as the dependency's version, so this
            // is the same exact version project.assets.json would have reported.
            var purl = $"pkg:nuget/{id}@{version}";
            if (representedNames.Contains(id) || !seenComponentKeys.Add(purl))
                continue;

            target.Add(new JsonObject
            {
                ["type"] = "library",
                ["bom-ref"] = purl,
                ["name"] = id,
                ["version"] = version,
                ["purl"] = purl,
                ["scope"] = "required"
            });
            if (rootRef is not null)
                AddDependsOn(merged, rootRef, purl);
        }
    }

    // Cross-checks what the package actually ships against the components derived from the
    // dependency graph. Our own bundled modules are recorded as manufacturer-supplied components
    // with a verifiable SHA-512 of the shipped bytes; any third-party binary that no restored
    // dependency accounts for is added and flagged, so a future bundling regression can't
    // silently escape the SBOM.
    //
    // These shipped binaries are *constituents* of the package, not dependencies of it: the package
    // is made up of them (Component C bundles D and E, in CycloneDX's terms), so they're modelled as
    // a CycloneDX "assembly" via a top-level `compositions` entry rather than with a dependsOn edge
    // from the root - "contains" and "depends on" are different relationships, and an assembly does
    // not imply a dependency. They stay flat top-level components (not nested subcomponents) so their
    // per-assembly hashes remain visible to scanners that ignore nested components.
    static void AddPackageContentComponents(JsonObject merged, HashSet<string> seenComponentKeys,
        string nupkgPath, string packageId, IReadOnlyList<string> firstPartyNames, PackageMetadata meta)
    {
        var productNames = new HashSet<string>(firstPartyNames, StringComparer.OrdinalIgnoreCase) { packageId };
        var representedNames = (merged["components"]?.AsArray() ?? new JsonArray())
            .Select(c => c?["name"]?.GetValue<string>())
            .Where(n => n is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

        var supplier = meta.Authors is null ? null : new JsonObject { ["name"] = meta.Authors };
        var target = merged["components"]?.AsArray() ?? (JsonArray)(merged["components"] = new JsonArray());
        var rootRef = merged["metadata"]?["component"]?["bom-ref"]?.GetValue<string>();

        // The bundled first-party assemblies share the package's licence; cyclonedx-dotnet only puts
        // it on the root component, so carry it onto them too (same derivation as EnrichRootComponent).
        var productLicenses = SpdxToLicenses(meta.LicenseExpression ?? meta.LicenseId);
        if (productLicenses is null && meta.LicenseFile is not null)
            productLicenses = new JsonArray(new JsonObject
                { ["license"] = new JsonObject { ["name"] = Path.GetFileName(meta.LicenseFile) } });

        // bom-refs of every shipped binary added below; declared as a complete assembly at the end.
        var assemblyRefs = new List<string>();

        using var file = File.Open(nupkgPath, FileMode.Open, FileAccess.Read);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        foreach (var (path, bytes) in EnumerateShippedBinaries(zip))
        {
            var assemblyName = TryReadAssemblyName(bytes, out var assemblyVersion);
            var simpleName = assemblyName ?? BinaryName(path);

            // Third-party binaries already represented by a NuGet/npm component need no duplicate.
            if (assemblyName is not null && representedNames.Contains(simpleName))
                continue;

            var isProduct = productNames.Contains(simpleName)
                || simpleName.StartsWith("Avalonia.", StringComparison.OrdinalIgnoreCase)
                || simpleName.StartsWith("AvaloniaUI.", StringComparison.OrdinalIgnoreCase)
                || simpleName.Equals("Avalonia", StringComparison.OrdinalIgnoreCase);

            // The package's primary assembly is the root component itself - don't list it as its own subcomponent.
            if (isProduct && simpleName.Equals(packageId, StringComparison.OrdinalIgnoreCase))
                continue;

            var version = assemblyVersion ?? meta.Version;
            var bomRef = $"binary:{simpleName}@{version}";
            if (!seenComponentKeys.Add(bomRef))
                continue;

            if (!isProduct)
                Warning($"SBOM: package '{packageId}' ships '{path}' ({simpleName}) which no restored dependency accounts for - added from package contents, please verify its provenance.");

            var component = new JsonObject
            {
                ["type"] = "library",
                ["bom-ref"] = bomRef,
                ["name"] = simpleName,
                ["version"] = version,
                ["scope"] = "required",
                ["hashes"] = new JsonArray(new JsonObject
                {
                    ["alg"] = "SHA-512",
                    ["content"] = Convert.ToHexString(SHA512.HashData(bytes))
                }),
                ["properties"] = new JsonArray(new JsonObject
                {
                    ["name"] = "avalonia:packagePath",
                    ["value"] = path
                })
            };
            if (isProduct)
            {
                if (supplier is not null)
                    component["supplier"] = supplier.DeepClone();
                if (productLicenses is not null)
                    component["licenses"] = productLicenses.DeepClone();
            }
            target.Add(component);

            assemblyRefs.Add(bomRef);
        }

        // Declare the package's assembly: the root component consists of exactly these bundled
        // binaries. We've read every shipped binary out of the .nupkg, so the enumeration is
        // complete (third-party dependencies remain in the dependency graph, as they should).
        if (assemblyRefs.Count > 0)
        {
            var assemblies = new JsonArray();
            if (rootRef is not null)
                assemblies.Add(rootRef);
            foreach (var bomRef in assemblyRefs)
                assemblies.Add(bomRef);

            var compositions = merged["compositions"]?.AsArray()
                ?? (JsonArray)(merged["compositions"] = new JsonArray());
            compositions.Add(new JsonObject
            {
                ["aggregate"] = "complete",
                ["assemblies"] = assemblies
            });
        }
    }

    // Walks the package for shipped binaries, yielding each as (path inside the package, bytes).
    //
    // Descends into nested .zip entries: a NativeAOT tool package ships its macOS build as a
    // zipped .app bundle (re-zipping it in the .nupkg is what preserves the bundle structure and
    // the executable permission bits), so on that platform every shipped binary lives one level
    // down. Without this the macOS package would record no shipped binaries at all while the
    // Windows and Linux packages record theirs.
    static IEnumerable<(string Path, byte[] Bytes)> EnumerateShippedBinaries(
        ZipArchive archive, string pathPrefix = "")
    {
        foreach (var entry in archive.Entries)
        {
            // Directory entries have an empty Name and no content of their own.
            if (entry.Name.Length == 0)
                continue;

            var path = pathPrefix + entry.FullName;

            // Reference assemblies under ref/ are compile-time surface, not shipped runtime code;
            // the real implementation lives under lib/ and is scanned there. Only meaningful at the
            // package root - a ref/ directory inside a bundled app is that app's own layout.
            if (pathPrefix.Length == 0 && entry.FullName.StartsWith("ref/", StringComparison.OrdinalIgnoreCase))
                continue;

            if (Path.GetExtension(entry.FullName).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                // Buffered into memory because ZipArchive needs a seekable stream, which the
                // deflate stream of an entry is not.
                using var nestedBytes = new MemoryStream(ReadEntry(entry));
                using var nested = new ZipArchive(nestedBytes, ZipArchiveMode.Read);
                foreach (var inner in EnumerateShippedBinaries(nested, path + "/"))
                    yield return inner;
                continue;
            }

            if (!IsShippedBinary(entry.FullName) && !HasNativeExecutableHeader(entry))
                continue;

            yield return (path, ReadEntry(entry));
        }
    }

    static bool IsShippedBinary(string path) => IsBinaryExtension(Path.GetExtension(path));

    static bool IsBinaryExtension(string extension) =>
        extension.ToLowerInvariant() is ".dll" or ".so" or ".dylib" or ".wasm" or ".node" or ".a" or ".exe";

    // The shipped name of a binary. Path.GetFileNameWithoutExtension can't be used unconditionally:
    // a NativeAOT executable is extensionless outside Windows, so for AvaloniaUI.DeveloperTools it
    // would strip ".DeveloperTools" as though it were an extension and record the component as
    // "AvaloniaUI". Only recognised binary extensions are stripped.
    static string BinaryName(string path)
    {
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(name);
        return IsBinaryExtension(extension) ? name[..^extension.Length] : name;
    }

    // A NativeAOT application ships as a native executable - app.exe on Windows, extensionless on
    // Linux and inside a macOS .app bundle - which is the package's primary deliverable and the one
    // binary that must not be missing from its SBOM. An extension list can't spot the extensionless
    // form, so classify by the executable-format magic instead. Only the first bytes of the entry
    // are inflated, so this stays cheap for the non-binary entries it rejects.
    static bool HasNativeExecutableHeader(ZipArchiveEntry entry)
    {
        Span<byte> header = stackalloc byte[4];
        using var stream = entry.Open();
        return stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length
               && IsNativeExecutableHeader(header);
    }

    static bool IsNativeExecutableHeader(ReadOnlySpan<byte> header) =>
        // PE - Windows executables and DLLs.
        (header[0] == (byte)'M' && header[1] == (byte)'Z')
        // ELF - Linux executables and shared objects.
        || (header[0] == 0x7F && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F')
        // Mach-O, 32- and 64-bit. Both byte orders, since the magic is stored in the host order.
        || MatchesMagic(header, 0xFEEDFACE) || MatchesMagic(header, 0xFEEDFACF)
        // Mach-O universal ("fat") wrapper, which a macOS build targeting both architectures
        // produces. Shared with the Java class-file magic, but a .class in one of these packages
        // would at worst be recorded as an extra component with a verifiable hash.
        || MatchesMagic(header, 0xCAFEBABE);

    static bool MatchesMagic(ReadOnlySpan<byte> header, uint magic) =>
        BinaryPrimitives.ReadUInt32BigEndian(header) == magic
        || BinaryPrimitives.ReadUInt32LittleEndian(header) == magic;

    static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    // Returns the managed assembly's simple name (and version), or null for native / non-managed binaries.
    static string? TryReadAssemblyName(byte[] bytes, out string? version)
    {
        version = null;
        try
        {
            using var pe = new PEReader(new MemoryStream(bytes));
            if (!pe.HasMetadata)
                return null;
            var reader = pe.GetMetadataReader();
            if (!reader.IsAssembly)
                return null;
            var assembly = reader.GetAssemblyDefinition();
            version = assembly.Version.ToString();
            return reader.GetString(assembly.Name);
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    // The subset of a shipped package's manifest the SBOM cares about, read from either a .nuspec
    // (ReadNuspecMetadata) or an extension.vsixmanifest (ReadVsixManifestMetadata).
    class PackageMetadata
    {
        public string Id = "";
        public string Version = "";
        // CycloneDX root-component type: "library" for a .nupkg, "application" for a .vsix.
        public string ComponentType = "library";
        // The root component's purl. Nuget packages have a pkg:nuget purl; a VSIX has no registered
        // purl type, so it falls back to pkg:generic.
        public string Purl = "";
        public string? Authors;
        public string? LicenseId;
        public string? LicenseExpression;
        public string? LicenseFile;
        public string? ProjectUrl;
        public string? RepositoryUrl;
        public string? Description;
        public string? Copyright;
        public List<(string Id, string Version)> Dependencies = new();
    }

    static PackageMetadata ReadNuspecMetadata(string nupkgPath)
    {
        using var file = File.Open(nupkgPath, FileMode.Open, FileAccess.Read);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        // Case-insensitive to match NuGet's own PackageArchiveReader manifest lookup.
        var nuspecEntry = zip.Entries.First(e =>
            e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) && e.FullName == e.Name);
        using var nuspecStream = nuspecEntry.Open();
        var metadata = XDocument.Load(nuspecStream).Root!
            .Elements().First(x => x.Name.LocalName == "metadata");

        string? Value(string name) => metadata.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value;
        var license = metadata.Elements().FirstOrDefault(x => x.Name.LocalName == "license");
        var repository = metadata.Elements().FirstOrDefault(x => x.Name.LocalName == "repository");

        var id = Value("id") ?? "";
        var version = Value("version") ?? "";
        return new PackageMetadata
        {
            Id = id,
            Version = version,
            ComponentType = "library",
            Purl = $"pkg:nuget/{id}@{version}",
            Authors = Value("authors"),
            // A nuspec <license> is either type="expression" (an SPDX expression) or type="file"
            // (a path to a bundled licence file); only the former is a valid SPDX id/expression.
            LicenseExpression = license?.Attribute("type")?.Value == "expression" ? license.Value : null,
            LicenseFile = license?.Attribute("type")?.Value == "file" ? license.Value : null,
            LicenseId = license?.Attribute("type")?.Value is "expression" or "file" ? null : license?.Value,
            ProjectUrl = Value("projectUrl"),
            RepositoryUrl = repository?.Attribute("url")?.Value,
            Description = Value("description"),
            Copyright = Value("copyright"),
            // Distinct: multi-targeting packages repeat the same dependency in per-TFM groups.
            Dependencies = metadata.Elements().FirstOrDefault(x => x.Name.LocalName == "dependencies")
                ?.Descendants().Where(x => x.Name.LocalName == "dependency")
                .Select(d => (Id: (string?)d.Attribute("id"), Version: (string?)d.Attribute("version")))
                .Where(d => !string.IsNullOrEmpty(d.Id) && !string.IsNullOrEmpty(d.Version))
                .Select(d => (d.Id!, d.Version!))
                .Distinct()
                .ToList() ?? new()
        };
    }

    // Reads the shipped VSIX's extension.vsixmanifest. Unlike a .nuspec this carries no dependency
    // list (a VSIX bundles everything it needs) and no repository/copyright fields, so those stay
    // empty; Id/Version/Publisher/License/description come from the <Metadata> block. Parsed by
    // LocalName so the vsx-schema namespace version doesn't matter.
    static PackageMetadata ReadVsixManifestMetadata(string vsixPath)
    {
        using var file = File.Open(vsixPath, FileMode.Open, FileAccess.Read);
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        var manifestEntry = zip.Entries.FirstOrDefault(e =>
            e.FullName.Equals("extension.vsixmanifest", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"SBOM: '{Path.GetFileName(vsixPath)}' has no extension.vsixmanifest - is it a valid VSIX?");
        using var stream = manifestEntry.Open();
        var root = XDocument.Load(stream).Root!;

        XElement? Child(XElement parent, string name) =>
            parent.Elements().FirstOrDefault(x => x.Name.LocalName == name);

        var metadata = Child(root, "Metadata")
            ?? throw new InvalidOperationException(
                $"SBOM: '{Path.GetFileName(vsixPath)}' vsixmanifest has no <Metadata> element.");
        var identity = Child(metadata, "Identity")
            ?? throw new InvalidOperationException(
                $"SBOM: '{Path.GetFileName(vsixPath)}' vsixmanifest has no <Identity> element.");

        var id = identity.Attribute("Id")?.Value ?? "";
        var version = identity.Attribute("Version")?.Value ?? "";

        // Optional metadata may be present but blank (e.g. <License />); treat that as absent so the
        // SBOM doesn't carry a meaningless empty licence name, external-reference URL or description
        // (an empty LicenseFile is not null, so it would otherwise reach Path.GetFileName("") = "").
        static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        return new PackageMetadata
        {
            Id = id,
            Version = version,
            ComponentType = "application",
            // No registered purl type for a VSIX; pkg:generic is CycloneDX's documented fallback.
            Purl = $"pkg:generic/{id}@{version}",
            Authors = NullIfBlank(identity.Attribute("Publisher")?.Value),
            // <License> is a path to a licence file bundled in the VSIX, like a nuspec type="file".
            LicenseFile = NullIfBlank(Child(metadata, "License")?.Value),
            Description = NullIfBlank(Child(metadata, "Description")?.Value)
                ?? NullIfBlank(Child(metadata, "DisplayName")?.Value),
            ProjectUrl = NullIfBlank(Child(metadata, "MoreInfo")?.Value),
        };
    }

    public static string ReadPackageId(string nupkgPath) => ReadNuspecMetadata(nupkgPath).Id;

    // For repositories that call GenerateForPackage directly but whose package versions are
    // computed by MSBuild: the shipped .nuspec is authoritative for what was actually packed.
    public static string ReadPackageVersion(string nupkgPath) => ReadNuspecMetadata(nupkgPath).Version;
}
