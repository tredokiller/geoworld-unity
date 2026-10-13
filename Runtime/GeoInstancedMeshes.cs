using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GeoWorld
{
    // Many copies of a few meshes (a world's trees), drawn as GPU instances: lists of matrices per cell, mesh part and
    // material, and every camera draws each list it can see in one instanced call (shadows included). Unity's terrain
    // draws its trees one call each, which cost more than everything else in a town; here a tile's thousands of trees
    // are a few dozen calls. Worth it only where the copies share few materials (trees: one bark, four greens).
    [ExecuteAlways, DisallowMultipleComponent]
    public class GeoInstancedMeshes : MonoBehaviour
    {
        [Serializable]
        public class Batch
        {
            public Mesh mesh;
            public int submesh;
            public Material material;
            [Tooltip("World bounds of all the copies: the batch is skipped when the camera cannot see them.")]
            public Bounds bounds;
            public Matrix4x4[] matrices = new Matrix4x4[0];
        }

        public List<Batch> batches = new List<Batch>();
        [Tooltip("Batches further than this from the camera are not drawn (metres).")]
        [Min(50)] public float drawDistance = 1500;
        public ShadowCastingMode shadows = ShadowCastingMode.On;
        [Tooltip("How many copies the batches draw (each copy may have several parts).")]
        public int count;

        readonly Plane[] planes = new Plane[6];

        void OnEnable() => RenderPipelineManager.beginCameraRendering += Draw;
        void OnDisable() => RenderPipelineManager.beginCameraRendering -= Draw;

        void Draw(ScriptableRenderContext context, Camera camera)
        {
            if (camera.cameraType == CameraType.Preview || camera.cameraType == CameraType.Reflection) return;
            GeometryUtility.CalculateFrustumPlanes(camera, planes);
            Vector3 eye = camera.transform.position;
            float far = drawDistance * drawDistance;
            foreach (var batch in batches)
            {
                if (!batch.mesh || !batch.material || batch.matrices.Length == 0) continue;
                if (batch.bounds.SqrDistance(eye) > far || !GeometryUtility.TestPlanesAABB(planes, batch.bounds)) continue;
                var parameters = new RenderParams(batch.material)
                {
                    camera = camera, worldBounds = batch.bounds, layer = gameObject.layer,
                    shadowCastingMode = shadows, receiveShadows = true
                };
                Graphics.RenderMeshInstanced(parameters, batch.mesh, batch.submesh, batch.matrices);
            }
        }

        // World bounds of a mesh placed by each of the matrices.
        public static Bounds WorldBounds(Bounds local, IList<Matrix4x4> matrices)
        {
            var result = new Bounds();
            for (int i = 0; i < matrices.Count; i++)
            {
                var m = matrices[i];
                Vector3 centre = m.MultiplyPoint3x4(local.center), e = local.extents;
                var extents = new Vector3(
                    Mathf.Abs(m.m00) * e.x + Mathf.Abs(m.m01) * e.y + Mathf.Abs(m.m02) * e.z,
                    Mathf.Abs(m.m10) * e.x + Mathf.Abs(m.m11) * e.y + Mathf.Abs(m.m12) * e.z,
                    Mathf.Abs(m.m20) * e.x + Mathf.Abs(m.m21) * e.y + Mathf.Abs(m.m22) * e.z);
                var b = new Bounds(centre, extents * 2);
                if (i == 0) result = b; else result.Encapsulate(b);
            }
            return result;
        }
    }
}
