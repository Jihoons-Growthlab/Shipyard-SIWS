using System.Collections.Generic;
using UnityEngine;

namespace ShipyardTwin
{
    /// <summary>
    /// Paint and surface colours shared by the ship, crane and yard. Ship names match the <c>usemtl</c> names written
    /// by pipeline/generate_models.py (paint.mtl). Materials are copies of one base material (URP Lit, from the
    /// generated Base prefab), so the shader is already in the build.
    /// </summary>
    public sealed class ShipPalette
    {
        public readonly struct Paint
        {
            public Paint(Color color, float smoothness, float metallic = 0f)
            {
                Color = color;
                Smoothness = smoothness;
                Metallic = metallic;
            }

            public Color Color { get; }
            public float Smoothness { get; }
            public float Metallic { get; }
        }

        public static readonly IReadOnlyDictionary<string, Paint> Paints = new Dictionary<string, Paint>
        {
            // Ship (paint.mtl)
            ["antifouling"] = new Paint(new Color(0.55f, 0.13f, 0.10f), 0.35f),
            ["boottop"] = new Paint(new Color(0.08f, 0.08f, 0.09f), 0.4f),
            ["topside"] = new Paint(new Color(0.16f, 0.22f, 0.33f), 0.45f),
            ["deck"] = new Paint(new Color(0.36f, 0.45f, 0.38f), 0.25f),
            ["primer"] = new Paint(new Color(0.56f, 0.54f, 0.50f), 0.15f),
            ["trunk"] = new Paint(new Color(0.62f, 0.65f, 0.66f), 0.3f),
            ["fitting"] = new Paint(new Color(0.86f, 0.87f, 0.87f), 0.35f),
            ["pipe"] = new Paint(new Color(0.74f, 0.76f, 0.78f), 0.6f, 0.5f),
            ["machinery"] = new Paint(new Color(0.24f, 0.35f, 0.30f), 0.35f),
            ["superstructure"] = new Paint(new Color(0.93f, 0.93f, 0.91f), 0.35f),
            ["glass"] = new Paint(new Color(0.10f, 0.15f, 0.22f), 0.92f, 0.3f),
            ["funnel"] = new Paint(new Color(0.93f, 0.93f, 0.91f), 0.35f),
            ["funnelband"] = new Paint(new Color(0.10f, 0.32f, 0.62f), 0.4f),
            ["bronze"] = new Paint(new Color(0.76f, 0.56f, 0.26f), 0.75f, 0.8f),
            ["weld"] = new Paint(new Color(0.30f, 0.27f, 0.25f), 0.25f, 0.3f),
            ["hull_text"] = new Paint(new Color(0.95f, 0.95f, 0.92f), 0.3f),
            // Crane
            ["crane_white"] = new Paint(new Color(0.92f, 0.93f, 0.93f), 0.4f),
            ["crane_red"] = new Paint(new Color(0.76f, 0.12f, 0.10f), 0.45f),
            ["crane_yellow"] = new Paint(new Color(0.96f, 0.74f, 0.08f), 0.45f),
            ["crane_dark"] = new Paint(new Color(0.16f, 0.17f, 0.19f), 0.3f, 0.3f),
            ["wire"] = new Paint(new Color(0.20f, 0.20f, 0.21f), 0.6f, 0.7f),
            // Yard
            ["concrete"] = new Paint(new Color(0.50f, 0.50f, 0.49f), 0.06f),
            ["dock_floor"] = new Paint(new Color(0.38f, 0.38f, 0.37f), 0.05f),
            ["keel_block"] = new Paint(new Color(0.40f, 0.36f, 0.32f), 0.05f),
            // A rougher sea surface keeps the directional light from producing a bright, bloom-heavy sun glint.
            ["water"] = new Paint(new Color(0.09f, 0.22f, 0.30f), 0.18f),
            ["shop_wall"] = new Paint(new Color(0.66f, 0.69f, 0.72f), 0.2f),
            ["shop_roof"] = new Paint(new Color(0.23f, 0.40f, 0.60f), 0.3f),
            ["rail"] = new Paint(new Color(0.30f, 0.30f, 0.32f), 0.6f, 0.8f),
            ["marking"] = new Paint(new Color(0.95f, 0.80f, 0.15f), 0.2f),
            ["asphalt"] = new Paint(new Color(0.30f, 0.31f, 0.32f), 0.1f),
            ["hinterland"] = new Paint(new Color(0.42f, 0.47f, 0.38f), 0.05f),
            // Scenery (pipeline/generate_scenery.py paint.mtl)
            ["wall_panel"] = new Paint(new Color(0.78f, 0.80f, 0.83f), 0.3f),
            ["wall_rib"] = new Paint(new Color(0.66f, 0.69f, 0.73f), 0.3f),
            ["wall_base"] = new Paint(new Color(0.35f, 0.38f, 0.42f), 0.2f),
            ["roof"] = new Paint(new Color(0.30f, 0.48f, 0.70f), 0.35f),
            ["skylight"] = new Paint(new Color(0.82f, 0.88f, 0.93f), 0.7f),
            ["door"] = new Paint(new Color(0.52f, 0.56f, 0.60f), 0.3f),
            ["roof_vent"] = new Paint(new Color(0.62f, 0.64f, 0.66f), 0.3f),
            ["wall_white"] = new Paint(new Color(0.92f, 0.92f, 0.90f), 0.3f),
            ["stack"] = new Paint(new Color(0.74f, 0.75f, 0.76f), 0.5f, 0.4f),
            ["vehicle_yellow"] = new Paint(new Color(0.95f, 0.70f, 0.10f), 0.5f),
            ["tire"] = new Paint(new Color(0.10f, 0.10f, 0.11f), 0.2f),
            ["lamp"] = new Paint(new Color(1.0f, 0.95f, 0.75f), 0.8f),
            ["lamp_orange"] = new Paint(new Color(1.0f, 0.55f, 0.10f), 0.6f),
            ["car_white"] = new Paint(new Color(0.93f, 0.93f, 0.93f), 0.75f, 0.3f),
            ["car_silver"] = new Paint(new Color(0.70f, 0.72f, 0.74f), 0.75f, 0.6f),
            ["car_black"] = new Paint(new Color(0.10f, 0.11f, 0.12f), 0.75f, 0.4f),
            ["car_red"] = new Paint(new Color(0.70f, 0.12f, 0.12f), 0.75f, 0.3f),
            ["car_blue"] = new Paint(new Color(0.15f, 0.30f, 0.60f), 0.6f, 0.3f),
            ["trailer"] = new Paint(new Color(0.38f, 0.40f, 0.43f), 0.3f, 0.4f),
            ["steel"] = new Paint(new Color(0.45f, 0.43f, 0.41f), 0.45f, 0.6f),
            ["quay_crane"] = new Paint(new Color(0.95f, 0.76f, 0.10f), 0.45f),
            ["container_blue"] = new Paint(new Color(0.13f, 0.33f, 0.62f), 0.35f),
            ["container_orange"] = new Paint(new Color(0.85f, 0.42f, 0.12f), 0.35f),
            ["road"] = new Paint(new Color(0.22f, 0.23f, 0.24f), 0.15f),
            ["road_marking"] = new Paint(new Color(0.95f, 0.95f, 0.92f), 0.3f),
            ["rock"] = new Paint(new Color(0.42f, 0.40f, 0.37f), 0.15f),
            ["riprap"] = new Paint(new Color(0.38f, 0.37f, 0.35f), 0.1f),
            ["bank"] = new Paint(new Color(0.50f, 0.46f, 0.39f), 0.06f),
            // Site perimeter (YardEnvironment.Perimeter, SatelliteGround grass band): precast wall, verge and trees.
            ["embankment"] = new Paint(new Color(0.37f, 0.45f, 0.29f), 0.04f),   // under the mottled concrete texture
            ["fence"] = new Paint(new Color(0.78f, 0.78f, 0.75f), 0.12f),
            ["fence_post"] = new Paint(new Color(0.58f, 0.60f, 0.60f), 0.15f),
            ["tree_trunk"] = new Paint(new Color(0.30f, 0.23f, 0.17f), 0.05f),
            ["tree_leaf"] = new Paint(new Color(0.17f, 0.30f, 0.15f), 0.08f),
            ["rim"] = new Paint(new Color(0.70f, 0.72f, 0.74f), 0.6f, 0.8f),
            ["grating"] = new Paint(new Color(0.30f, 0.31f, 0.33f), 0.3f, 0.5f),
        };

