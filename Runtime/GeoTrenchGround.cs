using UnityEngine;

namespace GeoWorld
{
    // The trenches of a tile (GeoTrenchMesh, under its "Trenches" group): their floor, spoil and apron (sub-mesh 0, and again
    // sub-meshes 2.. for a terrain that draws its layers in more than one pass) are drawn with the tile's own terrain
    // material, its splat textures set on copies of it, so they look like the ground around them: the satellite, and whatever
    // else the project's terrain material does (wet ground, seasons, snow). In play the copies follow the terrain's material
    // (a project that gives its terrains another one when they load, a wet one, gets the same on the trenches) and its data,
    // and are made again when their layer settings are lost: those are no properties of a terrain shader (Unity sets them on
    // the terrain's own draws), so a shader reload in the editor drops them from a copy, whose splat then draws black — the
    // spoil stood as a dark band beside the lit field. No fields: a package carries the component as it is.
    [DisallowMultipleComponent]
    public class GeoTrenchGround : MonoBehaviour
    {
        static readonly int RemapId = Shader.PropertyToID("_DiffuseRemapScale0");

        Terrain terrain;
        TerrainData data;
        Material source;
        Material[] made;
        Vector4 remap;
        int lookups;

        void Start() => Bind();

        void LateUpdate()
        {
            if (!terrain) { if (lookups < 600) Bind(); return; }
            if (terrain.materialTemplate != source || terrain.terrainData != data || Lost()) Bind();
        }

        // the first layer's colour scale is gone from the copy (a shader reload)
        bool Lost() => made != null && made.Length > 0 && made[0] && made[0].GetVector(RemapId) != remap;

        void OnDestroy() => Release(made);

        static void Release(Material[] materials)
        {
            if (materials == null) return;
            foreach (var m in materials) if (m) Destroy(m);
        }

        void Bind()
        {
            lookups++;
            if (!terrain)
            {
                var root = GetComponentInParent<GeoWorldRoot>();
                terrain = root ? root.terrain : null;
                if (!terrain) return;
            }
            source = terrain.materialTemplate;
            data = terrain.terrainData;
            if (!source || !data) return;
            var passes = GroundMaterials(terrain, source);
            if (passes == null) return;
            foreach (var m in passes) m.name = source.name + " (trenches)";
            foreach (var r in GetComponentsInChildren<MeshRenderer>())
            {
                var filter = r.GetComponent<MeshFilter>();
                var mesh = filter ? filter.sharedMesh : null;
                var materials = r.sharedMaterials;
                if (!mesh || materials.Length == 0) continue;
                // sub-mesh 0 the first pass, 1 the walls (kept), 2.. the further passes, as many as both have
                var walls = materials.Length > 1 ? materials[1] : null;
                materials = new Material[Mathf.Max(1, Mathf.Min(mesh.subMeshCount, passes.Length + 1))];
                materials[0] = passes[0];
                if (materials.Length > 1) materials[1] = walls;
                for (int i = 2; i < materials.Length; i++) materials[i] = passes[i - 1];
                r.sharedMaterials = materials;
            }
            Release(made);
            made = passes;
            remap = passes[0].GetVector(RemapId);
        }

        // Copies of a terrain material that draw a mesh (texture coordinates: the terrain's, 0-1 over it) as the terrain draws
        // itself: instancing off (an instanced terrain shader places its vertices from the heightmap), and the per-terrain data
        // Unity sets only for the terrain's own draws on it: control maps, layers and their settings. One for every four of the
        // terrain's layers: the first draws layers 0-3, the others (the shader's AddPassShader) add 4-7, 8-11 and so on over it,
        // as the terrain's own passes do. Null without a terrain, its data or a material.
        public static Material[] GroundMaterials(Terrain terrain, Material from)
        {
            if (!terrain || !from || !terrain.terrainData) return null;
            var data = terrain.terrainData;
            var layers = data.terrainLayers;
            var controls = data.alphamapTextures;
            var add = from.shader ? from.shader.GetDependency("AddPassShader") : null;
            int passes = add ? Mathf.Clamp(Mathf.CeilToInt(layers.Length / 4f), 1, Mathf.Max(1, controls.Length)) : 1;
            var result = new Material[passes];
            for (int pass = 0; pass < passes; pass++)
            {
                var material = new Material(from) { enableInstancing = false };
                if (pass > 0) material.shader = add;
                material.DisableKeyword("_TERRAIN_INSTANCED_PERPIXEL_NORMAL");
                // a terrain material that knows trench soil (the environment layer's wet terrain) draws the mesh's own soil by
                // UV1; the first pass only (the others add their layers under it)
                if (pass == 0) material.EnableKeyword("_TRENCH_GROUND"); else material.DisableKeyword("_TRENCH_GROUND");
                if (pass < controls.Length && material.HasProperty("_Control")) material.SetTexture("_Control", controls[pass]);
                material.SetFloat("_NumLayersCount", layers.Length);
                bool normals = false, masks = false;
                for (int i = 0; i < 4; i++)
                {
                    int index = pass * 4 + i;
                    var layer = index < layers.Length ? layers[index] : null;
                    if (!layer) continue;
                    material.SetTexture("_Splat" + i, layer.diffuseTexture);
                    material.SetVector("_Splat" + i + "_ST", new Vector4(data.size.x / Mathf.Max(0.01f, layer.tileSize.x), data.size.z / Mathf.Max(0.01f, layer.tileSize.y),
                        layer.tileOffset.x / Mathf.Max(0.01f, layer.tileSize.x), layer.tileOffset.y / Mathf.Max(0.01f, layer.tileSize.y)));
                    if (layer.normalMapTexture) { material.SetTexture("_Normal" + i, layer.normalMapTexture); normals = true; }
                    material.SetFloat("_NormalScale" + i, layer.normalScale);
                    if (layer.maskMapTexture) { material.SetTexture("_Mask" + i, layer.maskMapTexture); masks = true; }
                    material.SetFloat("_LayerHasMask" + i, layer.maskMapTexture ? 1 : 0);
                    material.SetFloat("_Metallic" + i, layer.metallic);
                    material.SetFloat("_Smoothness" + i, layer.smoothness);
                    material.SetVector("_DiffuseRemapScale" + i, layer.diffuseRemapMax);
                    material.SetVector("_MaskMapRemapOffset" + i, layer.maskMapRemapMin);
                    material.SetVector("_MaskMapRemapScale" + i, layer.maskMapRemapMax - layer.maskMapRemapMin);
                }
                if (normals) material.EnableKeyword("_NORMALMAP"); else material.DisableKeyword("_NORMALMAP");
                if (masks) material.EnableKeyword("_MASKMAP"); else material.DisableKeyword("_MASKMAP");
                result[pass] = material;
            }
            return result;
        }

        // the first of them (layers 0-3)
        public static Material GroundMaterial(Terrain terrain, Material from)
        {
            var passes = GroundMaterials(terrain, from);
            if (passes == null) return null;
            for (int i = 1; i < passes.Length; i++) DestroyImmediate(passes[i]);
            return passes[0];
        }
    }
}
