using System.Collections.Generic;
using UnityEngine;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Данные карты одного мира GeoWorld для слоя окружения: дороги (RoadAreas — покрытие дорог, трава у дорог, точка
    /// взлёта), поля (FieldAreas — рядки и колеи, FieldRows.hlsl) и контуры лесов с типом листвы (TreeLeafAreas — породы
    /// деревьев, TileTreeSpecies). Висит на объекте мира (GeoTiledWorld) рядом с его GeoLandCover; ассеты лежат в папке мира
    /// (&lt;папка мира&gt;/Environment), их запекает WorldMapBaker (после генерации тайлов, кнопкой в инспекторе этого
    /// компонента и Tools/Environment/Bake World Map Data). Раньше они были одним харьковским комплектом в Resources пакета, и
    /// на другой карте харьковские дороги, поля и леса легли бы по тем же локальным координатам. Мир без своих данных — без
    /// дорог по осям (трава уходит по клеткам дорог карты земель), рядков полей и типа листвы; чужих он не берёт.
    /// </summary>
    [DisallowMultipleComponent, ExecuteAlways]
    public sealed class WorldMapData : MonoBehaviour
    {
        public RoadAreas roads;
        public FieldAreas fields;
        public TreeLeafAreas leafAreas;
        public CraterField craters;
        [HideInInspector] public string report;

        private static readonly List<WorldMapData> Active = new();

        /// <summary>Дороги загруженного мира; null — у загруженных миров их нет.</summary>
        public static RoadAreas Roads
        {
            get
            {
                foreach (WorldMapData data in Active)
                    if (data && data.roads)
                        return data.roads;

                return null;
            }
        }

        public static FieldAreas Fields
        {
            get
            {
                foreach (WorldMapData data in Active)
                    if (data && data.fields)
                        return data.fields;

                return null;
            }
        }

        public static TreeLeafAreas LeafAreas
        {
            get
            {
                foreach (WorldMapData data in Active)
                    if (data && data.leafAreas)
                        return data.leafAreas;

                return null;
            }
        }

        /// <summary>Воронки загруженного мира (война); null — у него их нет.</summary>
        public static CraterField Craters
        {
            get
            {
                foreach (WorldMapData data in Active)
                    if (data && data.craters)
                        return data.craters;

                return null;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetState() => Active.Clear();

        private void OnEnable()
        {
            if (!Active.Contains(this))
                Active.Add(this);
        }

        private void OnDisable() => Active.Remove(this);
    }
}
