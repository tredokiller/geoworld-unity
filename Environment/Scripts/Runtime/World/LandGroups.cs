using _Game.Scripts.Locations.FlyPoints;
using GeoWorld;
using UnityEngine;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Земля карты GeoWorld (GeoLandCover, ~10 м) для травы: класс → группа (луг, поле, лес...), край поля и берег у
    /// воды, множитель записи профиля в узлах решётки классов и билинейная выборка между узлами. Общее для травы по
    /// карте (GroundDressing, в фоновом потоке — поэтому только массивы) и зоны точки взлёта.
    /// </summary>
    public static class LandGroups
    {
        // в лесу листва лежит не только у деревьев тайла, а почти везде
        public const float ForestLitterCover = 0.6f;
        // берег у воды наполовину растёт как болото: камыш и осока полосой вдоль реки и пруда
        private const float BankWetland = 0.6f;

        public static FlyPointLandGroup Of(GeoLandClass land) => land switch
        {
            GeoLandClass.Cropland => FlyPointLandGroup.Field,
            GeoLandClass.Forest => FlyPointLandGroup.Forest,
            GeoLandClass.Wetland or GeoLandClass.Water => FlyPointLandGroup.Wetland,
            GeoLandClass.Urban or GeoLandClass.Building => FlyPointLandGroup.Settlement,
            GeoLandClass.DirtRoad or GeoLandClass.Gravel or GeoLandClass.Asphalt or GeoLandClass.Paving or GeoLandClass.Rail
                => FlyPointLandGroup.Track,
            GeoLandClass.Sand or GeoLandClass.Bare or GeoLandClass.Rock or GeoLandClass.Path or GeoLandClass.Snow
                => FlyPointLandGroup.Barren,
            // луг, кусты, сад; где карта молчит — тоже как луг
            _ => FlyPointLandGroup.Meadow
        };

        // Дороги любого покрытия, тропинки и рельсы: на них ничего не растёт и ничего не лежит — ни травы, ни цветов, ни
        // листвы, ни кустов (дорога в траве выглядела закиданной).
        public static bool IsRoad(GeoLandClass land) =>
            land is GeoLandClass.Asphalt or GeoLandClass.Paving or GeoLandClass.Gravel or GeoLandClass.DirtRoad or GeoLandClass.Path or
                GeoLandClass.Rail;

        // Где трава не растёт совсем: вода, здания, дороги и тропинки (IsRoad).
        public static bool IsHard(GeoLandClass land) => land is GeoLandClass.Water or GeoLandClass.Building || IsRoad(land);

        // Множитель записи в каждом узле решётки (countX × countZ, по строкам z). Поле у края (межа, у дороги и леса)
        // — FieldEdge, берег у воды — наполовину болото.
        public static float[] EntryScales(byte[] classes, int countX, int countZ, FlyPointLandScales scales)
        {
            float[] result = new float[classes.Length];

            for (int z = 0; z < countZ; z++)
            {
                for (int x = 0; x < countX; x++)
                {
                    int index = z * countX + x;
                    GeoLandClass land = (GeoLandClass)classes[index];
                    FlyPointLandGroup group = Of(land);

                    if (group == FlyPointLandGroup.Field && BordersOtherThanField(classes, countX, countZ, x, z))
                        group = FlyPointLandGroup.FieldEdge;

                    float value = scales.Of(group);

                    if (land != GeoLandClass.Water && land != GeoLandClass.Wetland && BordersWater(classes, countX, countZ, x, z))
                        value = Mathf.Lerp(value, scales.Wetland, BankWetland);

                    result[index] = value;
                }
            }

            return result;
        }

        // 1 в узлах этой группы, 0 в остальных: билинейно — её доля в ячейке.
        public static float[] Share(byte[] classes, FlyPointLandGroup group)
        {
            float[] result = new float[classes.Length];

            for (int i = 0; i < classes.Length; i++)
                result[i] = Of((GeoLandClass)classes[i]) == group ? 1f : 0f;

            return result;
        }

        // roads false — дороги известны осями (RoadAreas): их клетки по 10 м здесь не в счёт, полотно вырезается точно
        public static float[] Hard(byte[] classes, bool roads = true)
        {
            float[] result = new float[classes.Length];

            for (int i = 0; i < classes.Length; i++)
            {
                GeoLandClass land = (GeoLandClass)classes[i];
                result[i] = IsHard(land) && (roads || !IsRoad(land)) ? 1f : 0f;
            }

            return result;
        }

        // Билинейно по решётке; gx, gz — в узлах (0 — первый узел), за краем — крайний узел.
        public static float Sample(float[] lattice, int countX, int countZ, float gx, float gz)
        {
            int x0 = Mathf.Clamp(Mathf.FloorToInt(gx), 0, countX - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt(gz), 0, countZ - 1);
            int x1 = Mathf.Min(x0 + 1, countX - 1);
            int z1 = Mathf.Min(z0 + 1, countZ - 1);
            float tx = Mathf.Clamp01(gx - x0);
            float tz = Mathf.Clamp01(gz - z0);
            float south = Mathf.Lerp(lattice[z0 * countX + x0], lattice[z0 * countX + x1], tx);
            float north = Mathf.Lerp(lattice[z1 * countX + x0], lattice[z1 * countX + x1], tx);
            return Mathf.Lerp(south, north, tz);
        }

        // Рядом (8 соседей) земля другая, чем поле, и карта о ней знает.
        private static bool BordersOtherThanField(byte[] classes, int countX, int countZ, int x, int z)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx;
                    int nz = z + dz;

                    if ((dx == 0 && dz == 0) || nx < 0 || nz < 0 || nx >= countX || nz >= countZ)
                        continue;

                    if (IsFieldBoundary((GeoLandClass)classes[nz * countX + nx]))
                        return true;
                }
            }

            return false;
        }

        // Межа — только у настоящей границы поля: лес, кусты и сад, вода и болото, посёлок, дороги и тропинки. «Луг» и
        // «голая земля» WorldCover рядом с пашней чаще то же поле (убранное или вспаханное) — их пятна давали полосы
        // цветов посреди поля.
        private static bool IsFieldBoundary(GeoLandClass land) =>
            land is GeoLandClass.Forest or GeoLandClass.Scrub or GeoLandClass.Orchard or GeoLandClass.Wetland or
                GeoLandClass.Water or GeoLandClass.Urban or GeoLandClass.Building || IsRoad(land);

        // 1 в узлах края поля, 0 в остальных (билинейно — доля края в ячейке)
        public static float[] FieldEdgeShare(byte[] classes, int countX, int countZ)
        {
            float[] result = new float[classes.Length];

            for (int z = 0; z < countZ; z++)
            {
                for (int x = 0; x < countX; x++)
                {
                    int i = z * countX + x;

                    if (Of((GeoLandClass)classes[i]) == FlyPointLandGroup.Field && BordersOtherThanField(classes, countX, countZ, x, z))
                        result[i] = 1f;
                }
            }

            return result;
        }

        // 1 там, где WorldCover путает поле с лугом или голой землёй: луг, пустое, голый грунт (не песок, не скала)
        public static float[] MaybeFieldShare(byte[] classes)
        {
            float[] result = new float[classes.Length];

            for (int i = 0; i < classes.Length; i++)
                result[i] = (GeoLandClass)classes[i] is GeoLandClass.Grass or GeoLandClass.None or GeoLandClass.Bare ? 1f : 0f;

            return result;
        }

        private static bool BordersWater(byte[] classes, int countX, int countZ, int x, int z)
        {
            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx;
                    int nz = z + dz;

                    if ((dx == 0 && dz == 0) || nx < 0 || nz < 0 || nx >= countX || nz >= countZ)
                        continue;

                    if ((GeoLandClass)classes[nz * countX + nx] == GeoLandClass.Water)
                        return true;
                }
            }

            return false;
        }
    }
}
