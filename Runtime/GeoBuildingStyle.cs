using UnityEngine;

namespace GeoWorld
{
    [CreateAssetMenu(menuName="Geo World/Building style")]
    public class GeoBuildingStyle : ScriptableObject
    {
        public Material walls, roof, foundation, windows, door;
        public GeoRoofShape defaultRoof;
        [Min(0)] public float defaultRoofHeight = 2;
        [Min(1)] public float windowSpacing = 3;
        public Vector2 windowSize = new Vector2(1.1f,1.3f);
        [Tooltip("Optional generators for trim, facade details or other building parts.")]
        public GeoBuildingModule[] modules = new GeoBuildingModule[0];

        public void ApplyDefaults(GeoBuilding building)
        {
            building.roof=defaultRoof;building.roofHeight=defaultRoofHeight;
            building.windowSpacing=windowSpacing;building.windowWidth=windowSize.x;building.windowHeight=windowSize.y;
        }
    }
}