        public const string Fallback = "primer";

        /// <summary>Detail texture per paint: Resources/ShipyardTwin/Textures/&lt;Texture&gt;_albedo/_normal, tile size in metres.</summary>
        public readonly struct Detail
        {
            public Detail(string texture, float tileMetres, float bump, bool albedo = true)
            {
                Texture = texture;
                TileMetres = tileMetres;
                Bump = bump;
                Albedo = albedo;
            }

            public string Texture { get; }
            public float TileMetres { get; }
            public float Bump { get; }
            public bool Albedo { get; }
        }

        private static readonly Detail Hull = new Detail("hull", 10f, 0.8f);
        private static readonly Detail Steel = new Detail("steel", 4f, 0.5f);
        private static readonly Detail Cladding = new Detail("cladding", 2f, 0.6f);
        private static readonly Detail Concrete = new Detail("concrete", 12f, 0.4f);
        private static readonly Detail Asphalt = new Detail("asphalt", 8f, 0.6f);

        public static readonly IReadOnlyDictionary<string, Detail> Details = new Dictionary<string, Detail>
        {
            ["antifouling"] = Hull, ["boottop"] = Hull, ["topside"] = Hull,
            ["deck"] = Steel, ["trunk"] = Steel, ["primer"] = Steel, ["steel"] = Steel, ["superstructure"] = Steel, ["funnel"] = Steel,
            ["crane_white"] = Steel, ["crane_red"] = Steel, ["crane_yellow"] = Steel, ["crane_dark"] = Steel, ["quay_crane"] = Steel,
            ["vehicle_yellow"] = Steel, ["trailer"] = Steel, ["container_blue"] = Steel, ["container_orange"] = Steel,
            ["fitting"] = Steel, ["stack"] = Steel, ["machinery"] = Steel,
            ["wall_panel"] = Cladding, ["wall_rib"] = Cladding, ["roof"] = Cladding,
            ["concrete"] = Concrete, ["dock_floor"] = Concrete, ["keel_block"] = Concrete, ["wall_base"] = Concrete,
            ["rock"] = new Detail("concrete", 3f, 1.2f), ["riprap"] = new Detail("asphalt", 2f, 1.5f), ["bank"] = new Detail("concrete", 5f, 1.8f),
            ["embankment"] = new Detail("concrete", 9f, 1.2f), ["fence"] = Concrete,
            ["asphalt"] = Asphalt, ["road"] = Asphalt,
            ["water"] = new Detail("water", 24f, 0.6f, albedo: false),
        };

