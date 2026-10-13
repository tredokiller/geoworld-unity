using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace GeoWorld.Editor
{
    /// <summary>Road albedo lives in ordinary TerrainLayers, so it follows Terrain
    /// tessellation, LOD and collision exactly. No raised ground-road geometry.</summary>
    public sealed class GeoRoadPaint
    {
        readonly GeoBounds bounds;
        readonly GeoRoadNetwork network;
        readonly List<Road> roads = new List<Road>();

        sealed class Road
        {
            public PathBuild path;
            public int layer;
            public readonly List<Stroke> surface = new List<Stroke>(), markings = new List<Stroke>();
        }

        struct Stroke
        {
            public Vector2 a, direction;
            public float length, halfWidth, minX, maxX, minZ, maxZ;
            public bool roundCaps;
            public Stroke(Vector2 a, Vector2 b, float halfWidth, bool roundCaps)
            {
                this.a=a; length=Vector2.Distance(a,b); direction=(b-a)/Mathf.Max(length,0.00001f);
                this.halfWidth=halfWidth; this.roundCaps=roundCaps;
                minX=Mathf.Min(a.x,b.x)-halfWidth; maxX=Mathf.Max(a.x,b.x)+halfWidth;
                minZ=Mathf.Min(a.y,b.y)-halfWidth; maxZ=Mathf.Max(a.y,b.y)+halfWidth;
            }
            // Integrate a thin stripe over the projected texel footprint, instead of
            // point-sampling it. Sub-texel centre lines must not randomly disappear.
            static float Interval(float position,float low,float high,float footprint)
            {
                float r=footprint*0.5f;
                return Mathf.Clamp01((Mathf.Min(high,position+r)-Mathf.Max(low,position-r))/footprint);
            }
            public float Coverage(float x,float z,float dx,float dz)
            {
                var p=new Vector2(x,z)-a;
                float along=Vector2.Dot(p,direction);
                float across=p.x*-direction.y+p.y*direction.x;
                float filter=Mathf.Max(0.0001f,Mathf.Abs(direction.y)*dx+Mathf.Abs(direction.x)*dz);
                if(roundCaps)
                {
                    float end=along-Mathf.Clamp(along,0,length);
                    float distance=Mathf.Sqrt(across*across+end*end);
                    return Interval(distance,-halfWidth,halfWidth,filter);
                }
                float longitudinal=Mathf.Max(0.0001f,Mathf.Abs(direction.x)*dx+Mathf.Abs(direction.y)*dz);
                return Interval(across,-halfWidth,halfWidth,filter)*Interval(along,0,length,longitudinal);
            }
        }

        public GeoRoadPaint(GeoBounds bounds,GeoRoadNetwork network,bool markings)
        {
            this.bounds=bounds;this.network=network;
            // Paint low priority first; asphalt wins over paving/gravel/dirt at a junction.
            foreach(var path in network.OrderedRoads().Reverse())
            {
                var road=new Road {path=path,layer=SurfaceLayer(path)};
                float travelled=0;
                for(int i=1;i<path.controls.Count;i++)
                {
                    var a=new Vector2(path.controls[i-1].x,path.controls[i-1].z);
                    var b=new Vector2(path.controls[i].x,path.controls[i].z);
                    float length=Vector2.Distance(a,b);
                    if(length<0.0001f)continue;
                    road.surface.Add(new Stroke(a,b,path.feature.width*0.5f,true));
                    if(markings&&path.feature.material=="asphalt"&&path.feature.width>=5)
                    {
                        // Exact four-metre dashes, independent of path subdivision length.
                        for(float dash=Mathf.Floor(travelled/8)*8;dash<travelled+length;dash+=8)
                        {
                            float start=Mathf.Max(0,dash-travelled),end=Mathf.Min(length,dash+4-travelled);
                            if(end>start)road.markings.Add(new Stroke(Vector2.Lerp(a,b,start/length),Vector2.Lerp(a,b,end/length),0.09f,false));
                        }
                    }
                    travelled+=length;
                }
                roads.Add(road);
            }
        }

        static int SurfaceLayer(PathBuild path)
        {
            switch(GeoData.Surface(path.feature.material))
            {
                case GroundSurface.Dirt:return 2;
                case GroundSurface.Gravel:return 4;
                case GroundSurface.Paving:return 5;
                default:return GeoTerrain.AsphaltLayer;
            }
        }

        public void Apply(TerrainData terrain,float[,,] ground,Action<float> progress=null)
        {
            const int tileSize=128;
            int n=terrain.alphamapResolution;
            // Build in strips but upload only once. SetAlphamaps can reallocate whole
            // GPU control textures on every call; hundreds of tile uploads can exhaust
            // the graphics driver's upload heap before Unity gets its next frame.
            var paint=new float[n,n,GeoTerrain.LayerNames.Length];
            for(int z=0;z<n;z+=tileSize)
            {
                progress?.Invoke(z/(float)n);
                for(int x=0;x<n;x+=tileSize)
                {
                    int width=Mathf.Min(tileSize,n-x),height=Mathf.Min(tileSize,n-z);
                    var tile=PaintTile(ground,n,x,z,width,height);
                    for(int row=0;row<height;row++)
                        Array.Copy(tile,row*width*GeoTerrain.LayerNames.Length,paint,((z+row)*n+x)*GeoTerrain.LayerNames.Length,width*GeoTerrain.LayerNames.Length);
                }
            }
            terrain.SetAlphamaps(0,0,paint);
            progress?.Invoke(1);
        }

        public float[,,] PaintTile(float[,,] ground,int resolution,int x0,int z0,int width,int height)
        {
            int layers=GeoTerrain.LayerNames.Length;
            var result=new float[height,width,layers];
            var coverage=new float[height,width];
            var owner=new int[height,width];
            float dx=bounds.Width/resolution,dz=bounds.Depth/resolution;
            int nz=ground.GetLength(0),nx=ground.GetLength(1);
            for(int z=0;z<height;z++)for(int x=0;x<width;x++)
            {
                owner[z,x]=-1;
                float sx=Mathf.Clamp((x+x0+0.5f)/resolution*nx-0.5f,0,nx-1);
                float sz=Mathf.Clamp((z+z0+0.5f)/resolution*nz-0.5f,0,nz-1);
                int ix=(int)sx,iz=(int)sz,jx=Mathf.Min(ix+1,nx-1),jz=Mathf.Min(iz+1,nz-1);
                for(int l=0;l<layers;l++)
                    result[z,x,l]=Mathf.Lerp(Mathf.Lerp(ground[iz,ix,l],ground[iz,jx,l],sx-ix),Mathf.Lerp(ground[jz,ix,l],ground[jz,jx,l],sx-ix),sz-iz);
            }
            for(int r=0;r<roads.Count;r++)
            {
                Array.Clear(coverage,0,coverage.Length);
                if(!Raster(roads[r].surface,coverage,x0,z0,dx,dz))continue;
                for(int z=0;z<height;z++)for(int x=0;x<width;x++)
                {
                    float weight=coverage[z,x];
                    if(weight<=0)continue;
                    Blend(result,x,z,roads[r].layer,weight);
                    if(weight>=0.5f)owner[z,x]=r;
                }
            }
            for(int r=0;r<roads.Count;r++)
            {
                if(roads[r].markings.Count==0)continue;
                Array.Clear(coverage,0,coverage.Length);
                if(!Raster(roads[r].markings,coverage,x0,z0,dx,dz))continue;
                for(int z=0;z<height;z++)for(int x=0;x<width;x++)
                {
                    if(coverage[z,x]<=0||owner[z,x]!=r)continue;
                    var p=new Vector3(bounds.minX+(x0+x+0.5f)*dx,0,bounds.minZ+(z0+z+0.5f)*dz);
                    if(!network.HidesMarking(roads[r].path,p))Blend(result,x,z,GeoTerrain.MarkingLayer,coverage[z,x]);
                }
            }
            return result;
        }

        bool Raster(List<Stroke> strokes,float[,] coverage,int x0,int z0,float dx,float dz)
        {
            int width=coverage.GetLength(1),height=coverage.GetLength(0);
            bool touched=false;
            foreach(var stroke in strokes)
            {
                int minX=Mathf.Max(0,Mathf.FloorToInt((stroke.minX-bounds.minX)/dx)-x0-1);
                int maxX=Mathf.Min(width-1,Mathf.CeilToInt((stroke.maxX-bounds.minX)/dx)-x0+1);
                int minZ=Mathf.Max(0,Mathf.FloorToInt((stroke.minZ-bounds.minZ)/dz)-z0-1);
                int maxZ=Mathf.Min(height-1,Mathf.CeilToInt((stroke.maxZ-bounds.minZ)/dz)-z0+1);
                if(minX<=maxX&&minZ<=maxZ)touched=true;
                for(int z=minZ;z<=maxZ;z++)for(int x=minX;x<=maxX;x++)
                {
                    float c=stroke.Coverage(bounds.minX+(x0+x+0.5f)*dx,bounds.minZ+(z0+z+0.5f)*dz,dx,dz);
                    coverage[z,x]=Mathf.Max(coverage[z,x],c);
                }
            }
            return touched;
        }

        static void Blend(float[,,] weights,int x,int z,int layer,float amount)
        {
            for(int l=0;l<weights.GetLength(2);l++)weights[z,x,l]*=1-amount;
            weights[z,x,layer]+=amount;
        }
    }
}
