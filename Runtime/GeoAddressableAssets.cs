using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.ResourceLocations;
using Object = UnityEngine.Object;

namespace GeoWorld
{
    // Assets that only a player showing a Geo World needs (the tree species, the ground detail and road textures, the wet terrain
    // and water materials with their shaders, the tiles' tree data: some 1.1 GB with what they refer to), loaded by address from
    // the world's shared Addressables group (GeoTileAddressables.ShareDependencies puts them there) instead of from Resources,
    // which goes into every player of a project, with a world or without. A player whose catalog has no such address (built
    // without the world's groups) gets null; the editor falls back to the AssetDatabase while the project's groups do not have
    // them yet. Loaded once, as Resources.Load was: the result is kept, null too.
    public static class GeoAddressableAssets
    {
        public const string TreeSpeciesProfile = "Packages/com.tredokiller.geoworld/Environment/Profiles/TreeSpeciesProfile.asset";
        public const string GroundDetailSet = "Packages/com.tredokiller.geoworld/Environment/Profiles/GroundDetailSet.asset";
        public const string RoadSurfaceSet = "Packages/com.tredokiller.geoworld/Environment/Profiles/RoadSurfaceSet.asset";
        public const string WorldTerrainWet = "Packages/com.tredokiller.geoworld/Environment/Materials/WorldTerrainWet.mat";
        public const string TakeoffTerrainLit = "Packages/com.tredokiller.geoworld/Environment/Materials/TakeoffTerrainLit.mat";
        public const string LakeWater = "Packages/com.tredokiller.geoworld/Environment/Materials/Water (lakes).mat";
        public const string SwampWater = "Packages/com.tredokiller.geoworld/Environment/Materials/Water (swamp).mat";
        public const string SeaWater = "Packages/com.tredokiller.geoworld/Environment/Materials/Water (sea).mat";
        public const string ProjectWater = "Packages/com.tredokiller.geoworld/Environment/Materials/Water (project).mat";
        public static readonly string[] All =
        {
            TreeSpeciesProfile, GroundDetailSet, RoadSurfaceSet, WorldTerrainWet, TakeoffTerrainLit, LakeWater, SwampWater, SeaWater, ProjectWater,
            // not loaded: they keep the variants of the wet terrain shader that a player makes at runtime (craters' per-pixel
            // normals, trench ground with and without normal and mask maps) in the bundle that has the shader
            "Packages/com.tredokiller.geoworld/Environment/Materials/WorldTerrainWetCraters.mat",
            "Packages/com.tredokiller.geoworld/Environment/Materials/WorldTerrainWetTrenches.mat",
            "Packages/com.tredokiller.geoworld/Environment/Materials/WorldTerrainWetTrenchesMask.mat",
            "Packages/com.tredokiller.geoworld/Environment/Materials/WorldTerrainWetTrenchesNormal.mat",
            "Packages/com.tredokiller.geoworld/Environment/Materials/WorldTerrainWetTrenchesNormalMask.mat"
        };

        // The tiles' tree data (GeoTreeData, GeoTreeDataExporter) lies in the project under this folder, at its key
        // (GeoTerrainTreeLoader.resourceKey); its address is its path.
        public const string TreeDataRoot = GeoPaths.Project + "/";
        public static string TreeData(string key) => TreeDataRoot + key + ".asset";

        static readonly Dictionary<string, Object> loaded = new Dictionary<string, Object>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetState() => loaded.Clear();

        public static T Load<T>(string address) where T : Object
        {
            if (loaded.TryGetValue(address, out var cached)) return cached as T;
            var location = Locate<T>(address);
            T asset = location != null ? Addressables.LoadAssetAsync<T>(location).WaitForCompletion() : null;
#if UNITY_EDITOR
            if (!asset) asset = UnityEditor.AssetDatabase.LoadAssetAtPath<T>(address);
#endif
            loaded[address] = asset;
            return asset;
        }

        // Where an address is in the catalog (null if it is not: a player built without the world's groups).
        public static IResourceLocation Locate<T>(string address)
        {
            var locations = Addressables.LoadResourceLocationsAsync(address, typeof(T));
            IList<IResourceLocation> found = locations.WaitForCompletion();
            var location = found != null && found.Count > 0 ? found[0] : null;
            Addressables.Release(locations);
            return location;
        }
    }
}
