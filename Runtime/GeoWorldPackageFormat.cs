using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace GeoWorld
{
    // One tile of a world package on disk (tiles/tile_<x>_<z>.gwt): what a generated tile is made of, which a player cannot keep as
    // assets (meshes, textures, materials, terrain data), and its objects with their components. Library assets (home and ruin
    // models, tree prefabs, road pictures) are referred to by their key in GeoRuntimeAssets, never copied.
    //   "GWT1", format version (uint16), flags (uint16), header length (int32), header (UTF-8 JSON, GeoTileHeader),
    //   then a deflate stream of the body: the objects (textures, meshes, materials, terrain layers, terrain data, home sets),
    //   the nodes (name, parent, transform, components) and the navigation data (JSON, GeoTileNavigation). Little-endian; the same
    //   tile always gives the same bytes.
    public static class GeoWorldPackageFormat
    {
        public const int Version = 1;
        static readonly byte[] Magic = { (byte)'G', (byte)'W', (byte)'T', (byte)'1' };

        // object kinds
        const byte KTexture = 1, KMesh = 2, KMaterial = 3, KTerrainLayer = 4, KTerrainData = 5, KHomeSet = 6;
        // component kinds
        const byte CMeshFilter = 1, CMeshRenderer = 2, CMeshCollider = 3, CBoxCollider = 4, CCapsuleCollider = 5, CSphereCollider = 6,
            CTerrain = 7, CTerrainCollider = 8, CLODGroup = 9, CWorldRoot = 10, CCityChunk = 11, CHomeInstances = 12, CInstanced = 13,
            CJson = 99;

        // ------------------------------------------------------------------------------------------------------------ writing

        // The tile (root and everything under it) as the bytes of a tile file. Main thread (it reads Unity objects); the
        // returned bytes are compressed already. notes: what could not be written (components a package does not carry).
        public static byte[] Write(GameObject root, GeoTileHeader header, GeoTileNavigation navigation, List<string> notes)
        {
            var writer = new TileWriter(notes);
            writer.Collect(root.transform, -1);
            byte[] body;
            using (var raw = new MemoryStream())
            {
                using (var deflate = new DeflateStream(raw, System.IO.Compression.CompressionLevel.Optimal, true))
                using (var w = new BinaryWriter(deflate, Encoding.UTF8, true))
                {
                    writer.WriteBody(w);
                    w.Write(JsonUtility.ToJson(navigation ?? new GeoTileNavigation()));
                }
                body = raw.ToArray();
            }
            var json = Encoding.UTF8.GetBytes(JsonUtility.ToJson(header));
            using (var file = new MemoryStream(body.Length + json.Length + 16))
            using (var w = new BinaryWriter(file, Encoding.UTF8))
            {
                w.Write(Magic); w.Write((ushort)Version); w.Write((ushort)0);
                w.Write(json.Length); w.Write(json);
                w.Write(body);
                w.Flush();
                return file.ToArray();
            }
        }

        public static GeoTileHeader ReadHeader(byte[] file, out int bodyOffset)
        {
            if (file.Length < 12 || file[0] != Magic[0] || file[1] != Magic[1] || file[2] != Magic[2] || file[3] != Magic[3])
                throw new InvalidDataException("Not a Geo World tile file.");
            int version = BitConverter.ToUInt16(file, 4);
            if (version > Version) throw new InvalidDataException($"Tile file version {version} is newer than this Geo World reads ({Version}).");
            int length = BitConverter.ToInt32(file, 8);
            bodyOffset = 12 + length;
            return JsonUtility.FromJson<GeoTileHeader>(Encoding.UTF8.GetString(file, 12, length));
        }

        sealed class TileWriter
        {
            readonly List<string> notes;
            readonly Dictionary<Object, int> local = new Dictionary<Object, int>();
            readonly List<Object> objects = new List<Object>();
            readonly List<Transform> nodes = new List<Transform>();
            readonly Dictionary<Transform, int> nodeIndex = new Dictionary<Transform, int>();
            readonly HashSet<string> warned = new HashSet<string>();

            public TileWriter(List<string> notes) { this.notes = notes; }

            void Warn(string text) { if (warned.Add(text)) notes?.Add(text); }

            // nodes in depth-first order, every object they use before them
            public void Collect(Transform t, int parent)
            {
                nodeIndex[t] = nodes.Count; nodes.Add(t);
                foreach (var c in t.GetComponents<Component>())
                {
                    switch (c)
                    {
                        case MeshFilter f: Use(f.sharedMesh); break;
                        case MeshRenderer r: foreach (var m in r.sharedMaterials) Use(m); break;
                        case MeshCollider mc: Use(mc.sharedMesh); break;
                        case Terrain terrain: Use(terrain.terrainData); Use(terrain.materialTemplate); break;
                        case TerrainCollider tc: Use(tc.terrainData); break;
                        case GeoHomeInstances h: Use(h.set); break;
                        case GeoInstancedMeshes im: foreach (var b in im.batches) { Use(b.mesh); Use(b.material); } break;
                    }
                }
                for (int i = 0; i < t.childCount; i++)
                {
                    // objects that are never saved (the homes GeoHomeInstances makes from its set) are not part of the tile either
                    var child = t.GetChild(i);
                    if ((child.gameObject.hideFlags & HideFlags.DontSaveInEditor) == 0) Collect(child, nodeIndex[t]);
                }
            }

            // a generated object is written once, after what it uses; a library asset is referred to by key
            void Use(Object o)
            {
                if (!o || local.ContainsKey(o) || GeoAssets.Key(o) != null) return;
                switch (o)
                {
                    case Material m:
                        var shader = m.shader;
                        if (shader)
                            for (int i = 0; i < shader.GetPropertyCount(); i++)
                                if (shader.GetPropertyType(i) == ShaderPropertyType.Texture) Use(m.GetTexture(shader.GetPropertyNameId(i)));
                        break;
                    case TerrainLayer l: Use(l.diffuseTexture); Use(l.normalMapTexture); Use(l.maskMapTexture); break;
                    case TerrainData d: foreach (var l in d.terrainLayers) Use(l); break;
                    case GeoHomeSet set: foreach (var h in set.homes) foreach (var p in h.parts) { Use(p.mesh); if (p.materials != null) foreach (var m in p.materials) Use(m); } break;
                    case Texture2D _: case Mesh _: break;
                    default: Warn("Package: " + o.GetType().Name + " " + o.name + " is not carried by a package (left out)."); return;
                }
                local[o] = objects.Count; objects.Add(o);
            }

            void Ref(BinaryWriter w, Object o)
            {
                if (!o) { w.Write((byte)0); return; }
                if (local.TryGetValue(o, out int index)) { w.Write((byte)1); w.Write(index); return; }
                string key = GeoAssets.Key(o);
                if (key != null) { w.Write((byte)2); w.Write(key); return; }
                Warn("Package: " + o.GetType().Name + " " + o.name + " is neither generated nor in GeoRuntimeAssets (left out).");
                w.Write((byte)0);
            }

            public void WriteBody(BinaryWriter w)
            {
                w.Write(objects.Count);
                foreach (var o in objects)
                {
                    switch (o)
                    {
                        case Texture2D t: w.Write(KTexture); WriteTexture(w, t); break;
                        case Mesh m: w.Write(KMesh); WriteMesh(w, m); break;
                        case Material m: w.Write(KMaterial); WriteMaterial(w, m); break;
                        case TerrainLayer l: w.Write(KTerrainLayer); WriteLayer(w, l); break;
                        case TerrainData d: w.Write(KTerrainData); WriteTerrainData(w, d); break;
                        case GeoHomeSet s: w.Write(KHomeSet); WriteHomeSet(w, s); break;
                    }
                }
                w.Write(nodes.Count);
                foreach (var t in nodes)
                {
                    w.Write(t.name);
                    w.Write(t.parent && nodeIndex.TryGetValue(t.parent, out int parent) ? parent : -1);
                    Vec(w, t.localPosition); Quat(w, t.localRotation); Vec(w, t.localScale);
                    w.Write(t.gameObject.activeSelf); w.Write(t.gameObject.layer);
                    var components = new List<Component>();
                    foreach (var c in t.GetComponents<Component>()) if (c && !(c is Transform) && Writable(c)) components.Add(c);
                    w.Write(components.Count);
                    foreach (var c in components) WriteComponent(w, c);
                }
            }

            bool Writable(Component c)
            {
                switch (c)
                {
                    case MeshFilter _: case MeshRenderer _: case MeshCollider _: case BoxCollider _: case CapsuleCollider _: case SphereCollider _:
                    case Terrain _: case TerrainCollider _: case LODGroup _: case GeoWorldRoot _: case GeoCityChunk _: case GeoHomeInstances _:
                    case GeoInstancedMeshes _: case GeoPath _: case GeoPlacedProp _:
                        return true;
                    case MonoBehaviour mb:
                        string json = JsonUtility.ToJson(mb);
                        if (json.Contains("\"instanceID\"")) Warn("Package: " + mb.GetType().Name + " keeps its plain fields only (references to objects are not carried).");
                        return true;
                    default:
                        Warn("Package: " + c.GetType().Name + " components are not carried by a package (left out).");
                        return false;
                }
            }

            void WriteComponent(BinaryWriter w, Component c)
            {
                switch (c)
                {
                    case MeshFilter f: w.Write(CMeshFilter); Ref(w, f.sharedMesh); break;
                    case MeshRenderer r:
                        w.Write(CMeshRenderer);
                        var materials = r.sharedMaterials;
                        w.Write(materials.Length); foreach (var m in materials) Ref(w, m);
                        w.Write((int)r.shadowCastingMode); w.Write(r.receiveShadows); w.Write(r.enabled);
                        w.Write((int)r.lightProbeUsage); w.Write((int)r.reflectionProbeUsage); w.Write(r.renderingLayerMask); w.Write(r.staticShadowCaster);
                        break;
                    case MeshCollider mc: w.Write(CMeshCollider); Ref(w, mc.sharedMesh); w.Write(mc.convex); w.Write(mc.enabled); w.Write(mc.isTrigger); break;
                    case BoxCollider b: w.Write(CBoxCollider); Vec(w, b.center); Vec(w, b.size); w.Write(b.enabled); w.Write(b.isTrigger); break;
                    case CapsuleCollider cc: w.Write(CCapsuleCollider); Vec(w, cc.center); w.Write(cc.radius); w.Write(cc.height); w.Write(cc.direction); w.Write(cc.enabled); w.Write(cc.isTrigger); break;
                    case SphereCollider sc: w.Write(CSphereCollider); Vec(w, sc.center); w.Write(sc.radius); w.Write(sc.enabled); w.Write(sc.isTrigger); break;
                    case Terrain t:
                        w.Write(CTerrain); Ref(w, t.terrainData); Ref(w, t.materialTemplate);
                        w.Write(t.heightmapPixelError); w.Write(t.basemapDistance); w.Write(t.drawInstanced); w.Write(t.treeDistance);
                        w.Write(t.treeBillboardDistance); w.Write(t.treeCrossFadeLength); w.Write(t.treeMaximumFullLODCount);
                        w.Write(t.detailObjectDistance); w.Write(t.detailObjectDensity); w.Write((int)t.shadowCastingMode); w.Write(t.groupingID);
                        w.Write(t.allowAutoConnect); w.Write(t.drawTreesAndFoliage); w.Write(t.drawHeightmap); w.Write(t.enabled);
                        break;
                    case TerrainCollider tc: w.Write(CTerrainCollider); Ref(w, tc.terrainData); w.Write(tc.enabled); break;
                    case LODGroup g:
                        w.Write(CLODGroup); w.Write((int)g.fadeMode); w.Write(g.animateCrossFading);
                        var lods = g.GetLODs();
                        w.Write(lods.Length);
                        foreach (var lod in lods)
                        {
                            w.Write(lod.screenRelativeTransitionHeight); w.Write(lod.fadeTransitionWidth);
                            var renderers = lod.renderers ?? new Renderer[0];
                            w.Write(renderers.Length);
                            foreach (var r in renderers)
                            {
                                // the renderer by its node and its index among the renderers there
                                int node = r && nodeIndex.TryGetValue(r.transform, out int n) ? n : -1;
                                w.Write(node);
                                w.Write(r ? Array.IndexOf(r.GetComponents<Renderer>(), r) : -1);
                            }
                        }
                        break;
                    case GeoWorldRoot root: w.Write(CWorldRoot); w.Write(JsonUtility.ToJson(GeoWorldRootData.From(root))); break;
                    case GeoCityChunk chunk: w.Write(CCityChunk); w.Write(JsonUtility.ToJson(chunk)); break;
                    case GeoHomeInstances h: w.Write(CHomeInstances); Ref(w, h.set); break;
                    case GeoInstancedMeshes im:
                        w.Write(CInstanced); w.Write(im.drawDistance); w.Write((int)im.shadows); w.Write(im.count); w.Write(im.batches.Count);
                        foreach (var b in im.batches)
                        {
                            Ref(w, b.mesh); w.Write(b.submesh); Ref(w, b.material); Vec(w, b.bounds.center); Vec(w, b.bounds.extents);
                            w.Write(b.matrices.Length);
                            foreach (var m in b.matrices) for (int k = 0; k < 16; k++) w.Write(m[k]);
                        }
                        break;
                    default:
                        w.Write(CJson); w.Write(c.GetType().AssemblyQualifiedName); w.Write(JsonUtility.ToJson(c));
                        break;
                }
            }

            static void WriteTexture(BinaryWriter w, Texture2D t)
            {
                w.Write(t.name); w.Write(t.width); w.Write(t.height); w.Write((int)t.format); w.Write(t.mipmapCount);
                w.Write(!t.isDataSRGB); w.Write((int)t.wrapModeU); w.Write((int)t.wrapModeV); w.Write((int)t.filterMode); w.Write(t.anisoLevel); w.Write(t.mipMapBias);
                var data = t.GetRawTextureData<byte>();
                w.Write(data.Length); w.Write(data.ToArray());
            }

            static void WriteMesh(BinaryWriter w, Mesh m)
            {
                w.Write(m.name);
                using (var array = Mesh.AcquireReadOnlyMeshData(m))
                {
                    var d = array[0];
                    w.Write(d.vertexCount);
                    var attributes = m.GetVertexAttributes();
                    w.Write(attributes.Length);
                    foreach (var a in attributes) { w.Write((int)a.attribute); w.Write((int)a.format); w.Write(a.dimension); w.Write(a.stream); }
                    w.Write(d.vertexBufferCount);
                    for (int s = 0; s < d.vertexBufferCount; s++) { var bytes = d.GetVertexData<byte>(s); w.Write(bytes.Length); w.Write(bytes.ToArray()); }
                    w.Write((int)d.indexFormat);
                    var indices = d.GetIndexData<byte>();
                    w.Write(indices.Length); w.Write(indices.ToArray());
                    w.Write(d.subMeshCount);
                    for (int s = 0; s < d.subMeshCount; s++)
                    {
                        var sub = d.GetSubMesh(s);
                        w.Write(sub.indexStart); w.Write(sub.indexCount); w.Write((int)sub.topology); w.Write(sub.baseVertex);
                        w.Write(sub.firstVertex); w.Write(sub.vertexCount); Vec(w, sub.bounds.center); Vec(w, sub.bounds.extents);
                    }
                }
                Vec(w, m.bounds.center); Vec(w, m.bounds.extents);
            }

            void WriteMaterial(BinaryWriter w, Material m)
            {
                var shader = m.shader;
                w.Write(m.name); w.Write(shader ? shader.name : ""); w.Write(m.rawRenderQueue); w.Write(m.enableInstancing); w.Write(m.doubleSidedGI);
                w.Write((int)m.globalIlluminationFlags);
                var keywords = m.shaderKeywords;
                Array.Sort(keywords, StringComparer.Ordinal);
                w.Write(keywords.Length); foreach (var k in keywords) w.Write(k);
                int count = shader ? shader.GetPropertyCount() : 0;
                w.Write(count);
                for (int i = 0; i < count; i++)
                {
                    string name = shader.GetPropertyName(i); var type = shader.GetPropertyType(i);
                    w.Write(name); w.Write((int)type);
                    switch (type)
                    {
                        case ShaderPropertyType.Color: Col(w, m.GetColor(name)); break;
                        case ShaderPropertyType.Vector: V4(w, m.GetVector(name)); break;
                        case ShaderPropertyType.Float: case ShaderPropertyType.Range: w.Write(m.GetFloat(name)); break;
                        case ShaderPropertyType.Int: w.Write(m.GetInteger(name)); break;
                        case ShaderPropertyType.Texture:
                            Ref(w, m.GetTexture(name));
                            V2(w, m.GetTextureScale(name)); V2(w, m.GetTextureOffset(name));
                            break;
                    }
                }
            }

            void WriteLayer(BinaryWriter w, TerrainLayer l)
            {
                w.Write(l.name); Ref(w, l.diffuseTexture); Ref(w, l.normalMapTexture); Ref(w, l.maskMapTexture);
                V2(w, l.tileSize); V2(w, l.tileOffset); Col(w, l.specular); w.Write(l.metallic); w.Write(l.smoothness); w.Write(l.normalScale);
                V4(w, l.diffuseRemapMin); V4(w, l.diffuseRemapMax); V4(w, l.maskMapRemapMin); V4(w, l.maskMapRemapMax);
            }

            void WriteTerrainData(BinaryWriter w, TerrainData d)
            {
                int n = d.heightmapResolution;
                w.Write(d.name); w.Write(n); Vec(w, d.size);
                var heights = d.GetHeights(0, 0, n, n);
                var packed = new byte[n * n * 2];
                for (int z = 0, k = 0; z < n; z++) for (int x = 0; x < n; x++, k += 2)
                {
                    int v = Mathf.Clamp(Mathf.RoundToInt(heights[z, x] * 65535f), 0, 65535);
                    packed[k] = (byte)v; packed[k + 1] = (byte)(v >> 8);
                }
                w.Write(packed);
                var layers = d.terrainLayers;
                w.Write(layers.Length); foreach (var l in layers) Ref(w, l);
                int a = d.alphamapResolution;
                w.Write(a);
                if (layers.Length > 0)
                {
                    var maps = d.GetAlphamaps(0, 0, a, a);
                    var weights = new byte[a * a * layers.Length];
                    for (int z = 0, k = 0; z < a; z++) for (int x = 0; x < a; x++) for (int l = 0; l < layers.Length; l++, k++)
                        weights[k] = (byte)Mathf.Clamp(Mathf.RoundToInt(maps[z, x, l] * 255f), 0, 255);
                    w.Write(weights);
                }
                w.Write(d.baseMapResolution);
                var prototypes = d.treePrototypes;
                w.Write(prototypes.Length);
                foreach (var p in prototypes) { Ref(w, p.prefab); w.Write(p.bendFactor); w.Write(p.navMeshLod); }
                var trees = d.treeInstances;
                w.Write(trees.Length);
                foreach (var t in trees)
                {
                    Vec(w, t.position); w.Write(t.widthScale); w.Write(t.heightScale); w.Write(t.rotation);
                    C32(w, t.color); C32(w, t.lightmapColor); w.Write(t.prototypeIndex);
                }
                w.Write(d.detailResolution); w.Write(d.detailResolutionPerPatch);
            }

            void WriteHomeSet(BinaryWriter w, GeoHomeSet s)
            {
                w.Write(s.name); w.Write(s.cullHeight); w.Write(s.homes.Count);
                foreach (var h in s.homes)
                {
                    w.Write(h.name ?? ""); var parts = h.parts ?? new GeoHomeSet.Part[0];
                    w.Write(parts.Length);
                    foreach (var p in parts)
                    {
                        w.Write(p.name ?? ""); Ref(w, p.mesh);
                        var materials = p.materials ?? new Material[0];
                        w.Write(materials.Length); foreach (var m in materials) Ref(w, m);
                        Vec(w, p.position); Vec(w, p.scale); Quat(w, p.rotation);
                    }
                }
            }
        }

        static void Vec(BinaryWriter w, Vector3 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        static void V2(BinaryWriter w, Vector2 v) { w.Write(v.x); w.Write(v.y); }
        static void V4(BinaryWriter w, Vector4 v) { w.Write(v.x); w.Write(v.y); w.Write(v.z); w.Write(v.w); }
        static void Quat(BinaryWriter w, Quaternion q) { w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w); }
        static void Col(BinaryWriter w, Color c) { w.Write(c.r); w.Write(c.g); w.Write(c.b); w.Write(c.a); }
        static void C32(BinaryWriter w, Color32 c) { w.Write(c.r); w.Write(c.g); w.Write(c.b); w.Write(c.a); }

        // ------------------------------------------------------------------------------------------------------------ reading

        // A tile read from its file (any thread): its objects as plain data, made into Unity objects by Build on the main thread.
        public sealed class TileData
        {
            public GeoTileHeader header;
            public GeoTileNavigation navigation;
            internal readonly List<object> objects = new List<object>();
            internal readonly List<NodeData> nodes = new List<NodeData>();
            public int ObjectCount => objects.Count;
            public int NodeCount => nodes.Count;
        }

        internal struct RefData { public byte kind; public int index; public string key; }
        internal sealed class TextureData { public string name; public int width, height, format, mips, wrapU, wrapV, filter, aniso; public bool linear; public float bias; public byte[] bytes; }
        internal sealed class MeshData
        {
            public string name; public int vertexCount; public VertexAttributeDescriptor[] attributes; public byte[][] streams; public int indexFormat; public byte[] indices;
            public SubMeshDescriptor[] submeshes; public Bounds bounds;
        }
        internal sealed class MaterialData
        {
            public string name, shader; public int queue, gi; public bool instancing, doubleSidedGI; public string[] keywords;
            public readonly List<(string name, ShaderPropertyType type, Vector4 value, RefData texture, Vector2 scale, Vector2 offset)> properties = new List<(string, ShaderPropertyType, Vector4, RefData, Vector2, Vector2)>();
        }
        internal sealed class LayerData { public string name; public RefData diffuse, normal, mask; public Vector2 tileSize, tileOffset; public Color specular; public float metallic, smoothness, normalScale; public Vector4 remapMin, remapMax, maskMin, maskMax; }
        internal sealed class TerrainDataData
        {
            public string name; public int resolution, alphamapResolution, baseMapResolution, detailResolution, detailPerPatch; public Vector3 size; public byte[] heights; public RefData[] layers; public byte[] weights;
            public (RefData prefab, float bend, int lod)[] prototypes; public TreeInstance[] trees;
        }
        internal sealed class HomeSetData { public string name; public float cull; public List<(string name, List<(string name, RefData mesh, RefData[] materials, Vector3 position, Vector3 scale, Quaternion rotation)> parts)> homes = new List<(string, List<(string, RefData, RefData[], Vector3, Vector3, Quaternion)>)>(); }
        internal sealed class NodeData { public string name; public int parent; public Vector3 position, scale; public Quaternion rotation; public bool active; public int layer; public readonly List<ComponentData> components = new List<ComponentData>(); }
        internal sealed class ComponentData { public byte kind; public RefData[] refs; public float[] numbers; public int[] ints; public bool[] flags; public string text, type; public (int node, int index)[][] lods; public float[][] lodNumbers; public List<GeoInstancedMeshes.Batch> batches; public RefData[] batchRefs; }

        public static TileData Read(byte[] file)
        {
            var data = new TileData { header = ReadHeader(file, out int offset) };
            using (var input = new MemoryStream(file, offset, file.Length - offset))
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            using (var r = new BinaryReader(deflate, Encoding.UTF8))
            {
                int objects = r.ReadInt32();
                for (int i = 0; i < objects; i++)
                {
                    byte kind = r.ReadByte();
                    switch (kind)
                    {
                        case KTexture: data.objects.Add(ReadTexture(r)); break;
                        case KMesh: data.objects.Add(ReadMesh(r)); break;
                        case KMaterial: data.objects.Add(ReadMaterial(r)); break;
                        case KTerrainLayer: data.objects.Add(ReadLayer(r)); break;
                        case KTerrainData: data.objects.Add(ReadTerrainData(r)); break;
                        case KHomeSet: data.objects.Add(ReadHomeSet(r)); break;
                        default: throw new InvalidDataException("Unknown object kind " + kind + " in a tile file.");
                    }
                }
                int nodes = r.ReadInt32();
                for (int i = 0; i < nodes; i++)
                {
                    var n = new NodeData { name = r.ReadString(), parent = r.ReadInt32(), position = RVec(r), rotation = RQuat(r), scale = RVec(r),
                        active = r.ReadBoolean(), layer = r.ReadInt32() };
                    int components = r.ReadInt32();
                    for (int c = 0; c < components; c++) n.components.Add(ReadComponent(r));
                    data.nodes.Add(n);
                }
                data.navigation = JsonUtility.FromJson<GeoTileNavigation>(r.ReadString());
            }
            return data;
        }

        static RefData RRef(BinaryReader r)
        {
            byte kind = r.ReadByte();
            return kind == 1 ? new RefData { kind = 1, index = r.ReadInt32() } : kind == 2 ? new RefData { kind = 2, key = r.ReadString() } : default;
        }

        static TextureData ReadTexture(BinaryReader r)
        {
            var t = new TextureData { name = r.ReadString(), width = r.ReadInt32(), height = r.ReadInt32(), format = r.ReadInt32(), mips = r.ReadInt32(),
                linear = r.ReadBoolean(), wrapU = r.ReadInt32(), wrapV = r.ReadInt32(), filter = r.ReadInt32(), aniso = r.ReadInt32(), bias = r.ReadSingle() };
            t.bytes = r.ReadBytes(r.ReadInt32());
            return t;
        }

        static MeshData ReadMesh(BinaryReader r)
        {
            var m = new MeshData { name = r.ReadString(), vertexCount = r.ReadInt32() };
            m.attributes = new VertexAttributeDescriptor[r.ReadInt32()];
            for (int i = 0; i < m.attributes.Length; i++)
                m.attributes[i] = new VertexAttributeDescriptor((VertexAttribute)r.ReadInt32(), (VertexAttributeFormat)r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
            m.streams = new byte[r.ReadInt32()][];
            for (int s = 0; s < m.streams.Length; s++) m.streams[s] = r.ReadBytes(r.ReadInt32());
            m.indexFormat = r.ReadInt32();
            m.indices = r.ReadBytes(r.ReadInt32());
            m.submeshes = new SubMeshDescriptor[r.ReadInt32()];
            for (int s = 0; s < m.submeshes.Length; s++)
            {
                var sub = new SubMeshDescriptor(r.ReadInt32(), r.ReadInt32(), (MeshTopology)r.ReadInt32()) { baseVertex = r.ReadInt32(), firstVertex = r.ReadInt32(), vertexCount = r.ReadInt32() };
                Vector3 c = RVec(r), e = RVec(r);
                sub.bounds = new Bounds(c, e * 2);
                m.submeshes[s] = sub;
            }
            Vector3 center = RVec(r), extents = RVec(r);
            m.bounds = new Bounds(center, extents * 2);
            return m;
        }

        static MaterialData ReadMaterial(BinaryReader r)
        {
            var m = new MaterialData { name = r.ReadString(), shader = r.ReadString(), queue = r.ReadInt32(), instancing = r.ReadBoolean(), doubleSidedGI = r.ReadBoolean(), gi = r.ReadInt32() };
            m.keywords = new string[r.ReadInt32()];
            for (int i = 0; i < m.keywords.Length; i++) m.keywords[i] = r.ReadString();
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string name = r.ReadString(); var type = (ShaderPropertyType)r.ReadInt32();
                Vector4 value = default; RefData texture = default; Vector2 scale = Vector2.one, offset = Vector2.zero;
                switch (type)
                {
                    case ShaderPropertyType.Color: value = new Vector4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); break;
                    case ShaderPropertyType.Vector: value = RV4(r); break;
                    case ShaderPropertyType.Float: case ShaderPropertyType.Range: value.x = r.ReadSingle(); break;
                    case ShaderPropertyType.Int: value.x = r.ReadInt32(); break;
                    case ShaderPropertyType.Texture: texture = RRef(r); scale = RV2(r); offset = RV2(r); break;
                }
                m.properties.Add((name, type, value, texture, scale, offset));
            }
            return m;
        }

        static LayerData ReadLayer(BinaryReader r) => new LayerData
        {
            name = r.ReadString(), diffuse = RRef(r), normal = RRef(r), mask = RRef(r), tileSize = RV2(r), tileOffset = RV2(r),
            specular = new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()), metallic = r.ReadSingle(), smoothness = r.ReadSingle(), normalScale = r.ReadSingle(),
            remapMin = RV4(r), remapMax = RV4(r), maskMin = RV4(r), maskMax = RV4(r)
        };

        static TerrainDataData ReadTerrainData(BinaryReader r)
        {
            var d = new TerrainDataData { name = r.ReadString(), resolution = r.ReadInt32(), size = RVec(r) };
            d.heights = r.ReadBytes(d.resolution * d.resolution * 2);
            d.layers = new RefData[r.ReadInt32()];
            for (int i = 0; i < d.layers.Length; i++) d.layers[i] = RRef(r);
            d.alphamapResolution = r.ReadInt32();
            if (d.layers.Length > 0) d.weights = r.ReadBytes(d.alphamapResolution * d.alphamapResolution * d.layers.Length);
            d.baseMapResolution = r.ReadInt32();
            d.prototypes = new (RefData, float, int)[r.ReadInt32()];
            for (int i = 0; i < d.prototypes.Length; i++) d.prototypes[i] = (RRef(r), r.ReadSingle(), r.ReadInt32());
            d.trees = new TreeInstance[r.ReadInt32()];
            for (int i = 0; i < d.trees.Length; i++)
                d.trees[i] = new TreeInstance { position = RVec(r), widthScale = r.ReadSingle(), heightScale = r.ReadSingle(), rotation = r.ReadSingle(),
                    color = RC32(r), lightmapColor = RC32(r), prototypeIndex = r.ReadInt32() };
            d.detailResolution = r.ReadInt32(); d.detailPerPatch = r.ReadInt32();
            return d;
        }

        static HomeSetData ReadHomeSet(BinaryReader r)
        {
            var s = new HomeSetData { name = r.ReadString(), cull = r.ReadSingle() };
            int homes = r.ReadInt32();
            for (int i = 0; i < homes; i++)
            {
                string name = r.ReadString(); int count = r.ReadInt32();
                var parts = new List<(string, RefData, RefData[], Vector3, Vector3, Quaternion)>(count);
                for (int p = 0; p < count; p++)
                {
                    string part = r.ReadString(); var mesh = RRef(r);
                    var materials = new RefData[r.ReadInt32()];
                    for (int m = 0; m < materials.Length; m++) materials[m] = RRef(r);
                    parts.Add((part, mesh, materials, RVec(r), RVec(r), RQuat(r)));
                }
                s.homes.Add((name, parts));
            }
            return s;
        }

        static ComponentData ReadComponent(BinaryReader r)
        {
            var c = new ComponentData { kind = r.ReadByte() };
            switch (c.kind)
            {
                case CMeshFilter: c.refs = new[] { RRef(r) }; break;
                case CMeshRenderer:
                    c.refs = new RefData[r.ReadInt32()];
                    for (int i = 0; i < c.refs.Length; i++) c.refs[i] = RRef(r);
                    c.ints = new[] { r.ReadInt32() }; c.flags = new[] { r.ReadBoolean(), r.ReadBoolean() };
                    c.ints = new[] { c.ints[0], r.ReadInt32(), r.ReadInt32() }; c.numbers = new[] { (float)r.ReadUInt32() }; c.flags = new[] { c.flags[0], c.flags[1], r.ReadBoolean() };
                    break;
                case CMeshCollider: c.refs = new[] { RRef(r) }; c.flags = new[] { r.ReadBoolean(), r.ReadBoolean(), r.ReadBoolean() }; break;
                case CBoxCollider: c.numbers = new[] { r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle() }; c.flags = new[] { r.ReadBoolean(), r.ReadBoolean() }; break;
                case CCapsuleCollider: c.numbers = new[] { r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle() }; c.ints = new[] { r.ReadInt32() }; c.flags = new[] { r.ReadBoolean(), r.ReadBoolean() }; break;
                case CSphereCollider: c.numbers = new[] { r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle() }; c.flags = new[] { r.ReadBoolean(), r.ReadBoolean() }; break;
                case CTerrain:
                    c.refs = new[] { RRef(r), RRef(r) };
                    c.numbers = new[] { r.ReadSingle(), r.ReadSingle() }; bool instanced = r.ReadBoolean();
                    c.numbers = new[] { c.numbers[0], c.numbers[1], r.ReadSingle(), r.ReadSingle(), r.ReadSingle() };
                    int fullLod = r.ReadInt32();
                    c.numbers = new[] { c.numbers[0], c.numbers[1], c.numbers[2], c.numbers[3], c.numbers[4], r.ReadSingle(), r.ReadSingle() };
                    c.ints = new[] { fullLod, r.ReadInt32(), r.ReadInt32() };
                    c.flags = new[] { instanced, r.ReadBoolean(), r.ReadBoolean(), r.ReadBoolean(), r.ReadBoolean() };
                    break;
                case CTerrainCollider: c.refs = new[] { RRef(r) }; c.flags = new[] { r.ReadBoolean() }; break;
                case CLODGroup:
                    c.ints = new[] { r.ReadInt32() }; c.flags = new[] { r.ReadBoolean() };
                    int lods = r.ReadInt32();
                    c.lods = new (int, int)[lods][]; c.lodNumbers = new float[lods][];
                    for (int i = 0; i < lods; i++)
                    {
                        c.lodNumbers[i] = new[] { r.ReadSingle(), r.ReadSingle() };
                        c.lods[i] = new (int, int)[r.ReadInt32()];
                        for (int k = 0; k < c.lods[i].Length; k++) c.lods[i][k] = (r.ReadInt32(), r.ReadInt32());
                    }
                    break;
                case CWorldRoot: case CCityChunk: c.text = r.ReadString(); break;
                case CHomeInstances: c.refs = new[] { RRef(r) }; break;
                case CInstanced:
                    c.numbers = new[] { r.ReadSingle() }; c.ints = new[] { r.ReadInt32(), r.ReadInt32() };
                    int batches = r.ReadInt32();
                    c.batches = new List<GeoInstancedMeshes.Batch>(batches); c.batchRefs = new RefData[batches * 2];
                    for (int i = 0; i < batches; i++)
                    {
                        c.batchRefs[i * 2] = RRef(r); int submesh = r.ReadInt32(); c.batchRefs[i * 2 + 1] = RRef(r);
                        Vector3 center = RVec(r), extents = RVec(r);
                        var matrices = new Matrix4x4[r.ReadInt32()];
                        for (int m = 0; m < matrices.Length; m++) { var x = new Matrix4x4(); for (int k = 0; k < 16; k++) x[k] = r.ReadSingle(); matrices[m] = x; }
                        c.batches.Add(new GeoInstancedMeshes.Batch { submesh = submesh, bounds = new Bounds(center, extents * 2), matrices = matrices });
                    }
                    break;
                case CJson: c.type = r.ReadString(); c.text = r.ReadString(); break;
                default: throw new InvalidDataException("Unknown component kind " + c.kind + " in a tile file.");
            }
            return c;
        }

        static Vector3 RVec(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        static Vector2 RV2(BinaryReader r) => new Vector2(r.ReadSingle(), r.ReadSingle());
        static Vector4 RV4(BinaryReader r) => new Vector4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        static Quaternion RQuat(BinaryReader r) => new Quaternion(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        static Color32 RC32(BinaryReader r) => new Color32(r.ReadByte(), r.ReadByte(), r.ReadByte(), r.ReadByte());

        // ------------------------------------------------------------------------------------------------------------ building

        // Makes the tile's objects (main thread), step by step: each call of Step does a part and says whether it is done, so a
        // caller can spread a tile over frames. created: every object made, for Unload.
        internal sealed class Builder
        {
            readonly TileData data;
            readonly Transform parent;
            readonly Object[] made;
            readonly GameObject[] gameObjects;
            readonly List<Object> created;
            readonly GeoTileLoadOptions options;
            int nextObject, nextNode, nextComponentNode;
            readonly List<(LODGroup group, ComponentData data)> lods = new List<(LODGroup, ComponentData)>();
            public readonly List<string> missing = new List<string>();
            public GameObject Root => gameObjects.Length > 0 ? gameObjects[0] : null;

            public Builder(TileData data, Transform parent, List<Object> created, GeoTileLoadOptions options)
            {
                this.data = data; this.parent = parent; this.created = created; this.options = options ?? new GeoTileLoadOptions();
                made = new Object[data.objects.Count];
                gameObjects = new GameObject[data.nodes.Count];
            }

            Object Resolve(RefData r)
            {
                if (r.kind == 1) return r.index >= 0 && r.index < made.Length ? made[r.index] : null;
                if (r.kind == 2)
                {
                    var found = GeoAssets.Resolve(r.key);
                    if (!found && missing.Count < 20 && !missing.Contains(r.key)) missing.Add(r.key);
                    return found;
                }
                return null;
            }

            T Get<T>(RefData r) where T : Object => Resolve(r) as T;

            // What the last step made (an object's kind and name, or a node), for a caller that times them.
            public string Last { get; private set; }

            // Does one piece of work; false when everything is made.
            public bool Step()
            {
                if (nextObject < data.objects.Count) { Last = data.objects[nextObject].GetType().Name.Replace("Data", "") + " " + Name(data.objects[nextObject]); made[nextObject] = MakeObject(data.objects[nextObject]); if (made[nextObject]) created.Add(made[nextObject]); nextObject++; return true; }
                if (nextNode < data.nodes.Count) { Last = "node " + data.nodes[nextNode].name; MakeNode(nextNode++); return true; }
                if (nextComponentNode < data.nodes.Count) { Last = "components of " + data.nodes[nextComponentNode].name; MakeComponents(nextComponentNode++); return true; }
                Last = "levels of detail";
                if (lods.Count > 0) { foreach (var (group, d) in lods) SetLods(group, d); lods.Clear(); return true; }
                return false;
            }

            static string Name(object o) => o is TextureData t ? t.name : o is MeshData m ? m.name : o is MaterialData x ? x.name : o is LayerData l ? l.name :
                o is TerrainDataData d ? d.name : o is HomeSetData h ? h.name : "";

            Object MakeObject(object o)
            {
                switch (o)
                {
                    case TextureData t:
                    {
                        var texture = new Texture2D(t.width, t.height, (TextureFormat)t.format, t.mips, t.linear)
                            { name = t.name, wrapModeU = (TextureWrapMode)t.wrapU, wrapModeV = (TextureWrapMode)t.wrapV, filterMode = (FilterMode)t.filter, anisoLevel = t.aniso, mipMapBias = t.bias };
                        texture.LoadRawTextureData(t.bytes);
                        texture.Apply(false, true);
                        t.bytes = null;
                        return texture;
                    }
                    case MeshData m:
                    {
                        var mesh = new Mesh { name = m.name };
                        var array = Mesh.AllocateWritableMeshData(1);
                        var d = array[0];
                        d.SetVertexBufferParams(m.vertexCount, m.attributes);
                        for (int s = 0; s < m.streams.Length; s++) d.GetVertexData<byte>(s).CopyFrom(m.streams[s]);
                        var format = (IndexFormat)m.indexFormat;
                        d.SetIndexBufferParams(m.indices.Length / (format == IndexFormat.UInt16 ? 2 : 4), format);
                        d.GetIndexData<byte>().CopyFrom(m.indices);
                        d.subMeshCount = m.submeshes.Length;
                        const MeshUpdateFlags flags = MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers;
                        for (int s = 0; s < m.submeshes.Length; s++) d.SetSubMesh(s, m.submeshes[s], flags);
                        Mesh.ApplyAndDisposeWritableMeshData(array, mesh, flags);
                        mesh.bounds = m.bounds;
                        m.streams = null; m.indices = null;
                        return mesh;
                    }
                    case MaterialData m:
                    {
                        var shader = GeoAssets.Shader(m.shader);
                        if (!shader) { if (missing.Count < 20) missing.Add("shader " + m.shader); shader = GeoAssets.Shader("Universal Render Pipeline/Lit"); }
                        var material = new Material(shader) { name = m.name, enableInstancing = m.instancing, doubleSidedGI = m.doubleSidedGI, globalIlluminationFlags = (MaterialGlobalIlluminationFlags)m.gi };
                        foreach (var p in m.properties)
                        {
                            if (!material.HasProperty(p.name)) continue;
                            switch (p.type)
                            {
                                case ShaderPropertyType.Color: material.SetColor(p.name, new Color(p.value.x, p.value.y, p.value.z, p.value.w)); break;
                                case ShaderPropertyType.Vector: material.SetVector(p.name, p.value); break;
                                case ShaderPropertyType.Float: case ShaderPropertyType.Range: material.SetFloat(p.name, p.value.x); break;
                                case ShaderPropertyType.Int: material.SetInteger(p.name, (int)p.value.x); break;
                                case ShaderPropertyType.Texture:
                                    material.SetTexture(p.name, Get<Texture>(p.texture));
                                    material.SetTextureScale(p.name, p.scale); material.SetTextureOffset(p.name, p.offset);
                                    break;
                            }
                        }
                        material.shaderKeywords = m.keywords;
                        if (m.queue >= 0) material.renderQueue = m.queue;
                        return material;
                    }
                    case LayerData l:
                        return new TerrainLayer
                        {
                            name = l.name, diffuseTexture = Get<Texture2D>(l.diffuse), normalMapTexture = Get<Texture2D>(l.normal), maskMapTexture = Get<Texture2D>(l.mask),
                            tileSize = l.tileSize, tileOffset = l.tileOffset, specular = l.specular, metallic = l.metallic, smoothness = l.smoothness, normalScale = l.normalScale,
                            diffuseRemapMin = l.remapMin, diffuseRemapMax = l.remapMax, maskMapRemapMin = l.maskMin, maskMapRemapMax = l.maskMax
                        };
                    case TerrainDataData d:
                    {
                        var terrain = new TerrainData { name = d.name };
                        terrain.heightmapResolution = d.resolution;
                        terrain.size = d.size;
                        int n = d.resolution;
                        var heights = new float[n, n];
                        for (int z = 0, k = 0; z < n; z++) for (int x = 0; x < n; x++, k += 2) heights[z, x] = (d.heights[k] | d.heights[k + 1] << 8) / 65535f;
                        terrain.SetHeights(0, 0, heights);
                        terrain.size = d.size;
                        if (d.layers.Length > 0)
                        {
                            var layers = new TerrainLayer[d.layers.Length];
                            for (int i = 0; i < layers.Length; i++) layers[i] = Get<TerrainLayer>(d.layers[i]);
                            terrain.alphamapResolution = d.alphamapResolution;
                            terrain.terrainLayers = layers;
                            int a = d.alphamapResolution;
                            var maps = new float[a, a, layers.Length];
                            for (int z = 0, k = 0; z < a; z++) for (int x = 0; x < a; x++) for (int l = 0; l < layers.Length; l++, k++) maps[z, x, l] = d.weights[k] / 255f;
                            terrain.SetAlphamaps(0, 0, maps);
                        }
                        terrain.baseMapResolution = d.baseMapResolution;
                        if (d.prototypes.Length > 0)
                        {
                            var prototypes = new TreePrototype[d.prototypes.Length];
                            for (int i = 0; i < prototypes.Length; i++) prototypes[i] = new TreePrototype { prefab = Get<GameObject>(d.prototypes[i].prefab), bendFactor = d.prototypes[i].bend, navMeshLod = d.prototypes[i].lod };
                            terrain.treePrototypes = prototypes;
                            terrain.SetTreeInstances(d.trees, false);
                        }
                        if (d.detailResolution > 0) terrain.SetDetailResolution(d.detailResolution, Mathf.Max(8, d.detailPerPatch));
                        d.heights = null; d.weights = null;
                        return terrain;
                    }
                    case HomeSetData s:
                    {
                        var set = ScriptableObject.CreateInstance<GeoHomeSet>();
                        set.name = s.name; set.cullHeight = s.cull;
                        foreach (var (name, parts) in s.homes)
                        {
                            var home = new GeoHomeSet.Home { name = name, parts = new GeoHomeSet.Part[parts.Count] };
                            for (int i = 0; i < parts.Count; i++)
                            {
                                var p = parts[i];
                                var materials = new Material[p.materials.Length];
                                for (int m = 0; m < materials.Length; m++) materials[m] = Get<Material>(p.materials[m]);
                                home.parts[i] = new GeoHomeSet.Part { name = p.name, mesh = Get<Mesh>(p.mesh), materials = materials, position = p.position, scale = p.scale, rotation = p.rotation };
                            }
                            set.homes.Add(home);
                        }
                        return set;
                    }
                }
                return null;
            }

            void MakeNode(int i)
            {
                var n = data.nodes[i];
                var go = new GameObject(n.name);
                // made inactive and switched on when everything is in place (components wake up once)
                if (i == 0) go.SetActive(false);
                var t = go.transform;
                t.SetParent(n.parent >= 0 ? gameObjects[n.parent].transform : parent, false);
                t.SetLocalPositionAndRotation(n.position, n.rotation);
                t.localScale = n.scale;
                go.layer = options.layer >= 0 ? options.layer : n.layer;
                if (i != 0 && !n.active) go.SetActive(false);
                gameObjects[i] = go;
            }

            void MakeComponents(int i)
            {
                var n = data.nodes[i];
                var go = gameObjects[i];
                if (i == 0 && !n.active) { }
                foreach (var c in n.components) MakeComponent(go, c);
            }

            void MakeComponent(GameObject go, ComponentData c)
            {
                switch (c.kind)
                {
                    case CMeshFilter: go.AddComponent<MeshFilter>().sharedMesh = Get<Mesh>(c.refs[0]); break;
                    case CMeshRenderer:
                    {
                        var r = go.AddComponent<MeshRenderer>();
                        var materials = new Material[c.refs.Length];
                        for (int i = 0; i < materials.Length; i++) materials[i] = Get<Material>(c.refs[i]);
                        r.sharedMaterials = materials;
                        r.shadowCastingMode = (ShadowCastingMode)c.ints[0]; r.receiveShadows = c.flags[0]; r.enabled = c.flags[1];
                        r.lightProbeUsage = (LightProbeUsage)c.ints[1]; r.reflectionProbeUsage = (ReflectionProbeUsage)c.ints[2];
                        r.renderingLayerMask = (uint)c.numbers[0]; r.staticShadowCaster = c.flags[2];
                        break;
                    }
                    case CMeshCollider:
                        if (!options.colliders) break;
                        var mc = go.AddComponent<MeshCollider>(); mc.convex = c.flags[0]; mc.sharedMesh = Get<Mesh>(c.refs[0]); mc.enabled = c.flags[1]; mc.isTrigger = c.flags[2];
                        break;
                    case CBoxCollider:
                        if (!options.colliders) break;
                        var b = go.AddComponent<BoxCollider>(); b.center = new Vector3(c.numbers[0], c.numbers[1], c.numbers[2]); b.size = new Vector3(c.numbers[3], c.numbers[4], c.numbers[5]); b.enabled = c.flags[0]; b.isTrigger = c.flags[1];
                        break;
                    case CCapsuleCollider:
                        if (!options.colliders) break;
                        var cc = go.AddComponent<CapsuleCollider>(); cc.center = new Vector3(c.numbers[0], c.numbers[1], c.numbers[2]); cc.radius = c.numbers[3]; cc.height = c.numbers[4]; cc.direction = c.ints[0]; cc.enabled = c.flags[0]; cc.isTrigger = c.flags[1];
                        break;
                    case CSphereCollider:
                        if (!options.colliders) break;
                        var sc = go.AddComponent<SphereCollider>(); sc.center = new Vector3(c.numbers[0], c.numbers[1], c.numbers[2]); sc.radius = c.numbers[3]; sc.enabled = c.flags[0]; sc.isTrigger = c.flags[1];
                        break;
                    case CTerrain:
                    {
                        var t = go.AddComponent<Terrain>();
                        t.terrainData = Get<TerrainData>(c.refs[0]);
                        t.materialTemplate = Get<Material>(c.refs[1]);
                        t.heightmapPixelError = c.numbers[0]; t.basemapDistance = c.numbers[1]; t.drawInstanced = c.flags[0]; t.treeDistance = c.numbers[2];
                        t.treeBillboardDistance = c.numbers[3]; t.treeCrossFadeLength = c.numbers[4]; t.treeMaximumFullLODCount = c.ints[0];
                        t.detailObjectDistance = c.numbers[5]; t.detailObjectDensity = c.numbers[6]; t.shadowCastingMode = (ShadowCastingMode)c.ints[1]; t.groupingID = c.ints[2];
                        t.allowAutoConnect = c.flags[1]; t.drawTreesAndFoliage = c.flags[2]; t.drawHeightmap = c.flags[3]; t.enabled = c.flags[4];
                        break;
                    }
                    case CTerrainCollider:
                        if (!options.colliders) break;
                        var tc = go.AddComponent<TerrainCollider>(); tc.terrainData = Get<TerrainData>(c.refs[0]); tc.enabled = c.flags[0];
                        break;
                    case CLODGroup:
                    {
                        var g = go.AddComponent<LODGroup>();
                        g.fadeMode = (LODFadeMode)c.ints[0]; g.animateCrossFading = c.flags[0];
                        lods.Add((g, c));
                        break;
                    }
                    case CWorldRoot:
                    {
                        // its terrain is set when every object is made (Activate): the children have no components yet
                        JsonUtility.FromJson<GeoWorldRootData>(c.text).To(go.AddComponent<GeoWorldRoot>());
                        break;
                    }
                    case CCityChunk: JsonUtility.FromJsonOverwrite(c.text, go.AddComponent<GeoCityChunk>()); break;
                    case CHomeInstances:
                    {
                        // the component makes its homes when it is switched on: the set first
                        bool active = go.activeSelf; if (active) go.SetActive(false);
                        go.AddComponent<GeoHomeInstances>().set = Get<GeoHomeSet>(c.refs[0]);
                        if (active) go.SetActive(true);
                        break;
                    }
                    case CInstanced:
                    {
                        var im = go.AddComponent<GeoInstancedMeshes>();
                        im.drawDistance = c.numbers[0]; im.shadows = (ShadowCastingMode)c.ints[0]; im.count = c.ints[1];
                        for (int i = 0; i < c.batches.Count; i++) { c.batches[i].mesh = Get<Mesh>(c.batchRefs[i * 2]); c.batches[i].material = Get<Material>(c.batchRefs[i * 2 + 1]); }
                        im.batches = c.batches;
                        break;
                    }
                    case CJson:
                    {
                        var type = Type.GetType(c.type);
                        if (type == null || !typeof(Component).IsAssignableFrom(type)) { if (missing.Count < 20) missing.Add("component " + c.type); break; }
                        JsonUtility.FromJsonOverwrite(c.text, go.AddComponent(type));
                        break;
                    }
                }
            }

            void SetLods(LODGroup group, ComponentData c)
            {
                var lods = new LOD[c.lods.Length];
                for (int i = 0; i < lods.Length; i++)
                {
                    var renderers = new List<Renderer>();
                    foreach (var (node, index) in c.lods[i])
                    {
                        if (node < 0 || node >= gameObjects.Length) continue;
                        var all = gameObjects[node].GetComponents<Renderer>();
                        if (index >= 0 && index < all.Length) renderers.Add(all[index]);
                    }
                    lods[i] = new LOD(c.lodNumbers[i][0], renderers.ToArray()) { fadeTransitionWidth = c.lodNumbers[i][1] };
                }
                group.SetLODs(lods);
                group.RecalculateBounds();
            }

            public void Activate()
            {
                var root = Root;
                var world = root ? root.GetComponent<GeoWorldRoot>() : null;
                if (world && !world.terrain) world.terrain = root.GetComponentInChildren<Terrain>(true);
                if (root && data.nodes[0].active) root.SetActive(true);
            }
        }
    }

    // What a package keeps of a tile's GeoWorldRoot: everything but references to objects (the terrain is found again under it,
    // the libraries are the registry's) and the paths of the export it was made from on the computer that made it.
    [Serializable]
    public sealed class GeoWorldRootData
    {
        public GenerationSettings settings;
        public int roadCount, riverCount, buildingCount, waterCount, treeCount, junctionCount, propCount;
        public string report;
        public bool tile;
        public Vector2Int tileIndex;
        public Rect tileArea;
        public float tileSize, tileBaseY, tileHeightRange;
        public int tileParts;

        public static GeoWorldRootData From(GeoWorldRoot r)
        {
            var settings = JsonUtility.FromJson<GenerationSettings>(JsonUtility.ToJson(r.settings));
            settings.ruinLibrary = null; settings.homeLibrary = null; settings.propCatalog = null; settings.treePrefabs = new GameObject[0];
            return new GeoWorldRootData
            {
                settings = settings, roadCount = r.roadCount, riverCount = r.riverCount, buildingCount = r.buildingCount, waterCount = r.waterCount,
                treeCount = r.treeCount, junctionCount = r.junctionCount, propCount = r.propCount, report = r.report, tile = r.tile, tileIndex = r.tileIndex,
                tileArea = r.tileArea, tileSize = r.tileSize, tileBaseY = r.tileBaseY, tileHeightRange = r.tileHeightRange, tileParts = r.tileParts
            };
        }

        public void To(GeoWorldRoot r)
        {
            r.settings = settings ?? new GenerationSettings();
            r.settings.homeLibrary = GeoAssets.HomeLibrary; r.settings.ruinLibrary = GeoAssets.RuinLibrary;
            r.roadCount = roadCount; r.riverCount = riverCount; r.buildingCount = buildingCount; r.waterCount = waterCount; r.treeCount = treeCount;
            r.junctionCount = junctionCount; r.propCount = propCount; r.report = report; r.tile = tile; r.tileIndex = tileIndex; r.tileArea = tileArea;
            r.tileSize = tileSize; r.tileBaseY = tileBaseY; r.tileHeightRange = tileHeightRange; r.tileParts = tileParts;
        }
    }
}
