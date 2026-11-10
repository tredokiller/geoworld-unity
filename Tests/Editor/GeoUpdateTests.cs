using System;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace GeoWorld.Editor.Tests
{
    // Updates find the newest version by the repository's v<version> tags and compare it with package.json's version, so a
    // release keeps them together; fast updates clone into the project's own Packages folder, never into the Package
    // Manager's copy that Unity maps Packages/com.tredokiller.geoworld to.
    public class GeoUpdateTests
    {
        const string Project = "C:/Projects/Game";
        const string Target = Project + "/Packages/" + GeoGitUpdate.PackageFolder;
        const string Cache = Project + "/Library/PackageCache/com.tredokiller.geoworld@c607d8fdf84a";

        static string Read(string file) => File.ReadAllText(GeoPaths.Disk(GeoPaths.Package + "/" + file));
        static string PackageVersion => (string)JObject.Parse(Read("package.json"))["version"];

        [Test] public void FastUpdatesCloneWherePackagesHasNoGeoWorldFolder_ThoughUnityMapsThatPathToTheCache()
        {
            string[] packages = { Project + "/Packages/manifest.json", Project + "/Packages/packages-lock.json" };
            Assert.IsNull(GeoGitUpdate.Blocked(packages, Target, Cache));
            Assert.IsNull(GeoGitUpdate.Blocked(packages, Target.Replace('/', '\\'), Cache.Replace('/', '\\')), "Windows separators");
        }

        [Test] public void FastUpdatesLeaveAFolderAlreadyInPackages()
        {
            string[] packages = { Project + "/Packages/manifest.json", Target };
            Assert.That(GeoGitUpdate.Blocked(packages, Target, Cache), Does.Contain("is there already"));
            Assert.That(GeoGitUpdate.Blocked(new[] { Target.ToUpperInvariant() + "\\" }, Target, Cache), Does.Contain("is there already"));
        }

        [Test] public void FastUpdatesNeverWriteToThePackageManagersCopy()
        {
            Assert.That(GeoGitUpdate.Blocked(new string[0], Cache, Cache), Does.Contain("nothing was changed"));
            Assert.That(GeoGitUpdate.Blocked(new string[0], Project + "/Library/PackageCache/x/Packages/" + GeoGitUpdate.PackageFolder, Cache),
                Does.Contain("nothing was changed"));
            Assert.That(GeoGitUpdate.Blocked(new string[0], Target, Target), Does.Contain("nothing was changed"), "the target is the installed copy");
            Assert.That(GeoGitUpdate.Blocked(null, Target, Cache), Does.Contain("could not be read"));
        }

        // 1.21.0 was tagged on a commit whose package.json still said 1.20.0: every project on it or after it was offered
        // "1.21.0" forever, and taking it moved a project pinned past the tag back to it.
        [Test] public void PackageVersionIsAThreePartVersion_AndTheGeneratorSaysTheSame()
        {
            Assert.That(PackageVersion, Does.Match(@"^\d+\.\d+\.\d+$"));
            Assert.IsTrue(Version.TryParse(PackageVersion, out _));
            Assert.AreEqual(PackageVersion, GeoWorldGenerator.Version, "GeoWorldGenerator.Version is written into every package: keep it with package.json");
        }

        [Test] public void TheChangelogsNewestReleaseIsThePackageVersion()
        {
            var release = Regex.Match(Read("CHANGELOG.md"), @"^## (\d+\.\d+\.\d+)\s*$", RegexOptions.Multiline);
            Assert.IsTrue(release.Success, "CHANGELOG.md has a ## <version> heading");
            Assert.AreEqual(PackageVersion, release.Groups[1].Value, "the newest ## <version> of CHANGELOG.md (under ## Unreleased)");
        }

        [Test] public void TheReadmeInstallsThePackageVersion()
        {
            string readme = Read("README.md"), tag = "v" + PackageVersion;
            var tags = Regex.Matches(readme, @"\bv\d+\.\d+\.\d+\b");
            Assert.Greater(tags.Count, 0);
            foreach (Match match in tags) Assert.AreEqual(tag, match.Value, "every install and update example of README.md names " + tag);
        }
    }
}
