using System;
using UnityEngine;

namespace _Game.Scripts.Locations.FlyPoints
{
    /// <summary>
    /// Плотность детали по тому, что спутник тайла показывает в ячейке (SatelliteStates): зелень (луг, всходы, лес),
    /// жёлтое (спелый хлеб, стерня, сухая трава), тёмная земля (пахота, чернозём), голая земля (рыже-бурый или серый
    /// сухой грунт, песок, мел). Доли состояний в ячейке в сумме 1, множитель — их взвешенная сумма, поэтому переходы
    /// плавные. Все нули — запись из профиля до этих настроек: работает прежний BareGroundScale; заданные — вместо него.
    /// </summary>
    [Serializable]
    public struct FlyPointSatelliteScales
    {
        [Tooltip("Зелень на спутнике: луг, всходы, газон, лес.")]
        [Range(0f, 4f)] public float Green;
        [Tooltip("Жёлтое и светлое: спелые хлеба, стерня, сухая трава.")]
        [Range(0f, 4f)] public float Yellow;
        [Tooltip("Тёмная земля: пахота, чернозём, влажный грунт.")]
        [Range(0f, 4f)] public float Dark;
        [Tooltip("Голая земля: рыже-бурый или серый сухой грунт, песок, мел.")]
        [Range(0f, 4f)] public float Bare;

        public static FlyPointSatelliteScales Everywhere => new() { Green = 1f, Yellow = 1f, Dark = 1f, Bare = 1f };

        // все нули — запись из профиля, сохранённого до этих настроек
        public bool IsUnset => Green == 0f && Yellow == 0f && Dark == 0f && Bare == 0f;

        // state: x зелень, y жёлтое, z тёмное, w голое (SatelliteStates), в сумме 1
        public float Of(Vector4 state) => Green * state.x + Yellow * state.y + Dark * state.z + Bare * state.w;
    }
}
