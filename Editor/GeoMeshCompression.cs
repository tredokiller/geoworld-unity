using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace GeoWorld.Editor
{
    // Baked chunks (buildings, props) hold millions of vertices, and a vertex in Unity's default layout is 48 bytes:
    // position, normal and tangent as 32-bit floats and the texture coordinate too. Normals and tangents are unit
    // vectors, and 8 bits per component is finer than any shading shows (under half a degree); the palette and model
    // texture coordinates lie between 0 and 1, where a 16-bit float is exact to a twentieth of a texel of a 1024 texture.
    // So a chunk is rewritten as position (32-bit floats), normal and tangent (8-bit) and texture coordinate (16-bit):
    // 24 bytes a vertex, half the memory and half the disk. Positions stay 32-bit: a chunk spans hundreds of metres.
    // A second texture coordinate (a road's offset from its centreline and half width, for the edge fade) is kept too.
    public static class GeoMeshCompression
    {
        // Texture coordinates up to 2 keep 16-bit precision of better than a thousandth (an atlas or the palette).
        // Repeating textures (roofs in metres) may go further: at 32 a 16-bit float is still exact to 1/64 of a repeat.
        public const float SmallUv = 2, RepeatingUv = 32;
        // The second texture coordinate (metres across a road) in 16 bits up to 64: exact to 3 cm at the far end.
        public const float SmallUv2 = 64;

        // A new mesh with the same triangles, sub-meshes and bounds in the compact layout; the given mesh is destroyed.
        // halfUv: the largest texture coordinate stored in 16 bits (beyond it, 32-bit coordinates).
        public static Mesh Compact(Mesh mesh, float halfUv = SmallUv)
        {
            int count = mesh.vertexCount;
            if (count == 0) return mesh;
            var positions = mesh.vertices; var normals = mesh.normals; var tangents = mesh.tangents;
            var uvs = new List<Vector2>(); mesh.GetUVs(0, uvs);
            var uvs2 = new List<Vector2>(); mesh.GetUVs(1, uvs2);
            bool hasNormals = normals.Length == count, hasTangents = tangents.Length == count, hasUv = uvs.Count == count, hasUv2 = uvs2.Count == count;
            bool smallUv = !hasUv || Within(uvs, halfUv), smallUv2 = !hasUv2 || Within(uvs2, SmallUv2);
            var layout = new List<VertexAttributeDescriptor>
            {
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.SNorm8, 4)
            };
            if (hasTangents) layout.Add(new VertexAttributeDescriptor(VertexAttribute.Tangent, VertexAttributeFormat.SNorm8, 4));
            layout.Add(new VertexAttributeDescriptor(VertexAttribute.TexCoord0, smallUv ? VertexAttributeFormat.Float16 : VertexAttributeFormat.Float32, 2));
            if (hasUv2) layout.Add(new VertexAttributeDescriptor(VertexAttribute.TexCoord1, smallUv2 ? VertexAttributeFormat.Float16 : VertexAttributeFormat.Float32, 2));

            // every attribute is a whole number of 4-byte words: the vertex buffer is written as words, in layout order
            int stride = 3 + 1 + (hasTangents ? 1 : 0) + (smallUv ? 1 : 2) + (hasUv2 ? (smallUv2 ? 1 : 2) : 0);
            var words = new int[count * stride];
            for (int i = 0, o = 0; i < count; i++)
            {
                var p = positions[i];
                words[o++] = BitConverter.SingleToInt32Bits(p.x); words[o++] = BitConverter.SingleToInt32Bits(p.y); words[o++] = BitConverter.SingleToInt32Bits(p.z);
                words[o++] = (int)Pack(hasNormals ? normals[i] : Vector3.up, 0);
                if (hasTangents) words[o++] = (int)Pack(tangents[i], tangents[i].w);
                o = Write(words, o, hasUv ? uvs[i] : Vector2.zero, smallUv);
                if (hasUv2) o = Write(words, o, uvs2[i], smallUv2);
            }
            var result = new Mesh { name = mesh.name, indexFormat = count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            result.SetVertexBufferParams(count, layout.ToArray());
            result.SetVertexBufferData(words, 0, 0, words.Length, 0, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            result.subMeshCount = mesh.subMeshCount;
            for (int sub = 0; sub < mesh.subMeshCount; sub++) result.SetTriangles(mesh.GetTriangles(sub), sub, false);
            result.bounds = mesh.bounds;
            Object.DestroyImmediate(mesh);
            return result;
        }

        static bool Within(List<Vector2> values, float limit)
        {
            foreach (var v in values) if (Mathf.Abs(v.x) > limit || Mathf.Abs(v.y) > limit) return false;
            return true;
        }

        // a texture coordinate as two 16-bit floats in one word, or two 32-bit floats
        static int Write(int[] words, int o, Vector2 uv, bool half)
        {
            if (half) { words[o++] = (int)(Mathf.FloatToHalf(uv.x) | (uint)Mathf.FloatToHalf(uv.y) << 16); return o; }
            words[o++] = BitConverter.SingleToInt32Bits(uv.x); words[o++] = BitConverter.SingleToInt32Bits(uv.y);
            return o;
        }

        // Four signed components of -1..1 in 8 bits each.
        static uint Pack(Vector3 v, float w) =>
            (uint)(byte)(sbyte)Mathf.RoundToInt(Mathf.Clamp(v.x, -1, 1) * 127) |
            (uint)(byte)(sbyte)Mathf.RoundToInt(Mathf.Clamp(v.y, -1, 1) * 127) << 8 |
            (uint)(byte)(sbyte)Mathf.RoundToInt(Mathf.Clamp(v.z, -1, 1) * 127) << 16 |
            (uint)(byte)(sbyte)Mathf.RoundToInt(Mathf.Clamp(w, -1, 1) * 127) << 24;

        static uint Pack(Vector4 v, float w) => Pack(new Vector3(v.x, v.y, v.z), w);
    }
}
