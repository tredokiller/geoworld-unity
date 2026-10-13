using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GeoWorld.Editor
{
    public class PathBuild
    {
        public LineFeature feature;
        public bool river;
        public List<Vector3> controls, samples;
        public bool Elevated => feature.bridge || feature.layer != 0;
    }

    public class MeshDraft
    {
        public readonly List<Vector3> vertices = new List<Vector3>();
        public readonly List<Vector2> uv = new List<Vector2>();
        // optional second UV set; a road surface keeps its edges here (GeoMeshes.RoadEdges) for the GeoWorld/Road shader
        public readonly List<Vector2> uv2 = new List<Vector2>();
        public readonly List<int> triangles = new List<int>();
        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector2 uvA, Vector2 uvB, Vector2 uvC, Vector2 uvD)
        {
            int n = vertices.Count; vertices.AddRange(new[] { a,b,c,d }); uv.AddRange(new[] { uvA,uvB,uvC,uvD });
            triangles.AddRange(new[] { n,n+1,n+2,n,n+2,n+3 });
        }
        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) => Quad(a,b,c,d,Vector2.zero,Vector2.right,Vector2.one,Vector2.up);
        public Mesh Mesh(string name, System.Func<Vector3, Vector3> normal = null)
        {
            var result = new Mesh { name = name, indexFormat = vertices.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            result.SetVertices(vertices); result.SetUVs(0,uv); result.SetTriangles(triangles,0);
            if(uv2.Count>0&&uv2.Count==vertices.Count)result.SetUVs(1,uv2);
            if(normal==null)result.RecalculateNormals();else result.SetNormals(vertices.ConvertAll(p=>normal(p)));
            result.RecalculateBounds(); return result;
        }
    }

    public static class GeoMeshes
    {
        public static Vector3 Side(List<Vector3> points, int i)
        {
            Vector3 before = points[i] - points[Mathf.Max(0,i-1)], after = points[Mathf.Min(points.Count-1,i+1)] - points[i];
            before.y=0; after.y=0;
            if (before.sqrMagnitude<0.00001f) before=after;
            if (after.sqrMagnitude<0.00001f) after=before;
            before.Normalize(); after.Normalize();
            Vector3 tangent = (before+after).normalized;
            if (tangent.sqrMagnitude<0.01f) tangent=after;
            Vector3 side = new Vector3(-tangent.z,0,tangent.x);
            float denominator = Mathf.Abs(Vector3.Dot(side,new Vector3(-after.z,0,after.x)));
            return side / Mathf.Max(0.5f,denominator); // Bounded miter: sharp bends cannot create spikes.
        }

        public static Mesh Ribbon(PathBuild path, HeightField field, float offset)
        {
            var mesh = new MeshDraft();
            float length = 0, half=path.feature.width*0.5f;
            const int lanes=4;
            for(int i=0;i<path.samples.Count;i++)
            {
                if(i>0) length+=Vector3.Distance(path.samples[i-1],path.samples[i]);
                Vector3 side=Side(path.samples,i);
                for(int j=0;j<=lanes;j++)
                {
                    Vector3 v=path.samples[i]+side*Mathf.Lerp(-half,half,j/(float)lanes);
                    if(!path.river&&!path.Elevated) v.y=field.Sample(v.x,v.z);
                    v.y+=offset;
                    mesh.vertices.Add(v); mesh.uv.Add(new Vector2(j*path.feature.width/lanes/4,length/4));
                    if(i==0||j==0) continue;
                    int d=i*(lanes+1)+j,a=d-(lanes+1)-1,b=a+1,c=d-1;
                    // side points left; cross-width then forward gives upward-facing triangles.
                    mesh.triangles.AddRange(new[] { a,b,c,b,d,c });
                }
            }
            return mesh.Mesh(path.river?"River surface":"Road surface");
        }

        public static Mesh Markings(PathBuild path, HeightField field, GeoRoadNetwork network)
        {
            var mesh = new MeshDraft();
            if(path.river||path.feature.material!="asphalt"||path.feature.width<5) return mesh.Mesh("Markings");
            float distance=0;
            for(int i=1;i<path.samples.Count;i++)
            {
                Vector3 a=path.samples[i-1],b=path.samples[i];
                float span=Vector3.Distance(a,b);
                Vector3 middle=(a+b)*0.5f;
                bool junction=network.HidesMarking(path,middle)||network.HidesMarking(path,a)||network.HidesMarking(path,b);
                if(!junction && Mathf.Repeat(distance+span*0.5f,8)<4)
                {
                    var sa=Side(path.samples,i-1)*0.065f; var sb=Side(path.samples,i)*0.065f;
                    Vector3 v0=a-sa,v1=b-sb,v2=b+sb,v3=a+sa;
                    if(!path.Elevated)
                    { v0.y=network.Height(v0.x,v0.z);v1.y=network.Height(v1.x,v1.z);v2.y=network.Height(v2.x,v2.z);v3.y=network.Height(v3.x,v3.z); }
                    if(!path.Elevated&&(!network.Owns(path,v0)||!network.Owns(path,v1)||!network.Owns(path,v2)||!network.Owns(path,v3)))
                    {distance+=span;continue;}
                    Vector3 lift=Vector3.up*(GeoRoadNetwork.SurfaceOffset+0.035f);
                    mesh.Quad(v0+lift,v3+lift,v2+lift,v1+lift);
                }
                distance+=span;
            }
            return mesh.Mesh("Dashed centre line");
        }

        // UVs that run across and along one road: u is 0 to 1 over its width, v is metres along it / lengthMetres (plus an
        // offset, so every road starts at a different place in the picture). Each vertex takes the nearest part of the centreline.
        public static void RoadUv(PathBuild path, MeshDraft draft, float lengthMetres, float offset)
        {
            if (path.samples.Count < 2 || draft.vertices.Count == 0) return;
            var along = Along(path);
            draft.uv.Clear();
            foreach (var p in draft.vertices) draft.uv.Add(RoadUvAt(path, along, p, lengthMetres, offset, 0, 1));
        }

        // A junction is made of pieces of the surfaces of the roads that meet there. Each piece takes the picture of its own
        // road, laid the same way as along the road, so the worn asphalt runs on through the crossing. Near the sides the
        // picture is held at its asphalt (low..high of u), so the dust of the verge does not lie across the mouth of a side road.
        public static void JunctionUv(MeshDraft draft, List<PathBuild> owners, float lengthMetres, float low, float high)
        {
            var cache = new Dictionary<PathBuild, float[]>();
            draft.uv.Clear();
            for (int i = 0; i < draft.vertices.Count; i++)
            {
                var path = owners[i];
                if (path == null || path.samples.Count < 2) { draft.uv.Add(Vector2.zero); continue; }
                if (!cache.TryGetValue(path, out var along)) cache[path] = along = Along(path);
                float offset = GeoBuildingDamage.Hash01(GeoBuildingDamage.Seed(path.feature.id ?? ""), 11);
                // lengthMetres 0: a square picture that spans the road, so it repeats every road width
                float length = lengthMetres > 0 ? lengthMetres : Mathf.Max(3, path.feature.width);
                draft.uv.Add(RoadUvAt(path, along, draft.vertices[i], length, offset, low, high));
            }
        }

        // Where each vertex lies across its road, for the soft edges of the GeoWorld/Road shader: uv2 = (metres left of the
        // centreline, half the road's width). The offset runs linearly across the road, so the shader gets the exact distance
        // to the edge (half - |offset|) anywhere inside a triangle, however coarse the terrain grid the surface follows.
        public static void RoadEdges(PathBuild path, MeshDraft draft)
        {
            draft.uv2.Clear();
            float half = Mathf.Max(0.25f, path.feature.width * 0.5f);
            foreach (var p in draft.vertices) draft.uv2.Add(new Vector2(Lateral(path, p), half));
        }

        // The same for a junction: every piece is measured across the road it belongs to (owners, one per vertex), so the
        // edges of the crossing fade where the roads' own edges are; a vertex without a road gets no fade (half width 0).
        public static void JunctionEdges(MeshDraft draft, List<PathBuild> owners)
        {
            draft.uv2.Clear();
            for (int i = 0; i < draft.vertices.Count; i++)
            {
                var path = i < owners.Count ? owners[i] : null;
                draft.uv2.Add(path == null || path.samples.Count < 2 ? Vector2.zero
                    : new Vector2(Lateral(path, draft.vertices[i]), Mathf.Max(0.25f, path.feature.width * 0.5f)));
            }
        }

        // Metres left of the nearest part of the centreline (negative: right of it).
        static float Lateral(PathBuild path, Vector3 p)
        {
            var s = path.samples;
            float best = float.MaxValue, lateral = 0;
            for (int i = 1; i < s.Count; i++)
            {
                float ax = s[i - 1].x, az = s[i - 1].z, dx = s[i].x - ax, dz = s[i].z - az;
                float len2 = dx * dx + dz * dz; if (len2 < 1e-8f) continue;
                float t = Mathf.Clamp01(((p.x - ax) * dx + (p.z - az) * dz) / len2);
                float qx = p.x - (ax + dx * t), qz = p.z - (az + dz * t), d2 = qx * qx + qz * qz;
                if (d2 >= best) continue;
                best = d2;
                lateral = (qx * -dz + qz * dx) / Mathf.Sqrt(len2);
            }
            return lateral;
        }

        static float[] Along(PathBuild path)
        {
            var s = path.samples; var along = new float[s.Count];
            for (int i = 1; i < s.Count; i++) along[i] = along[i - 1] + Vector3.Distance(s[i - 1], s[i]);
            return along;
        }

        static Vector2 RoadUvAt(PathBuild path, float[] along, Vector3 p, float lengthMetres, float offset, float low, float high)
        {
            var s = path.samples;
            float width = Mathf.Max(0.5f, path.feature.width);
            float best = float.MaxValue, u = 0.5f, v = 0;
            for (int i = 1; i < s.Count; i++)
            {
                float ax = s[i - 1].x, az = s[i - 1].z, dx = s[i].x - ax, dz = s[i].z - az;
                float len2 = dx * dx + dz * dz; if (len2 < 1e-8f) continue;
                float t = Mathf.Clamp01(((p.x - ax) * dx + (p.z - az) * dz) / len2);
                float qx = p.x - (ax + dx * t), qz = p.z - (az + dz * t), d2 = qx * qx + qz * qz;
                if (d2 >= best) continue;
                best = d2;
                float len = Mathf.Sqrt(len2);
                float lateral = (qx * -dz + qz * dx) / len; // left of the direction of travel is positive
                u = 0.5f + lateral / width;
                v = (along[i - 1] + t * len) / lengthMetres + offset;
            }
            return new Vector2(Mathf.Clamp(u, low, high), v);
        }

        // Markings of a ground road, the Ukrainian way: main roads (secondary and above, and tertiary) get a broken white
        // centre line, secondary and above also solid edge lines; streets and tracks have none. Lines follow the same terrain
        // triangles as the road surface and stop at junctions and wherever another road owns the surface.
        public static Mesh GroundMarkings(PathBuild path, GeoRoadNetwork network)
        {
            var mesh = new MeshDraft();
            string type = path.feature.type ?? "";
            bool main = type == "motorway" || type == "trunk" || type == "primary" || type == "secondary" || type == "tertiary";
            if (path.river || path.Elevated || !main || path.feature.width < 5 || GeoData.Surface(path.feature.material) != GroundSurface.Asphalt)
                return mesh.Mesh("Markings");
            bool edges = type != "tertiary" && path.feature.width >= 6;
            float half = path.feature.width * 0.5f, distance = 0;
            const float period = 12, dash = 4, line = 0.06f;
            Vector3 lift = Vector3.up * (GeoRoadNetwork.SurfaceOffset + 0.03f);
            for (int i = 1; i < path.samples.Count; i++)
            {
                Vector3 a = path.samples[i - 1], b = path.samples[i];
                float span = Vector3.Distance(a, b);
                bool junction = network.HidesMarking(path, (a + b) * 0.5f) || network.HidesMarking(path, a) || network.HidesMarking(path, b);
                if (!junction)
                {
                    Vector3 sa = Side(path.samples, i - 1), sb = Side(path.samples, i);
                    if (Mathf.Repeat(distance + span * 0.5f, period) < dash) Strip(mesh, network, path, a, b, sa, sb, 0, line, lift);
                    if (edges)
                    {
                        Strip(mesh, network, path, a, b, sa, sb, half - 0.45f, line, lift);
                        Strip(mesh, network, path, a, b, sa, sb, -(half - 0.45f), line, lift);
                    }
                }
                distance += span;
            }
            return mesh.Mesh("Road markings");
        }

        static void Strip(MeshDraft mesh, GeoRoadNetwork network, PathBuild path, Vector3 a, Vector3 b, Vector3 sa, Vector3 sb, float offset, float halfWidth, Vector3 lift)
        {
            Vector3 v0 = a + sa * (offset - halfWidth), v1 = b + sb * (offset - halfWidth), v2 = b + sb * (offset + halfWidth), v3 = a + sa * (offset + halfWidth);
            v0.y = network.Height(v0.x, v0.z); v1.y = network.Height(v1.x, v1.z); v2.y = network.Height(v2.x, v2.z); v3.y = network.Height(v3.x, v3.z);
            if (!network.Owns(path, v0) || !network.Owns(path, v1) || !network.Owns(path, v2) || !network.Owns(path, v3)) return;
            mesh.Quad(v0 + lift, v3 + lift, v2 + lift, v1 + lift);
        }

        public static Mesh Roof(float width,float length,float wallHeight)
        {
            float x=width*0.52f,z=length*0.52f,peak=wallHeight+Mathf.Min(width*0.32f,3);
            var m=new MeshDraft();
            m.Quad(new Vector3(-x,wallHeight,-z),new Vector3(-x,wallHeight,z),new Vector3(0,peak,z),new Vector3(0,peak,-z));
            m.Quad(new Vector3(0,peak,-z),new Vector3(0,peak,z),new Vector3(x,wallHeight,z),new Vector3(x,wallHeight,-z));
            m.Quad(new Vector3(-x,wallHeight,-z),new Vector3(0,peak,-z),new Vector3(x,wallHeight,-z),new Vector3(x,wallHeight,-z));
            m.Quad(new Vector3(x,wallHeight,z),new Vector3(0,peak,z),new Vector3(-x,wallHeight,z),new Vector3(-x,wallHeight,z));
            return m.Mesh("Gabled roof");
        }

        public static Mesh Windows(float width,float length,float height)
        {
            var mesh=new MeshDraft();
            int floors=Mathf.Clamp(Mathf.FloorToInt(height/3),1,10);
            for(int side=0;side<4;side++)
            {
                float span=side%2==0?width:length;
                int columns=Mathf.Clamp(Mathf.FloorToInt(span/3.5f),1,16);
                Quaternion rotation=Quaternion.Euler(0,side*90,0);
                float depth=(side%2==0?length:width)*0.5f+0.035f;
                for(int row=0;row<floors;row++) for(int col=0;col<columns;col++)
                {
                    float x=-span*0.5f+span*(col+0.5f)/columns,y=Mathf.Min(height-0.9f,1.2f+row*3);
                    mesh.Quad(rotation*new Vector3(x-0.5f,y,-depth),rotation*new Vector3(x-0.5f,y+1,-depth),
                        rotation*new Vector3(x+0.5f,y+1,-depth),rotation*new Vector3(x+0.5f,y,-depth));
                }
            }
            return mesh.Mesh("Windows");
        }
    }
}
