using System.Collections.Generic;
using GeoWorld;
using UnityEngine;
using UnityEngine.Rendering;

namespace _Game.Scripts.World
{
    /// <summary>
    /// Карта воронок вокруг камеры для шейдера террейна (GroundCraters.hlsl). Воронки мира (CraterField, тот же список, что
    /// генератор врезал в землю тайлов) рисуются сверху в окно windowSize (Hidden/Game/Crater Map: рельеф мелких — тех, что
    /// уже сетки высот тайла, чаша любой, свежий выброс, вода — _CraterMap, _CraterWater, _CraterMapRect), и террейн по ним
    /// наклоняет нормаль мелких воронок, темнит чаши, светлит свежий выброс и наливает прудки. Крупные воронки видны самой
    /// землёй тайла: для них террейн берёт нормаль из сетки высот в каждом пикселе (_TERRAIN_INSTANCED_PERPIXEL_NORMAL у
    /// мокрого материала, GroundWetness), а не в вершинах лода. Окно перерисовывается, когда камера уходит от его середины на
    /// recentreDistance. Мир без воронок — карты нет (_CraterMapRect.w = 0).
    /// </summary>
    public class CraterMap : MonoBehaviour
    {
        private const string MaterialResource = "Materials/CraterMap";
        private static readonly int MapId = Shader.PropertyToID("_CraterMap");
        private static readonly int WaterId = Shader.PropertyToID("_CraterWater");
        private static readonly int RectId = Shader.PropertyToID("_CraterMapRect");
        private static readonly int DrawId = Shader.PropertyToID("_CraterDraw");

        [Tooltip("Сторона окна карты воронок вокруг камеры, м.")]
        [SerializeField] private float windowSize = 256f;
        [Tooltip("Текселей по стороне окна (0.25 м на тексель при 1024 и 256 м: яма в метр — четыре текселя).")]
        [SerializeField] private int resolution = 1024;
        [Tooltip("На сколько камера уходит от середины окна, прежде чем карту перерисовать, м.")]
        [SerializeField] private float recentreDistance = 48f;

        private readonly List<Vector3> _positions = new();
        private readonly List<Vector2> _centres = new();
        private readonly List<Vector4> _craters = new();
        private readonly List<Vector2> _ends = new();
        private readonly List<int> _foundTrenches = new();
        private readonly List<int> _indices = new();
        private readonly List<int> _found = new();
        private readonly RenderTargetIdentifier[] _targets = new RenderTargetIdentifier[2];
        private RenderTexture _map;
        private RenderTexture _water;
        private Mesh _mesh;
        private Material _material;
        private CommandBuffer _commands;
        private CraterField _field;
        private Vector2 _origin;
        private bool _drawn;
        private float _drawnCarved;
        private float _carvedCheckAt;

        /// <summary>Самая мелкая воронка, которую сетка высот тайлов держит чашей (м): мельче рисует только эта карта. По
        /// террейнам тайлов GeoWorld (под GeoWorldRoot): у чужих террейнов своя сетка; пока тайлов нет — 3 м.</summary>
        public static float CarvedDiameter
        {
            get
            {
                foreach (Terrain terrain in Terrain.activeTerrains)
                {
                    if (terrain != null && terrain.terrainData != null && terrain.GetComponentInParent<GeoWorldRoot>() != null)
                        return 1.5f * Mathf.Max(terrain.terrainData.heightmapScale.x, terrain.terrainData.heightmapScale.z);
                }

                return 3f;
            }
        }

        private void OnDisable()
        {
            Shader.SetGlobalVector(RectId, Vector4.zero);
            Release(ref _map);
            Release(ref _water);

            if (_mesh != null)
                Destroy(_mesh);

            if (_material != null)
                Destroy(_material);

            _commands?.Release();
            _commands = null;
            _mesh = null;
            _material = null;
            _drawn = false;
        }

        private static void Release(ref RenderTexture texture)
        {
            if (texture != null)
            {
                texture.Release();
                Destroy(texture);
            }

            texture = null;
        }

        private void LateUpdate()
        {
            Camera camera = Camera.main;
            CraterField field = CraterField.Instance;

            if (field == null || field.craters.Length == 0 && field.trenches.Length == 0)
            {
                if (_drawn)
                {
                    Shader.SetGlobalVector(RectId, Vector4.zero);
                    _drawn = false;
                }

                _field = null;
                return;
            }

            if (camera == null || !EnsureResources())
                return;

            Vector2 eye = new(camera.transform.position.x, camera.transform.position.z);
            Vector2 centre = _origin + Vector2.one * (windowSize * 0.5f);

            // сетка высот тайлов стала известна (окно нарисовано до первого тайла) или другая: мелкие воронки — заново
            if (_drawn && Time.unscaledTime >= _carvedCheckAt)
            {
                _carvedCheckAt = Time.unscaledTime + 0.5f;

                if (Mathf.Abs(CarvedDiameter - _drawnCarved) > 0.01f)
                    _drawn = false;
            }

            if (_drawn && field == _field && _map.IsCreated() && (eye - centre).sqrMagnitude < recentreDistance * recentreDistance)
                return;

            _field = field;
            Draw(field, eye);
        }

