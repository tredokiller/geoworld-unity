using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeoWorld
{
    public static class GeoPolygon
    {
        const float Epsilon=0.00001f;
        public static float Cross(Vector2 a,Vector2 b)=>a.x*b.y-a.y*b.x;
        public static float Area(IList<Vector2> points)
        {
            double sum=0;
            for(int i=0;i<points.Count;i++){var a=points[i];var b=points[(i+1)%points.Count];sum+=(double)a.x*b.y-(double)b.x*a.y;}
            return (float)(sum*0.5);
        }
        public static List<Vector2> Clean(IList<Vector2> input)
        {
            if(input==null||input.Count<3||input.Count>2048)throw new ArgumentException("A footprint needs 3 to 2048 corners.");
            var points=new List<Vector2>();
            foreach(var p in input)
            {
                if(float.IsNaN(p.x)||float.IsNaN(p.y)||float.IsInfinity(p.x)||float.IsInfinity(p.y)||Mathf.Abs(p.x)>2000||Mathf.Abs(p.y)>2000)
                    throw new ArgumentException("Footprint coordinates must be finite local metres within 2 km.");
                if(points.Count==0||(points[points.Count-1]-p).sqrMagnitude>0.000001f)points.Add(p);
            }
            if(points.Count>1&&(points[0]-points[points.Count-1]).sqrMagnitude<=0.000001f)points.RemoveAt(points.Count-1);
            bool changed=true;
            while(changed&&points.Count>3)
            {
                changed=false;
                for(int i=0;i<points.Count;i++)
                {
                    var a=points[(i+points.Count-1)%points.Count];var b=points[i];var c=points[(i+1)%points.Count];
                    if(Mathf.Abs(Cross(b-a,c-b))<Epsilon&&Vector2.Dot(b-a,c-b)>=0)
                    {points.RemoveAt(i);changed=true;break;}
                }
            }
            if(points.Count<3||Mathf.Abs(Area(points))<0.01f)throw new ArgumentException("The footprint has no usable area.");
            for(int i=0;i<points.Count;i++)for(int j=i+1;j<points.Count;j++)
            {
                int nextI=(i+1)%points.Count,nextJ=(j+1)%points.Count;
                if(nextI==j||nextJ==i)continue;
                if(Intersects(points[i],points[nextI],points[j],points[nextJ]))throw new ArgumentException("Footprint edges cross or touch. Move the corners into a simple outline.");
            }
            if(Area(points)<0)points.Reverse();
            return points;
        }
        static bool Intersects(Vector2 a,Vector2 b,Vector2 c,Vector2 d)
        {
            if(Mathf.Max(a.x,b.x)<Mathf.Min(c.x,d.x)-Epsilon||Mathf.Max(c.x,d.x)<Mathf.Min(a.x,b.x)-Epsilon||
               Mathf.Max(a.y,b.y)<Mathf.Min(c.y,d.y)-Epsilon||Mathf.Max(c.y,d.y)<Mathf.Min(a.y,b.y)-Epsilon)return false;
            float abC=Cross(b-a,c-a),abD=Cross(b-a,d-a),cdA=Cross(d-c,a-c),cdB=Cross(d-c,b-c);
            return abC*abD<=Epsilon&&cdA*cdB<=Epsilon;
        }
        public static List<int> Triangulate(IList<Vector2> points)
        {
            var remaining=new List<int>();var triangles=new List<int>();
            for(int i=0;i<points.Count;i++)remaining.Add(i);
            while(remaining.Count>3)
            {
                bool clipped=false;
                for(int i=0;i<remaining.Count;i++)
                {
                    int a=remaining[(i+remaining.Count-1)%remaining.Count],b=remaining[i],c=remaining[(i+1)%remaining.Count];
                    if(Cross(points[b]-points[a],points[c]-points[b])<=Epsilon)continue;
                    bool occupied=false;
                    foreach(int p in remaining)
                        if(p!=a&&p!=b&&p!=c&&InTriangle(points[p],points[a],points[b],points[c])){occupied=true;break;}
                    if(occupied)continue;
                    triangles.AddRange(new[]{a,b,c});remaining.RemoveAt(i);clipped=true;break;
                }
                if(!clipped)throw new ArgumentException("The footprint cannot be triangulated. Check for overlapping or extremely short edges.");
            }
            triangles.AddRange(remaining);return triangles;
        }
        static bool InTriangle(Vector2 p,Vector2 a,Vector2 b,Vector2 c)=>
            Cross(b-a,p-a)>=-Epsilon&&Cross(c-b,p-b)>=-Epsilon&&Cross(a-c,p-c)>=-Epsilon;
        public static bool Contains(IList<Vector2> points,Vector2 p)
        {
            bool inside=false;
            for(int i=0,j=points.Count-1;i<points.Count;j=i++)
            {var a=points[i];var b=points[j];if((a.y>p.y)!=(b.y>p.y)&&p.x<(b.x-a.x)*(p.y-a.y)/(b.y-a.y)+a.x)inside=!inside;}
            return inside;
        }
    }
}
