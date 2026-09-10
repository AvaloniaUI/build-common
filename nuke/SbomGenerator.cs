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
using NuGet.Versioning;
using static Serilog.Log;

namespace NukeExtensions;

// Keep this file synchronized with Avalonia's nukebuild/SbomGenerator.cs.
// That copy also rebuilds Numerge merge groups.
public static class SbomGenerator
{
    // If version is null, Generate reads the packed version from each .nuspec.
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
            throw new InvalidOperationException(
                $"SBOM: no .nupkg files found in {packagesDirectory} - was the SBOM target run before packing?");
        }

        foreach (var nupkg in nupkgs)
        {
            string metaId, metaVersion;
            using (var reader = new ArchivePackageReader((string)nupkg))
            {
                var meta = ReadNuspecMetadata(reader, nupkg.Name);
                (metaId, metaVersion) = (meta.Id, meta.Version);
            }

            GenerateForPackage(cycloneDx, rootDirectory, nupkg, outputDirectory, version ?? metaVersion, metaId,
                new[] { metaId });
        }
    }

    // projectSearchDirs lists root-relative directories that contain constituent projects.
    // The default directories are src and packages.
    //
    // additionalProductNames lists first-party binaries that do not match a project ID or an
    // Avalonia.*/AvaloniaUI.* prefix.
    //
    // baseIntermediateOutputPath supplies the cyclonedx-dotnet -biop value for an Arcade layout.
    // Other projects use the MSBuild value of ProjectAssetsFile.
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

        Func<IPackageReader>? openContent =
            packagePath is null ? null : () => new ArchivePackageReader((string)packagePath);
        PackageMetadata? meta = null;
        if (openContent is not null)
        {
            using var reader = openContent();
            meta = ReadNuspecMetadata(reader, packagePath!.Name);
        }
        else
        {
            Warning($"SBOM: couldn't find the built .nupkg for '{packageId}' - root metadata and package-content verification were skipped.");
        }

        FinalizeAndEmit(scan.Value, meta, openContent, outputDirectory, version, packageId,
            constituentProjectIds, additionalProductNames, Array.Empty<AbsolutePath>(),
            packagePath is null ? null : json => EmbedSbomInArchive((string)packagePath, json));
    }

    // constituentProjectIds lists every project whose output is part of the VSIX. The list must
    // include the primary extension assembly.
    //
    // npmPackageJsons lists production dependency trees that the VSIX bundles. Their node_modules
    // directories must exist during the scan.
    public static void GenerateForVsix(Tool cycloneDx, AbsolutePath rootDirectory, AbsolutePath vsixPath,
        AbsolutePath outputDirectory, IReadOnlyList<string> constituentProjectIds,
        IReadOnlyList<AbsolutePath>? npmPackageJsons = null,
        IReadOnlyList<string>? projectSearchDirs = null,
        IReadOnlyList<string>? additionalProductNames = null,
        AbsolutePath? baseIntermediateOutputPath = null)
    {
        GenerateForVsixCore(cycloneDx, rootDirectory,
            () => new ArchivePackageReader((string)vsixPath), vsixPath.Name,
            json => EmbedSbomInArchive(vsixPath, json),
            outputDirectory, constituentProjectIds, npmPackageJsons,
            projectSearchDirs, additionalProductNames, baseIntermediateOutputPath);
    }

    public static void GenerateForVsixDirectory(Tool cycloneDx, AbsolutePath rootDirectory,
        AbsolutePath vsixContentDir, AbsolutePath outputDirectory, IReadOnlyList<string> constituentProjectIds,
        IReadOnlyList<AbsolutePath>? npmPackageJsons = null,
        IReadOnlyList<string>? projectSearchDirs = null,
        IReadOnlyList<string>? additionalProductNames = null,
        AbsolutePath? baseIntermediateOutputPath = null)
    {
        GenerateForVsixCore(cycloneDx, rootDirectory,
            () => new DirectoryPackageReader(vsixContentDir), vsixContentDir.Name,
            json => EmbedSbomInDirectory(vsixContentDir, json),
            outputDirectory, constituentProjectIds, npmPackageJsons,
            projectSearchDirs, additionalProductNames, baseIntermediateOutputPath);
    }

    static void GenerateForVsixCore(Tool cycloneDx, AbsolutePath rootDirectory,
        Func<IPackageReader> openContent, string sourceLabel, Action<string> embed,
        AbsolutePath outputDirectory, IReadOnlyList<string> constituentProjectIds,
        IReadOnlyList<AbsolutePath>? npmPackageJsons,
        IReadOnlyList<string>? projectSearchDirs,
        IReadOnlyList<string>? additionalProductNames,
        AbsolutePath? baseIntermediateOutputPath)
    {
        PackageMetadata meta;
        using (var reader = openContent())
            meta = ReadVsixManifestMetadata(reader, sourceLabel);

        var scan = ScanConstituentProjects(cycloneDx, rootDirectory, outputDirectory, meta.Version, meta.Id,
            constituentProjectIds, projectSearchDirs, baseIntermediateOutputPath);
        if (scan is null)
        {
            Warning($"SBOM: no source projects could be scanned for '{meta.Id}', no SBOM was generated for it.");
            return;
        }

        FinalizeAndEmit(scan.Value, meta, openContent, outputDirectory, meta.Version, meta.Id,
            constituentProjectIds, additionalProductNames,
            npmPackageJsons ?? Array.Empty<AbsolutePath>(), embed);
    }

    readonly record struct ConstituentScan(
        JsonObject Merged, HashSet<string> SeenComponentKeys, List<AbsolutePath> ScannedProjectDirs);

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
            // No quotes around the interpolated values: Tool takes an ArgumentStringHandler,
            // which already quotes whatever is interpolated into it. Quoting here as well
            // produces ""C:\Some Dir\proj.csproj"", which the OS reads as an empty quoted
            // string followed by a bare path, so the argument splits at the first space and
            // the tool rejects the remainder. Only bites where a path contains a space.
            // The optional fragment stays a separate argument for the same reason.
            if (baseIntermediateOutputPath is null)
                cycloneDx(
                    $"{project} -o {outputDirectory} -fn {tempBom.Name} -F Json -dpr -ed -sn {packageId} -sv {version}",
                    workingDirectory: rootDirectory);
            else
                cycloneDx(
                    $"{project} -o {outputDirectory} -fn {tempBom.Name} -F Json -dpr -ed -sn {packageId} -sv {version} -biop {baseIntermediateOutputPath}",
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

                // The -sn and -sv values give all scans the same root reference.
                MergeDependencyGraph(merged, doc["dependencies"]?.AsArray() ?? new JsonArray());
            }
        }

        return merged is null ? null : new ConstituentScan(merged, seenComponentKeys, scannedProjectDirs);
    }

    static void FinalizeAndEmit(ConstituentScan scan, PackageMetadata? meta, Func<IPackageReader>? openContent,
        AbsolutePath outputDirectory, string version, string packageId,
        IReadOnlyList<string> constituentProjectIds, IReadOnlyList<string>? additionalProductNames,
        IReadOnlyList<AbsolutePath> extraNpmPackageJsons, Action<string>? embed)
    {
        var merged = scan.Merged;
        var seenComponentKeys = scan.SeenComponentKeys;

        // cyclonedx-dotnet only scans the MSBuild and NuGet graph. Scan bundled npm dependencies separately.
        var rootRef = merged["metadata"]?["component"]?["bom-ref"]?.GetValue<string>();
        foreach (var projectDir in scan.ScannedProjectDirs)
            AddWebappNpmComponents(merged, seenComponentKeys, projectDir, rootRef);
        foreach (var packageJson in extraNpmPackageJsons)
            AddNpmComponentsFromPackageJson(merged, seenComponentKeys, packageJson, rootRef);

        if (meta is not null && openContent is not null)
        {
            EnrichRootComponent(merged, meta);
            AddDeclaredDependencyComponents(merged, seenComponentKeys, meta, rootRef);
            var productNames = additionalProductNames is null
                ? constituentProjectIds
                : constituentProjectIds.Concat(additionalProductNames).ToList();
            using var content = openContent();
            AddPackageContentComponents(merged, seenComponentKeys, content, packageId,
                productNames, meta);
        }

        // cyclonedx-dotnet can leave edges to excluded development dependencies. Remove these invalid edges.
        PruneDanglingDependencyEdges(merged);

        var sbomJson = merged.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(outputDirectory / $"{packageId}.{version}.cdx.json", sbomJson);

        embed?.Invoke(sbomJson);
    }

    // Uses the Microsoft.Sbom.Targets manifest layout and the standard *.cdx.json suffix.
    const string EmbeddedSbomEntryPath = "_manifest/cyclonedx/bom.cdx.json";

    // A signed archive cannot be changed.
    static void EmbedSbomInArchive(AbsolutePath packagePath, string sbomJson)
    {
        using var file = File.Open(packagePath, FileMode.Open, FileAccess.ReadWrite);
        using var zip = new ZipArchive(file, ZipArchiveMode.Update);

        // These paths identify NuGet and OPC signatures.
        var isSigned = zip.Entries.Any(e =>
            e.FullName.EndsWith(".signature.p7s", StringComparison.OrdinalIgnoreCase)
            || e.FullName.StartsWith("package/services/digital-signature/", StringComparison.OrdinalIgnoreCase));
        if (isSigned)
        {
            Warning($"SBOM: '{packagePath.Name}' is already signed - skipping embed so its signature stays valid.");
            return;
        }

        zip.GetEntry(EmbeddedSbomEntryPath)?.Delete();
        using (var entryStream = zip.CreateEntry(EmbeddedSbomEntryPath).Open())
        using (var writer = new StreamWriter(entryStream))
            writer.Write(sbomJson);

        EnsureJsonContentTypeRegisteredInArchive(zip);
    }

    static void EmbedSbomInDirectory(AbsolutePath contentDir, string sbomJson)
    {
        var sbomPath = contentDir / EmbeddedSbomEntryPath;
        sbomPath.Parent.CreateDirectory();
        File.WriteAllText(sbomPath, sbomJson);

        EnsureJsonContentTypeRegisteredInDirectory(contentDir);
    }

    // OPC readers reject an undeclared file extension. Register the .json extension in
    // [Content_Types].xml.
    static void EnsureJsonContentTypeRegisteredInArchive(ZipArchive zip)
    {
        const string contentTypesEntryName = "[Content_Types].xml";

        var entry = zip.GetEntry(contentTypesEntryName);
        if (entry is null)
            return; // Do not add metadata to an invalid OPC package.

        XDocument doc;
        using (var read = entry.Open())
            doc = XDocument.Load(read);

        if (!TryRegisterJsonContentType(doc))
            return;

        entry.Delete();
        using var write = zip.CreateEntry(contentTypesEntryName).Open();
        doc.Save(write);
    }

    static void EnsureJsonContentTypeRegisteredInDirectory(AbsolutePath contentDir)
    {
        var contentTypesFile = contentDir / "[Content_Types].xml";
        if (!contentTypesFile.FileExists())
            return; // Do not add metadata to an invalid OPC package.

        var doc = XDocument.Load(contentTypesFile);
        if (!TryRegisterJsonContentType(doc))
            return;

        doc.Save(contentTypesFile);
    }

    // Adds the default JSON content type when it is absent. Returns true after a change.
    static bool TryRegisterJsonContentType(XDocument doc)
    {
        XNamespace ns = "http://schemas.openxmlformats.org/package/2006/content-types";

        var alreadyRegistered = doc.Root!.Elements(ns + "Default")
            .Any(d => string.Equals((string?)d.Attribute("Extension"), "json", StringComparison.OrdinalIgnoreCase));
        if (alreadyRegistered)
            return false;

        doc.Root.Add(new XElement(ns + "Default",
            new XAttribute("Extension", "json"),
            new XAttribute("ContentType", "application/json")));
        return true;
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

    static void AddWebappNpmComponents(JsonObject merged, HashSet<string> seenComponentKeys, AbsolutePath projectDir,
        string? rootRef)
    {
        foreach (var packageJsonPath in projectDir.GlobFiles("**/webapp/package.json"))
            AddNpmComponentsFromPackageJson(merged, seenComponentKeys, packageJsonPath, rootRef);
    }

    // Scans installed production dependencies and ignores devDependencies. The installed versions
    // replace declared ranges. Direct dependencies link to the root component.
    static void AddNpmComponentsFromPackageJson(JsonObject merged, HashSet<string> seenComponentKeys,
        AbsolutePath packageJsonPath, string? rootRef)
    {
        if (!packageJsonPath.FileExists())
        {
            Warning($"SBOM: npm manifest '{packageJsonPath}' doesn't exist - no npm components were added from it.");
            return;
        }

        var packageJson = JsonNode.Parse(File.ReadAllText(packageJsonPath))!.AsObject();
        var nodeModules = packageJsonPath.Parent / "node_modules";
        var dependencies = packageJson["dependencies"]?.AsObject() ?? new JsonObject();

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

    // Creates each purl once, which also stops dependency cycles. Repeated purls remain available
    // for new dependency edges.
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
        // Registry tools cannot verify a hash of the unpacked directory.
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

    // esbuild removes @types/* declaration packages from the shipped output.
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
        // Old npm manifests store licenses in objects instead of SPDX strings.
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
        // Prefer a nested version before the version that npm or Bun hoisted.
        var installedDir = new[] { parentNodeModules / name, topLevelNodeModules / name }
            .FirstOrDefault(d => File.Exists(d / "package.json"));

        if (declaredRange.StartsWith("github:") || declaredRange.StartsWith("git") || declaredRange.Contains("://"))
        {
            if (TryParseGitHubDependency(declaredRange, out var owner, out var repo, out var reference))
                return ($"pkg:github/{owner}/{repo}@{reference}", reference, installedDir);

            // Other Git and URL sources have no supported purl. Record a generic component.
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
            // Private package manifests can omit the version.
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

    // Keep different component versions separate. Use a new GUID when no stable identity exists.
    static string ComponentKey(JsonNode? component) =>
        component?["purl"]?.GetValue<string>()
        ?? component?["bom-ref"]?.GetValue<string>()
        ?? (component?["name"]?.GetValue<string>() is { } name
            ? $"{name}@{component?["version"]?.GetValue<string>()}"
            : Guid.NewGuid().ToString());

    static void EnrichRootComponent(JsonObject merged, PackageMetadata meta)
    {
        var component = merged["metadata"]?["component"]?.AsObject();
        if (component is null)
            return;

        component["type"] = meta.ComponentType;
        // A version mismatch means that the restored dependencies can differ from the shipped package.
        var scannedVersion = component["version"]?.GetValue<string>();
        if (scannedVersion is not null && !IsSameVersion(scannedVersion, meta.Version))
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
        // A license file has no SPDX ID. Record its file name.
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

        if (supplier is not null && merged["metadata"] is JsonObject metadata)
            metadata["supplier"] = supplier.DeepClone();
    }

    // NuGet pack normalizes whatever version it is handed ("12.2" ships as "12.2.0"), so the
    // build-supplied and nuspec strings can spell the same version differently; only a semantic
    // difference means the packages are stale. Unparseable versions keep the exact-string check.
    static bool IsSameVersion(string scanned, string shipped) =>
        NuGetVersion.TryParse(scanned, out var scannedVersion) && NuGetVersion.TryParse(shipped, out var shippedVersion)
            ? scannedVersion == shippedVersion
            : string.Equals(scanned, shipped, StringComparison.Ordinal);

    // The -ed option can exclude a PrivateAssets package that the .nuspec still declares.
    // Add all missing .nuspec dependencies because consumers restore them.
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
            // NuGet pack writes the resolved dependency version to the .nuspec.
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

    // Records shipped binaries as a CycloneDX assembly, not as dependencies. Flat components keep
    // their SHA-512 hashes visible to tools that ignore nested components.
    static void AddPackageContentComponents(JsonObject merged, HashSet<string> seenComponentKeys,
        IPackageReader content, string packageId, IReadOnlyList<string> firstPartyNames, PackageMetadata meta)
    {
        var productNames = new HashSet<string>(firstPartyNames, StringComparer.OrdinalIgnoreCase) { packageId };
        var representedNames = (merged["components"]?.AsArray() ?? new JsonArray())
            .Select(c => c?["name"]?.GetValue<string>())
            .Where(n => n is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

        var supplier = meta.Authors is null ? null : new JsonObject { ["name"] = meta.Authors };
        var target = merged["components"]?.AsArray() ?? (JsonArray)(merged["components"] = new JsonArray());
        var rootRef = merged["metadata"]?["component"]?["bom-ref"]?.GetValue<string>();

        var productLicenses = SpdxToLicenses(meta.LicenseExpression ?? meta.LicenseId);
        if (productLicenses is null && meta.LicenseFile is not null)
            productLicenses = new JsonArray(new JsonObject
                { ["license"] = new JsonObject { ["name"] = Path.GetFileName(meta.LicenseFile) } });

        var assemblyRefs = new List<string>();

        foreach (var (path, bytes) in EnumerateShippedBinaries(content))
        {
            var assemblyName = TryReadAssemblyName(bytes, out var assemblyVersion, out var assemblyCulture);
            var simpleName = assemblyName ?? BinaryName(path);

            // A satellite resource assembly contains no independent code or version.
            if (assemblyName is not null && assemblyCulture is not null
                && simpleName.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
                continue;

            if (assemblyName is not null && representedNames.Contains(simpleName))
                continue;

            var isProduct = productNames.Contains(simpleName)
                || simpleName.StartsWith("Avalonia.", StringComparison.OrdinalIgnoreCase)
                || simpleName.StartsWith("AvaloniaUI.", StringComparison.OrdinalIgnoreCase)
                || simpleName.Equals("Avalonia", StringComparison.OrdinalIgnoreCase);

            // The root component already represents the primary assembly.
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

    // NativeAOT tool packages contain the macOS .app bundle in a nested .zip file.
    static IEnumerable<(string Path, byte[] Bytes)> EnumerateShippedBinaries(
        IPackageReader reader, string pathPrefix = "")
    {
        foreach (var entry in reader.Entries)
        {
            var path = pathPrefix + entry.FullPath;

            // Root ref/ assemblies are compile-time files. Scan their lib/ implementations instead.
            if (pathPrefix.Length == 0 && entry.FullPath.StartsWith("ref/", StringComparison.OrdinalIgnoreCase))
                continue;

            if (Path.GetExtension(entry.FullPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                // ZipArchive needs a seekable stream.
                byte[] nestedBytes;
                using (var stream = entry.Open())
                    nestedBytes = ReadAllBytes(stream);
                using var nested = new ArchivePackageReader(new MemoryStream(nestedBytes));
                foreach (var inner in EnumerateShippedBinaries(nested, path + "/"))
                    yield return inner;
                continue;
            }

            // Read file headers only for extensionless NativeAOT candidates.
            if (!IsShippedBinary(entry.FullPath)
                && !(Path.GetExtension(entry.FullPath).Length == 0 && HasNativeExecutableHeader(entry)))
                continue;

            using (var stream = entry.Open())
                yield return (path, ReadAllBytes(stream));
        }
    }

    static bool IsShippedBinary(string path) => IsBinaryExtension(Path.GetExtension(path));

    static bool IsBinaryExtension(string extension) =>
        extension.ToLowerInvariant() is ".dll" or ".so" or ".dylib" or ".wasm" or ".node" or ".a" or ".exe";

    // Path.GetFileNameWithoutExtension truncates dotted names of extensionless NativeAOT binaries.
    static string BinaryName(string path)
    {
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(name);
        return IsBinaryExtension(extension) ? name[..^extension.Length] : name;
    }

    // File extensions cannot identify NativeAOT executables on Linux and macOS. Use their file signatures.
    static bool HasNativeExecutableHeader(IPackageEntry entry)
    {
        Span<byte> header = stackalloc byte[4];
        using var stream = entry.Open();
        return stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length
               && IsNativeExecutableHeader(header);
    }

    static bool IsNativeExecutableHeader(ReadOnlySpan<byte> header) =>
        // PE
        (header[0] == (byte)'M' && header[1] == (byte)'Z')
        // ELF
        || (header[0] == 0x7F && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F')
        // Mach-O, 32-bit and 64-bit
        || MatchesMagic(header, 0xFEEDFACE) || MatchesMagic(header, 0xFEEDFACF)
        // Mach-O universal binary
        || MatchesMagic(header, 0xCAFEBABE);

    static bool MatchesMagic(ReadOnlySpan<byte> header, uint magic) =>
        BinaryPrimitives.ReadUInt32BigEndian(header) == magic
        || BinaryPrimitives.ReadUInt32LittleEndian(header) == magic;

    static byte[] ReadAllBytes(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    // Returns null for native binaries and files without managed metadata.
    static string? TryReadAssemblyName(byte[] bytes, out string? version, out string? culture)
    {
        version = null;
        culture = null;
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
            var cultureName = reader.GetString(assembly.Culture);
            culture = string.IsNullOrEmpty(cultureName) ? null : cultureName;
            return reader.GetString(assembly.Name);
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    class PackageMetadata
    {
        public string Id = "";
        public string Version = "";
        public string ComponentType = "library";
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

    static PackageMetadata ReadNuspecMetadata(IPackageReader reader, string sourceLabel)
    {
        // NuGet only accepts a .nuspec at the package root.
        var nuspecEntry = reader.Find(p =>
            p.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) && !p.Contains('/'))
            ?? throw new InvalidOperationException(
                $"SBOM: '{sourceLabel}' has no root .nuspec - is it a valid NuGet package?");
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
            // A license file path is not an SPDX expression.
            LicenseExpression = license?.Attribute("type")?.Value == "expression" ? license.Value : null,
            LicenseFile = license?.Attribute("type")?.Value == "file" ? license.Value : null,
            LicenseId = license?.Attribute("type")?.Value is "expression" or "file" ? null : license?.Value,
            ProjectUrl = Value("projectUrl"),
            RepositoryUrl = repository?.Attribute("url")?.Value,
            Description = Value("description"),
            Copyright = Value("copyright"),
            // Target framework groups can repeat a dependency.
            Dependencies = metadata.Elements().FirstOrDefault(x => x.Name.LocalName == "dependencies")
                ?.Descendants().Where(x => x.Name.LocalName == "dependency")
                .Select(d => (Id: (string?)d.Attribute("id"), Version: (string?)d.Attribute("version")))
                .Where(d => !string.IsNullOrEmpty(d.Id) && !string.IsNullOrEmpty(d.Version))
                .Select(d => (d.Id!, d.Version!))
                .Distinct()
                .ToList() ?? new()
        };
    }

    static PackageMetadata ReadVsixManifestMetadata(IPackageReader reader, string sourceLabel)
    {
        var manifestEntry = reader.Find(p =>
            p.Equals("extension.vsixmanifest", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"SBOM: '{sourceLabel}' has no extension.vsixmanifest - is it a valid VSIX?");
        using var stream = manifestEntry.Open();
        var root = XDocument.Load(stream).Root!;

        XElement? Child(XElement parent, string name) =>
            parent.Elements().FirstOrDefault(x => x.Name.LocalName == name);

        var metadata = Child(root, "Metadata")
            ?? throw new InvalidOperationException(
                $"SBOM: '{sourceLabel}' vsixmanifest has no <Metadata> element.");
        var identity = Child(metadata, "Identity")
            ?? throw new InvalidOperationException(
                $"SBOM: '{sourceLabel}' vsixmanifest has no <Identity> element.");

        var id = identity.Attribute("Id")?.Value;
        var version = identity.Attribute("Version")?.Value;
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version))
            throw new InvalidOperationException(
                $"SBOM: '{sourceLabel}' vsixmanifest <Identity> is missing an Id or Version.");

        // Treat empty optional elements as absent.
        static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        return new PackageMetadata
        {
            Id = id,
            Version = version,
            ComponentType = "application",
            // CycloneDX has no registered VSIX purl type.
            Purl = $"pkg:generic/{id}@{version}",
            Authors = NullIfBlank(identity.Attribute("Publisher")?.Value),
            LicenseFile = NullIfBlank(Child(metadata, "License")?.Value),
            Description = NullIfBlank(Child(metadata, "Description")?.Value)
                ?? NullIfBlank(Child(metadata, "DisplayName")?.Value),
            ProjectUrl = NullIfBlank(Child(metadata, "MoreInfo")?.Value),
        };
    }

    public static string ReadPackageId(string nupkgPath)
    {
        using var reader = new ArchivePackageReader(nupkgPath);
        return ReadNuspecMetadata(reader, ((AbsolutePath)nupkgPath).Name).Id;
    }

    // The shipped .nuspec is the source of the packed version.
    public static string ReadPackageVersion(string nupkgPath)
    {
        using var reader = new ArchivePackageReader(nupkgPath);
        return ReadNuspecMetadata(reader, ((AbsolutePath)nupkgPath).Name).Version;
    }

    interface IPackageEntry
    {
        // Package paths always use '/'.
        string FullPath { get; }
        Stream Open();
    }

    interface IPackageReader : IDisposable
    {
        // Excludes directory entries.
        IEnumerable<IPackageEntry> Entries { get; }
        IPackageEntry? Find(Func<string, bool> pathPredicate);
    }

    sealed class ArchivePackageReader : IPackageReader
    {
        readonly Stream _stream;
        readonly ZipArchive _zip;

        public ArchivePackageReader(string path)
            : this(File.Open(path, FileMode.Open, FileAccess.Read))
        {
        }

        // Takes ownership of stream.
        public ArchivePackageReader(Stream stream)
        {
            _stream = stream;
            // This reader disposes the stream after it disposes the archive.
            _zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        }

        public IEnumerable<IPackageEntry> Entries =>
            _zip.Entries.Where(e => e.Name.Length != 0).Select(e => (IPackageEntry)new Entry(e));

        public IPackageEntry? Find(Func<string, bool> pathPredicate) =>
            _zip.Entries.Where(e => e.Name.Length != 0)
                .Select(e => (IPackageEntry)new Entry(e))
                .FirstOrDefault(e => pathPredicate(e.FullPath));

        public void Dispose()
        {
            _zip.Dispose();
            _stream.Dispose();
        }

        sealed class Entry(ZipArchiveEntry entry) : IPackageEntry
        {
            public string FullPath => entry.FullName;
            public Stream Open() => entry.Open();
        }
    }

    sealed class DirectoryPackageReader : IPackageReader
    {
        readonly AbsolutePath _directory;
        IReadOnlyList<IPackageEntry>? _entries;

        public DirectoryPackageReader(AbsolutePath directory) => _directory = directory;

        // Cache the recursive file scan for both Find and Entries.
        public IEnumerable<IPackageEntry> Entries =>
            _entries ??= _directory.GlobFiles("**/*").Select(f => (IPackageEntry)new Entry(_directory, f)).ToList();

        public IPackageEntry? Find(Func<string, bool> pathPredicate) =>
            Entries.FirstOrDefault(e => pathPredicate(e.FullPath));

        public void Dispose()
        {
        }

        sealed class Entry(AbsolutePath root, AbsolutePath file) : IPackageEntry
        {
            // Match the path format of archive entries on all operating systems.
            public string FullPath => Path.GetRelativePath(root, file).Replace('\\', '/');
            public Stream Open() => File.OpenRead(file);
        }
    }
}
