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

        // A rectangle turned in the plane: its centre, the unit direction of its first side (u; the second, v, is square to
        // it) and half its sides along each.
        public struct Box
        {
            public Vector2 centre,u;public float halfU,halfV;
            public Vector2 V=>new Vector2(-u.y,u.x);
            public float Area=>4*halfU*halfV;
            public Box(Vector2 centre,Vector2 u,float halfU,float halfV){this.centre=centre;this.u=u;this.halfU=halfU;this.halfV=halfV;}
            public Vector2 Corner(int i)
            {
                float a=i==0||i==3?-halfU:halfU,b=i<2?-halfV:halfV;
                return centre+u*a+V*b;
            }
            public Rect Bounds
            {
                get
                {
                    float ex=Mathf.Abs(u.x)*halfU+Mathf.Abs(u.y)*halfV,ey=Mathf.Abs(u.y)*halfU+Mathf.Abs(u.x)*halfV;
                    return new Rect(centre.x-ex,centre.y-ey,ex*2,ey*2);
                }
            }
        }

        // The area of an outline (any simple polygon, either winding) inside a box: Sutherland-Hodgman against the box's four
        // sides. The clipped polygon of a concave outline may run along a side twice, which adds nothing to its area.
        public static float AreaInside(IList<Vector2> outline,Box box,List<Vector2> scratchA=null,List<Vector2> scratchB=null)
        {
            if(outline==null||outline.Count<3)return 0;
            var a=scratchA??new List<Vector2>();var b=scratchB??new List<Vector2>();
            a.Clear();
            var v=box.V;
            foreach(var p in outline){var d=p-box.centre;a.Add(new Vector2(Vector2.Dot(d,box.u),Vector2.Dot(d,v)));}
            ClipAxis(a,b,0,box.halfU,true);ClipAxis(b,a,0,-box.halfU,false);
            ClipAxis(a,b,1,box.halfV,true);ClipAxis(b,a,1,-box.halfV,false);
            return a.Count<3?0:Mathf.Abs(Area(a));
        }

        // keeps the side of x (axis 0) or y (axis 1) below the plane (above it for a lower plane)
        static void ClipAxis(List<Vector2> input,List<Vector2> output,int axis,float plane,bool below)
        {
            output.Clear();
            for(int i=0;i<input.Count;i++)
            {
                var current=input[i];var next=input[(i+1)%input.Count];
                float dc=(plane-current[axis])*(below?1:-1),dn=(plane-next[axis])*(below?1:-1);
                if(dc>=0)output.Add(current);
                if((dc>=0)!=(dn>=0))output.Add(Vector2.Lerp(current,next,dc/(dc-dn)));
            }
        }

        // The area two boxes share.
        public static float Overlap(Box a,Box b,List<Vector2> scratchA=null,List<Vector2> scratchB=null,List<Vector2> corners=null)
        {
            var c=corners??new List<Vector2>(4);c.Clear();
            for(int i=0;i<4;i++)c.Add(a.Corner(i));
            return AreaInside(c,b,scratchA,scratchB);
        }
    }
}
