using UnityEngine;

namespace GeoWorld
{
    [DisallowMultipleComponent]
    public class GeoPlacedProp : MonoBehaviour
    {
        public string sourceId;
        public string kind;
        [Tooltip("Mapped: position/line is in OSM. Manual_photo: user placed an observed object on the map. Generated: position was chosen from vegetation coverage, a tree row, or street context.")]
        public string placementSource;
        public string evidence;
        [Tooltip("Mapped positions can still have estimated orientation and preview dimensions.")]
        public string rotationSource;
        public GeoPhotoReference photoReference;
        [Tooltip("After the battle (where the world has craters or damaged buildings): 0 intact, 1 damaged (leaning, panels gone, chipped), 2 broken (snapped, knocked over, flattened, breached), 3 a wall down to its rubble.")]
        public int damage;
        [Tooltip("Preserve this object and its children during a whole-world rebuild.")]
        public bool keepOnRebuild;
    }
}
