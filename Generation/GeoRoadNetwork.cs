using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GeoWorld.Editor
{
    /// <summary>
    /// A planar road arrangement, partitioned into non-overlapping material regions.
    /// Every connected surface is clipped to the SAME terrain triangles. Junction
    /// boundaries therefore share heights even when the roads have different widths,
    /// directions or sampling intervals. No stacked intersection decals are used.
    /// </summary>
    public sealed class GeoRoadNetwork
    {
        public const float SurfaceOffset = 0.10f;
        const double Epsilon = 1e-9;
        const double MinArea = 1e-7;
        public sealed class Junction
        {
            public Vector2 center;
            public float radius;
            public int layer;
            public readonly HashSet<PathBuild> paths = new HashSet<PathBuild>();
            public readonly MeshDraft[] surfaces = { new MeshDraft(), new MeshDraft(), new MeshDraft(), new MeshDraft() };
            // the road each vertex of a surface belongs to: a piece of a junction is a piece of one road's surface
            public readonly List<PathBuild>[] owners = { new List<PathBuild>(), new List<PathBuild>(), new List<PathBuild>(), new List<PathBuild>() };
        }
        public readonly List<Junction> junctions = new List<Junction>();
        public readonly Dictionary<PathBuild, MeshDraft> roads = new Dictionary<PathBuild, MeshDraft>();
        public int snappedEndpoints { get; private set; }
        public int triangleCount { get; private set; }
        readonly List<PathBuild> paths;
        readonly HeightField field;
        readonly Dictionary<Cell, List<Patch>> cells = new Dictionary<Cell, List<Patch>>();

        public GeoRoadNetwork(List<PathBuild> paths, HeightField field)
        { this.paths = paths; this.field = field; }

        static bool IsRoad(PathBuild p) => !p.river && !p.Elevated && !p.feature.tunnel;
        static Vector2 XZ(Vector3 p) => new Vector2(p.x, p.z);
        static int Priority(PathBuild p)
        {
            switch (GeoData.Surface(p.feature.material))
            {
                case GroundSurface.Asphalt: return 4;
                case GroundSurface.Paving: return 3;
                case GroundSurface.Gravel: return 2;
                default: return 1;
            }
        }
        public IEnumerable<PathBuild> OrderedRoads() => paths.Where(IsRoad)
            .OrderByDescending(Priority).ThenByDescending(p => p.feature.width)
            .ThenBy(p => p.feature.id, StringComparer.Ordinal);

        // Small endpoint inaccuracies in source data should not leave a hairline gap.
        // Only centreline-near endpoints are snapped, never bridges or other layers.
        public void Connect(float tolerance, float pathStep)
        {
            var sorted = OrderedRoads().ToList();
            foreach (var path in sorted)
            {
                bool changed = false;
                foreach (int endpoint in new[] { 0, path.controls.Count - 1 })
                {
                    Vector2 point = XZ(path.controls[endpoint]), nearest = point;
                    float best = tolerance;
                    foreach (var other in sorted)
                    {
                        if (other == path || other.feature.layer != path.feature.layer) continue;
                        for (int i = 1; i < other.controls.Count; i++)
                        {
                            Vector2 a = XZ(other.controls[i - 1]), b = XZ(other.controls[i]);
                            float distance = GeoData.SegmentDistance(point, a, b, out float t);
                            if (distance < best) { best = distance; nearest = Vector2.Lerp(a, b, t); }
                        }
                    }
                    if ((point - nearest).sqrMagnitude > 0.000001f)
                    {
                        path.controls[endpoint] = new Vector3(nearest.x, field.Sample(nearest.x, nearest.y), nearest.y);
                        snappedEndpoints++; changed = true;
                    }
                }
                if (changed)
                {
                    path.samples = GeoData.Resample(path.controls, pathStep);
                    for (int i = 0; i < path.samples.Count; i++)
                    { var p = path.samples[i]; p.y = field.Sample(p.x, p.z); path.samples[i] = p; }
                }
            }
            DetectJunctions(sorted);
        }

        void DetectJunctions(List<PathBuild> sorted)
        {
            for (int a = 0; a < sorted.Count; a++) for (int b = a + 1; b < sorted.Count; b++)
            {
                var first = sorted[a]; var second = sorted[b];
                if (first.feature.layer != second.feature.layer) continue;
                for (int i = 1; i < first.controls.Count; i++) for (int j = 1; j < second.controls.Count; j++)
                {
                    Vector2 p = XZ(first.controls[i - 1]), q = XZ(first.controls[i]);
                    Vector2 r = XZ(second.controls[j - 1]), s = XZ(second.controls[j]);
                    if (IntersectSegments(p, q, r, s, out var point)) AddJunction(point, first, second);
                }
                // A T-road may stop at the curb rather than at the through road's centre.
                DetectCurbEndpoint(first, second); DetectCurbEndpoint(second, first);
            }
        }

        void DetectCurbEndpoint(PathBuild branch, PathBuild through)
        {
            foreach (int end in new[] { 0, branch.controls.Count - 1 })
            {
                Vector2 p = XZ(branch.controls[end]);
                Vector2 direction = (p - XZ(branch.controls[end == 0 ? 1 : end - 1])).normalized;
                for (int i = 1; i < through.controls.Count; i++)
                {
                    Vector2 a = XZ(through.controls[i - 1]), b = XZ(through.controls[i]);
                    if (Mathf.Abs(Vector2.Dot(direction, (b - a).normalized)) > 0.97f) continue;
                    float d = GeoData.SegmentDistance(p, a, b, out float t);
                    if (d <= through.feature.width * 0.5f + 0.05f)
                        AddJunction(Vector2.Lerp(a, b, t), branch, through);
                }
            }
        }

        void AddJunction(Vector2 point, PathBuild a, PathBuild b)
        {
            float radius = Mathf.Max(a.feature.width, b.feature.width) * 0.85f + 1;
            var junction = junctions.FirstOrDefault(j => j.layer == a.feature.layer && (j.center - point).sqrMagnitude < 1);
            if (junction == null)
            { junction = new Junction { center = point, radius = radius, layer = a.feature.layer }; junctions.Add(junction); }
            junction.radius = Mathf.Max(radius, junction.radius);
            junction.paths.Add(a); junction.paths.Add(b);
        }

        public static bool IntersectSegments(Vector2 a, Vector2 b, Vector2 c, Vector2 d, out Vector2 point)
        {
            point = default;
            var p = new Point(a); var r = new Point(b) - p; var q = new Point(c); var s = new Point(d) - q;
            double cross = Cross(r, s);
            if (Math.Abs(cross) < Epsilon) return false;
            double t = Cross(q - p, s) / cross, u = Cross(q - p, r) / cross;
            if (t < -Epsilon || t > 1 + Epsilon || u < -Epsilon || u > 1 + Epsilon) return false;
            point = (p + r * Math.Max(0, Math.Min(1, t))).Vector;
            return true;
        }

        public void GradeJunctions(float shoulder)
        {
            int n = field.resolution;
            var sums = new float[n, n]; var weights = new float[n, n]; var maximum = new float[n, n];
            foreach (var junction in junctions)
            {
                float inner = junction.radius * 0.6f;
                float outer = junction.radius + Mathf.Max(shoulder, 1);
                float target = field.Sample(junction.center.x, junction.center.y);
                var p = new Vector3(junction.center.x, target, junction.center.y);
                field.RasterSegment(p, p, outer, (x, z, distance, t) => {
                    float w = 1 - Mathf.SmoothStep(0, 1, Mathf.InverseLerp(inner, outer, distance));
                    sums[z, x] += target * w; weights[z, x] += w; maximum[z, x] = Mathf.Max(maximum[z, x], w);
                });
            }
            for (int z = 0; z < n; z++) for (int x = 0; x < n; x++)
                if (weights[z, x] > 0) field.heights[z, x] = Mathf.Lerp(field.heights[z, x], sums[z, x] / weights[z, x], maximum[z, x]);
        }

        public void Build(Action<float> progress = null)
        {
            cells.Clear(); roads.Clear(); triangleCount = 0;
            foreach (var junction in junctions)
            {
                foreach (var draft in junction.surfaces) { draft.vertices.Clear(); draft.uv.Clear(); draft.uv2.Clear(); draft.triangles.Clear(); }
                foreach (var list in junction.owners) list.Clear();
            }
            foreach (var path in OrderedRoads())
            {
                roads[path] = new MeshDraft();
                float half = path.feature.width * 0.5f;
                for (int i = 1; i < path.samples.Count; i++)
                {
                    Vector3 a = path.samples[i - 1], b = path.samples[i];
                    Vector3 sa = GeoMeshes.Side(path.samples, i - 1) * half, sb = GeoMeshes.Side(path.samples, i) * half;
                    // The same two footprint triangles as the ribbon, resolved before any heights are assigned.
                    Index(new Patch(path, new Point(a - sa), new Point(a + sa), new Point(b - sb)));
                    Index(new Patch(path, new Point(a + sa), new Point(b + sb), new Point(b - sb)));
                }
            }
            int done = 0;
            foreach (var pair in cells)
            {
                if (done++ % 256 == 0) progress?.Invoke(done / (float)Mathf.Max(1, cells.Count));
                Cell cell = pair.Key;
                Point a = new Point(field.X(cell.x), field.Z(cell.z));
                Point b = new Point(field.X(cell.x + 1), field.Z(cell.z));
                Point c = new Point(field.X(cell.x + 1), field.Z(cell.z + 1));
                Point d = new Point(field.X(cell.x), field.Z(cell.z + 1));
                float ha = field.heights[cell.z, cell.x], hb = field.heights[cell.z, cell.x + 1];
                float hc = field.heights[cell.z + 1, cell.x + 1], hd = field.heights[cell.z + 1, cell.x];
                Vector2 middle = ((a + c) * 0.5).Vector;
                Junction junction = junctions.FirstOrDefault(j => j.layer == cell.layer && (j.center - middle).sqrMagnitude <= j.radius * j.radius);
                // Use the upper of the two possible terrain diagonals. This is continuous
                // at every cell edge and cannot sink below either terrain triangulation.
                if (ha + hc >= hb + hd)
                {
                    Partition(new HeightTriangle(a,b,c,ha,hb,hc), pair.Value, junction);
                    Partition(new HeightTriangle(a,c,d,ha,hc,hd), pair.Value, junction);
                }
                else
                {
                    Partition(new HeightTriangle(a,b,d,ha,hb,hd), pair.Value, junction);
                    Partition(new HeightTriangle(b,c,d,hb,hc,hd), pair.Value, junction);
                }
            }
        }

        void Index(Patch patch)
        {
            if (Area(patch.polygon) <= MinArea) return;
            int last = field.resolution - 2;
            int X(double value) => Mathf.FloorToInt((float)((value - field.bounds.minX) / field.bounds.Width * (field.resolution - 1)));
            int Z(double value) => Mathf.FloorToInt((float)((value - field.bounds.minZ) / field.bounds.Depth * (field.resolution - 1)));
            int x0 = X(patch.minX), x1 = X(patch.maxX), z0 = Z(patch.minZ), z1 = Z(patch.maxZ);
            if (x1 < 0 || z1 < 0 || x0 > last || z0 > last) return;
            for (int z = Mathf.Max(0,z0); z <= Mathf.Min(last,z1); z++) for (int x = Mathf.Max(0,x0); x <= Mathf.Min(last,x1); x++)
            {
                var key = new Cell(x, z, patch.path.feature.layer);
                if (!cells.TryGetValue(key, out var list)) { list = new List<Patch>(); cells.Add(key, list); }
                list.Add(patch);
            }
        }

        void Partition(HeightTriangle triangle, List<Patch> patches, Junction junction)
        {
            var remaining = new List<List<Point>> { triangle.polygon };
            foreach (var patch in patches)
            {
                var next = new List<List<Point>>();
                foreach (var polygon in remaining)
                {
                    var intersection = Intersection(polygon, patch.polygon);
                    if (Area(intersection) <= MinArea) { next.Add(polygon); continue; }
                    int surface = (int)GeoData.Surface(patch.path.feature.material);
                    var draft = junction == null ? roads[patch.path] : junction.surfaces[surface];
                    Emit(draft, intersection, triangle, junction == null ? null : junction.owners[surface], patch.path);
                    Subtract(polygon, patch.polygon, next);
                }
                remaining = next;
                if (remaining.Count == 0) break;
            }
        }

        void Emit(MeshDraft draft, List<Point> polygon, HeightTriangle triangle, List<PathBuild> owners = null, PathBuild owner = null)
        {
            int start = draft.vertices.Count;
            foreach (var p in polygon)
            {
                owners?.Add(owner);
                draft.vertices.Add(new Vector3((float)p.x, triangle.Height(p) + SurfaceOffset, (float)p.z));
                // Shared world UVs keep road and junction textures aligned at every seam.
                draft.uv.Add(new Vector2((float)p.x / 4, (float)p.z / 4));
            }
            for (int i = 1; i < polygon.Count - 1; i++)
            {
                if (Math.Abs(Cross(polygon[i] - polygon[0], polygon[i+1] - polygon[0])) <= MinArea) continue;
                // Conversion from double clipping coordinates to Unity floats can collapse
                // sub-millimetre slivers far from the origin; never emit a flipped/zero face.
                if(Vector3.Cross(draft.vertices[start+i+1]-draft.vertices[start],draft.vertices[start+i]-draft.vertices[start]).y<=0)continue;
                draft.triangles.Add(start); draft.triangles.Add(start + i + 1); draft.triangles.Add(start + i);
                if (++triangleCount > 2000000) throw new InvalidOperationException("Road mesh exceeds two million triangles. Use a smaller export or lower terrain resolution.");
            }
        }

        public bool HidesMarking(PathBuild path, Vector3 point)
        {
            Vector2 p = XZ(point);
            return junctions.Any(j => j.layer == path.feature.layer && j.paths.Contains(path) &&
                (j.center-p).sqrMagnitude <= Mathf.Pow(j.radius + 1.5f,2));
        }

        public bool Owns(PathBuild path,Vector3 point)
        {
            int x=Mathf.FloorToInt((point.x-field.bounds.minX)/field.bounds.Width*(field.resolution-1));
            int z=Mathf.FloorToInt((point.z-field.bounds.minZ)/field.bounds.Depth*(field.resolution-1));
            if(!cells.TryGetValue(new Cell(x,z,path.feature.layer),out var patches))return false;
            var p=new Point(point);
            foreach(var patch in patches)
            {
                var t=patch.polygon;
                if(Cross(t[1]-t[0],p-t[0])>=-Epsilon&&Cross(t[2]-t[1],p-t[1])>=-Epsilon&&Cross(t[0]-t[2],p-t[2])>=-Epsilon)
                    return patch.path==path;
            }
            return false;
        }

        public Vector3 Normal(Vector3 p)
        {
            float dx=field.bounds.Width/(field.resolution-1),dz=field.bounds.Depth/(field.resolution-1);
            float sx=(field.Sample(p.x+dx,p.z)-field.Sample(p.x-dx,p.z))/(2*dx);
            float sz=(field.Sample(p.x,p.z+dz)-field.Sample(p.x,p.z-dz))/(2*dz);
            return new Vector3(-sx,1,-sz).normalized;
        }

        // Both roads and markings evaluate the same piecewise planar surface.
        public float Height(float x, float z)
        {
            float gx = Mathf.Clamp((x-field.bounds.minX)/field.bounds.Width*(field.resolution-1),0,field.resolution-1);
            float gz = Mathf.Clamp((z-field.bounds.minZ)/field.bounds.Depth*(field.resolution-1),0,field.resolution-1);
            int ix = Mathf.Min((int)gx,field.resolution-2), iz = Mathf.Min((int)gz,field.resolution-2);
            float u=gx-ix,v=gz-iz;
            float a=field.heights[iz,ix],b=field.heights[iz,ix+1],c=field.heights[iz+1,ix+1],d=field.heights[iz+1,ix];
            if(a+c>=b+d) return u>=v?a+(b-a)*u+(c-b)*v:a+(c-d)*u+(d-a)*v;
            return u+v<=1?a+(b-a)*u+(d-a)*v:c+(d-c)*(1-u)+(b-c)*(1-v);
        }

        public Mesh JunctionMesh(Junction junction, out GroundSurface[] surfaces)
        {
            var vertices=new List<Vector3>(); var uv=new List<Vector2>(); var uv2=new List<Vector2>(); var submeshes=new List<int[]>();
            var used=new List<GroundSurface>(); bool edges=true;
            for(int i=0;i<4;i++)
            {
                var draft=junction.surfaces[i]; if(draft.triangles.Count==0)continue;
                int offset=vertices.Count;vertices.AddRange(draft.vertices);uv.AddRange(draft.uv);
                // the road edges (GeoMeshes.JunctionEdges), when every surface has them
                if(draft.uv2.Count==draft.vertices.Count)uv2.AddRange(draft.uv2);else edges=false;
                submeshes.Add(draft.triangles.Select(index=>index+offset).ToArray());used.Add((GroundSurface)i);
            }
            var mesh=new Mesh {name="Connected junction",indexFormat=vertices.Count>65535?UnityEngine.Rendering.IndexFormat.UInt32:UnityEngine.Rendering.IndexFormat.UInt16};
            mesh.SetVertices(vertices);mesh.SetUVs(0,uv);if(edges&&uv2.Count==vertices.Count)mesh.SetUVs(1,uv2);mesh.subMeshCount=submeshes.Count;
            for(int i=0;i<submeshes.Count;i++)mesh.SetTriangles(submeshes[i],i);
            mesh.SetNormals(vertices.Select(Normal).ToList());mesh.RecalculateBounds();surfaces=used.ToArray();return mesh;
        }

        readonly struct Cell : IEquatable<Cell>
        {
            public readonly int x,z,layer;
            public Cell(int x,int z,int layer){this.x=x;this.z=z;this.layer=layer;}
            public bool Equals(Cell other)=>x==other.x&&z==other.z&&layer==other.layer;
            public override bool Equals(object obj)=>obj is Cell other&&Equals(other);
            public override int GetHashCode()=>unchecked(x*73856093^z*19349663^layer*83492791);
        }
        readonly struct Point
        {
            public readonly double x,z;
            public Point(double x,double z){this.x=x;this.z=z;}
            public Point(Vector2 p):this(p.x,p.y){}
            public Point(Vector3 p):this(p.x,p.z){}
            public Vector2 Vector=>new Vector2((float)x,(float)z);
            public static Point operator +(Point a,Point b)=>new Point(a.x+b.x,a.z+b.z);
            public static Point operator -(Point a,Point b)=>new Point(a.x-b.x,a.z-b.z);
            public static Point operator *(Point a,double b)=>new Point(a.x*b,a.z*b);
        }
        sealed class Patch
        {
            public readonly PathBuild path;
            public readonly List<Point> polygon;
            public readonly double minX,maxX,minZ,maxZ;
            public Patch(PathBuild path,Point a,Point b,Point c)
            {
                this.path=path;polygon=Cross(b-a,c-a)>0?new List<Point>{a,b,c}:new List<Point>{a,c,b};
                minX=Math.Min(a.x,Math.Min(b.x,c.x));maxX=Math.Max(a.x,Math.Max(b.x,c.x));
                minZ=Math.Min(a.z,Math.Min(b.z,c.z));maxZ=Math.Max(a.z,Math.Max(b.z,c.z));
            }
        }
        readonly struct HeightTriangle
        {
            public readonly List<Point> polygon;
            readonly Point a,b,c;readonly float ha,hb,hc;
            public HeightTriangle(Point a,Point b,Point c,float ha,float hb,float hc)
            {this.a=a;this.b=b;this.c=c;this.ha=ha;this.hb=hb;this.hc=hc;polygon=new List<Point>{a,b,c};}
            public float Height(Point p)
            {double divisor=Cross(b-a,c-a);return (float)(ha+(hb-ha)*Cross(p-a,c-a)/divisor+(hc-ha)*Cross(b-a,p-a)/divisor);}
        }
        static double Cross(Point a,Point b)=>a.x*b.z-a.z*b.x;
        static double Area(List<Point> polygon)
        {
            if(polygon.Count<3)return 0;
            double sum=0;for(int i=1;i<polygon.Count-1;i++)sum+=Cross(polygon[i]-polygon[0],polygon[i+1]-polygon[0]);
            return Math.Abs(sum)*0.5;
        }
        static List<Point> Clip(List<Point> polygon,Point a,Point b,bool inside)
        {
            var result=new List<Point>();if(polygon.Count==0)return result;
            Point previous=polygon[polygon.Count-1];double previousDistance=Cross(b-a,previous-a)*(inside?1:-1);
            // Use one zero boundary for inside and outside so subtraction never adds an overlapping tolerance strip.
            foreach(Point current in polygon)
            {
                double distance=Cross(b-a,current-a)*(inside?1:-1);
                bool previousInside=previousDistance>=0,currentInside=distance>=0;
                if(previousInside!=currentInside)
                {
                    double t=previousDistance/(previousDistance-distance);
                    AddDistinct(result,previous+(current-previous)*t);
                }
                if(currentInside)AddDistinct(result,current);
                previous=current;previousDistance=distance;
            }
            if(result.Count>1&&DistanceSquared(result[0],result[result.Count-1])<1e-18)result.RemoveAt(result.Count-1);
            return result;
        }
        static void AddDistinct(List<Point> list,Point point)
        {if(list.Count==0||DistanceSquared(list[list.Count-1],point)>1e-18)list.Add(point);}
        static double DistanceSquared(Point a,Point b){var d=a-b;return d.x*d.x+d.z*d.z;}
        static List<Point> Intersection(List<Point> subject,List<Point> clip)
        {var result=subject;for(int i=0;i<clip.Count&&result.Count>=3;i++)result=Clip(result,clip[i],clip[(i+1)%clip.Count],true);return result;}
        static void Subtract(List<Point> subject,List<Point> clip,List<List<Point>> output)
        {
            var remaining=subject;
            for(int i=0;i<clip.Count&&remaining.Count>=3;i++)
            {
                var outside=Clip(remaining,clip[i],clip[(i+1)%clip.Count],false);
                if(Area(outside)>MinArea)output.Add(outside);
                remaining=Clip(remaining,clip[i],clip[(i+1)%clip.Count],true);
            }
        }
    }
}
