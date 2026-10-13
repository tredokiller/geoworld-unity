using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace GeoWorld.Editor.Tests
{
    public class GeoRoadNetworkTests
    {
        HeightField Field()
        {
            var f=new HeightField(new GeoBounds{minX=-20,minZ=-20,maxX=20,maxZ=20},33);
            for(int z=0;z<33;z++)for(int x=0;x<33;x++)
                f.heights[z,x]=Mathf.Sin(f.X(x)*0.22f)*Mathf.Cos(f.Z(z)*0.17f)*1.4f;
            return f;
        }
        PathBuild Road(string id,string material,float width,params float[] xy)
        {
            var controls=GeoData.Points(xy);
            return new PathBuild{feature=new LineFeature{id=id,material=material,width=width},controls=controls,samples=GeoData.Resample(controls,1.7f)};
        }
        GeoRoadNetwork Build(HeightField f,params PathBuild[] roads)
        {var n=new GeoRoadNetwork(roads.ToList(),f);n.Connect(0.75f,1.7f);n.Build();return n;}
        IEnumerable<MeshDraft> Drafts(GeoRoadNetwork n)=>n.roads.Values.Concat(n.junctions.SelectMany(j=>j.surfaces));
        double Area(MeshDraft draft)
        {
            double area=0;
            for(int i=0;i<draft.triangles.Count;i+=3)
            {var a=draft.vertices[draft.triangles[i]];var b=draft.vertices[draft.triangles[i+1]];var c=draft.vertices[draft.triangles[i+2]];area+=Math.Abs((double)(b.x-a.x)*(c.z-a.z)-(double)(b.z-a.z)*(c.x-a.x))*0.5;}
            return area;
        }
        double SurfaceArea(GeoRoadNetwork n,GroundSurface surface)
        {
            double result=n.roads.Where(p=>GeoData.Surface(p.Key.feature.material)==surface).Sum(p=>Area(p.Value));
            return result+n.junctions.Sum(j=>Area(j.surfaces[(int)surface]));
        }
        int Covers(GeoRoadNetwork n,Vector2 p)
        {
            int hits=0;
            foreach(var draft in Drafts(n))for(int i=0;i<draft.triangles.Count;i+=3)
            {
                Vector3 a=draft.vertices[draft.triangles[i]],b=draft.vertices[draft.triangles[i+1]],c=draft.vertices[draft.triangles[i+2]];
                float cross(Vector3 u,Vector3 v)=>(v.x-u.x)*(p.y-u.z)-(v.z-u.z)*(p.x-u.x);
                // Clockwise XZ triangles; sample points intentionally avoid grid/triangle edges.
                if(cross(a,b)<-0.000001f&&cross(b,c)<-0.000001f&&cross(c,a)<-0.000001f)hits++;
            }
            return hits;
        }

        [Test] public void CrossingWithoutSharedControlNode_HasOneSurfaceAndContinuousAsphalt()
        {
            var asphalt=Road("asphalt","asphalt",6,-18,0,18,0);
            var dirt=Road("dirt","dirt_road",6,0,-18,0,18);
            var network=Build(Field(),dirt,asphalt);
            Assert.That(network.junctions.Count,Is.EqualTo(1));
            Assert.That(SurfaceArea(network,GroundSurface.Asphalt),Is.EqualTo(216).Within(0.002));
            Assert.That(SurfaceArea(network,GroundSurface.Dirt),Is.EqualTo(180).Within(0.002));
            Assert.That(Drafts(network).Sum(Area),Is.EqualTo(396).Within(0.003));
            for(float z=-2.817f;z<3;z+=0.563f)for(float x=-2.723f;x<3;x+=0.497f)
                Assert.That(Covers(network,new Vector2(x,z)),Is.EqualTo(1),$"Overlap or hole at {x}, {z}");
        }

        [Test] public void TJunction_IsTrimmedAtThroughRoadEdge_WithoutHoles()
        {
            var asphalt=Road("through","asphalt",8,-18,0,18,0);
            var branch=Road("branch","gravel",4,0,-18,0,0);
            var network=Build(Field(),asphalt,branch);
            Assert.That(SurfaceArea(network,GroundSurface.Asphalt),Is.EqualTo(288).Within(0.003));
            Assert.That(SurfaceArea(network,GroundSurface.Gravel),Is.EqualTo(56).Within(0.003));
            foreach(float z in new[]{-4.001f,-3.999f,-0.39f,-12.13f})Assert.That(Covers(network,new Vector2(0.713f,z)),Is.EqualTo(1));
        }

        [Test] public void AcuteMixedSurfaceJunction_IsIndependentOfSourceOrder()
        {
            var a=Road("a","asphalt",7,-18,0,18,0);
            var b=Road("b","gravel",5,-15,-13,0,0,14,11);
            var first=Build(Field(),a,b);var second=Build(Field(),b,a);
            Assert.That(SurfaceArea(first,GroundSurface.Asphalt),Is.EqualTo(252).Within(0.003));
            Assert.That(SurfaceArea(first,GroundSurface.Gravel),Is.EqualTo(SurfaceArea(second,GroundSurface.Gravel)).Within(0.003));
            for(float z=-1.917f;z<2;z+=0.617f)for(float x=-2.713f;x<3;x+=0.519f)
                Assert.That(Covers(first,new Vector2(x,z)),Is.EqualTo(1));
        }

        [Test] public void UnevenGround_AllRoadAndJunctionVerticesShareTheSameHeightFunction()
        {
            var network=Build(Field(),Road("a","asphalt",8,-18,-2,18,3),Road("b","dirt_road",5,-2,-18,3,18));
            foreach(var draft in Drafts(network))
            {
                foreach(var v in draft.vertices)
                    Assert.That(v.y,Is.EqualTo(network.Height(v.x,v.z)+GeoRoadNetwork.SurfaceOffset).Within(0.00003));
                for(int i=0;i<draft.triangles.Count;i+=3)
                {
                    var a=draft.vertices[draft.triangles[i]];var b=draft.vertices[draft.triangles[i+1]];var c=draft.vertices[draft.triangles[i+2]];
                    Assert.That(Vector3.Cross(b-a,c-a).y,Is.GreaterThan(0));
                }
            }
        }

        [Test] public void NearEndpointSnaps_ButUnrelatedRoadAndBridgeDoNot()
        {
            var a=Road("main","asphalt",6,-18,0,18,0);
            var b=Road("near","gravel",4,0,-18,0,-0.4f);
            var c=Road("far","dirt_road",3,10,-18,10,-6);
            var bridge=Road("bridge","asphalt",6,-8,-18,-8,18);bridge.feature.bridge=true;
            var network=Build(Field(),a,b,c,bridge);
            Assert.That(b.controls.Last().z,Is.EqualTo(0).Within(0.001));
            Assert.That(c.controls.Last().z,Is.EqualTo(-6));
            Assert.That(network.snappedEndpoints,Is.EqualTo(1));
            Assert.IsFalse(network.roads.ContainsKey(bridge));
            Assert.IsTrue(network.junctions.All(j=>!j.paths.Contains(bridge)));
        }

        [Test] public void DifferentLayers_DoNotJoinOrSnap()
        {
            var a=Road("ground","asphalt",6,-18,0,18,0);
            var b=Road("upper","asphalt",6,0,-18,0,18);b.feature.layer=1;
            var network=Build(Field(),a,b);
            Assert.That(network.junctions,Is.Empty);Assert.That(network.snappedEndpoints,Is.Zero);
            Assert.IsFalse(network.roads.ContainsKey(b));
        }

        [Test] public void DuplicateRoadFootprints_DoNotCreateStackedSurfaces()
        {
            var network=Build(Field(),Road("a","asphalt",6,-18,0,18,0),Road("b","asphalt",6,-18,0,18,0));
            Assert.That(Drafts(network).Sum(Area),Is.EqualTo(216).Within(0.003));
            Assert.That(Covers(network,new Vector2(1.273f,0.419f)),Is.EqualTo(1));
        }

        [Test] public void DuplicateRoadMarkings_AreOnlyDrawnOnOwningSurface()
        {
            var field=Field();var a=Road("a","asphalt",6,-18,0,18,0);var b=Road("b","asphalt",6,-18,0,18,0);
            var network=Build(field,a,b);var mesh=GeoMeshes.Markings(b,field,network);
            try{Assert.That(mesh.vertexCount,Is.Zero);}finally{UnityEngine.Object.DestroyImmediate(mesh);}
        }

        [Test] public void ElevatedRibbon_DoesNotSnapBackOntoGround()
        {
            var p=Road("upper","asphalt",6,-18,0,18,0);p.feature.layer=1;
            for(int i=0;i<p.samples.Count;i++){var v=p.samples[i];v.y=12;p.samples[i]=v;}
            var mesh=GeoMeshes.Ribbon(p,Field(),GeoRoadNetwork.SurfaceOffset);
            try{foreach(var v in mesh.vertices)Assert.That(v.y,Is.EqualTo(12.1f).Within(0.001));}
            finally{UnityEngine.Object.DestroyImmediate(mesh);}
        }

        [Test] public void MarkingsStopBeforeNewlyDetectedCrossing()
        {
            var field=Field();var road=Road("a","asphalt",6,-18,0,18,0);
            var network=Build(field,road,Road("b","dirt_road",5,0,-18,0,18));
            var mesh=GeoMeshes.Markings(road,field,network);
            try
            {
                Assert.That(mesh.vertexCount,Is.GreaterThan(0));
                foreach(var v in mesh.vertices)Assert.That(Mathf.Abs(v.x),Is.GreaterThan(5));
            }
            finally{UnityEngine.Object.DestroyImmediate(mesh);}
        }
    }
}
