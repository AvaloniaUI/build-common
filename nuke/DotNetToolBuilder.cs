using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Versioning;
using Nuke.Common.IO;

namespace NukeExtensions;

public record NuGetPackageInfo(string PackageId, NuGetVersion Version)
{
    public string ProjectUrl { get; init; } = "https://avaloniaui.net/accelerate";
    public string? Author { get; init; } = "AvaloniaUI OÜ";
    public string? Copyright { get; init; } = $"Copyright 2019-{DateTime.Now.Year} © AvaloniaUI OÜ";
    public string? Description { get; init; } = PackageId;
    public AbsolutePath? ReleaseNotes { get; init; }
    public AbsolutePath? Readme { get; init; }
    public AbsolutePath? McpServerConfig { get; init; }
    public AbsolutePath? Icon { get; init; } = Statics.Icon;
}

public static class DotNetToolBuilder
{
    public static void BuildRuntimeSpecificPackage(
        Stream output,
        NuGetPackageInfo packageInfo,
        AbsolutePath binariesDir,
        string entryPoint,
        string commandName)
    {
        var metadata = CreateMetadata(packageInfo,
        [
            new PackageType("DotnetTool", PackageType.EmptyVersion),
            new PackageType("DotnetToolRidPackage", PackageType.EmptyVersion)
        ]);

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var files = new List<ManifestFile>();

            var manifestFile = Path.Combine(tempDir, "DotnetToolSettings.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(manifestFile)!);
            using (var writer = new StreamWriter(manifestFile))
            {
                writer.WriteLine("""<?xml version="1.0" encoding="utf-8"?>""");
                writer.WriteLine("""<DotNetCliTool Version="1">""");
                writer.WriteLine("""  <Commands>""");
                writer.WriteLine($"""    <Command Name="{commandName}" EntryPoint="{entryPoint}" Runner="dotnet" />""");
                writer.WriteLine("""  </Commands>""");
                writer.WriteLine("""</DotNetCliTool>""");
            }

            files.Add(new ManifestFile { Source = manifestFile, Target = "tools/net6.0/any/DotnetToolSettings.xml" });

            if (packageInfo.Icon is { } icon)
            {
                files.Add(new ManifestFile { Source = icon, Target = icon.Name });
            }
            if (packageInfo.Readme is { } readme)
            {
                files.Add(new ManifestFile { Source = readme, Target = readme.Name });
            }
            if (packageInfo.McpServerConfig is { } mcpServerConfig)
            {
                files.Add(new ManifestFile { Source = mcpServerConfig, Target = ".mcp/server.json" });
            }

            foreach (var file in binariesDir.GlobFiles("**/*"))
            {
                var relativePath = Path.GetRelativePath(binariesDir, file);
                files.Add(new ManifestFile
                {
                    Source = file,
                    Target = Path.Combine("tools/net6.0/any", relativePath)
                });
            }

            var builder = new PackageBuilder();
            builder.PopulateFiles("", files);
            builder.Populate(metadata);

            builder.Save(output);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    public static void BuildSharedPackage(
        Stream output,
        NuGetPackageInfo packageInfo,
        string commandName,
        IReadOnlyDictionary<string, string> platformPackagesPerRid)
    {
        var metadata = CreateMetadata(packageInfo,
        [
            new PackageType("DotnetTool", PackageType.EmptyVersion)
        ]);

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var files = new List<ManifestFile>();

            var manifestFile = Path.Combine(tempDir, "DotnetToolSettings.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(manifestFile)!);
            using (var writer = new StreamWriter(manifestFile))
            {
                writer.WriteLine("""<?xml version="1.0" encoding="utf-8"?>""");
                writer.WriteLine("""<DotNetCliTool Version="2">""");
                writer.WriteLine("""  <Commands>""");
                writer.WriteLine($"""    <Command Name="{commandName}" />""");
                writer.WriteLine("""  </Commands>""");
                writer.WriteLine("""  <RuntimeIdentifierPackages>""");

                foreach (var (rid, ridPackage) in platformPackagesPerRid)
                {
                    writer.WriteLine(
                        $"""    <RuntimeIdentifierPackage RuntimeIdentifier="{rid}" Id="{ridPackage}" />""");
                }

                writer.WriteLine("""  </RuntimeIdentifierPackages>""");
                writer.WriteLine("""</DotNetCliTool>""");
            }

            files.Add(new ManifestFile { Source = manifestFile, Target = "tools/net10.0/any/DotnetToolSettings.xml" });

            if (packageInfo.Icon is { } icon)
            {
                files.Add(new ManifestFile { Source = icon, Target = icon.Name });
            }
            if (packageInfo.Readme is { } readme)
            {
                files.Add(new ManifestFile { Source = readme, Target = readme.Name });
            }
            if (packageInfo.McpServerConfig is { } mcpServerConfig)
            {
                files.Add(new ManifestFile { Source = mcpServerConfig, Target = ".mcp/server.json" });
            }

            var builder = new PackageBuilder();
            builder.PopulateFiles("", files);
            builder.Populate(metadata);

            builder.Save(output);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    private static ManifestMetadata CreateMetadata(NuGetPackageInfo packageInfo, IEnumerable<PackageType> packageTypes)
    {
        var metadata = new ManifestMetadata
        {
            Id = packageInfo.PackageId,
            Version = packageInfo.Version,
            Authors = packageInfo.Author is { } author ? [author] : [],
            Description = packageInfo.Description,
            Copyright = packageInfo.Copyright,
            RequireLicenseAcceptance = false,
            Readme = packageInfo.Readme?.Name,
            Icon = packageInfo.Icon?.Name,
            PackageTypes = packageTypes
        };
        if (packageInfo.ProjectUrl is { } projectUrl)
        {
            metadata.SetProjectUrl(projectUrl);
        }

        if (File.Exists(packageInfo.ReleaseNotes))
        {
            var releaseNotesContent = File.ReadAllText(packageInfo.ReleaseNotes);
            metadata.ReleaseNotes = TrimReleaseNotesByLines(releaseNotesContent);
        }

        return metadata;
    }

    private static string TrimReleaseNotesByLines(string content, int maxLength = 35000)
    {
        if (content.Length <= maxLength)
            return content;

        var lines = content.Split(['\r', '\n'], StringSplitOptions.None);
        var result = new StringBuilder();

        foreach (var line in lines)
        {
            if (result.Length + (line.Length + Environment.NewLine.Length) > maxLength)
                break;

            result.AppendLine(line);
        }

        return result.ToString().TrimEnd();
    }
}
