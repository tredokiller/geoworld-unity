using System.Collections.Generic;
using GeoWorld;
using GeoWorld.Editor;
using UnityEngine;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Тайлы пакета мира GeoWorld (мир, сгенерированный плеером) — как тайлы мира из редактора. Деревья пакет рисует
    /// экземплярами своей простой модели (GeoInstancedMeshes, «Geo tree» 7 × 4 м), а слой окружения держится на деревьях
    /// террейна под Nature Renderer: породы по месту, их сезоны и переход LOD, лесная подстилка, опад и сухостой под ними.
    /// При загрузке тайла (GeoWorldPackage.TileLoaded) его деревья становятся деревьями террейна — то же место, поворот,
    /// ширина и высота, — высоты берут по карте земель пакета (GeoTreeHeights: лес 15–24 м, ряды 8–14, сады, посёлки),
    /// виды — по месту (TileTreeSpecies.Process), рисует их Nature Renderer (GeoNatureRenderer.Ensure), а экземпляры пакета
    /// выключаются. Только при слое окружения в сцене (WorldEnvironment): без него тайлы пакета остаются, как есть.
    /// </summary>
    public static class PackageTiles
    {
        private const string TreeMesh = "Geo tree";
        // модель дерева пакета (GeoTerrainTrees): 7 м высотой, 4 м в ширину
        private const float ModelHeight = 7f;

        public static int ConvertedTrees { get; private set; }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
#endif
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Register()
        {
            ConvertedTrees = 0;
            GeoWorldPackage.TileLoaded -= OnTileLoaded;
            GeoWorldPackage.TileLoaded += OnTileLoaded;
        }

        private static void OnTileLoaded(GeoWorldPackage package, GeoWorldTileHandle handle)
        {
            WorldEnvironment environment = WorldEnvironment.Instance;
            Terrain terrain = handle.Terrain;

            if (environment == null || terrain == null || !Application.isPlaying)
                return;

            // дороги на земле рисует слой окружения (RoadMap по осям WorldMapData): меши дорог и перекрёстков пакета
            // ложились поверх них вторым полотном; мосты и насыпи остаются
            if (environment.GetComponent<RoadMap>() != null && RoadAreas.Instance != null)
                HideGroundRoads(handle.Root);

            GeoInstancedMeshes trees = FindTrees(handle.Root);

            if (trees == null || !PlantOnTerrain(trees, terrain))
                return;

            trees.gameObject.SetActive(false);

            if (environment.TreeSpecies != null)
                environment.TreeSpecies.Process(terrain);

            // Nature Renderer рисует деревья, с которыми начал: уже стоящий — заново
            if (GeoWorld.GeoNatureRenderer.Of(terrain) != null)
                GeoNatureRenderer.Refresh(terrain);
            else
                GeoNatureRenderer.Ensure(terrain);
        }

        // Куски дорог и перекрёстков тайла (GeoWorldBuild: «Roads x_z | материал», «Junctions x_z | материал» в группе дорог)
        public static int HideGroundRoads(GameObject root)
        {
            int hidden = 0;

            foreach (Transform group in root.GetComponentsInChildren<Transform>(true))
            {
                if (!group.name.StartsWith("Roads - ", System.StringComparison.Ordinal))
                    continue;

                foreach (Transform piece in group)
                {
                    if (!piece.name.StartsWith("Roads ", System.StringComparison.Ordinal) && !piece.name.StartsWith("Junctions ", System.StringComparison.Ordinal))
                        continue;

                    if (piece.TryGetComponent(out MeshRenderer renderer) && renderer.enabled)
                    {
                        renderer.enabled = false;
                        hidden++;
                    }
                }
            }

            return hidden;
        }

        private static GeoInstancedMeshes FindTrees(GameObject root)
        {
            foreach (GeoInstancedMeshes drawer in root.GetComponentsInChildren<GeoInstancedMeshes>(true))
                foreach (GeoInstancedMeshes.Batch batch in drawer.batches)
                    if (batch.mesh != null && batch.mesh.name == TreeMesh)
                        return drawer;

            return null;
        }

        // Деревья экземпляров — деревьями террейна (прототип — модель пакета: виды подставит TileTreeSpecies), с высотами по
        // карте земель. false — деревьев нет.
        private static bool PlantOnTerrain(GeoInstancedMeshes drawer, Terrain terrain)
        {
            GameObject[] prefabs = GeoTerrainTrees.Prefabs();
            TerrainData data = terrain.terrainData;
            Vector3 origin = terrain.transform.position;
            Vector3 size = data.size;
            float modelHeight = GeoTreeHeights.PrefabHeight(prefabs[0]);
            float heightScale = modelHeight > 0.1f ? ModelHeight / modelHeight : 1f;
            List<TreeInstance> instances = new();

            foreach (GeoInstancedMeshes.Batch batch in drawer.batches)
            {
                // ствол и крона — два подмеша с одними матрицами: дерево — по стволу
                if (batch.mesh == null || batch.mesh.name != TreeMesh || batch.submesh != 0)
                    continue;

                foreach (Matrix4x4 matrix in batch.matrices)
                {
                    Vector3 position = matrix.GetColumn(3);
                    Vector3 at = new((position.x - origin.x) / size.x, (position.y - origin.y) / size.y, (position.z - origin.z) / size.z);

                    if (at.x < 0f || at.x > 1f || at.z < 0f || at.z > 1f)
                        continue;

                    Vector3 forward = matrix.GetColumn(2);
                    float width = ((Vector3)matrix.GetColumn(0)).magnitude;
                    float height = ((Vector3)matrix.GetColumn(1)).magnitude;
                    instances.Add(new TreeInstance
                    {
                        position = at, prototypeIndex = 0, widthScale = width, heightScale = height * heightScale,
                        rotation = Mathf.Atan2(forward.x, forward.z), color = Color.white, lightmapColor = Color.white
                    });
                }
            }

            if (instances.Count == 0)
                return false;

            TreeInstance[] trees = instances.ToArray();
            TreePrototype[] prototypes = { new() { prefab = prefabs[0] } };
            GeoLandCover cover = GeoLandCover.At(origin.x + size.x * 0.5f, origin.z + size.z * 0.5f);

            // как GeoTerrainTreeLoader у тайлов мира из редактора: высота — по тому, в чём дерево стоит
            if (cover != null && cover.treeHeights != null && cover.treeHeights.enabled)
                cover.treeHeights.Apply(cover, trees, origin, size, GeoTreeHeights.PrototypeHeights(prototypes));

            data.treePrototypes = prototypes;
            data.SetTreeInstances(trees, false);
            terrain.drawTreesAndFoliage = false;
            ConvertedTrees += trees.Length;
            return true;
        }
    }
}
