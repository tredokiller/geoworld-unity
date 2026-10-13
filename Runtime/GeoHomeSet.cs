using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeoWorld
{
    // The homes of one part of a merged city as data (GeoHomeInstances makes their objects): per home its name and
    // parts, each part the library's mesh and materials with a transform under the home. Kept in the world's binary
    // asset instead of the scene, where 2,000 homes were 8,000 objects and 20 MB of text.
    [PreferBinarySerialization]
    public class GeoHomeSet : ScriptableObject
    {
        [Serializable]
        public struct Part
        {
            public string name;
            public Mesh mesh;
            public Material[] materials;
            public Vector3 position, scale;
            public Quaternion rotation;
        }

        [Serializable]
        public class Home
        {
            public string name;
            public Part[] parts;
        }

        [Tooltip("Share of the screen's height below which a home is not drawn (its LODGroup).")]
        public float cullHeight = 0.004f;
        public List<Home> homes = new List<Home>();
    }
}
