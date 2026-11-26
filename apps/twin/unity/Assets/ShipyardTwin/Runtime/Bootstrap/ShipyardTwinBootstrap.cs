using System;
using System.Linq;
using GrowthLab.Core;
using UnityEngine;

namespace ShipyardTwin
{
    /// <summary>Playback options. Override any field with the optional settings TextAsset (JSON).</summary>
    [Serializable]
    public sealed class ShipyardTwinSettings
    {
        /// <summary>Resources path of the yard snapshot (pipeline/export_yard.py), used until/unless the ERP answers.</summary>
        public string SnapshotResource = "ShipyardTwin/yard.snapshot";

        /// <summary>Resources folder holding block models as &lt;model_asset&gt;.bytes (OBJ text).</summary>
        public string ModelFolder = "ShipyardTwin/Models/";

        /// <summary>Plan day the yard is shown at (yyyy-MM-dd); empty = today. Command line -shipyard-asof.</summary>
        public string AsOf = "";

        public float DaysPerSecond = 4f;
        public float LiftDays = 2f;

        /// <summary>Frame rate cap; 0 = display refresh (vSync). Command line -shipyard-fps.</summary>
        public int TargetFps = 0;

        /// <summary>high | medium | low | off (shadow distance, cascades, map size). Command line -shipyard-shadows.</summary>
        public string Shadows = "high";

        /// <summary>Render every Nth frame while nothing moves (1 = always). Command line -shipyard-idle-render.</summary>
        public int IdleRenderInterval = 1;
    }

    /// <summary>
    /// Loads the yard plan: the exported snapshot synchronously (so the scene is built at once), then the ERP in the
    /// background (<see cref="YardDataSource"/>). Registers the data source and, for the environment, camera and
    /// crane built around one hull, a <see cref="TwinFile"/> of the first hull with the yard facilities.
    /// Command line: <c>-shipyard-asof yyyy-MM-dd</c> plan day, <c>-shipyard-hull ID</c> preselects a hull,
    /// <c>-shipyard-seek &lt;days&gt;</c> moves its simulation (screenshots).
    /// </summary>
    public sealed class ShipyardTwinBootstrap : CoreBootstrap
    {
        public const string SeekArgument = "-shipyard-seek";
        public const string AsOfArgument = "-shipyard-asof";
        public const string SnapshotFileArgument = "-shipyard-snapshot-file";

        /// <summary>Plan day for tests (yyyy-MM-dd), ahead of the command line and settings; null in normal runs.</summary>
        public static string AsOfOverride;

        [Tooltip("Optional JSON overriding ShipyardTwinSettings fields, e.g. {\"DaysPerSecond\":8}.")]
        [SerializeField] private TextAsset _settingsJson;

        [Tooltip("Base material (Built-in Standard) copied for every paint in ShipPalette.")]
        [SerializeField] private Material _baseMaterial;

        protected override void Configure(CoreContext context)
        {
            var settings = context.Settings.SetDefaults(new ShipyardTwinSettings(), _settingsJson != null ? _settingsJson.text : null);
            context.Services.Register(new ShipPalette(_baseMaterial));
            context.Services.Register(new YardDisplayLayers());
            var asOfText = AsOfOverride ?? ArgumentValue(AsOfArgument) ?? WebHost.Query("asof") ?? settings.AsOf;
            var asOf = YardModel.ParseDay(asOfText) ?? DateTime.Today;

            var started = Time.realtimeSinceStartup;
            YardSnapshot snapshot = null;
            var file = ArgumentValue(SnapshotFileArgument);
            if (!string.IsNullOrEmpty(file) && System.IO.File.Exists(file))
            {
                snapshot = YardSnapshot.Parse(System.IO.File.ReadAllText(file));   // benchmarks and other datasets
            }

            var asset = snapshot == null ? Resources.Load<TextAsset>(settings.SnapshotResource) : null;
            if (asset != null)
            {
                try
                {
                    snapshot = YardSnapshot.Parse(asset.text);
                }
                catch (FormatException exception)
                {
                    context.Log.Error("ShipyardTwin", exception.Message);
                }

                Resources.UnloadAsset(asset);
            }

            var model = YardModel.Build(snapshot ?? new YardSnapshot().Normalised(), asOf);
            var source = new YardDataSource(model, asOf, context.Log);
            context.Services.Register(source);
            context.Services.Register(model.ToTwinFile(model.Hulls.FirstOrDefault()));
            context.Log.Info("ShipyardTwin", "Yard snapshot: " + model.Hulls.Count + " hulls, " + model.Blocks.Count + " blocks, plan day " +
                                             asOf.ToString("yyyy-MM-dd") + " (" + (Time.realtimeSinceStartup - started).ToString("0.000") + " s)");
            RenderBudget.Apply(settings);
            source.Refresh();
        }

        private static string ArgumentValue(string name)
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
    }
}
