using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ShipyardTwin
{
    /// <summary>
    /// Outline and rim glow on the selected model itself (blocks, ships, cranes, vehicles, buildings), replacing the old
    /// screen-space corner brackets. Every frame it draws the model's meshes again with two extra materials through
    /// <see cref="Graphics.RenderMesh"/> — "ShipyardTwin/SelectionFill" (rim glow, stencil mark) and
    /// "ShipyardTwin/SelectionOutline" (constant-pixel silhouette line from back faces pushed out along smoothed normals).
    /// No GameObject, renderer, material or collider of the model is added or changed, so its materials, transparency,
    /// lift colours, shadows and picking stay as they are; there is nothing to clean up in the scene, and a renderer that
    /// is disabled, hidden or destroyed simply stops being drawn in the same frame. URP and WebGL 2 compatible.
    /// Owned by <see cref="ObjectDetailView"/>: <see cref="Show"/> on select, <see cref="Render"/> each LateUpdate,
    /// <see cref="Clear"/> on close.
    /// </summary>
    public sealed class SelectionHighlight : IDisposable
    {
        public static readonly Color Accent = new Color(0.45f, 0.87f, 0.76f, 1f);

        /// <summary>Outline width in screen pixels (fades in from 0 when a target is shown).</summary>
        public const float WidthPixels = 2.6f;

        public const float FadeSeconds = 0.22f;

        /// <summary>Meshes above this many vertices in total are outlined with their own normals (no smoothing pass).</summary>
        public const int SmoothVertexBudget = 400_000;

        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int WidthId = Shader.PropertyToID("_Width");
        private static readonly int FadeId = Shader.PropertyToID("_Fade");

        /// <summary>How often renderers added under the target after it was selected (late-loaded parts) are picked up.</summary>
        public const float RescanSeconds = 0.5f;

        // Smoothed outline copies of this selection's meshes: reused while the selection lasts, destroyed when it ends or
        // when their source mesh is destroyed, so no mesh outlives the selection that needed it.
        private readonly Dictionary<Mesh, Mesh> _smoothed = new Dictionary<Mesh, Mesh>();
        private readonly List<Part> _parts = new List<Part>();
        private readonly List<MeshRenderer> _scratch = new List<MeshRenderer>();
        private readonly List<Mesh> _stale = new List<Mesh>();
        private Material _fill, _outline;
        private MaterialPropertyBlock _block;
        private float _shownAt, _nextScan;
        private int _budget;

        private sealed class Part
        {
            public MeshRenderer Renderer;
            public MeshFilter Filter;
            public Mesh Source;    // the filter's mesh when Outline was made
            public Mesh Outline;   // smoothed copy, or Source itself over the budget / unreadable
        }

        public HoverTarget Target { get; private set; }

        public bool IsShowing => Target != null;

        /// <summary>Renderers drawn in the last <see cref="Render"/> (tests, diagnostics).</summary>
        public int Drawn { get; private set; }

        public int PartCount => _parts.Count;

        /// <summary>Smoothed mesh copies this selection holds (tests: none left after <see cref="Clear"/>).</summary>
        public int CachedMeshes => _smoothed.Count;

        /// <summary>The outline mesh drawn for a renderer this frame (tests), else null.</summary>
        public Mesh OutlineOf(Renderer renderer) => _parts.Find(p => p.Renderer == renderer)?.Outline;

        /// <summary>Starts outlining a target's renderers (null clears). Calling it with another target replaces the first.</summary>
        public void Show(HoverTarget target)
        {
            Clear();
            if (target == null)
            {
                return;
            }

            Target = target;
            _shownAt = Time.unscaledTime;
            _budget = SmoothVertexBudget;
            Collect();
        }

        public void Clear()
        {
            Target = null;
            _parts.Clear();
            foreach (var copy in _smoothed.Values) DestroyObject(copy);
            _smoothed.Clear();
            Drawn = 0;
        }

        /// <summary>
        /// Draws this frame's highlight. Clears itself when the target was destroyed; skips (without clearing) renderers
        /// and targets that are hidden, so a layer toggled off and on again keeps the selection. A mesh that arrives late
        /// or is swapped on a renderer (MeshFilter.sharedMesh) is outlined from that frame on; renderers added under the
        /// target later are picked up every <see cref="RescanSeconds"/>.
        /// </summary>
        public void Render()
        {
            Drawn = 0;
            if (Target == null)
            {
                if (_parts.Count > 0 || _smoothed.Count > 0) Clear();   // destroyed (Unity null) since the last frame
                return;
            }

            if (!Target.gameObject.activeInHierarchy)
            {
                return;
            }

            if (Time.unscaledTime >= _nextScan) Collect();
            if (!EnsureMaterials())
            {
                return;
            }

            var fade = Ease(Mathf.Clamp01((Time.unscaledTime - _shownAt) / FadeSeconds));
            _block.SetFloat(FadeId, fade);
            for (var i = 0; i < _parts.Count; i++)
            {
                var part = _parts[i];
                var renderer = part.Renderer;
                if (renderer == null || part.Filter == null || !renderer.enabled || renderer.forceRenderingOff ||
                    !renderer.gameObject.activeInHierarchy)
                {
                    continue;
                }

                var mesh = part.Filter.sharedMesh;
                // Unity compares a destroyed mesh equal to null. Detect the reference change so its copy is released.
                if (!ReferenceEquals(mesh, part.Source))
                {
                    part.Source = mesh;
                    part.Outline = OutlineMesh(mesh);
                    PruneDestroyed();
                }

                if (mesh == null || part.Outline == null)
                {
                    continue;   // no mesh yet (still loading)
                }

                var matrix = renderer.localToWorldMatrix;
                var fill = Params(_fill, renderer);
                var outline = Params(_outline, renderer);
                for (var sub = 0; sub < part.Outline.subMeshCount; sub++)
                {
                    Graphics.RenderMesh(fill, part.Outline, sub, matrix);
                    Graphics.RenderMesh(outline, part.Outline, sub, matrix);
                }

                Drawn++;
            }
        }

        /// <summary>Adds the target's mesh renderers not yet known (with or without a mesh yet); drops destroyed ones.</summary>
        private void Collect()
        {
            _nextScan = Time.unscaledTime + RescanSeconds;
            _parts.RemoveAll(p => p.Renderer == null || p.Filter == null);
            _scratch.Clear();
            Target.GetComponentsInChildren(true, _scratch);
            foreach (var renderer in _scratch)
            {
                if (_parts.Exists(p => p.Renderer == renderer)) continue;
                var filter = renderer.GetComponent<MeshFilter>();
                if (filter == null) continue;
                _parts.Add(new Part { Renderer = renderer, Filter = filter, Source = filter.sharedMesh, Outline = OutlineMesh(filter.sharedMesh) });
            }

            _scratch.Clear();
        }

        /// <summary>The smoothed copy of a mesh for this selection (made once, within the vertex budget), else the mesh itself.</summary>
        private Mesh OutlineMesh(Mesh mesh)
        {
            if (mesh == null)
            {
                return null;
            }

            if (_smoothed.TryGetValue(mesh, out var copy) && copy != null)
            {
                return copy;
            }

            if (!mesh.isReadable || mesh.vertexCount > _budget)
            {
                return mesh;
            }

            _budget -= mesh.vertexCount;
            copy = SmoothedCopy(mesh);
            _smoothed[mesh] = copy;
            return copy;
        }

        /// <summary>Destroys copies whose source mesh was destroyed (a swapped-out, unloaded mesh).</summary>
        private void PruneDestroyed()
        {
            _stale.Clear();
            foreach (var pair in _smoothed)
            {
                if (pair.Key == null) _stale.Add(pair.Key);
            }

            foreach (var key in _stale)
            {
                DestroyObject(_smoothed[key]);
                _smoothed.Remove(key);
            }

            _stale.Clear();
        }

        public void Dispose()
        {
            Clear();
            DestroyObject(_fill);
            DestroyObject(_outline);
            _fill = _outline = null;
        }

        private RenderParams Params(Material material, Renderer renderer) => new RenderParams(material)
        {
            layer = renderer.gameObject.layer,
            renderingLayerMask = renderer.renderingLayerMask,
            shadowCastingMode = ShadowCastingMode.Off,
            receiveShadows = false,
            motionVectorMode = MotionVectorGenerationMode.ForceNoMotion,
            lightProbeUsage = LightProbeUsage.Off,
            reflectionProbeUsage = ReflectionProbeUsage.Off,
            matProps = _block,
        };

        private bool EnsureMaterials()
        {
            if (_fill != null && _outline != null)
            {
                return true;
            }

            _fill = Create("ShipyardTwin/SelectionFill");
            _outline = Create("ShipyardTwin/SelectionOutline");
            if (_fill == null || _outline == null)
            {
                return false;
            }

            _outline.SetFloat(WidthId, WidthPixels);
            _block ??= new MaterialPropertyBlock();
            return true;
        }

        private static Material Create(string shaderName)
        {
            var shader = Shader.Find(shaderName);
            if (shader == null || !shader.isSupported)
            {
                return null;
            }

            var material = new Material(shader) { name = shaderName, hideFlags = HideFlags.DontSave };
            material.SetColor(ColorId, Accent);
            return material;
        }

        private static void DestroyObject(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(o);
            else UnityEngine.Object.DestroyImmediate(o);
        }

        private static float Ease(float t) => 1f - (1f - t) * (1f - t) * (1f - t);

        /// <summary>
        /// The mesh a MeshRenderer draws (through its MeshFilter); none for other renderers — particles, lines and trails
        /// have no silhouette, and a skinned mesh's shared mesh is in bind pose, not where it is drawn.
        /// </summary>
        public static Mesh MeshOf(Renderer renderer)
        {
            if (!(renderer is MeshRenderer))
            {
                return null;
            }

            var filter = renderer.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }

        /// <summary>
        /// A new copy of the mesh whose normals are averaged over vertices at the same position, so the outline pushed out
        /// along them stays closed at hard edges (box-shaped blocks, buildings). Sub-meshes, positions, bounds and the
        /// source mesh are unchanged. The caller owns (and destroys) the copy; unreadable meshes come back as they are.
        /// </summary>
        public static Mesh SmoothedCopy(Mesh mesh)
        {
            if (mesh == null || !mesh.isReadable)
            {
                return mesh;
            }

            var copy = UnityEngine.Object.Instantiate(mesh);
            copy.name = mesh.name + " (outline)";
            copy.hideFlags = HideFlags.DontSave;
            copy.normals = SmoothNormals(mesh.vertices, mesh.normals);
            return copy;
        }

        /// <summary>Normals averaged over vertices that share a position (to 0.1 mm); a mesh without normals gets them from its centre.</summary>
        public static Vector3[] SmoothNormals(Vector3[] vertices, Vector3[] normals)
        {
            var result = new Vector3[vertices.Length];
            var hasNormals = normals != null && normals.Length == vertices.Length;
            var centre = Vector3.zero;
            if (!hasNormals)
            {
                foreach (var v in vertices) centre += v;
                if (vertices.Length > 0) centre /= vertices.Length;
            }

            var sums = new Dictionary<Vector3Int, Vector3>(vertices.Length);
            var keys = new Vector3Int[vertices.Length];
            for (var i = 0; i < vertices.Length; i++)
            {
                var v = vertices[i];
                var key = new Vector3Int(Mathf.RoundToInt(v.x * 1e4f), Mathf.RoundToInt(v.y * 1e4f), Mathf.RoundToInt(v.z * 1e4f));
                keys[i] = key;
                var n = hasNormals ? normals[i] : v - centre;
                sums[key] = sums.TryGetValue(key, out var sum) ? sum + n : n;
            }

            for (var i = 0; i < vertices.Length; i++)
            {
                var n = sums[keys[i]];
                result[i] = n.sqrMagnitude > 1e-12f ? n.normalized : (hasNormals ? normals[i] : Vector3.up);
            }

            return result;
        }
    }
}
