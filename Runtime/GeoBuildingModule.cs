using UnityEngine;

namespace GeoWorld
{
    // Modules produce local-space geometry. The editor owns persistence and Undo.
    public abstract class GeoBuildingModule : ScriptableObject
    {
        public abstract void Build(GeoBuildingContext context, GeoBuildingOutput output);
    }
}
