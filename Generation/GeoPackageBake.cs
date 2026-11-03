using System;
using System.Collections.Generic;
using GeoWorld.Editor;
using UnityEngine;

namespace GeoWorld
{
    // What GeoWorldGenerator.Baking gives the assemblies that bake data of the whole world into a package: the export (its
    // environment.json, read), the tiles, the land cover just baked, and Add for their files (an extra of the manifest each).
    public sealed class GeoPackageBake
    {
        public string EnvironmentPath { get; }
        public EnvironmentData Environment { get; }
        public float TileSize { get; }
        public IReadOnlyList<Vector2Int> Tiles { get; }
        // the export's ground map (null when it has none): what the ground is at a point, as the package's land cover says
        public GeoGroundMap Ground { get; internal set; }
        // notes for the package's manifest
        public List<string> Notes { get; }

        internal readonly List<(string name, string file, byte[] bytes)> files = new List<(string, string, byte[])>();

        internal GeoPackageBake(string environmentPath, EnvironmentData environment, float tileSize, IList<Vector2Int> tiles, List<string> notes)
        {
            EnvironmentPath = environmentPath; Environment = environment; TileSize = tileSize; Tiles = new List<Vector2Int>(tiles); Notes = notes;
        }

        // A file for the package (next to world.json, under file) that GeoWorldPackage.ReadExtra(name) gives back.
        public void Add(string name, string file, byte[] bytes)
        {
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(file) || bytes == null) throw new ArgumentException("An extra needs a name, a file and its bytes.");
            if (files.Exists(f => f.name == name || f.file == file)) throw new ArgumentException("The package has an extra " + name + " (" + file + ") already.");
            if (file.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || file == GeoWorldPackage.ManifestFile) throw new ArgumentException("An extra's file is a plain name next to world.json: " + file);
            files.Add((name, file, bytes));
        }
    }
}
