using System.Collections.Generic;
using System.Linq;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Serilog;

namespace NukeExtensions;

public static class IlMerge
{
    public record MergeTargetFramework(
        AbsolutePath OutputDirectory,
        IEnumerable<string> DependencyFileNames);

    public static void Merge(
        Tool ilRepack,
        string assemblyName,
        IEnumerable<MergeTargetFramework> targets,
        bool internalize = true,
        bool renameInternalized = false,
        AbsolutePath? publicApiList = null,
        AbsolutePath? signKey = null)
    {
        foreach (var (outputDir, dependencyFiles) in targets)
        {
            var dll = outputDir / (assemblyName + ".dll");
            var dependencies = dependencyFiles.Select(file => outputDir / (file + ".dll")).ToArray();
            Log.Information("Obfuscating {FileName} in {Folder}. And merging with {Dependencies}",
                assemblyName, outputDir.Name, dependencies);

            var args = new ArgumentStringHandler();
            if (internalize)
            {
                if (publicApiList is not null)
                {
                    args.AppendFormatted($"/internalize:{publicApiList}");
                }
                else
                {
                    args.AppendFormatted($"/internalize");
                }

                if (renameInternalized)
                {
                    args.AppendLiteral(" /renameinternalized ");
                }
            }

            args.AppendLiteral(" /parallel /ndebug ");
            if (signKey is not null)
            {
                args.AppendLiteral(" /keyfile: ");
                args.AppendFormatted(signKey);
            }

            args.AppendLiteral($" /out:{dll} ");
            args.AppendFormatted(dependencies);

            ilRepack.Invoke(args, outputDir);
        }

        Log.Information("Merged {AssemblyName}", assemblyName);
    }
}