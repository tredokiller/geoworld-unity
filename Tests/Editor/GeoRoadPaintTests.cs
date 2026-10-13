using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace GeoWorld.Editor.Tests
{
    public class GeoRoadPaintTests
    {
        readonly GeoBounds bounds=new GeoBounds {minX=-32,minZ=-32,maxX=32,maxZ=32};
        PathBuild Road(string id,string material,float width,params float[] points)
        {
            var controls=GeoData.Points(points);
            return new PathBuild {feature=new LineFeature {id=id,material=material,width=width},controls=controls,samples=GeoData.Resample(controls,2)};
        }
        float[,,] Ground(int n=16)
        {
            var ground=new float[n,n,GeoTerrain.LayerNames.Length];
            for(int z=0;z<n;z++)for(int x=0;x<n;x++)ground[z,x,0]=1;
            return ground;
        }
        GeoRoadPaint Painter(bool markings,params PathBuild[] roads)
        {
            var network=new GeoRoadNetwork(new List<PathBuild>(roads),new HeightField(bounds,33));
            network.Connect(0,2);
            return new GeoRoadPaint(bounds,network,markings);
        }
        float At(float[,,] map,float x,float z,int layer)
        {
            int n=map.GetLength(0);
            return map[Mathf.Clamp((int)((z+32)/64*n),0,n-1),Mathf.Clamp((int)((x+32)/64*n),0,n-1),layer];
        }

        [Test] public void Crossing_PaintsAsphaltOverDirt_InEitherInputOrder_AndNormalizes()
        {
            var main=Road("main","asphalt",8,-30,0,30,0);
            var side=Road("side","dirt_road",6,0,-30,0,30);
            var first=Painter(false,main,side).PaintTile(Ground(),256,0,0,256,256);
            var second=Painter(false,side,main).PaintTile(Ground(),256,0,0,256,256);
            Assert.That(At(first,0,0,GeoTerrain.AsphaltLayer),Is.EqualTo(1).Within(0.0001));
            Assert.That(At(first,0,12,2),Is.EqualTo(1).Within(0.0001));
            Assert.That(At(first,12,12,0),Is.EqualTo(1).Within(0.0001));
            for(int z=0;z<256;z++)for(int x=0;x<256;x++)
            {
                float sum=0;
                for(int l=0;l<GeoTerrain.LayerNames.Length;l++)
                {Assert.That(first[z,x,l],Is.EqualTo(second[z,x,l]).Within(0.00001));sum+=first[z,x,l];}
                Assert.That(sum,Is.EqualTo(1).Within(0.00001));
            }
        }

        [Test] public void TJunction_HasNoGapAtCurb_AndBendRemainsCovered()
        {
            var main=Road("main","asphalt",8,-30,0,30,0);
            var side=Road("side","gravel",6,0,-24,0,-4);
            var bend=Road("bend","paving",4,12,10,20,10,20,24);
            var map=Painter(false,main,side,bend).PaintTile(Ground(),256,0,0,256,256);
            for(float z=-10;z<4;z+=0.25f)
                Assert.That(At(map,0,z,4)+At(map,0,z,GeoTerrain.AsphaltLayer),Is.GreaterThan(0.99));
            Assert.That(At(map,20,10,5),Is.GreaterThan(0.99));
        }

        [Test] public void BridgesAndOtherLevels_LeaveGroundPaintUntouched()
        {
            var bridge=Road("bridge","asphalt",8,-30,0,30,0);bridge.feature.bridge=true;
            var upper=Road("upper","paving",8,0,-30,0,30);upper.feature.layer=1;
            var map=Painter(true,bridge,upper).PaintTile(Ground(),64,0,0,64,64);
            for(int z=0;z<64;z++)for(int x=0;x<64;x++)Assert.That(map[z,x,0],Is.EqualTo(1));
        }

        [Test] public void ThinMarkings_AreAntialiased_AndStopAtJunctions()
        {
            var main=Road("main","asphalt",8,-30,0,30,0);
            var side=Road("side","dirt_road",6,0,-30,0,30);
            var map=Painter(true,main,side).PaintTile(Ground(),256,0,0,256,256);
            Assert.That(At(map,-29,0,GeoTerrain.MarkingLayer),Is.GreaterThan(0.1).And.LessThan(1));
            Assert.That(At(map,-24,0,GeoTerrain.MarkingLayer),Is.Zero,"Four-metre gap between dashes.");
            Assert.That(At(map,0,0,GeoTerrain.MarkingLayer),Is.Zero);
            Assert.That(At(map,-5,0,GeoTerrain.MarkingLayer),Is.Zero);
        }

        [Test] public void DuplicateRoads_DoNotDoubleTheirMarkings()
        {
            var first=Road("a","asphalt",8,-30,0,30,0);
            var duplicate=Road("z","asphalt",8,-30,0,30,0);
            var one=Painter(true,first).PaintTile(Ground(),128,0,0,128,128);
            var two=Painter(true,first,duplicate).PaintTile(Ground(),128,0,0,128,128);
            for(int z=0;z<128;z++)for(int x=0;x<128;x++)
                Assert.That(two[z,x,GeoTerrain.MarkingLayer],Is.EqualTo(one[z,x,GeoTerrain.MarkingLayer]).Within(0.00001));
        }

        [Test] public void TiledPainting_MatchesWholeImage_WithNoTileSeams()
        {
            var painter=Painter(true,Road("diagonal","asphalt",7,-30,-25,30,22));
            var ground=Ground();
            var all=painter.PaintTile(ground,256,0,0,256,256);
            var tile=painter.PaintTile(ground,256,120,120,16,16);
            for(int z=0;z<16;z++)for(int x=0;x<16;x++)for(int l=0;l<GeoTerrain.LayerNames.Length;l++)
                Assert.That(tile[z,x,l],Is.EqualTo(all[z+120,x+120,l]).Within(0.00001));
        }
    }
}
