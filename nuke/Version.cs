using System;
using Semver;

namespace NukeExtensions;

public static class VersionResolver
{
    public static SemVersion? GetGitHubVersion(Version baseVersionNumber, bool isPackingToLocalCache)
    {
        return GetVersion(
            baseVersionNumber,
            isPackingToLocalCache,
            Environment.GetEnvironmentVariable("GITHUB_REF_NAME"),
            Environment.GetEnvironmentVariable("GITHUB_RUN_NUMBER"));
    }

    public static SemVersion? GetVersion(Version baseVersionNumber, bool isPackingToLocalCache, string? refName, string? runNumber)
    {
        // Release tag
        if (SemVersion.TryParse(refName, out var tagVersion))
        {
            return tagVersion;
        }
        // Release branch
        else if (SemVersion.TryParse(refName?.Replace("release/", "") ?? "", out var releaseVersion))
        {
            return releaseVersion;
        }
        // CI build number
        else if (int.TryParse(runNumber, out var ciRun))
        {
            return SemVersion.Parse(
                baseVersionNumber + "-cibuild" + ciRun.ToString("0000000") + "-alpha",
                SemVersionStyles.Any);
        }

        if (isPackingToLocalCache)
        {
            return SemVersion.Parse("9999.0.0-localbuild");
        }

        return SemVersion.Parse(baseVersionNumber + "-localbuild-alpha", SemVersionStyles.Any);
    }
}