using Nuke.Common;
using Nuke.Common.IO;

namespace NukeExtensions;

public static class Statics
{
    public static AbsolutePath DefaultBuildCommon => NukeBuild.RootDirectory / "build-common";
    public static AbsolutePath AvaloniaStrongNameKey => DefaultBuildCommon / "avalonia.snk";
    public static AbsolutePath BabelLicense => DefaultBuildCommon / "babel.license";
    public static AbsolutePath BabelRules => DefaultBuildCommon / "babel.rules";
    public static AbsolutePath Icon => DefaultBuildCommon / "branding" / "icon.png";
}