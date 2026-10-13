using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GeoWorld
{
    public sealed class GeoBuildingPart
    {
        public string name;
        public Mesh mesh;
        public Material material;
        public bool collider;
    }
    public sealed class GeoBuildingOutput : IDisposable
    {
        public readonly List<GeoBuildingPart> parts=new List<GeoBuildingPart>();
        public void Add(string name,Mesh mesh,Material material,bool collider)
        {
            if(mesh.vertexCount==0){UnityEngine.Object.DestroyImmediate(mesh);return;}
            parts.Add(new GeoBuildingPart{name=name,mesh=mesh,material=material,collider=collider});
        }
        // Used on a failed build before the editor takes ownership of the meshes.
        public void Dispose(){foreach(var p in parts)if(p.mesh)UnityEngine.Object.DestroyImmediate(p.mesh);parts.Clear();}
    }
    public sealed class GeoBuildingContext
    {
        public readonly GeoBuilding building;
        public readonly GeoBuildingStyle style;
        public readonly List<Vector2> outline;
        public readonly List<int> triangles;
        public readonly float height,eaves,roofRise;
        public readonly Vector2 roofAxis;
        public readonly float roofMiddle,roofHalfSpan;
        public readonly Vector3[] hipPlanes;
        public GeoBuildingContext(GeoBuilding building)
        {
            this.building=building;style=building.style;
            if(!style)throw new ArgumentException("Choose a building style first.");
            if(!Finite(building.height)||building.height<2||building.height>600||!Finite(building.roofHeight)||
                !Finite(building.roofDirection)||!Finite(building.windowSpacing)||!Finite(building.windowWidth)||!Finite(building.windowHeight))
                throw new ArgumentException("Building dimensions must be finite. Height must be 2 to 600 metres.");
            outline=GeoPolygon.Clean(building.footprint);triangles=GeoPolygon.Triangulate(outline);
            height=building.height;roofRise=building.roof!=GeoRoofShape.Flat?Mathf.Clamp(building.roofHeight,0,height-2):0;
            eaves=height-roofRise;
            float angle=building.roofDirection*Mathf.Deg2Rad;
            roofAxis=new Vector2(Mathf.Cos(angle),-Mathf.Sin(angle));
            float min=float.MaxValue,max=float.MinValue;
            foreach(var p in outline){float d=Vector2.Dot(p,roofAxis);min=Mathf.Min(min,d);max=Mathf.Max(max,d);}
            roofMiddle=(min+max)*0.5f;roofHalfSpan=Mathf.Max(0.001f,(max-min)*0.5f);
            var cross=new Vector2(-roofAxis.y,roofAxis.x);float crossMin=float.MaxValue,crossMax=float.MinValue;
            foreach(var p in outline){float d=Vector2.Dot(p,cross);crossMin=Mathf.Min(crossMin,d);crossMax=Mathf.Max(crossMax,d);}
            float span=Mathf.Max(0.001f,Mathf.Min(roofHalfSpan,(crossMax-crossMin)*0.5f));
            hipPlanes=new[]{new Vector3(roofAxis.x,roofAxis.y,-min)/span,new Vector3(-roofAxis.x,-roofAxis.y,max)/span,
                new Vector3(cross.x,cross.y,-crossMin)/span,new Vector3(-cross.x,-cross.y,crossMax)/span};
        }
        static bool Finite(float v)=>!float.IsNaN(v)&&!float.IsInfinity(v);
        public static float PlaneHeight(Vector3 plane,Vector2 p)=>plane.x*p.x+plane.y*p.y+plane.z;
        public float RoofHeight(Vector2 p)
        {
            if(building.roof==GeoRoofShape.Hipped)
            {
                float rise=1;
                foreach(var plane in hipPlanes)rise=Mathf.Min(rise,PlaneHeight(plane,p));
                return eaves+roofRise*Mathf.Clamp01(rise);
            }
            // one slope: the highest edge is up the slope (against roofAxis, which points downslope), the lowest is at the eaves
            if(building.roof==GeoRoofShape.Skillion)
                return eaves+roofRise*Mathf.Clamp01(0.5f-(Vector2.Dot(p,roofAxis)-roofMiddle)/(2*roofHalfSpan));
            return eaves+roofRise*(1-Mathf.Clamp01(Mathf.Abs(Vector2.Dot(p,roofAxis)-roofMiddle)/roofHalfSpan));
        }
    }
    public sealed class GeoBuildingMesh
    {
        public readonly List<Vector3> vertices=new List<Vector3>();
        readonly List<Vector2> uv=new List<Vector2>();
        readonly List<int> triangles=new List<int>();
        public static Vector3 V(Vector2 p,float y)=>new Vector3(p.x,y,p.y);
        public void Triangle(Vector3 a,Vector3 b,Vector3 c)
        {
            if(vertices.Count>500000)throw new ArgumentException("Building detail exceeds 500,000 vertices. Reduce floors or facade detail.");
            if(Vector3.Cross(b-a,c-a).sqrMagnitude<0.00000001f)return;
            int n=vertices.Count;vertices.Add(a);vertices.Add(b);vertices.Add(c);
            uv.Add(new Vector2(a.x,a.z)*0.25f);uv.Add(new Vector2(b.x,b.z)*0.25f);uv.Add(new Vector2(c.x,c.z)*0.25f);
            triangles.AddRange(new[]{n,n+1,n+2});
        }
        public void Wall(Vector2 a,Vector2 b,float bottom,float topA,float topB)
        {
            if(vertices.Count>500000)throw new ArgumentException("Building detail exceeds 500,000 vertices. Reduce floors or facade detail.");
            int n=vertices.Count;float width=Vector2.Distance(a,b);
            vertices.AddRange(new[]{V(a,bottom),V(a,topA),V(b,topB),V(b,bottom)});
            uv.AddRange(new[]{new Vector2(0,bottom/3),new Vector2(0,topA/3),new Vector2(width/3,topB/3),new Vector2(width/3,bottom/3)});
            if(topA-bottom>0.0001f)triangles.AddRange(new[]{n,n+1,n+2});
            if(topB-bottom>0.0001f)triangles.AddRange(new[]{n,n+2,n+3});
        }
        public Mesh ToMesh(string name)
        {
            var mesh=new Mesh{name=name,indexFormat=vertices.Count>65535?IndexFormat.UInt32:IndexFormat.UInt16};
            mesh.SetVertices(vertices);mesh.SetUVs(0,uv);mesh.SetTriangles(triangles,0);mesh.RecalculateNormals();mesh.RecalculateBounds();return mesh;
        }
    }
    public static class GeoBuildingGeometry
    {
        // ground: the terrain's height relative to the building's base at a local point (x, z); a ruin model drapes its
        // yard and rubble over it. Null is flat ground.
        public static GeoBuildingOutput Build(GeoBuilding building,float foundationDepth,Func<Vector2,float> ground=null)
        {
            var c=new GeoBuildingContext(building);
            // an intact-building model replaces the walls and roof of a building that still stands
            if(GeoHomes.Draws(building))return GeoHomes.Build(c,foundationDepth);
            // a ruin model replaces the walls and roof, whatever the procedural damage would have been
            if(GeoRuins.Draws(building))return BuildRuin(c,ground);
            var state=GeoBuildingDamage.StateOf(building);
            // what is left of a heavily damaged or destroyed building replaces the usual walls and roof
            if(state>=GeoConditionState.HeavilyDamaged)return GeoBuildingDamage.BuildBroken(c,foundationDepth,state);
            var output=new GeoBuildingOutput();
            try
            {
                var walls=new GeoBuildingMesh();var roof=new GeoBuildingMesh();var foundation=new GeoBuildingMesh();
                uint seed=GeoBuildingDamage.Seed(building.sourceId??building.name);
                // a damaged roof is not one colour: some of its patches are scorched
                var burnt=state==GeoConditionState.Damaged?new GeoBuildingMesh():null;
                GeoBuildingMesh RoofFor(List<Vector2> patch)=>burnt!=null&&patch.Count>=3&&GeoBuildingDamage.Scorched(seed,Centre(patch))?burnt:roof;
                foundationDepth=Mathf.Max(0.2f,foundationDepth);
                for(int i=0;i<c.outline.Count;i++)
                {
                    Vector2 a=c.outline[i],b=c.outline[(i+1)%c.outline.Count];
                    foundation.Wall(a,b,-foundationDepth,0,0);
                    // Split wall edges at the ridge as well, so gable ends close exactly.
                    float da=Vector2.Dot(a,c.roofAxis)-c.roofMiddle,db=Vector2.Dot(b,c.roofAxis)-c.roofMiddle;
                    if(c.roofRise>0&&building.roof==GeoRoofShape.Hipped)
                    {
                        var cuts=new List<float>{0,1};
                        for(int j=0;j<c.hipPlanes.Length;j++)for(int k=j+1;k<c.hipPlanes.Length;k++)
                        {
                            var difference=c.hipPlanes[j]-c.hipPlanes[k];
                            float first=GeoBuildingContext.PlaneHeight(difference,a),last=GeoBuildingContext.PlaneHeight(difference,b);
                            if(first*last<0)cuts.Add(first/(first-last));
                        }
                        cuts.Sort();
                        for(int j=1;j<cuts.Count;j++)
                        {
                            var start=Vector2.Lerp(a,b,cuts[j-1]);var end=Vector2.Lerp(a,b,cuts[j]);
                            if((end-start).sqrMagnitude>0.00000001f)walls.Wall(start,end,0,c.RoofHeight(start),c.RoofHeight(end));
                        }
                    }
                    else if(c.roofRise>0&&building.roof==GeoRoofShape.Gabled&&da*db<0)
                    {
                        var ridge=Vector2.Lerp(a,b,da/(da-db));
                        walls.Wall(a,ridge,0,c.RoofHeight(a),c.RoofHeight(ridge));
                        walls.Wall(ridge,b,0,c.RoofHeight(ridge),c.RoofHeight(b));
                    }
                    else walls.Wall(a,b,0,c.RoofHeight(a),c.RoofHeight(b));
                }
                for(int i=0;i<c.triangles.Count;i+=3)
                {
                    var a=c.outline[c.triangles[i]];var b=c.outline[c.triangles[i+1]];var d=c.outline[c.triangles[i+2]];
                    foundation.Triangle(GeoBuildingMesh.V(a,0),GeoBuildingMesh.V(d,0),GeoBuildingMesh.V(b,0));
                    foundation.Triangle(GeoBuildingMesh.V(a,-foundationDepth),GeoBuildingMesh.V(b,-foundationDepth),GeoBuildingMesh.V(d,-foundationDepth));
                    var triangle=new List<Vector2>{a,b,d};
                    if(c.roofRise>0&&building.roof==GeoRoofShape.Hipped)
                    {
                        // Each hip plane covers the part where it is the lowest plane.
                        // Clip the already-triangulated footprint, preserving concave notches.
                        for(int plane=0;plane<c.hipPlanes.Length;plane++)
                        {
                            var patch=triangle;
                            for(int other=0;other<c.hipPlanes.Length&&patch.Count>0;other++)
                                if(other!=plane)patch=ClipPlane(patch,c.hipPlanes[other]-c.hipPlanes[plane]);
                            RoofPatch(patch,c,RoofFor(patch));
                        }
                    }
                    else if(c.roofRise>0&&building.roof==GeoRoofShape.Gabled)
                    {
                        var high=Clip(triangle,c,true);var low=Clip(triangle,c,false);
                        RoofPatch(high,c,RoofFor(high));RoofPatch(low,c,RoofFor(low));
                    }
                    else RoofPatch(triangle,c,RoofFor(triangle));
                }
                // the roof itself always remains: a building whose every patch is scorched is drawn with the plain roof
                if(burnt!=null&&roof.vertices.Count==0){roof=burnt;burnt=null;}
                output.Add("Foundation",foundation.ToMesh("Footprint foundation"),c.style.foundation,true);
                output.Add("Walls",walls.ToMesh("Footprint walls"),building.wallMaterial?building.wallMaterial:c.style.walls,true);
                output.Add("Roof",roof.ToMesh("Footprint roof"),building.roofMaterial?building.roofMaterial:c.style.roof,true);
                if(burnt!=null)output.Add("Burnt roof",burnt.ToMesh("Scorched roof"),building.roofMaterial?building.roofMaterial:c.style.roof,true);
                Facade(c,output,state==GeoConditionState.Damaged?0.65f:0f,null,seed);
                foreach(var module in c.style.modules??new GeoBuildingModule[0])if(module)module.Build(c,output);
                return output;
            }
            catch{output.Dispose();throw;}
        }
        static GeoBuildingOutput BuildRuin(GeoBuildingContext c,Func<Vector2,float> ground)
        {
            var output=new GeoBuildingOutput();
            try
            {
                var target=GeoRuins.TargetOf(c.building,c.outline);target.ground=ground;
                output.Add("Ruin",GeoRuinFit.Build(c.building.ruin,target,out _),c.building.ruin.material,true);
                return output;
            }
            catch{output.Dispose();throw;}
        }
        static List<Vector2> Clip(List<Vector2> polygon,GeoBuildingContext c,bool positive)
        {
            var result=new List<Vector2>();
            for(int i=0;i<polygon.Count;i++)
            {
                var a=polygon[i];var b=polygon[(i+1)%polygon.Count];
                float da=(Vector2.Dot(a,c.roofAxis)-c.roofMiddle)*(positive?1:-1);
                float db=(Vector2.Dot(b,c.roofAxis)-c.roofMiddle)*(positive?1:-1);
                if(da>=0)result.Add(a);
                if((da>=0)!=(db>=0))result.Add(Vector2.Lerp(a,b,da/(da-db)));
            }
            return result;
        }
        static List<Vector2> ClipPlane(List<Vector2> polygon,Vector3 plane)
        {
            var result=new List<Vector2>();
            for(int i=0;i<polygon.Count;i++)
            {
                var a=polygon[i];var b=polygon[(i+1)%polygon.Count];
                float da=GeoBuildingContext.PlaneHeight(plane,a),db=GeoBuildingContext.PlaneHeight(plane,b);
                if(da>=0)result.Add(a);
                if((da>=0)!=(db>=0))result.Add(Vector2.Lerp(a,b,da/(da-db)));
            }
            return result;
        }
        static Vector2 Centre(List<Vector2> polygon)
        {
            var sum=Vector2.zero;foreach(var p in polygon)sum+=p;
            return sum/Mathf.Max(1,polygon.Count);
        }
        static void RoofPatch(List<Vector2> polygon,GeoBuildingContext c,GeoBuildingMesh mesh)
        {
            for(int i=1;i+1<polygon.Count;i++)
                mesh.Triangle(GeoBuildingMesh.V(polygon[0],c.RoofHeight(polygon[0])),GeoBuildingMesh.V(polygon[i+1],c.RoofHeight(polygon[i+1])),GeoBuildingMesh.V(polygon[i],c.RoofHeight(polygon[i])));
        }
        // brokenChance: share of windows that are dark holes instead of glass. wallTop(edge, metres along it) is how
        // high the wall still stands there (null: all of it); windows and the door above that are left out.
        internal static void Facade(GeoBuildingContext c,GeoBuildingOutput output,float brokenChance=0f,Func<int,float,float> wallTop=null,uint seed=0)
        {
            var b=c.building;var glass=new GeoBuildingMesh();var frames=new GeoBuildingMesh();var doors=new GeoBuildingMesh();var dark=new GeoBuildingMesh();
            int floors=Mathf.Clamp(b.floors,1,100);
            float storey=c.eaves/floors;
            for(int i=0;i<c.outline.Count;i++)
            {
                var a=c.outline[i];var end=c.outline[(i+1)%c.outline.Count];var direction=(end-a).normalized;
                var outside=new Vector2(direction.y,-direction.x);
                float length=Vector2.Distance(a,end);
                bool entrance=b.entrance&&i==Mathf.Clamp(b.entranceEdge,0,c.outline.Count-1)&&length>=1.5f;
                float doorAt=Mathf.Clamp(length*b.entrancePosition,0.65f,length-0.65f);
                float doorHeight=Mathf.Min(2.2f,c.eaves*0.9f);
                if(entrance&&wallTop!=null&&wallTop(i,doorAt)<doorHeight)entrance=false;
                if(entrance)doors.Wall(a+direction*(doorAt-0.5f)+outside*0.032f,a+direction*(doorAt+0.5f)+outside*0.032f,0.02f,doorHeight,doorHeight);
                if(!b.windows||storey<1.5f)continue;
                int columns=Mathf.Clamp(Mathf.FloorToInt(length/Mathf.Max(1,b.windowSpacing)),0,128);
                if(columns==0)continue;
                float width=Mathf.Min(Mathf.Max(0.2f,b.windowWidth),length/columns*0.6f);
                float height=Mathf.Min(Mathf.Max(0.2f,b.windowHeight),storey*0.65f);
                for(int floor=0;floor<floors;floor++)for(int col=0;col<columns;col++)
                {
                    float at=length*(col+0.5f)/columns;
                    float bottom=storey*(floor+0.5f)-height*0.5f,top=bottom+height;
                    if(entrance&&bottom<doorHeight+0.15f&&Mathf.Abs(at-doorAt)<width*0.5f+0.65f)continue;
                    if(wallTop!=null&&wallTop(i,at)<top+0.1f)continue;
                    bool broken=brokenChance>0&&GeoBuildingDamage.Hash01(seed,i*977+floor,col)<brokenChance;
                    var left=a+direction*(at-width*0.5f);var right=a+direction*(at+width*0.5f);
                    (broken?dark:glass).Wall(left+outside*0.035f,right+outside*0.035f,bottom,top,top);
                    // Sills are separate from glass, avoiding coplanar facade overlays.
                    if(!broken)frames.Wall(left-direction*0.08f+outside*0.05f,right+direction*0.08f+outside*0.05f,bottom-0.08f,bottom,bottom);
                }
            }
            output.Add("Windows",glass.ToMesh("Footprint windows"),c.style.windows,false);
            output.Add("Window sills",frames.ToMesh("Window sills"),c.style.foundation,false);
            output.Add("Entrance",doors.ToMesh("Entrance"),c.style.door,false);
            output.Add("Broken windows",dark.ToMesh("Broken windows"),c.style.door,false);
        }
    }
}
