using System;
using UnityEngine;

namespace _Game.Scripts.Locations.FlyPoints
{
    public enum FlyPointLandGroup : byte
    {
        Meadow,
        Field,
        FieldEdge,
        Forest,
        Wetland,
        Settlement,
        Track,
        Barren
    }

    /// <summary>
    /// Плотность детали по земле карты GeoWorld (GeoLandCover, ячейка ~10 м): одни и те же виды по-разному растут на
    /// лугу, в поле, в лесу, на болоте. Множитель к плотности записи; где у мира карты нет — как на лугу.
    /// </summary>
    [Serializable]
    public struct FlyPointLandScales
    {
        [Tooltip("Луг, кусты, сад.")]
        [Range(0f, 4f)] public float Meadow;
        [Tooltip("Поле (пашня, посевы) вдали от края.")]
        [Range(0f, 4f)] public float Field;
        [Tooltip("Край поля шириной ~10 м: межа, у дороги, у леса.")]
        [Range(0f, 4f)] public float FieldEdge;
        [Tooltip("Лес.")]
        [Range(0f, 4f)] public float Forest;
        [Tooltip("Болото и берег у воды.")]
        [Range(0f, 4f)] public float Wetland;
        [Tooltip("Посёлок: дворы, огороды, у зданий.")]
        [Range(0f, 4f)] public float Settlement;
        [Tooltip("Грунтовка, гравий, обочины асфальта и рельсов.")]
        [Range(0f, 4f)] public float Track;
        [Tooltip("Песок, голая земля, камень, тропа.")]
        [Range(0f, 4f)] public float Barren;
        [Tooltip("Брошенная земля, где шли бои (воронки вокруг, CraterField.AbandonedAt): залежь, луг, дворы и руины — вместо " +
                 "своей группы (лес, болото и дороги — по своей). 0 — по своей группе.")]
        [Range(0f, 4f)] public float Fallow;

        public static FlyPointLandScales Everywhere => new()
        {
            Meadow = 1f, Field = 1f, FieldEdge = 1f, Forest = 1f, Wetland = 1f, Settlement = 1f, Track = 1f, Barren = 1f
        };

        // все нули — запись из профиля, сохранённого до этих настроек: растёт везде, как раньше
        public bool IsUnset =>
            Meadow == 0f && Field == 0f && FieldEdge == 0f && Forest == 0f &&
            Wetland == 0f && Settlement == 0f && Track == 0f && Barren == 0f && Fallow == 0f;

        public float Of(FlyPointLandGroup group) => group switch
        {
            FlyPointLandGroup.Field => Field,
            FlyPointLandGroup.FieldEdge => FieldEdge,
            FlyPointLandGroup.Forest => Forest,
            FlyPointLandGroup.Wetland => Wetland,
            FlyPointLandGroup.Settlement => Settlement,
            FlyPointLandGroup.Track => Track,
            FlyPointLandGroup.Barren => Barren,
            _ => Meadow
        };
    }
}