        private void Draw(CraterField field, Vector2 eye)
        {
            float texel = windowSize / resolution;
            // начало окна — по сетке текселей: когда окно переезжает, воронки не дрожат
            _origin = new Vector2(Mathf.Floor((eye.x - windowSize * 0.5f) / texel) * texel, Mathf.Floor((eye.y - windowSize * 0.5f) / texel) * texel);
            _drawn = true;
            field.Query(new Rect(_origin, Vector2.one * windowSize), _found);
            field.QueryTrenches(new Rect(_origin, Vector2.one * windowSize), _foundTrenches);
            _drawnCarved = CarvedDiameter;
            BuildMesh(field, _drawnCarved);

            _commands.Clear();
            _targets[0] = _map;
            _targets[1] = _water;
            _commands.SetRenderTarget(_targets, _map.depthBuffer);
            _commands.ClearRenderTarget(false, true, Color.clear);
            _commands.SetGlobalVector(DrawId, new Vector4(_origin.x, _origin.y, 1f / windowSize, SystemInfo.graphicsUVStartsAtTop ? -1f : 1f));

            if (_mesh.vertexCount > 0)
                _commands.DrawMesh(_mesh, Matrix4x4.identity, _material, 0, 0);

            Graphics.ExecuteCommandBuffer(_commands);
            Shader.SetGlobalTexture(MapId, _map);
            Shader.SetGlobalTexture(WaterId, _water);
            Shader.SetGlobalVector(RectId, new Vector4(_origin.x, _origin.y, 1f / windowSize, resolution));
        }

        // Квадрат по зоне воронки — четыре вершины; в каждой — её центр, радиус ямы, глубина, износ и флаги (мелкая, вода,
        // свежая), фрагмент сам считает форму (CraterCommon.hlsl).
        private void BuildMesh(CraterField field, float carved)
        {
            _positions.Clear();
            _centres.Clear();
            _craters.Clear();
            _ends.Clear();
            _indices.Clear();

            foreach (int i in _found)
            {
                CraterField.Crater c = field.craters[i];
                float radius = Mathf.Max(0.05f, c.diameter * 0.5f);
                float reach = radius * 2.25f + windowSize / resolution;
                // 16: найдена на снимке — темнота её чаши уже в нём (террейн чашу не темнит)
                float flags = (c.diameter < carved ? 1f : 0f) + (c.water ? 2f : 0f) + (c.age < 0.35f ? 4f : 0f) + (c.source == 0 ? 16f : 0f);
                var data = new Vector4(radius, c.depth, c.age, flags);
                var centre = new Vector2(c.x, c.z);
                int first = _positions.Count;
                AddVertex(centre + new Vector2(-reach, -reach), centre, centre, data);
                AddVertex(centre + new Vector2(reach, -reach), centre, centre, data);
                AddVertex(centre + new Vector2(reach, reach), centre, centre, data);
                AddVertex(centre + new Vector2(-reach, reach), centre, centre, data);
                AddQuad(first);
            }

            // окопы карта больше не рисует: они — меши тайлов (GeoTrenchMesh, с грунтом из материала террейна), террейн под
            // ними срезан ниже; нарисованный здесь ров лёг бы поверх меша

            _mesh.Clear();
            _mesh.indexFormat = _positions.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            _mesh.SetVertices(_positions);
            _mesh.SetUVs(0, _centres);
            _mesh.SetUVs(1, _craters);
            _mesh.SetUVs(2, _ends);
            _mesh.SetTriangles(_indices, 0, false);
            _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
        }

        private void AddVertex(Vector2 corner, Vector2 centre, Vector2 end, Vector4 data)
        {
            _positions.Add(new Vector3(corner.x, 0f, corner.y));
            _centres.Add(centre);
            _craters.Add(data);
            _ends.Add(end);
        }

        private void AddQuad(int first)
        {
            _indices.Add(first);
            _indices.Add(first + 1);
            _indices.Add(first + 2);
            _indices.Add(first);
            _indices.Add(first + 2);
            _indices.Add(first + 3);
        }

        private bool EnsureResources()
        {
            if (_material == null)
            {
                Material source = Resources.Load<Material>(MaterialResource);
                Shader shader = source != null ? source.shader : Shader.Find("Hidden/Game/Crater Map");

                if (shader == null || !shader.isSupported)
                    return false;

                _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }

            _commands ??= new CommandBuffer { name = "Crater map" };
            _mesh ??= new Mesh { name = "Crater map", hideFlags = HideFlags.HideAndDontSave };

            if (_map == null || _map.width != resolution)
            {
                Release(ref _map);
                Release(ref _water);
                _map = Target("Crater map", RenderTextureFormat.ARGBHalf, true);
                _water = Target("Crater water", RenderTextureFormat.RGHalf, false);
                _drawn = false;
            }

            return true;
        }

        private RenderTexture Target(string name, RenderTextureFormat format, bool depth)
        {
            var texture = new RenderTexture(resolution, resolution, depth ? 16 : 0, format, RenderTextureReadWrite.Linear)
            {
                name = name, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, useMipMap = false, autoGenerateMips = false,
                hideFlags = HideFlags.HideAndDontSave
            };
            texture.Create();
            return texture;
        }
    }
}
