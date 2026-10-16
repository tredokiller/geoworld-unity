using UnityEngine;

namespace GeoWorld.Editor
{
    // A saved set of generation settings: everything the Geo World window sets (tiles or one world, the switches,
    // Advanced settings, your tree prefabs), kept in an asset so it is not set again for every world and can be shared
    // through the project. The window edits the config it has open and remembers it on this computer.
    [CreateAssetMenu(menuName = "Geo World/Generation config", fileName = "Geo World config")]
    public class GeoGenerationConfig : ScriptableObject
    {
        public const string Folder = GeoPaths.Project + "/Configs";

        public bool tiled = true;
        [Min(250)] public float tileSize = GeoDefaults.TileSize;
        public GenerationSettings settings = new GenerationSettings();
    }
}
