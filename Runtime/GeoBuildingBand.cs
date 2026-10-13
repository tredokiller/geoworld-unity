using UnityEngine;

namespace GeoWorld
{
    [CreateAssetMenu(menuName="Geo World/Building modules/Facade band")]
    public class GeoBuildingBand : GeoBuildingModule
    {
        public Material material;
        [Range(0.05f,0.95f)] public float relativeHeight = 0.7f;
        [Min(0.02f)] public float thickness = 0.18f;
        public override void Build(GeoBuildingContext context,GeoBuildingOutput output)
        {
            var mesh=new GeoBuildingMesh();
            float bottom=Mathf.Clamp(context.eaves*relativeHeight,0,context.eaves);
            float top=Mathf.Min(context.eaves,bottom+thickness);
            for(int i=0;i<context.outline.Count;i++)
            {
                Vector2 a=context.outline[i],b=context.outline[(i+1)%context.outline.Count];
                Vector2 edge=(b-a).normalized,offset=new Vector2(edge.y,-edge.x)*0.025f;
                mesh.Wall(a+offset,b+offset,bottom,top,top);
            }
            output.Add("Facade band",mesh.ToMesh("Facade band"),material?material:context.style.foundation,false);
        }
    }
}
