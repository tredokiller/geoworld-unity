using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GeoWorld.Editor
{
    public class PropPlacement
    {
        public string id, kind, source, evidence, rotationSource;
        public GeoPhotoReference photoReference;
        public Vector3 position;
        public float yaw, height, width, length;
        public bool segment;
    }

    // PNG rows are north to south; Unity GetPixels32 starts at the bottom of the image.
    public class CanopyMask
    {
        readonly MaterialMapInfo info;
        readonly Color32[] pixels;
        public CanopyMask(MaterialMapInfo info, Color32[] pixels) { this.info=info; this.pixels=pixels; }
        public bool Contains(float x,float z)
        {
            int col=Mathf.FloorToInt((x-info.minX)/info.cellSize), row=Mathf.FloorToInt((info.maxZ-z)/info.cellSize);
            return col>=0 && row>=0 && col<info.width && row<info.height && pixels[(info.height-1-row)*info.width+col].r>=128;
        }
        public static CanopyMask Load(EnvironmentData env,string path,List<string> notes)
        {
            var m=env.materials;
            if(m==null || string.IsNullOrEmpty(m.canopyFile) || string.IsNullOrEmpty(path)) return null;
            Texture2D texture=null;
            try
            {
                if(m.width<=0 || m.height<=0 || (long)m.width*m.height>16000000 || !GeoData.Finite(m.cellSize) || m.cellSize<=0 ||
                    !GeoData.Finite(m.minX) || !GeoData.Finite(m.maxZ)) throw new InvalidDataException("Invalid canopy grid.");
                texture=new Texture2D(2,2,TextureFormat.RGBA32,false,true);
                if(!texture.LoadImage(File.ReadAllBytes(GeoData.PackageFile(path,m.canopyFile))) || texture.width!=m.width || texture.height!=m.height)
                    throw new InvalidDataException("Canopy image dimensions do not match the export.");
                return new CanopyMask(m,texture.GetPixels32());
            }
            catch(Exception e) when(e is IOException || e is ArgumentException)
            { notes.Add("Canopy vegetation skipped: "+e.Message); return null; }
            finally { if(texture) Object.DestroyImmediate(texture); }
        }
    }

    public static class GeoPropPlanner
    {
        // Geometry is indexed by 20 m cells so large exports do not scan every road for every tree.
        public class Clearance
        {
            class Obstacle { public Rect bounds; public Func<Vector2,float,bool> intersects; }
            readonly Dictionary<Vector2Int,List<Obstacle>> cells=new Dictionary<Vector2Int,List<Obstacle>>();
            void Add(Rect bounds,Func<Vector2,float,bool> test)
            {
                var o=new Obstacle{bounds=bounds,intersects=test};
                for(int x=Mathf.FloorToInt(bounds.xMin/20);x<=Mathf.FloorToInt(bounds.xMax/20);x++)
                    for(int z=Mathf.FloorToInt(bounds.yMin/20);z<=Mathf.FloorToInt(bounds.yMax/20);z++)
                    {var key=new Vector2Int(x,z);if(!cells.TryGetValue(key,out var list))cells[key]=list=new List<Obstacle>();list.Add(o);}
            }
            public void Circle(Vector2 p,float radius)
            {Add(new Rect(p.x-radius,p.y-radius,2*radius,2*radius),(q,r)=>(p-q).sqrMagnitude<(radius+r)*(radius+r));}
            public void Segment(Vector2 a,Vector2 b,float width)
            {float r=width/2;Add(Rect.MinMaxRect(Mathf.Min(a.x,b.x)-r,Mathf.Min(a.y,b.y)-r,Mathf.Max(a.x,b.x)+r,Mathf.Max(a.y,b.y)+r),
                (p,padding)=>GeoData.SegmentDistance(p,a,b,out _)<r+padding);}
            public void Polygon(float[][] rings)
            {
                if(rings==null || rings.Length==0)return;
                var pts=GeoData.Points(rings[0]);if(pts.Count<3)return;
                Add(Rect.MinMaxRect(pts.Min(p=>p.x),pts.Min(p=>p.z),pts.Max(p=>p.x),pts.Max(p=>p.z)),(p,r)=>GeoData.InPolygon(p.x,p.y,rings));
                foreach(var ring in rings)
                {var points=GeoData.Points(ring);for(int i=0;i<points.Count;i++)Segment(XZ(points[i]),XZ(points[(i+1)%points.Count]),0);}
            }
            public bool Free(Vector2 p,float radius)
            {
                var visited=new HashSet<Obstacle>();
                for(int x=Mathf.FloorToInt((p.x-radius)/20);x<=Mathf.FloorToInt((p.x+radius)/20);x++)
                    for(int z=Mathf.FloorToInt((p.y-radius)/20);z<=Mathf.FloorToInt((p.y+radius)/20);z++)
                        if(cells.TryGetValue(new Vector2Int(x,z),out var list))
                            foreach(var o in list)if(visited.Add(o)&&o.intersects(p,radius))return false;
                return true;
            }
        }

        public static Vector2 XZ(Vector3 p)=>new Vector2(p.x,p.z);
        public static float Sample(string id,int seed,int channel=0)
        {
            unchecked {uint h=2166136261;foreach(char c in id??""){h^=c;h*=16777619;}h^=(uint)seed;h*=16777619;h^=(uint)channel*374761393u;
                h^=h>>13;h*=1274126177;h^=h>>16;return (h&0xffffff)/16777216f;}
        }
        static float Positive(float value,float fallback,float max=60)=>GeoData.Finite(value)&&value>0?Mathf.Min(value,max):fallback;
        static bool Valid(float x,float z,GeoBounds b)=>GeoData.Finite(x)&&GeoData.Finite(z)&&b.Contains(x,z);
        static float Yaw(Vector3 delta)=>Mathf.Atan2(delta.x,delta.z)*Mathf.Rad2Deg;
        static IEnumerable<T> Sorted<T>(IEnumerable<T> source,Func<T,string> id)=>source.OrderBy(id,StringComparer.Ordinal);

        public const int MaxObjects=20000,MaxBaked=200000;
        const float NearCell=60;

        // baked: the props become part of chunks of mesh (a merged city), so there can be far more of them,
        // and fences and walls are made of longer pieces.
        public static List<PropPlacement> Plan(EnvironmentData env,BuildingsData buildings,GenerationSettings settings,
            CanopyMask canopy,List<string> notes,IEnumerable<GeoPlacedProp> retained=null,bool baked=false)
        {
            var result=new List<PropPlacement>();var clear=new Clearance();var ids=new HashSet<string>();
            int limit=Mathf.Clamp(settings.maxProps,100,baked?MaxBaked:MaxObjects);bool capped=false;
            float pieceLength=baked?8f:2f;
            float spacing=Positive(settings.treeSpacing,8,25);spacing=Mathf.Max(3,spacing);
            float density=GeoData.Finite(settings.vegetationDensity)?Mathf.Clamp01(settings.vegetationDensity):0.7f;
            var bounds=env.bounds;var around=env.surround??bounds;var outer=env.outer??bounds;
            foreach(var r in env.roads.Concat(env.railways).Concat(env.waterways))
            {
                if(r==null || r.tunnel)continue;
                var pts=GeoData.Points(r.points);for(int i=1;i<pts.Count;i++)clear.Segment(XZ(pts[i-1]),XZ(pts[i]),Positive(r.width,3)/1f+1);
            }
            foreach(var w in env.water)if(w!=null)clear.Polygon(w.rings);
            // 60 m cells with a building in them: trees near buildings are the ones kept first when the limit is tight
            var built=new HashSet<Vector2Int>();
            foreach(var b in buildings.buildings)
            {
                if(b==null || !Valid(b.x,b.z,around))continue;
                built.Add(new Vector2Int(Mathf.FloorToInt(b.x/NearCell),Mathf.FloorToInt(b.z/NearCell)));
                var pts=GeoData.Points(b.footprint);
                if(pts.Count<3)
                {var q=Quaternion.Euler(0,GeoData.Finite(b.rotation)?b.rotation:0,0);float w=Positive(b.width,10)/2,l=Positive(b.length,10,500)/2;
                    pts=new[]{new Vector3(-w,0,-l),new Vector3(w,0,-l),new Vector3(w,0,l),new Vector3(-w,0,l)}.Select(p=>q*p).ToList();}
                clear.Polygon(new[]{pts.SelectMany(p=>new[]{p.x+b.x,p.z+b.z}).ToArray()});
                // Unknown entrances: reserve a small strip around every facade as an access margin.
                for(int i=0;i<pts.Count;i++)clear.Segment(XZ(pts[i])+new Vector2(b.x,b.z),XZ(pts[(i+1)%pts.Count])+new Vector2(b.x,b.z),2);
            }
            foreach(var p in env.props.Where(p=>p!=null && Valid(p.x,p.z,around)))
                clear.Circle(new Vector2(p.x,p.z),p.type=="entrance"||p.type=="gate"?2:Radius(p.type,settings));
            foreach(var t in env.trees.Where(t=>t!=null && Valid(t.x,t.z,around)))
                clear.Circle(new Vector2(t.x,t.z),Mathf.Max(1.5f,Positive(t.crown,4)/2));
            foreach(var b in env.barriers.Concat(env.hedges).Where(b=>b!=null))
            {var pts=GeoData.Points(b.points);for(int i=1;i<pts.Count;i++)clear.Segment(XZ(pts[i-1]),XZ(pts[i]),Positive(b.width,1));}
            foreach(var p in retained??Enumerable.Empty<GeoPlacedProp>())
            {ids.Add(p.sourceId??"");clear.Circle(XZ(p.transform.position),Radius(p.kind,settings));}

            bool Add(PropPlacement p)
            {
                if(!Valid(p.position.x,p.position.z,bounds)||ids.Contains(p.id??""))return false;
                if(result.Count>=limit){capped=true;return false;}
                ids.Add(p.id??"");result.Add(p);return true;
            }
            if(settings.mappedProps)
                foreach(var p in Sorted(env.props.Where(p=>p!=null),p=>p.id))
                {
                    if(p.type=="entrance"||!Valid(p.x,p.z,bounds))continue;
                    var pos=new Vector3(p.x,0,p.z);
                    bool tagged=(p.rotationSource=="tag"||p.rotationSource=="manual")&&GeoData.Finite(p.rotation);
                    bool photo=p.source=="photo_reference"||p.placementSource=="manual_photo";
                    Add(new PropPlacement{id=p.id,kind=p.type,source=string.IsNullOrEmpty(p.placementSource)?(photo?"manual_photo":"mapped"):p.placementSource,
                        evidence=string.IsNullOrEmpty(p.evidence)?(photo?p.observationId:p.id):p.evidence,position=pos,
                        photoReference=photo?new GeoPhotoReference{source=p.source,photoId=p.photoId,observationId=p.observationId,sourceUrl=p.sourceUrl,
                            capturedAt=p.capturedAt,confidence=p.confidence,description=p.description,evidence=p.evidence,attribution=p.attribution,license=p.license}:null,
                        yaw=tagged?p.rotation:FacingRoad(pos,env.roads),rotationSource=tagged?p.rotationSource:"inferred",
                        height=p.height,width=p.width});
                }
            if(settings.trees)
                foreach(var t in Sorted(env.trees.Where(t=>t!=null),t=>t.id))
                    Add(new PropPlacement{id=t.id,kind="tree",source="mapped",evidence=t.id,rotationSource="generated",
                        position=new Vector3(t.x,0,t.z),height=Positive(t.height,7),width=Positive(t.crown,4),yaw=Sample(t.id,settings.decorationSeed)*360});
            if(settings.streetDecoration)
            {
                var litTypes=new HashSet<string>{"residential","living_street","unclassified","primary","secondary","tertiary","service"};
                var nearby=new Clearance();
                foreach(var p in env.props.Where(p=>p!=null&&(p.type=="street_lamp"||p.type=="bench")&&Valid(p.x,p.z,bounds)))
                    nearby.Circle(new Vector2(p.x,p.z),20);
                var parks=env.areas.Where(a=>a!=null&&(a.tag=="leisure=park"||a.tag=="leisure=garden")).ToArray();
                foreach(var road in Sorted(env.roads.Where(r=>r!=null&&!r.bridge&&!r.tunnel&&r.layer==0),r=>r.id))
                {
                    bool lamp=road.type!=null&&litTypes.Contains(road.type)&&road.lit!="no";
                    bool bench=(road.kind=="path"||road.type=="footway"||road.type=="pedestrian")&&parks.Length>0;
                    if(!lamp&&!bench)continue;
                    var pts=GeoData.Points(road.points);float total=0,next=15,interval=lamp?35:60;int index=0;
                    for(int i=1;i<pts.Count && result.Count<limit;i++)
                    {
                        var a=pts[i-1];var b=pts[i];float length=Vector3.Distance(a,b);if(length<0.01f)continue;
                        var tangent=(b-a)/length;var side=new Vector3(-tangent.z,0,tangent.x);
                        while(next<total+length && result.Count<limit)
                        {
                            var center=Vector3.Lerp(a,b,(next-total)/length);next+=interval;
                            string id=road.id+"/decoration/"+index++;string kind=lamp?"street_lamp":"bench";
                            if(lamp&&!buildings.buildings.Any(v=>v!=null&&(new Vector2(v.x,v.z)-XZ(center)).sqrMagnitude<2500))continue;
                            // Alternate sides to avoid repetitive mirrored rows. Do not invent sidewalks.
                            float sign=index%2==0?1:-1;float offset=Positive(road.width,3)/2+(lamp?2.5f:1.8f);
                            var pos=center+side*offset*sign;float radius=Radius(kind,settings);
                            if(!Valid(pos.x,pos.z,bounds)||!clear.Free(XZ(pos),radius)||!nearby.Free(XZ(pos),radius))continue;
                            if(bench&&!parks.Any(p=>GeoData.InPolygon(pos.x,pos.z,p.rings)))continue;
                            if(Add(new PropPlacement{id=id,kind=kind,source="generated",evidence=road.id,rotationSource="inferred",position=pos,yaw=Yaw(center-pos)}))
                            {clear.Circle(XZ(pos),radius);nearby.Circle(XZ(pos),20);}
                        }
                        total+=length;
                    }
                }
            }

            void Segments(BarrierFeature barrier,string kind)
            {
                var points=GeoData.Points(barrier.points);int index=0;
                for(int i=1;i<points.Count;i++)
                {
                    Vector3 a=points[i-1],b=points[i];float distance=Vector3.Distance(a,b);if(distance<0.01f)continue;
                    var gaps=new List<Vector2>();
                    foreach(var gate in env.props.Where(p=>p!=null&&p.type=="gate"&&Valid(p.x,p.z,bounds)))
                        if(GeoData.SegmentDistance(new Vector2(gate.x,gate.z),XZ(a),XZ(b),out float t)<1.5f)
                        {float half=Positive(gate.width,3)/2;gaps.Add(new Vector2(Mathf.Max(0,t*distance-half),Mathf.Min(distance,t*distance+half)));}
                    gaps.Sort((x,y)=>x.x.CompareTo(y.x));float start=0;
                    foreach(var gap in gaps){if(gap.x>start)Emit(start,gap.x);start=Mathf.Max(start,gap.y);}if(start<distance)Emit(start,distance);
                    void Emit(float lo,float hi)
                    {
                        int count=Mathf.CeilToInt((hi-lo)/pieceLength);
                        for(int k=0;k<count;k++)
                        {
                            if(result.Count>=limit){capped=true;return;}
                            float from=Mathf.Lerp(lo,hi,k/(float)count),to=Mathf.Lerp(lo,hi,(k+1)/(float)count);
                            Add(new PropPlacement{id=barrier.id+"/segment/"+index++,kind=kind,source="mapped",evidence=barrier.id,
                                position=Vector3.Lerp(a,b,(from+to)/2/distance),yaw=Yaw(b-a),rotationSource="line",segment=true,
                                height=Positive(barrier.height,kind=="hedge"?1.6f:kind=="guard_rail"?0.8f:1.7f),
                                width=Positive(barrier.width,kind=="hedge"?1:kind=="wall"?0.25f:0.12f),length=to-from});
                        }
                    }
                    if(result.Count>=limit)break;
                }
            }
            bool TreeFits(string id,Vector3 p)
            {
                float radius=Radius("tree",settings);
                return !(Sample(id,settings.decorationSeed)>density || !Valid(p.x,p.z,bounds) ||
                    p.x-radius<outer.minX || p.x+radius>outer.maxX || p.z-radius<outer.minZ || p.z+radius>outer.maxZ || !clear.Free(XZ(p),radius));
            }
            // keep < 1 thins the trees evenly (by a hash of their id) when the limit is smaller than what the canopy allows
            void Tree(string id,Vector3 p,string evidence,float keep=1)
            {
                if(!TreeFits(id,p))return;
                if(keep<1&&Sample(id,settings.decorationSeed,6)>keep)return;
                float radius=Radius("tree",settings);
                if(Add(new PropPlacement{id=id,kind="tree",source="generated",evidence=evidence,rotationSource="generated",position=p,
                    height=5+Sample(id,settings.decorationSeed,1)*5,width=3+Sample(id,settings.decorationSeed,2)*2,yaw=Sample(id,settings.decorationSeed,3)*360}))
                    clear.Circle(XZ(p),radius);
            }
            if(settings.vegetation && settings.trees)
            {
                foreach(var row in Sorted(env.treeRows.Where(r=>r!=null),r=>r.id))
                {
                    var pts=GeoData.Points(row.points);float total=0,next=spacing/2;int index=0;
                    for(int i=1;i<pts.Count;i++)
                    {float length=Vector3.Distance(pts[i-1],pts[i]);while(next<total+length && result.Count<limit)
                        {Tree(row.id+"/tree/"+index++,Vector3.Lerp(pts[i-1],pts[i],(next-total)/length),row.id);next+=spacing;}total+=length;}
                }
                if(canopy!=null)
                {
                    float step=Mathf.Max(spacing,Mathf.Sqrt(bounds.Width*bounds.Depth/1000000f));
                    if(step>spacing)notes.Add("Vegetation grid coarsened to bound the work for this large area.");
                    // The trees may use what is left of the limit after the fences and hedges that will follow.
                    int treeCap=limit-Mathf.Min(limit/4,Mathf.CeilToInt(LinearLength()/pieceLength));
                    Vector3 Spot(int ix,int iz,string id)=>new Vector3((ix+0.5f+(Sample(id,settings.decorationSeed,4)-0.5f)*0.6f)*step,0,
                        (iz+0.5f+(Sample(id,settings.decorationSeed,5)-0.5f)*0.6f)*step);
                    bool Near(Vector3 p)
                    {
                        int cx=Mathf.FloorToInt(p.x/NearCell),cz=Mathf.FloorToInt(p.z/NearCell);
                        for(int dx=-1;dx<=1;dx++)for(int dz=-1;dz<=1;dz++)if(built.Contains(new Vector2Int(cx+dx,cz+dz)))return true;
                        return false;
                    }
                    // every grid cell that reaches into the bounds; a tree belongs to the bounds its spot is in, so tiles
                    // side by side share one grid without a gap or a double row
                    int near=0,far=0;
                    for(int iz=Mathf.FloorToInt(bounds.minZ/step);iz<=Mathf.FloorToInt(bounds.maxZ/step);iz++)
                        for(int ix=Mathf.FloorToInt(bounds.minX/step);ix<=Mathf.FloorToInt(bounds.maxX/step);ix++)
                        {
                            string id="canopy/"+ix+"/"+iz;var p=Spot(ix,iz,id);
                            if(canopy.Contains(p.x,p.z)&&TreeFits(id,p)){if(Near(p))near++;else far++;}
                        }
                    // Too many trees for the limit: thin them by a hash of their id. Trees near buildings go first
                    // (that is where the picture is looked at), but the countryside keeps a fifth of the room.
                    int room=Mathf.Max(0,treeCap-result.Count);
                    float keepNear=1,keepFar=1;
                    if(near+far>room)
                    {
                        int farMin=Mathf.Min(far,Mathf.RoundToInt(room*0.2f));
                        keepNear=near<=room-farMin?1:(room-farMin)*0.97f/near;
                        int farRoom=room-Mathf.RoundToInt(near*keepNear);
                        keepFar=far<=farRoom?1:Mathf.Max(0,farRoom)*0.97f/far;
                        notes.Add("Vegetation thinned to fit the prop limit: about "+Mathf.RoundToInt(near*keepNear+far*keepFar)+" of "+(near+far)+
                            " possible trees; trees near buildings are kept first ("+Mathf.RoundToInt(keepNear*100)+"% of them, "+Mathf.RoundToInt(keepFar*100)+"% elsewhere).");
                    }
                    for(int iz=Mathf.FloorToInt(bounds.minZ/step);iz<=Mathf.FloorToInt(bounds.maxZ/step)&&result.Count<treeCap;iz++)
                        for(int ix=Mathf.FloorToInt(bounds.minX/step);ix<=Mathf.FloorToInt(bounds.maxX/step)&&result.Count<treeCap;ix++)
                        {
                            string id="canopy/"+ix+"/"+iz;var p=Spot(ix,iz,id);
                            if(canopy.Contains(p.x,p.z))Tree(id,p,"canopy.png",Near(p)?keepNear:keepFar);
                        }
                }
            }
            // fences, walls and hedges come last: a city is not left without trees because of its fences
            if(settings.mappedProps)
                foreach(var b in Sorted(env.barriers.Where(b=>b!=null),b=>b.id))
                    Segments(b,b.type=="wall"||b.type=="retaining_wall"||b.type=="city_wall"?"wall":b.type=="guard_rail"?"guard_rail":"fence");
            if(settings.vegetation)
                foreach(var b in Sorted(env.hedges.Where(b=>b!=null),b=>b.id))Segments(b,"hedge");
            if(capped || result.Count>=limit)notes.Add("Prop limit reached ("+limit+"). Increase Maximum props"+(baked?"":" or use a smaller area")+".");
            return result;

            float LinearLength()
            {
                float total=0;
                var lines=new List<BarrierFeature>();
                if(settings.mappedProps)lines.AddRange(env.barriers);
                if(settings.vegetation)lines.AddRange(env.hedges);
                foreach(var b in lines)
                {
                    if(b==null)continue;
                    var pts=GeoData.Points(b.points);
                    for(int i=1;i<pts.Count;i++)total+=Vector3.Distance(pts[i-1],pts[i]);
                }
                return total;
            }
        }
        public static float Radius(string kind,GenerationSettings settings)
        {
            var item=settings.propCatalog?settings.propCatalog.Find(kind):null;
            return Mathf.Max(kind=="tree"?2.5f:kind=="shelter"?2:0.7f,item==null?0:Positive(item.clearanceRadius,1,50));
        }
        static float FacingRoad(Vector3 point,IEnumerable<LineFeature> roads)
        {
            float best=2500,yaw=0;
            foreach(var road in roads.Where(r=>r!=null&&!r.tunnel))
            {var pts=GeoData.Points(road.points);for(int i=1;i<pts.Count;i++)
                {float d=GeoData.SegmentDistance(XZ(point),XZ(pts[i-1]),XZ(pts[i]),out float t);if(d<best){best=d;yaw=Yaw(Vector3.Lerp(pts[i-1],pts[i],t)-point);}}}
            return yaw;
        }
    }
}
