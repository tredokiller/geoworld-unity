using System;
using _Game.Scripts.Enums;
using UnityEngine;

namespace _Game.Scripts.Locations.FlyPoints
{
    /// <summary>
    /// Плотность детали по поре года (Seasons): множитель к плотности записи — осенью листвы больше, а цветов нет, весной
    /// прошлогодней листвы мало, поле убрано и т. п. Зимой под снегом записи нет (поклеточно по узору снега шейдера —
    /// SnowCover), кроме AboveSnow-записей (тростник торчит из сугроба).
    /// </summary>
    [Serializable]
    public struct FlyPointSeasonScales
    {
        [Range(0f, 4f)] public float Spring;
        [Range(0f, 4f)] public float Summer;
        [Range(0f, 4f)] public float Autumn;
        [Tooltip("Зимой на голой земле (сухая трава); под снегом её нет, если запись не AboveSnow.")]
        [Range(0f, 4f)] public float Winter;

        public static FlyPointSeasonScales Default => new() { Spring = 1f, Summer = 1f, Autumn = 1f, Winter = 0f };

        // все нули — запись из профиля, сохранённого до этих настроек: растёт, как раньше, зимой — нет
        public bool IsUnset => Spring == 0f && Summer == 0f && Autumn == 0f && Winter == 0f;

        public float Of(Season season) => season switch
        {
            Season.Spring => Spring,
            Season.Autumn => Autumn,
            Season.Winter => Winter,
            _ => Summer
        };
    }
}
