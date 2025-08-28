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
        string[] DependencyFileNames);

    public static void Obfuscate(
        Tool babel,
        string assemblyName,
        IReadOnlyList<ObfuscationTargetFramework> targets,
        AbsolutePath? licenseFile = null,
        AbsolutePath? rulesFile = null,
        AbsolutePath? signKey = null)
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
            else
            {
                throw new Exception("Babel license is missing");
            }
        }

        try
        {
            foreach (var (outputDir, dependencyFiles) in targets)
            {
                var dll = outputDir / assemblyName;
                var dependencies = dependencyFiles.Select(file => outputDir / file);
                Log.Information("Obfuscating {FileName} in {Folder}. And merging with {Dependencies}",
                    assemblyName, outputDir.Name, dependencyFiles);

                var args = new ArgumentStringHandler();
                args.AppendLiteral(" --nologo ");
                if (licenseFile is not null)
                {
                    args.AppendLiteral($" --license ");
                    args.AppendFormatted(licenseFile);
                }

                args.AppendFormatted(dll);

                if (rulesFile is not null)
                {
                    args.AppendLiteral(" --rules ");
                    args.AppendFormatted(rulesFile);
                }

                if (signKey is not null)
                {
                    args.AppendLiteral(" --keyfile ");
                    args.AppendFormatted(signKey);
                }

                args.AppendFormatted(dll);
                args.AppendFormatted(dependencies);

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