        private const string TextureFolder = "ShipyardTwin/Textures/";
        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
        private static readonly int BumpMapId = Shader.PropertyToID("_BumpMap");
        private static readonly int BumpScaleId = Shader.PropertyToID("_BumpScale");

        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");
        private static readonly int GlossinessId = Shader.PropertyToID("_Glossiness");
        private static readonly int MetallicId = Shader.PropertyToID("_Metallic");

        private readonly Material _base;
        private readonly Dictionary<string, Material> _materials = new Dictionary<string, Material>();

        public ShipPalette(Material baseMaterial)
        {
            _base = baseMaterial;
        }

        public static Paint Lookup(string name) =>
            name != null && Paints.TryGetValue(name, out var paint) ? paint : Paints[Fallback];

        private static void ApplyDetail(Material material, Detail detail)
        {
            var tiling = Vector2.one / detail.TileMetres;   // UVs are in metres
            if (detail.Albedo)
            {
                var albedo = Resources.Load<Texture2D>(TextureFolder + detail.Texture + "_albedo");
                if (albedo != null)
                {
                    material.SetTexture(BaseMapId, albedo);
                    material.SetTexture(MainTexId, albedo);
                }
            }

            var normal = Resources.Load<Texture2D>(TextureFolder + detail.Texture + "_normal");
            if (normal != null)
            {
                material.SetTexture(BumpMapId, normal);
                material.SetFloat(BumpScaleId, detail.Bump);
                material.EnableKeyword("_NORMALMAP");
            }

            material.SetTextureScale(BaseMapId, tiling);
            material.SetTextureScale(MainTexId, tiling);
        }

        /// <summary>
        /// Animated sea (Resources/ShipyardTwin/Shaders/Water: travelling waves, ripples, foam), coloured from the water
        /// paint; null when the shader is unavailable, so the plain lit material is used instead.
        /// </summary>
        private static Material CreateSea(Paint paint)
        {
            var shader = Shader.Find("ShipyardTwin/Water");
            if (shader == null || !shader.isSupported)
            {
                return null;
            }

            var material = new Material(shader) { name = "water" };
            material.SetColor(BaseColorId, paint.Color);
            material.SetColor(ShallowColorId, new Color(0.10f, 0.42f, 0.44f));   // light scattered through crests
            var normal = Resources.Load<Texture2D>(TextureFolder + "water_normal");
            if (normal != null)
            {
                material.SetTexture(BumpMapId, normal);
            }

            return material;
        }

        private static readonly int ShallowColorId = Shader.PropertyToID("_ShallowColor");

        /// <summary>New (unshared) material from the base, e.g. for a textured ground.</summary>
        public Material Create(string name)
        {
            var material = _base != null ? new Material(_base) : new Material(Shader.Find("Universal Render Pipeline/Lit"));
            material.name = name;
            return material;
        }

        /// <summary>Shared material for a paint name (unknown names use primer).</summary>
        public Material Get(string name)
        {
            name = name != null && Paints.ContainsKey(name) ? name : Fallback;
            if (_materials.TryGetValue(name, out var material))
            {
                return material;
            }

            var paint = Paints[name];
            if (name == "water" && CreateSea(paint) is Material sea)
            {
                _materials[name] = sea;
                return sea;
            }

            material = _base != null ? new Material(_base) : new Material(Shader.Find("Standard"));
            material.name = name;
            material.color = paint.Color;
            // URP Lit (_BaseColor/_Smoothness) and Built-in Standard (_Color/_Glossiness) names, whichever the base uses.
            material.SetColor(ColorId, paint.Color);
            material.SetColor(BaseColorId, paint.Color);
            material.SetFloat(SmoothnessId, paint.Smoothness);
            material.SetFloat(GlossinessId, paint.Smoothness);
            material.SetFloat(MetallicId, paint.Metallic);
            if (Details.TryGetValue(name, out var detail))
            {
                ApplyDetail(material, detail);
            }

            _materials[name] = material;
            return material;
        }
    }
}
