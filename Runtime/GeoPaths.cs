using System.IO;

namespace GeoWorld
{
    // Where Geo World keeps things. The package (the code, shaders, building styles, tree models, roof, ground and road
    // textures) is read-only in a project that installs it from git; everything the tools make or import goes into the
    // project's Assets/GeoWorld: generated worlds and tiles, the ruin and home libraries, the reduced models.
    public static class GeoPaths
    {
        public const string PackageName = "com.tredokiller.geoworld";
        // the package's own files, as asset paths
        public const string Package = "Packages/" + PackageName;
        // what the tools write in the project
        public const string Project = "Assets/GeoWorld";

        // A file on disk for an asset path: a package installed from git is not under Packages/ on disk, and
        // Path.GetFullPath is what Unity resolves package paths with.
        public static string Disk(string assetPath) => Path.GetFullPath(assetPath);
        public static bool Exists(string assetPath) => File.Exists(Disk(assetPath));
    }
}
