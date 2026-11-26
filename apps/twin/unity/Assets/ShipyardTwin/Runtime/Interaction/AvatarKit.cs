using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace ShipyardTwin
{
    /// <summary>
    /// Shared building kit of the walk avatars (<see cref="WalkWorkerView"/>): every figure, cart and prop is built from
    /// low-poly primitives that are baked into <b>one mesh per rigid bone</b>, all drawn with <b>one material</b> whose
    /// base map is a small palette texture (each colour is a texel, each part's UVs point at its texel). So a whole
    /// preset costs a few dozen renderers, one material and one 16×16 texture, which keeps the mobile WebGL build light.
    /// Thin moving lines (fishing lines, rods, whip, tow ropes) are rebuilt each frame into one <see cref="LineMesh"/>.
    /// No colliders; everything sits on the Ignore Raycast layer so picking, search and the camera's wall check skip it.
    /// </summary>
    internal sealed class AvatarKit : IDisposable
    {
        public const int IgnoreRaycastLayer = 2;
        private const int PaletteSize = 16;

        private readonly Dictionary<Color32, int> _slots = new Dictionary<Color32, int>();
        private readonly Color32[] _texels = new Color32[PaletteSize * PaletteSize];
        private readonly Dictionary<Transform, List<PartSpec>> _pending = new Dictionary<Transform, List<PartSpec>>();
        private readonly List<Object> _owned = new List<Object>();
        private readonly Texture2D _texture;
        private bool _dirty;

        public Material Material { get; }

        /// <summary>Renderers made by this kit (baked bones and line meshes), for budget checks.</summary>
        public int RendererCount { get; private set; }

        private struct PartSpec
        {
            public Shape Shape;
            public Matrix4x4 Matrix;
            public Vector2 Uv;
        }

        public AvatarKit(ShipPalette palette)
        {
            if (palette == null) throw new ArgumentNullException(nameof(palette));
            _texture = new Texture2D(PaletteSize, PaletteSize, TextureFormat.RGBA32, false)
            {
                name = "WalkAvatarPalette", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.DontSave,
            };
            for (var i = 0; i < _texels.Length; i++) _texels[i] = new Color32(255, 255, 255, 255);
            _texture.SetPixels32(_texels);
            _texture.Apply(false, false);
            _owned.Add(_texture);

            Material = palette.Create("WalkAvatar");
            Material.hideFlags = HideFlags.DontSave;
            foreach (var id in new[] { "_BaseMap", "_MainTex" })
            {
                if (!Material.HasProperty(id)) continue;
                Material.SetTexture(id, _texture);
                Material.SetTextureScale(id, Vector2.one);
                Material.SetTextureOffset(id, Vector2.zero);
            }
            if (Material.HasProperty("_BaseColor")) Material.SetColor("_BaseColor", Color.white);
            if (Material.HasProperty("_Color")) Material.SetColor("_Color", Color.white);
            if (Material.HasProperty("_Smoothness")) Material.SetFloat("_Smoothness", 0.18f);
            if (Material.HasProperty("_Glossiness")) Material.SetFloat("_Glossiness", 0.18f);
            if (Material.HasProperty("_Metallic")) Material.SetFloat("_Metallic", 0f);
            if (Material.HasProperty("_BumpMap")) Material.SetTexture("_BumpMap", null);
            Material.DisableKeyword("_NORMALMAP");
            _owned.Add(Material);
        }

        /// <summary>Distinct colours used so far (palette texels).</summary>
        public int ColourCount => _slots.Count;

        /// <summary>UV of a colour's palette texel (allocated on first use).</summary>
        public Vector2 Uv(Color color)
        {
            Color32 key = color;
            key.a = 255;
            if (!_slots.TryGetValue(key, out var slot))
            {
                slot = Mathf.Min(_slots.Count, _texels.Length - 1);
                _slots[key] = slot;
                _texels[slot] = key;
                _dirty = true;
            }

            return new Vector2((slot % PaletteSize + 0.5f) / PaletteSize, (slot / PaletteSize + 0.5f) / PaletteSize);
        }

        /// <summary>Empty transform (a bone or a pivot) on the Ignore Raycast layer.</summary>
        public static Transform Empty(string name, Transform parent)
        {
            var t = new GameObject(name) { layer = IgnoreRaycastLayer }.transform;
            t.SetParent(parent, false);
            return t;
        }

        /// <summary>Records a primitive on a bone (bone-local placement); it becomes part of the bone's mesh at <see cref="Bake"/>.</summary>
        public void Part(Transform bone, PrimitiveType shape, Vector3 position, Vector3 size, Color color) =>
            Part(bone, shape, position, Quaternion.identity, size, color);

        public void Part(Transform bone, PrimitiveType shape, Vector3 position, Quaternion rotation, Vector3 size, Color color) =>
            Add(bone, LowPoly.Of(shape), Matrix4x4.TRS(position, rotation, size), color);

        /// <summary>A cone (base radius 0.5 at y = -1, apex at y = +1 in unit size), e.g. a parasol canopy.</summary>
        public void Cone(Transform bone, Vector3 position, Quaternion rotation, Vector3 size, Color color) =>
            Add(bone, LowPoly.Cone, Matrix4x4.TRS(position, rotation, size), color);

        /// <summary>A primitive between two bone-local points (capsules overlap their ends by their radius).</summary>
        public void Segment(Transform bone, PrimitiveType shape, Vector3 from, Vector3 to, float diameter, Color color)
        {
            var along = to - from;
            var length = along.magnitude;
            var height = shape == PrimitiveType.Capsule ? (length + diameter) / 2f : shape == PrimitiveType.Cube ? length : length / 2f;
            var rotation = length > 1e-6f ? Quaternion.FromToRotation(Vector3.up, along) : Quaternion.identity;
            Part(bone, shape, (from + to) / 2f, rotation, new Vector3(diameter, Mathf.Max(0.001f, height), diameter), color);
        }

        private void Add(Transform bone, Shape shape, Matrix4x4 matrix, Color color)
        {
            if (!_pending.TryGetValue(bone, out var list))
            {
                list = new List<PartSpec>();
                _pending[bone] = list;
            }

            list.Add(new PartSpec { Shape = shape, Matrix = matrix, Uv = Uv(color) });
        }

        /// <summary>Bakes every recorded bone into one mesh with a renderer (shadows on, no probes, no collider).</summary>
        public void Bake()
        {
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();
            foreach (var pair in _pending)
            {
                vertices.Clear();
                normals.Clear();
                uvs.Clear();
                triangles.Clear();
                foreach (var part in pair.Value)
                {
                    var normalMatrix = part.Matrix.inverse.transpose;
                    var start = vertices.Count;
                    var shape = part.Shape;
                    for (var i = 0; i < shape.Vertices.Length; i++)
                    {
                        vertices.Add(part.Matrix.MultiplyPoint3x4(shape.Vertices[i]));
                        normals.Add(normalMatrix.MultiplyVector(shape.Normals[i]).normalized);
                        uvs.Add(part.Uv);
                    }

                    foreach (var index in shape.Triangles) triangles.Add(start + index);
                }

                var mesh = new Mesh { name = pair.Key.name, hideFlags = HideFlags.DontSave };
                if (vertices.Count > 65000) mesh.indexFormat = IndexFormat.UInt32;
                mesh.SetVertices(vertices);
                mesh.SetNormals(normals);
                mesh.SetUVs(0, uvs);
                mesh.SetTriangles(triangles, 0);
                mesh.RecalculateBounds();
                _owned.Add(mesh);
                Attach(pair.Key.gameObject, mesh, ShadowCastingMode.On);
            }

            _pending.Clear();
            ApplyPalette();
        }

        /// <summary>A renderer for a mesh that is rebuilt each frame (lines); no shadows.</summary>
        public LineMesh Lines(string name, Transform parent)
        {
            var t = Empty(name, parent);
            var mesh = new Mesh { name = name, hideFlags = HideFlags.DontSave };
            mesh.MarkDynamic();
            _owned.Add(mesh);
            Attach(t.gameObject, mesh, ShadowCastingMode.Off);
            return new LineMesh(this, mesh);
        }

        private void Attach(GameObject go, Mesh mesh, ShadowCastingMode shadows)
        {
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = Material;
            renderer.shadowCastingMode = shadows;
            renderer.receiveShadows = true;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            RendererCount++;
        }

        private void ApplyPalette()
        {
            if (!_dirty) return;
            _dirty = false;
            _texture.SetPixels32(_texels);
            _texture.Apply(false, false);
        }

        public void Dispose()
        {
            foreach (var target in _owned) Release(target);
            _owned.Clear();
            _pending.Clear();
        }

        public static void Release(Object target)
        {
            if (target == null) return;
            if (Application.isPlaying) Object.Destroy(target);
            else Object.DestroyImmediate(target);
        }

        // ---------------------------------------------------------------- low-poly primitives

        internal sealed class Shape
        {
            public Vector3[] Vertices;
            public Vector3[] Normals;
            public int[] Triangles;
        }

        /// <summary>Unit primitives with Unity's sizes (cube 1, sphere ⌀1, cylinder and capsule ⌀1 × 2 tall), few vertices.</summary>
        internal static class LowPoly
        {
            private const int Sides = 10;
            public static readonly Shape Cube = MakeCube();
            public static readonly Shape Sphere = MakeRound(0f, 6);
            public static readonly Shape Capsule = MakeRound(0.5f, 3);
            public static readonly Shape Cylinder = MakeCylinder(0.5f);
            public static readonly Shape Cone = MakeCylinder(0f);

            public static Shape Of(PrimitiveType type) => type switch
            {
                PrimitiveType.Sphere => Sphere,
                PrimitiveType.Capsule => Capsule,
                PrimitiveType.Cylinder => Cylinder,
                _ => Cube,
            };

            private static Shape MakeCube()
            {
                var v = new List<Vector3>();
                var n = new List<Vector3>();
                var t = new List<int>();
                var axes = new[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };
                foreach (var normal in axes)
                {
                    var u = Mathf.Abs(normal.y) > 0.5f ? Vector3.right : Vector3.up;
                    var w = Vector3.Cross(normal, u);
                    var start = v.Count;
                    foreach (var (a, b) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
                    {
                        v.Add((normal + u * a + w * b) * 0.5f);
                        n.Add(normal);
                    }

                    t.AddRange(new[] { start, start + 2, start + 1, start, start + 3, start + 2 });
                }

                return Fix(new Shape { Vertices = v.ToArray(), Normals = n.ToArray(), Triangles = t.ToArray() });
            }

            /// <summary>Sphere (half = 0) or capsule (half = 0.5: hemispheres centred at y = ±0.5), radius 0.5.</summary>
            private static Shape MakeRound(float half, int ringsPerHalf)
            {
                var v = new List<Vector3>();
                var n = new List<Vector3>();
                var rings = new List<(float angle, float centre)>();
                for (var k = 0; k <= ringsPerHalf; k++) rings.Add((-90f + 90f * k / ringsPerHalf, -half));
                for (var k = 0; k <= ringsPerHalf; k++) rings.Add((90f * k / ringsPerHalf, half));
                foreach (var (angle, centre) in rings)
                {
                    var a = angle * Mathf.Deg2Rad;
                    for (var j = 0; j < Sides; j++)
                    {
                        var b = 2f * Mathf.PI * j / Sides;
                        var dir = new Vector3(Mathf.Cos(a) * Mathf.Cos(b), Mathf.Sin(a), Mathf.Cos(a) * Mathf.Sin(b));
                        v.Add(dir * 0.5f + Vector3.up * centre);
                        n.Add(dir);
                    }
                }

                var t = new List<int>();
                for (var r = 0; r < rings.Count - 1; r++)
                {
                    for (var j = 0; j < Sides; j++)
                    {
                        int a = r * Sides + j, b = r * Sides + (j + 1) % Sides, c = a + Sides, d = b + Sides;
                        t.AddRange(new[] { a, c, b, b, c, d });
                    }
                }

                return Fix(new Shape { Vertices = v.ToArray(), Normals = n.ToArray(), Triangles = t.ToArray() });
            }

            /// <summary>Cylinder ⌀1 × 2 tall with caps; topRadius 0 makes a cone.</summary>
            private static Shape MakeCylinder(float topRadius)
            {
                var v = new List<Vector3>();
                var n = new List<Vector3>();
                var t = new List<int>();
                var slope = (0.5f - topRadius) / 2f;
                for (var j = 0; j < Sides; j++)
                {
                    var b = 2f * Mathf.PI * j / Sides;
                    var radial = new Vector3(Mathf.Cos(b), 0f, Mathf.Sin(b));
                    var normal = (radial + Vector3.up * slope).normalized;
                    v.Add(radial * 0.5f + Vector3.down);
                    n.Add(normal);
                    v.Add(radial * topRadius + Vector3.up);
                    n.Add(normal);
                }

                for (var j = 0; j < Sides; j++)
                {
                    int a = 2 * j, b = 2 * ((j + 1) % Sides);
                    t.AddRange(new[] { a, a + 1, b, b, a + 1, b + 1 });
                }

                foreach (var (y, radius, up) in new[] { (-1f, 0.5f, false), (1f, topRadius, true) })
                {
                    if (radius <= 0f) continue;
                    var centre = v.Count;
                    var normal = up ? Vector3.up : Vector3.down;
                    v.Add(new Vector3(0f, y, 0f));
                    n.Add(normal);
                    for (var j = 0; j < Sides; j++)
                    {
                        var b = 2f * Mathf.PI * j / Sides;
                        v.Add(new Vector3(Mathf.Cos(b) * radius, y, Mathf.Sin(b) * radius));
                        n.Add(normal);
                    }

                    for (var j = 0; j < Sides; j++)
                    {
                        int a = centre + 1 + j, b = centre + 1 + (j + 1) % Sides;
                        if (up) t.AddRange(new[] { centre, b, a });
                        else t.AddRange(new[] { centre, a, b });
                    }
                }

                return Fix(new Shape { Vertices = v.ToArray(), Normals = n.ToArray(), Triangles = t.ToArray() });
            }

            /// <summary>Makes every triangle face along its vertex normals (outward), whatever winding it was written with.</summary>
            private static Shape Fix(Shape shape)
            {
                var t = shape.Triangles;
                for (var i = 0; i < t.Length; i += 3)
                {
                    Vector3 a = shape.Vertices[t[i]], b = shape.Vertices[t[i + 1]], c = shape.Vertices[t[i + 2]];
                    var face = Vector3.Cross(b - a, c - a);
                    var normal = shape.Normals[t[i]] + shape.Normals[t[i + 1]] + shape.Normals[t[i + 2]];
                    if (Vector3.Dot(face, normal) < 0f) (t[i + 1], t[i + 2]) = (t[i + 2], t[i + 1]);
                }

                return shape;
            }
        }
    }

    /// <summary>
    /// Thin tubes (square section) rebuilt every frame in the owner's local space: rods, fishing lines, whip, tow ropes.
    /// Call <see cref="Begin"/>, add polylines, then <see cref="End"/>; one renderer for all of them.
    /// </summary>
    internal sealed class LineMesh
    {
        private readonly AvatarKit _kit;
        private readonly Mesh _mesh;
        private readonly List<Vector3> _vertices = new List<Vector3>();
        private readonly List<Vector3> _normals = new List<Vector3>();
        private readonly List<Vector2> _uvs = new List<Vector2>();
        private readonly List<int> _triangles = new List<int>();

        public LineMesh(AvatarKit kit, Mesh mesh)
        {
            _kit = kit;
            _mesh = mesh;
        }

        public void Begin()
        {
            _vertices.Clear();
            _normals.Clear();
            _uvs.Clear();
            _triangles.Clear();
        }

        /// <summary>A tube through the points, radius tapering linearly from the first to the last point.</summary>
        public void Tube(IReadOnlyList<Vector3> points, int count, float startRadius, float endRadius, Color color)
        {
            if (count < 2) return;
            var uv = _kit.Uv(color);
            var start = _vertices.Count;
            var side = Vector3.right;
            for (var i = 0; i < count; i++)
            {
                var along = (points[Mathf.Min(i + 1, count - 1)] - points[Mathf.Max(i - 1, 0)]);
                along = along.sqrMagnitude > 1e-10f ? along.normalized : Vector3.up;
                // Carry the side vector along the line (parallel transport) so the tube does not twist.
                side = Vector3.ProjectOnPlane(side, along);
                if (side.sqrMagnitude < 1e-6f) side = Vector3.ProjectOnPlane(Vector3.forward, along);
                if (side.sqrMagnitude < 1e-6f) side = Vector3.ProjectOnPlane(Vector3.up, along);
                side.Normalize();
                var up = Vector3.Cross(along, side);
                var radius = Mathf.Lerp(startRadius, endRadius, i / (float)(count - 1));
                for (var k = 0; k < 4; k++)
                {
                    var angle = k * Mathf.PI / 2f;
                    var normal = side * Mathf.Cos(angle) + up * Mathf.Sin(angle);
                    _vertices.Add(points[i] + normal * radius);
                    _normals.Add(normal);
                    _uvs.Add(uv);
                }
            }

            for (var i = 0; i < count - 1; i++)
            {
                for (var k = 0; k < 4; k++)
                {
                    int a = start + i * 4 + k, b = start + i * 4 + (k + 1) % 4, c = a + 4, d = b + 4;
                    _triangles.AddRange(new[] { a, b, c, b, d, c });
                }
            }
        }

        public void End()
        {
            _mesh.Clear();
            _mesh.SetVertices(_vertices);
            _mesh.SetNormals(_normals);
            _mesh.SetUVs(0, _uvs);
            _mesh.SetTriangles(_triangles, 0);
            _mesh.RecalculateBounds();
        }

        public int VertexCount => _vertices.Count;
    }
}
