using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Serilog;

namespace NukeExtensions;

public static class Babel
{
    public record ObfuscationTargetFramework(
        AbsolutePath OutputDirectory,
        IEnumerable<string> DependencyFileNames);

    public static void Obfuscate(
        Tool babel,
        string assemblyName,
        IEnumerable<ObfuscationTargetFramework> targets,
        AbsolutePath? licenseFile = null,
        AbsolutePath? signKey = null,
        IReadOnlyCollection<AbsolutePath>? rulesFiles = null,
        Dictionary<string, List<AbsolutePath>>? perTargetDependencyMapFiles = null,
        bool inlineExpansion = false)
    {
        bool tempLicense = false;
        if (licenseFile is null)
        {
            var licenseEnvValue = Environment.GetEnvironmentVariable("BABEL_LICENSE");
            if (File.Exists(licenseEnvValue))
            {
                licenseFile = licenseEnvValue;
            }
            else if (!string.IsNullOrWhiteSpace(licenseEnvValue))
            {
                licenseFile = Path.GetTempFileName();
                File.WriteAllText(licenseFile, licenseEnvValue);
                tempLicense = true;
            }
        }

        if (!File.Exists(licenseFile))
        {
            Log.Warning("Babel license is not set");
        }

        try
        {
            foreach (var (outputDir, dependencyFiles) in targets)
            {
                var tfm = outputDir.Name;

                var dll = outputDir / (assemblyName + ".dll");
                var dependencies = dependencyFiles.Select(file => outputDir / (file + ".dll")).ToArray();
                Log.Information("Obfuscating {FileName} in {Folder}. And merging with {Dependencies}",
                    assemblyName, outputDir.Name, dependencies);

                var args = new ArgumentStringHandler(1, 1, out _);

                args.AppendFormatted(dll);
                args.AppendLiteral(" ");

                if (dependencies.Length > 0)
                {
                    args.AppendFormatted(dependencies);
                }

                args.AppendLiteral(" --nologo ");
                if (licenseFile is not null)
                {
                    args.AppendLiteral(" --license ");
                    args.AppendFormatted(licenseFile);
                }

                foreach (var rulesFile in rulesFiles ?? [])
                {
                    args.AppendLiteral(" --rules ");
                    args.AppendFormatted(rulesFile);
                }

                if(perTargetDependencyMapFiles is not null)
                {
                    if(perTargetDependencyMapFiles.TryGetValue(tfm, out var dependencyMapFiles))
                    {
                        foreach (var depMap in dependencyMapFiles)
                        {
                            args.AppendLiteral(" --map-in ");
                            args.AppendFormatted(depMap);
                        }
                    }                   

                    var mapOutput = outputDir / $"{assemblyName}.map.xml";

                    if(dependencyMapFiles is null)
                    {
                        dependencyMapFiles = [mapOutput];

                        perTargetDependencyMapFiles[tfm] = dependencyMapFiles;
                    }
                    else
                    {
                        dependencyMapFiles.Add(mapOutput);
                    }  

                    args.AppendLiteral(" --map-out ");
                    args.AppendFormatted(mapOutput);
                }

                if (signKey is not null)
                {
                    args.AppendLiteral(" --keyfile ");
                    args.AppendFormatted(signKey);
                }

                if (inlineExpansion)
                {
                    args.AppendLiteral(" --inlineexpansion ");
                }

                args.AppendLiteral(" --output ");
                args.AppendFormatted(dll);

                babel(args);
            }
            Log.Information("Obfuscated {AssemblyName}", assemblyName);
        }
        finally
        {
            if (tempLicense)
            {
                licenseFile.DeleteFile();
            }
        }
    }
}