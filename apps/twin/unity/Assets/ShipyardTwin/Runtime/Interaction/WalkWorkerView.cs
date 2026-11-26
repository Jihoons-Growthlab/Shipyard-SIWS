using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace ShipyardTwin
{
    /// <summary>
    /// Third-person walker of the yard walk (<see cref="OrbitCameraRig"/> walk mode): one of three original figures built
    /// from primitives, picked by <see cref="Show"/> (appearance only; it never touches the account, its rights or titles).
    /// Each has its own silhouette, gait and standing pose, in the playful wuxia look asked for:
    /// <list type="bullet">
    /// <item><see cref="WalkAvatar.Worker"/> (사원): a ragged labourer, stooped and hurried. Patched hemp shirt under a torn
    /// vest, rolled-up trousers with frayed hems, rope belt, straw sandals and leg wraps, a red headband with flying tails,
    /// and a wooden A-frame carrier (지게) with a bundle of logs on the back. Big arm swing.</item>
    /// <item><see cref="WalkAvatar.Steward"/> (관리자): a tidy middle-class clerk, upright and measured. Knee-length teal
    /// robe with a white crossed collar, ochre sash with a wooden name tag, two-tier black cap, neat moustache, a ledger
    /// held against the chest in the left hand; the right arm swings a little.</item>
    /// <item><see cref="WalkAvatar.Master"/> (임원): a very old martial master near the end of his days. Bent back, long
    /// faded robe with patches and frayed hem, wide sleeves, crimson sash with a gourd, white topknot and long hair, long
    /// brows and a long flowing beard, the left hand behind the back and a gnarled staff in the right, planted with each
    /// left step.</item>
    /// </list>
    /// All three keep a strong colour accent (red headband, ochre sash and white collar, white hair and crimson sash)
    /// so the figure reads against grey steel and concrete. Standing, they breathe, shift their weight and look around
    /// (the clerk glances at his ledger); walking, cloth tails, beard and tassels trail and flap.
    /// Root <see cref="BodyRootName"/> stands exactly on the feet point, turned by the body yaw only; each figure is a
    /// child built on first use, so a late account answer only swaps the figure.
    /// Gait from the rig: <c>phase</c> 0..1 is one cycle, 0.0 = left heel strike, 0.5 = right heel strike; <c>weight</c>
    /// 0..1 from the walking speed scales stride, bob and swings, so the stance settles when it falls to zero.
    /// No colliders (Ignore Raycast layer), no hover target: object search, picking and the camera's wall check ignore it.
    /// Materials use the scene's shared yard palette, which supplies the same shader in the Editor and WebGL.
    /// </summary>
    internal sealed class WalkWorkerView : IDisposable
    {
        /// <summary>World-space root of the walker (empty transform, scene root, at the feet).</summary>
        public const string BodyRootName = "WalkWorker";

        private const int IgnoreRaycastLayer = 2;

        private enum ArmMode { Swing, Ledger, BehindBack, Staff }

        // Worker (ragged labourer).
        private static readonly Color WorkerSkin = new Color(0.63f, 0.45f, 0.32f);
        private static readonly Color Hemp = new Color(0.64f, 0.58f, 0.46f);
        private static readonly Color Vest = new Color(0.37f, 0.27f, 0.17f);
        private static readonly Color Trousers = new Color(0.5f, 0.45f, 0.36f);
        private static readonly Color TrouserCuff = new Color(0.42f, 0.38f, 0.3f);
        private static readonly Color PatchBrown = new Color(0.47f, 0.34f, 0.2f);
        private static readonly Color PatchBlue = new Color(0.3f, 0.35f, 0.38f);
        private static readonly Color PatchOchre = new Color(0.6f, 0.49f, 0.3f);
        private static readonly Color Rope = new Color(0.7f, 0.58f, 0.32f);
        private static readonly Color Straw = new Color(0.82f, 0.7f, 0.42f);
        private static readonly Color WrapGrey = new Color(0.55f, 0.53f, 0.5f);
        private static readonly Color Headband = new Color(0.84f, 0.15f, 0.11f);
        private static readonly Color HairDark = new Color(0.08f, 0.07f, 0.06f);
        private static readonly Color Wood = new Color(0.45f, 0.3f, 0.16f);
        private static readonly Color Log = new Color(0.55f, 0.42f, 0.28f);

        // Steward (clerk).
        private static readonly Color StewardSkin = new Color(0.78f, 0.6f, 0.47f);
        private static readonly Color Robe = new Color(0.17f, 0.34f, 0.39f);
        private static readonly Color Collar = new Color(0.93f, 0.91f, 0.86f);
        private static readonly Color Sash = new Color(0.85f, 0.61f, 0.17f);
        private static readonly Color UnderTrousers = new Color(0.78f, 0.76f, 0.7f);
        private static readonly Color ShoeBlack = new Color(0.07f, 0.07f, 0.08f);
        private static readonly Color SoleWhite = new Color(0.86f, 0.85f, 0.8f);
        private static readonly Color CapBlack = new Color(0.06f, 0.06f, 0.07f);
        private static readonly Color LedgerCover = new Color(0.16f, 0.21f, 0.44f);
        private static readonly Color Pages = new Color(0.9f, 0.86f, 0.74f);
        private static readonly Color TagWood = new Color(0.42f, 0.28f, 0.15f);
        private static readonly Color Tassel = new Color(0.75f, 0.12f, 0.12f);

        // Master (old martial master).
        private static readonly Color MasterSkin = new Color(0.8f, 0.67f, 0.57f);
        private static readonly Color OldRobe = new Color(0.76f, 0.74f, 0.68f);
        private static readonly Color RobePatch = new Color(0.62f, 0.6f, 0.54f);
        private static readonly Color RobeInner = new Color(0.22f, 0.22f, 0.25f);
        private static readonly Color Crimson = new Color(0.52f, 0.1f, 0.12f);
        private static readonly Color WhiteHair = new Color(0.94f, 0.94f, 0.93f);
        private static readonly Color Gourd = new Color(0.76f, 0.5f, 0.22f);
        private static readonly Color Staff = new Color(0.32f, 0.21f, 0.12f);
        private static readonly Color DarkTrousers = new Color(0.3f, 0.3f, 0.32f);
        private static readonly Color ClothShoe = new Color(0.2f, 0.2f, 0.22f);
        private static readonly Color Eye = new Color(0.05f, 0.04f, 0.04f);

        private readonly Dictionary<Color, Material> _materials = new Dictionary<Color, Material>();
        private readonly Dictionary<PrimitiveType, Mesh> _meshes = new Dictionary<PrimitiveType, Mesh>();
        private readonly Dictionary<WalkAvatar, Figure> _figures = new Dictionary<WalkAvatar, Figure>();
        private readonly ShipPalette _palette;
        private Transform _root;
        private Figure _figure;
        private float _weight, _phase;
        private bool _shown, _posed;

        /// <summary>The figure shown (or last shown).</summary>
        public WalkAvatar Avatar { get; private set; } = WalkAvatar.Worker;

        public WalkWorkerView(ShipPalette palette) => _palette = palette ?? throw new ArgumentNullException(nameof(palette));

        private sealed class Style
        {
            public float HipHeight, HipHalfWidth, Thigh, Shin, Ankle, ToeAhead = 0.13f, HeelBehind = 0.1f;
            public float StrideHalf, FootLift, HeelLiftDeg = 16f, ToeUpDeg = 7f, HipBob, HipSway, IdleShift;
            public float WaistUp = 0.08f, SpineLength, ShoulderHalfWidth, ShoulderDrop = 0.06f, UpperArm, Forearm;
            public float ArmSwingDeg, ElbowDeg, AbductDeg = 7f;
            public float StoopDeg, WalkLeanDeg, HeadCompensation, TwistDeg, LookAroundDeg, NodDeg, Seed;
            /// <summary>Robe skirt: panels hang from this height above the hip joints, this long (0: none).</summary>
            public float SkirtTop = 0.06f, SkirtLength;
        }

        private sealed class Figure
        {
            public Style S;
            public Transform Root, Hips, Spine, Head, Staff;
            public readonly Leg[] Legs = new Leg[2];
            public readonly Arm[] Arms = new Arm[2];
            public readonly List<Panel> Panels = new List<Panel>();
            public readonly List<Swinger> Swingers = new List<Swinger>();
        }

        private sealed class Leg
        {
            public Transform Thigh, Shin, Foot;
            public float Side;
            public Vector3 Hip, Knee;
        }

        private sealed class Arm
        {
            public Transform Upper, Fore, Hand;
            public float Side;
            public ArmMode Mode;
            /// <summary>Wrist target in spine space (ledger hold, hand behind the back).</summary>
            public Vector3 Target;
        }

        /// <summary>Robe panel hanging from the waist, following its leg's thigh part of the way.</summary>
        private sealed class Panel
        {
            public Transform T;
            public int Leg;
            public float Follow;
            public Quaternion Flare;
        }

        /// <summary>Loose part (cloth tail, beard, tassel, gourd) that trails back while walking and flaps a little.</summary>
        private sealed class Swinger
        {
            public Transform T;
            public Quaternion Rest;
            public float Trail, Amp, Seed;
        }

        /// <summary>
        /// Shows the figure for this avatar (built on first use). Callable again at any time, also while walking: only
        /// the figure changes, in the current pose, so a late account answer just swaps the look.
        /// </summary>
        public void Show(WalkAvatar avatar)
        {
            if (!Enum.IsDefined(typeof(WalkAvatar), avatar)) avatar = WalkAvatar.Worker;
            if (_root == null)
            {
                _root = Empty(BodyRootName, null);
                _root.gameObject.hideFlags = HideFlags.DontSave;
            }

            if (!_figures.TryGetValue(avatar, out var figure))
            {
                figure = Build(avatar);
                _figures[avatar] = figure;
            }

            if (_figure != figure)
            {
                if (_figure != null) _figure.Root.gameObject.SetActive(false);
                _figure = figure;
            }

            figure.Root.gameObject.SetActive(true);
            Avatar = avatar;
            if (!_shown) _weight = 0f;
            _shown = true;
            _root.gameObject.SetActive(true);
            if (_posed) Pose(figure, _phase, _weight);
        }

        /// <param name="feet">World point on the ground under the walker.</param>
        /// <param name="yaw">Body heading, degrees (Unity y rotation).</param>
        /// <param name="phase">Gait cycle 0..1: 0 left heel strike, 0.5 right heel strike.</param>
        /// <param name="weight">0 standing .. 1 full walk (from the speed).</param>
        public void Update(Vector3 feet, float yaw, float phase, float weight)
        {
            if (!_shown || _figure == null)
            {
                return;
            }

            // Short smoothing so a sudden stop settles instead of snapping; the rig's weight already eases.
            var dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            _weight = Mathf.Lerp(_weight, Mathf.Clamp01(weight), 1f - Mathf.Exp(-12f * dt));
            if (_weight < 0.002f) _weight = 0f;
            _phase = phase - Mathf.Floor(phase);
            _root.SetPositionAndRotation(feet, Quaternion.Euler(0f, yaw, 0f));
            _posed = true;
            Pose(_figure, _phase, _weight);
        }

        /// <summary>Hides at once; the next <see cref="Show"/> starts from a settled stance.</summary>
        public void Hide()
        {
            _shown = false;
            if (_root != null) _root.gameObject.SetActive(false);
        }

        public void Dispose()
        {
            _shown = _posed = false;
            if (_root != null) Release(_root.gameObject);
            _root = null;
            _figure = null;
            _figures.Clear();
            foreach (var material in _materials.Values) Release(material);
            _materials.Clear();
            _meshes.Clear();   // built-in primitive meshes: shared, not ours to destroy
        }

        // ---------------------------------------------------------------- pose

        private static void Pose(Figure f, float phase, float w)
        {
            var s = f.S;
            var t = Time.unscaledTime;
            var idle = 1f - w;
            var cycle = 2f * Mathf.PI * phase;
            var breath = Mathf.Sin(t * 1.45f + s.Seed);   // about one breath in 4 s

            // Hips: lowest at each heel strike, highest mid-stance; a sway over the stance foot; standing, a slow weight shift.
            var hipY = s.HipHeight - s.HipBob * w * Mathf.Cos(2f * cycle);
            var sway = s.HipSway * w * Mathf.Sin(cycle) + s.IdleShift * idle * Mathf.Sin(t * 0.45f + s.Seed);
            f.Hips.localPosition = new Vector3(sway, hipY, 0f);

            for (var i = 0; i < 2; i++)
            {
                var leg = f.Legs[i];
                var foot = phase + (i == 0 ? 0f : 0.5f);   // left 0, right 0.5
                PoseLeg(s, leg, foot - Mathf.Floor(foot), w, new Vector3(sway + leg.Side * s.HipHalfWidth, hipY, 0f));
            }

            // Spine: the figure's stoop, a lean into the walk, breathing; shoulders turn against the hips.
            var twist = -s.TwistDeg * w * Mathf.Cos(cycle);
            var lean = s.StoopDeg + s.WalkLeanDeg * w;
            f.Spine.localPosition = new Vector3(sway, hipY + s.WaistUp, 0f);
            f.Spine.localRotation = Quaternion.Euler(lean + 1.2f * breath * (0.3f + 0.7f * idle), twist, 0f);

            // Head: looks ahead over the stoop; standing, it looks around (and nods, e.g. a glance at the ledger).
            var look = s.LookAroundDeg * idle * Mathf.Sin(t * 0.31f + s.Seed);
            var nod = s.NodDeg * idle * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.2f, 0.8f, Mathf.Sin(t * 0.23f + s.Seed)));
            f.Head.localRotation = Quaternion.Euler(-lean * s.HeadCompensation + nod + 1.5f * w * Mathf.Cos(2f * cycle), look - twist * 0.7f, 0f);

            var grip = f.Staff != null ? PoseStaff(f, phase, w, hipY, sway) : Vector3.zero;
            foreach (var arm in f.Arms) PoseArm(f, arm, cycle, w, grip);

            foreach (var panel in f.Panels)
            {
                var leg = f.Legs[panel.Leg];
                var along = leg.Knee - leg.Hip;
                var follow = along.sqrMagnitude > 1e-6f
                    ? Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(Vector3.down, along), panel.Follow)
                    : Quaternion.identity;
                panel.T.localPosition = new Vector3(sway + leg.Side * s.HipHalfWidth * 0.7f, hipY + s.SkirtTop, 0f);
                panel.T.localRotation = follow * panel.Flare;
            }

            foreach (var loose in f.Swingers)
            {
                var flap = loose.Amp * (w * Mathf.Sin(2f * cycle + loose.Seed) + 0.3f * Mathf.Sin(t * 1.7f + loose.Seed));
                loose.T.localRotation = loose.Rest * Quaternion.Euler(loose.Trail * w + flap, 0f, 0.5f * loose.Amp * w * Mathf.Cos(cycle + loose.Seed));
            }
        }

        /// <summary>
        /// One leg at gait position u (0 heel strike .. 0.5 toe-off .. 1 next heel strike): stance slides the planted foot
        /// back under the body and lifts the heel near its end; swing carries it forward slightly raised, toe up for the
        /// strike. Two-bone knee, bending forward.
        /// </summary>
        private static void PoseLeg(Style s, Leg leg, float u, float w, Vector3 hip)
        {
            float z, lift, pitch;
            if (u < 0.5f)
            {
                var k = u / 0.5f;
                z = s.StrideHalf * w * Mathf.Lerp(1f, -1f, k);
                lift = 0f;
                pitch = s.HeelLiftDeg * w * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.6f, 1f, k));
            }
            else
            {
                var k = (u - 0.5f) / 0.5f;
                z = -s.StrideHalf * w * Mathf.Cos(Mathf.PI * k);
                lift = s.FootLift * w * Mathf.Sin(Mathf.PI * k);
                pitch = k < 0.6f ? Mathf.Lerp(s.HeelLiftDeg, -s.ToeUpDeg, Mathf.SmoothStep(0f, 1f, k / 0.6f)) * w
                                 : Mathf.Lerp(-s.ToeUpDeg, 0f, Mathf.SmoothStep(0f, 1f, (k - 0.6f) / 0.4f)) * w;
            }

            // The foot rolls on the toe (heel up) or on the heel (toe up), so the contact point stays on the ground.
            var ground = new Vector3(leg.Side * s.HipHalfWidth, lift, z);
            var roll = Quaternion.Euler(pitch, 0f, 0f);
            var pivot = pitch >= 0f ? ground + Vector3.forward * s.ToeAhead : ground - Vector3.forward * s.HeelBehind;
            var ankle = pivot + roll * (ground + Vector3.up * s.Ankle - pivot);
            leg.Foot.localPosition = ankle;
            leg.Foot.localRotation = roll;

            var knee = Joint(hip, ankle, s.Thigh, s.Shin, Vector3.forward);
            leg.Hip = hip;
            leg.Knee = knee;
            Stretch(leg.Thigh, hip, knee);
            Stretch(leg.Shin, knee, ankle);
        }

        /// <summary>
        /// Arm from the shoulder (on the turning, stooping spine): a free swing counter to the legs with the elbow a little
        /// bent (more as it comes forward), or a two-bone reach to a held pose (ledger, hand behind the back, staff grip).
        /// </summary>
        private static void PoseArm(Figure f, Arm arm, float cycle, float w, Vector3 grip)
        {
            var s = f.S;
            var side = arm.Side;
            var shoulder = SpinePoint(f, new Vector3(side * s.ShoulderHalfWidth, s.SpineLength - s.ShoulderDrop, 0f));
            Vector3 elbow, wrist, up;
            if (arm.Mode == ArmMode.Swing)
            {
                var forward = side * Mathf.Cos(cycle);   // +1 when this arm is furthest forward (right arm with the left foot)
                var swing = s.ArmSwingDeg * w * forward * Mathf.Deg2Rad;
                var abduct = s.AbductDeg * Mathf.Deg2Rad;
                var upper = new Vector3(side * Mathf.Sin(abduct), -Mathf.Cos(swing) * Mathf.Cos(abduct), Mathf.Sin(swing)).normalized;
                elbow = shoulder + upper * s.UpperArm;
                var bend = swing + (s.ElbowDeg + 0.6f * s.ArmSwingDeg * w * Mathf.Max(0f, forward)) * Mathf.Deg2Rad;
                var fore = new Vector3(side * Mathf.Sin(abduct * 0.5f), -Mathf.Cos(bend), Mathf.Sin(bend)).normalized;
                wrist = elbow + fore * s.Forearm;
                up = new Vector3(side, 0f, 0f);   // back of the hand outwards, palm to the thigh
            }
            else
            {
                Vector3 target, pole;
                switch (arm.Mode)
                {
                    case ArmMode.Ledger:
                        target = SpinePoint(f, arm.Target);
                        pole = new Vector3(side, -0.6f, -0.4f);
                        up = Vector3.down;   // palm up under the book
                        break;
                    case ArmMode.BehindBack:
                        target = SpinePoint(f, arm.Target);
                        pole = new Vector3(side, -0.3f, -0.5f);
                        up = Vector3.back;
                        break;
                    default:
                        target = grip;
                        pole = new Vector3(side, -0.4f, -0.6f);
                        up = new Vector3(side, 0f, 0f);
                        break;
                }

                elbow = Joint(shoulder, target, s.UpperArm, s.Forearm, pole);
                var reach = target - elbow;
                wrist = elbow + (reach.sqrMagnitude > 1e-8f ? reach.normalized : Vector3.down) * s.Forearm;
            }

            Stretch(arm.Upper, shoulder, elbow);
            Stretch(arm.Fore, elbow, wrist);
            arm.Hand.localPosition = wrist;
            var dir = wrist - elbow;
            arm.Hand.localRotation = Quaternion.LookRotation(dir.sqrMagnitude > 1e-8f ? dir.normalized : Vector3.down, up);
        }

        /// <summary>
        /// The master's staff on his right: planted with the left foot (moves back with the ground during that stance),
        /// swung forward slightly raised during the right stance. Returns the grip point for the right hand.
        /// </summary>
        private static Vector3 PoseStaff(Figure f, float phase, float w, float hipY, float sway)
        {
            var s = f.S;
            float z, lift;
            if (phase < 0.5f)
            {
                z = s.StrideHalf * w * Mathf.Lerp(1f, -1f, phase / 0.5f);
                lift = 0f;
            }
            else
            {
                var k = (phase - 0.5f) / 0.5f;
                z = -s.StrideHalf * w * Mathf.Cos(Mathf.PI * k);
                lift = 0.07f * w * Mathf.Sin(Mathf.PI * k);
            }

            var tip = new Vector3(0.36f, lift, 0.26f + z);
            var grip = new Vector3(sway + 0.3f, hipY + 0.08f, 0.14f + 0.4f * z);
            f.Staff.localPosition = tip;
            f.Staff.localRotation = Quaternion.FromToRotation(Vector3.up, grip - tip);
            return grip;
        }

        private static Vector3 SpinePoint(Figure f, Vector3 local) => f.Spine.localPosition + f.Spine.localRotation * local;

        /// <summary>Middle joint of a two-bone chain from a to b, bent towards the pole.</summary>
        private static Vector3 Joint(Vector3 a, Vector3 b, float first, float second, Vector3 pole)
        {
            var reach = b - a;
            var d = Mathf.Clamp(reach.magnitude, 0.05f, first + second - 0.001f);
            var dir = reach.sqrMagnitude > 1e-8f ? reach.normalized : Vector3.down;
            var along = (first * first - second * second + d * d) / (2f * d);
            var h = Mathf.Sqrt(Mathf.Max(0f, first * first - along * along));
            var bend = pole - dir * Vector3.Dot(pole, dir);
            bend = bend.sqrMagnitude > 1e-6f ? bend.normalized : Vector3.Cross(dir, Vector3.right).normalized;
            return a + dir * along + bend * h;
        }

        /// <summary>A part whose local y axis runs from one figure-local point to another (unit-height child mesh).</summary>
        private static void Stretch(Transform part, Vector3 from, Vector3 to)
        {
            var along = to - from;
            part.localPosition = (from + to) / 2f;
            part.localRotation = along.sqrMagnitude > 1e-8f ? Quaternion.FromToRotation(Vector3.up, along) : Quaternion.identity;
            part.localScale = new Vector3(1f, Mathf.Max(0.01f, along.magnitude), 1f);
        }

        // ---------------------------------------------------------------- figures

        private Figure Build(WalkAvatar avatar)
        {
            switch (avatar)
            {
                case WalkAvatar.Steward: return BuildSteward();
                case WalkAvatar.Master: return BuildMaster();
                default: return BuildWorker();
            }
        }

        /// <summary>Skeleton transforms shared by every figure (parts are added by the figure's builder).</summary>
        private Figure Skeleton(string name, Style s)
        {
            var f = new Figure { S = s };
            f.Root = Empty(name, _root);
            f.Hips = Empty("Hips", f.Root);
            f.Spine = Empty("Spine", f.Root);
            f.Head = Empty("Head", f.Spine);
            f.Head.localPosition = new Vector3(0f, s.SpineLength + 0.05f, 0.02f);
            return f;
        }

        /// <summary>사원: ragged labourer with an A-frame carrier.</summary>
        private Figure BuildWorker()
        {
            var s = new Style
            {
                HipHeight = 0.84f, HipHalfWidth = 0.1f, Thigh = 0.43f, Shin = 0.42f, Ankle = 0.06f,
                StrideHalf = 0.42f, FootLift = 0.09f, HipBob = 0.025f, HipSway = 0.02f, IdleShift = 0.015f,
                SpineLength = 0.5f, ShoulderHalfWidth = 0.2f, UpperArm = 0.28f, Forearm = 0.26f,
                ArmSwingDeg = 22f, ElbowDeg = 18f, StoopDeg = 12f, WalkLeanDeg = 6f, HeadCompensation = 0.75f,
                TwistDeg = 7f, LookAroundDeg = 14f, NodDeg = 0f, Seed = 0.3f,
            };
            var f = Skeleton("Worker", s);

            // Hips: trousers, rope belt with a knot and loose ends.
            Part("Seat", f.Hips, PrimitiveType.Cube, new Vector3(0f, 0.02f, 0f), new Vector3(0.32f, 0.18f, 0.2f), Trousers);
            Part("RopeBelt", f.Hips, PrimitiveType.Cube, new Vector3(0f, 0.1f, 0f), new Vector3(0.335f, 0.035f, 0.215f), Rope);
            Part("RopeKnot", f.Hips, PrimitiveType.Sphere, new Vector3(0.06f, 0.1f, 0.11f), Vector3.one * 0.05f, Rope);
            for (var i = 0; i < 2; i++)
            {
                var end = Loose(f, "RopeEnd", f.Hips, new Vector3(0.05f + i * 0.025f, 0.09f, 0.115f), Quaternion.Euler(-6f, 0f, i * 8f), 22f, 14f, i * 1.3f);
                Part("Cord", end, PrimitiveType.Cube, new Vector3(0f, -0.07f + i * 0.015f, 0f), new Vector3(0.016f, 0.14f - i * 0.03f, 0.016f), Rope);
            }

            Part("SeatPatch", f.Hips, PrimitiveType.Cube, new Vector3(-0.08f, 0.0f, -0.102f), new Vector3(0.09f, 0.08f, 0.008f), PatchBlue);

            // Torso: hemp shirt under a torn vest, patches, a frayed hem.
            var spine = f.Spine;
            Part("Shirt", spine, PrimitiveType.Capsule, new Vector3(0f, 0.2f, 0f), new Vector3(0.33f, 0.33f, 0.21f), Hemp);
            Part("VestBack", spine, PrimitiveType.Cube, new Vector3(0f, 0.24f, -0.095f), new Vector3(0.35f, 0.42f, 0.04f), Vest);
            for (var i = 0; i < 2; i++)
            {
                var side = i == 0 ? -1f : 1f;
                Part("VestFront", spine, PrimitiveType.Cube, new Vector3(side * 0.11f, 0.23f, 0.095f), new Vector3(0.11f, 0.4f - i * 0.05f, 0.04f), Vest)
                    .localRotation = Quaternion.Euler(0f, 0f, side * 4f);
            }

            Part("VestPatch", spine, PrimitiveType.Cube, new Vector3(-0.12f, 0.3f, 0.118f), new Vector3(0.075f, 0.07f, 0.01f), PatchOchre)
                .localRotation = Quaternion.Euler(0f, 0f, 9f);
            Part("BackPatch", spine, PrimitiveType.Cube, new Vector3(0.07f, 0.1f, -0.118f), new Vector3(0.08f, 0.12f, 0.01f), PatchBrown)
                .localRotation = Quaternion.Euler(0f, 0f, -6f);
            Rags(spine, "ShirtHem", new Vector3(0f, -0.08f, 0f), 0.165f, 0.105f, 7, 0.05f, 0.11f, Hemp);
            Segment("Shoulders", spine, PrimitiveType.Capsule, new Vector3(-0.19f, 0.44f, 0f), new Vector3(0.19f, 0.44f, 0f), 0.12f, Hemp);
            Part("Neck", spine, PrimitiveType.Cylinder, new Vector3(0f, 0.53f, 0.01f), new Vector3(0.1f, 0.05f, 0.1f), WorkerSkin);

            // A-frame carrier (지게) on the back: two poles, cross bars, back prongs, a bundle of logs, straps.
            for (var i = 0; i < 2; i++)
            {
                var side = i == 0 ? -1f : 1f;
                Segment("JigePole", spine, PrimitiveType.Cylinder, new Vector3(side * 0.17f, -0.45f, -0.24f), new Vector3(side * 0.11f, 0.76f, -0.2f), 0.04f, Wood);
                Segment("JigeProng", spine, PrimitiveType.Cylinder, new Vector3(side * 0.16f, -0.28f, -0.23f), new Vector3(side * 0.16f, -0.08f, -0.52f), 0.03f, Wood);
                Segment("JigeStrap", spine, PrimitiveType.Cube, new Vector3(side * 0.13f, 0.42f, -0.2f), new Vector3(side * 0.14f, 0.47f, 0.02f), 0.025f, Rope);
                Segment("JigeStrapFront", spine, PrimitiveType.Cube, new Vector3(side * 0.14f, 0.47f, 0.02f), new Vector3(side * 0.12f, 0.12f, 0.125f), 0.025f, Rope);
            }

            foreach (var y in new[] { -0.3f, 0.05f, 0.4f, 0.68f })
            {
                Segment("JigeBar", spine, PrimitiveType.Cylinder, new Vector3(-0.2f, y, -0.23f), new Vector3(0.2f, y, -0.23f), 0.03f, Wood);
            }

            foreach (var log in new[] { new Vector3(0f, -0.15f, -0.34f), new Vector3(0f, -0.15f, -0.45f), new Vector3(0f, -0.06f, -0.39f), new Vector3(0f, 0.03f, -0.35f) })
            {
                Segment("Log", spine, PrimitiveType.Cylinder, log + Vector3.left * 0.24f, log + Vector3.right * 0.24f, 0.085f, Log);
            }

            Segment("LogTie", spine, PrimitiveType.Cylinder, new Vector3(0f, 0.1f, -0.33f), new Vector3(0f, -0.22f, -0.5f), 0.02f, Rope);

            // Head: tanned face, messy topknot, red headband with flying tails.
            var head = f.Head;
            Face(head, WorkerSkin, HairDark);
            Part("Hair", head, PrimitiveType.Sphere, new Vector3(0f, 0.17f, -0.015f), new Vector3(0.2f, 0.15f, 0.2f), HairDark);
            Part("Topknot", head, PrimitiveType.Sphere, new Vector3(0.01f, 0.25f, -0.04f), new Vector3(0.07f, 0.06f, 0.07f), HairDark);
            Part("Headband", head, PrimitiveType.Cylinder, new Vector3(0f, 0.155f, 0f), new Vector3(0.208f, 0.018f, 0.212f), Headband);
            Part("HeadbandKnot", head, PrimitiveType.Sphere, new Vector3(0f, 0.155f, -0.106f), Vector3.one * 0.045f, Headband);
            for (var i = 0; i < 2; i++)
            {
                var side = i == 0 ? -1f : 1f;
                var tail = Loose(f, "HeadbandTail", head, new Vector3(side * 0.015f, 0.155f, -0.115f), Quaternion.Euler(18f, 0f, side * 12f), 50f, 16f, i * 2.1f);
                Part("Cloth", tail, PrimitiveType.Cube, new Vector3(0f, -0.085f, 0f), new Vector3(0.035f, 0.17f - i * 0.03f, 0.008f), Headband);
            }

            // Legs: hemp trousers rolled to the knee with frayed cuffs, bare shins, leg wraps, straw sandals.
            for (var i = 0; i < 2; i++)
            {
                var side = i == 0 ? -1f : 1f;
                var prefix = i == 0 ? "Left" : "Right";
                var thigh = Limb(f, prefix + "Thigh", 0.15f, Trousers);
                Band(thigh, "RolledCuff", 0.95f, 0.16f, 0.165f, TrouserCuff);
                Rags(thigh, "Fray", new Vector3(0f, 0.53f, 0f), 0.082f, 0.082f, 5, 0.06f, 0.14f, TrouserCuff);
                if (i == 1) Part("KneePatch", thigh, PrimitiveType.Cube, new Vector3(0f, 0.25f, 0.072f), new Vector3(0.08f, 0.2f, 0.012f), PatchBrown);
                var shin = Limb(f, prefix + "Shin", 0.095f, WorkerSkin);
                Band(shin, "LegWrap", 0.82f, 0.34f, 0.108f, WrapGrey);
                f.Legs[i] = new Leg { Side = side, Thigh = thigh, Shin = shin, Foot = Sandal(f, prefix + "Sandal") };
            }

            // Arms: torn short sleeves, bare forearms, open hands.
            for (var i = 0; i < 2; i++)
            {
                var side = i == 0 ? -1f : 1f;
                var prefix = i == 0 ? "Left" : "Right";
                var upper = Limb(f, prefix + "UpperArm", 0.11f, Hemp);
                Rags(upper, "SleeveFray", new Vector3(0f, 0.2f, 0f), 0.058f, 0.058f, 4, 0.12f, 0.25f, Hemp);
                Band(upper, "Skin", 0.85f, 0.32f, 0.085f, WorkerSkin);
                var fore = Limb(f, prefix + "Forearm", 0.082f, WorkerSkin);
                f.Arms[i] = new Arm { Side = side, Mode = ArmMode.Swing, Upper = upper, Fore = fore, Hand = Hand(f, prefix + "Hand", side, WorkerSkin, false) };
            }

            return f;
        }

        /// <summary>관리자: tidy clerk in a knee-length robe with a ledger.</summary>
        private Figure BuildSteward()
        {
            var s = new Style
            {
                HipHeight = 0.9f, HipHalfWidth = 0.095f, Thigh = 0.45f, Shin = 0.45f, Ankle = 0.07f,
                StrideHalf = 0.38f, FootLift = 0.06f, HipBob = 0.012f, HipSway = 0.01f, IdleShift = 0.006f,
                SpineLength = 0.52f, ShoulderHalfWidth = 0.2f, UpperArm = 0.29f, Forearm = 0.27f,
                ArmSwingDeg = 11f, ElbowDeg = 12f, StoopDeg = -2f, WalkLeanDeg = 1f, HeadCompensation = 0.5f,
                TwistDeg = 3f, LookAroundDeg = 9f, NodDeg = 14f, Seed = 1.7f, SkirtLength = 0.68f,
            };
            var f = Skeleton("Steward", s);
            Part("Seat", f.Hips, PrimitiveType.Cube, new Vector3(0f, 0.04f, 0f), new Vector3(0.33f, 0.16f, 0.21f), Robe);

            // Torso: robe, white crossed collar, ochre sash with tails and a wooden name tag.
            var spine = f.Spine;
            Part("Robe", spine, PrimitiveType.Capsule, new Vector3(0f, 0.2f, 0f), new Vector3(0.35f, 0.34f, 0.23f), Robe);
            Segment("Shoulders", spine, PrimitiveType.Capsule, new Vector3(-0.19f, 0.46f, 0f), new Vector3(0.19f, 0.46f, 0f), 0.13f, Robe);
            Segment("CollarOuter", spine, PrimitiveType.Cube, new Vector3(0.08f, 0.5f, 0.07f), new Vector3(-0.09f, 0.2f, 0.12f), 0.035f, Collar);
            Segment("CollarInner", spine, PrimitiveType.Cube, new Vector3(-0.08f, 0.5f, 0.07f), new Vector3(-0.02f, 0.38f, 0.112f), 0.035f, Collar);
            Part("NeckBand", spine, PrimitiveType.Cylinder, new Vector3(0f, 0.53f, 0.01f), new Vector3(0.12f, 0.03f, 0.12f), Collar);
            Part("Neck", spine, PrimitiveType.Cylinder, new Vector3(0f, 0.57f, 0.01f), new Vector3(0.095f, 0.04f, 0.095f), StewardSkin);
            Part("Sash", spine, PrimitiveType.Cube, new Vector3(0f, -0.02f, 0f), new Vector3(0.37f, 0.06f, 0.245f), Sash);
            Part("SashKnot", spine, PrimitiveType.Sphere, new Vector3(0.08f, -0.02f, 0.125f), new Vector3(0.06f, 0.05f, 0.04f), Sash);
            for (var i = 0; i < 2; i++)
            {
                var tail = Loose(f, "SashTail", spine, new Vector3(0.07f + i * 0.03f, -0.04f, 0.13f), Quaternion.Euler(-4f, 0f, i * 5f), 18f, 8f, i * 1.1f);
                Part("Cloth", tail, PrimitiveType.Cube, new Vector3(0f, -0.11f, 0f), new Vector3(0.04f, 0.22f - i * 0.04f, 0.01f), Sash);
            }

            var tag = Loose(f, "NameTag", spine, new Vector3(-0.13f, -0.05f, 0.11f), Quaternion.identity, 14f, 12f, 0.6f);
            Part("Cord", tag, PrimitiveType.Cube, new Vector3(0f, -0.04f, 0f), new Vector3(0.006f, 0.08f, 0.006f), Tassel);
            Part("Tag", tag, PrimitiveType.Cube, new Vector3(0f, -0.12f, 0f), new Vector3(0.04f, 0.09f, 0.012f), TagWood);
            Part("Tassel", tag, PrimitiveType.Capsule, new Vector3(0f, -0.21f, 0f), new Vector3(0.018f, 0.04f, 0.018f), Tassel);

            // Ledger held against the chest (left hand under its outer edge).
            var ledger = Empty("Ledger", spine);
            ledger.localPosition = new Vector3(-0.07f, 0.24f, 0.165f);
            ledger.localRotation = Quaternion.Euler(-12f, 12f, 0f);
            Part("Cover", ledger, PrimitiveType.Cube, Vector3.zero, new Vector3(0.17f, 0.23f, 0.03f), LedgerCover);
            Part("Pages", ledger, PrimitiveType.Cube, new Vector3(0.006f, 0f, 0f), new Vector3(0.165f, 0.22f, 0.024f), Pages);
            Part("Label", ledger, PrimitiveType.Cube, new Vector3(0.02f, 0.03f, 0.016f), new Vector3(0.05f, 0.11f, 0.004f), Pages);

            // Head: neat face, moustache, two-tier black cap.
            var head = f.Head;
            Face(head, StewardSkin, HairDark);
            Part("Hair", head, PrimitiveType.Sphere, new Vector3(0f, 0.14f, -0.03f), new Vector3(0.195f, 0.15f, 0.18f), HairDark);
            for (var i = 0; i < 2; i++)
            {
                var side = i == 0 ? -1f : 1f;
                Part("Moustache", head, PrimitiveType.Cube, new Vector3(side * 0.024f, 0.066f, 0.103f), new Vector3(0.042f, 0.009f, 0.012f), HairDark)
                    .localRotation = Quaternion.Euler(0f, 0f, side * -14f);
            }

            Part("Cap", head, PrimitiveType.Cube, new Vector3(0f, 0.235f, -0.005f), new Vector3(0.2f, 0.1f, 0.2f), CapBlack);
            Part("CapTop", head, PrimitiveType.Cube, new Vector3(0f, 0.31f, -0.005f), new Vector3(0.16f, 0.07f, 0.16f), CapBlack);
            Part("CapBand", head, PrimitiveType.Cube, new Vector3(0f, 0.19f, -0.005f), new Vector3(0.206f, 0.016f, 0.206f), Sash);

            // Legs: light trousers with ankle ties, black shoes with white soles and a small upturned toe.
            for (var i = 0; i < 2; i++)
            {
                var side = i == 0 ? -1f : 1f;
                var prefix = i == 0 ? "Left" : "Right";
                var thigh = Limb(f, prefix + "Thigh", 0.15f, UnderTrousers);
                var shin = Limb(f, prefix + "Shin", 0.12f, UnderTrousers);
                Band(shin, "AnkleTie", 0.9f, 0.1f, 0.128f, Robe);
                f.Legs[i] = new Leg { Side = side, Thigh = thigh, Shin = shin, Foot = Shoe(f, prefix + "Shoe", ShoeBlack, SoleWhite, true) };
                RobePanels(f, i, s.SkirtLength, Robe, 0.7f, 0.5f);
            }

            // Arms: robe sleeves with white cuffs; left holds the ledger, right swings.
            for (var i = 0; i < 2; i++)
            {
                var side = i == 0 ? -1f : 1f;
                var prefix = i == 0 ? "Left" : "Right";
                var upper = Limb(f, prefix + "UpperArm", 0.12f, Robe);
                var fore = Limb(f, prefix + "Forearm", 0.115f, Robe);
                Band(fore, "Cuff", 0.93f, 0.1f, 0.12f, Collar);
                f.Arms[i] = new Arm
                {
                    Side = side, Mode = i == 0 ? ArmMode.Ledger : ArmMode.Swing, Target = new Vector3(-0.1f, 0.11f, 0.17f),
                    Upper = upper, Fore = fore, Hand = Hand(f, prefix + "Hand", side, StewardSkin, false),
                };
            }

            return f;
        }

        /// <summary>임원: bent old martial master with a long beard, a staff and a gourd.</summary>
        private Figure BuildMaster()
        {
            var s = new Style
            {
                HipHeight = 0.85f, HipHalfWidth = 0.095f, Thigh = 0.44f, Shin = 0.44f, Ankle = 0.07f,
                StrideHalf = 0.32f, FootLift = 0.045f, HeelLiftDeg = 10f, HipBob = 0.008f, HipSway = 0.008f, IdleShift = 0.004f,
                SpineLength = 0.5f, ShoulderHalfWidth = 0.19f, UpperArm = 0.29f, Forearm = 0.27f,
                ArmSwingDeg = 0f, ElbowDeg = 10f, StoopDeg = 18f, WalkLeanDeg = 3f, HeadCompensation = 0.8f,
                TwistDeg = 2f, LookAroundDeg = 6f, NodDeg = 5f, Seed = 3.1f, SkirtLength = 0.8f,
            };
            var f = Skeleton("Master", s);
            Part("Seat", f.Hips, PrimitiveType.Cube, new Vector3(0f, 0.04f, 0f), new Vector3(0.32f, 0.16f, 0.21f), OldRobe);

            // Torso: faded robe with a dark inner collar, patches, crimson sash, a gourd at the right hip.
            var spine = f.Spine;
            Part("Robe", spine, PrimitiveType.Capsule, new Vector3(0f, 0.2f, 0f), new Vector3(0.34f, 0.33f, 0.22f), OldRobe);
            Segment("Shoulders", spine, PrimitiveType.Capsule, new Vector3(-0.18f, 0.44f, 0f), new Vector3(0.18f, 0.44f, 0f), 0.12f, OldRobe);
            Segment("CollarOuter", spine, PrimitiveType.Cube, new Vector3(0.08f, 0.49f, 0.06f), new Vector3(-0.1f, 0.18f, 0.115f), 0.04f, RobeInner);
            Segment("CollarInner", spine, PrimitiveType.Cube, new Vector3(-0.08f, 0.49f, 0.06f), new Vector3(-0.02f, 0.37f, 0.108f), 0.035f, RobeInner);
            Part("Neck", spine, PrimitiveType.Cylinder, new Vector3(0f, 0.53f, 0.01f), new Vector3(0.09f, 0.05f, 0.09f), MasterSkin);
            Part("ShoulderPatch", spine, PrimitiveType.Cube, new Vector3(0.1f, 0.38f, -0.108f), new Vector3(0.09f, 0.08f, 0.01f), RobePatch)
                .localRotation = Quaternion.Euler(0f, 0f, 7f);
            Part("Sash", spine, PrimitiveType.Cube, new Vector3(0f, -0.02f, 0f), new Vector3(0.36f, 0.07f, 0.24f), Crimson);
            for (var i = 0; i < 2; i++)
            {
                var tail = Loose(f, "SashTail", spine, new Vector3(-0.06f - i * 0.03f, -0.05f, 0.125f), Quaternion.Euler(-3f, 0f, -i * 5f), 16f, 7f, i * 1.4f);
                Part("Cloth", tail, PrimitiveType.Cube, new Vector3(0f, -0.14f, 0f), new Vector3(0.045f, 0.28f - i * 0.05f, 0.01f), Crimson);
            }

            var gourd = Loose(f, "Gourd", spine, new Vector3(0.17f, -0.04f, 0.06f), Quaternion.Euler(0f, 0f, 6f), 12f, 10f, 0.4f);
            Part("Cord", gourd, PrimitiveType.Cube, new Vector3(0f, -0.03f, 0f), new Vector3(0.008f, 0.06f, 0.008f), Crimson);
            Part("Stopper", gourd, PrimitiveType.Cylinder, new Vector3(0f, -0.065f, 0f), new Vector3(0.022f, 0.012f, 0.022f), Wood);
            Part("Top", gourd, PrimitiveType.Sphere, new Vector3(0f, -0.1f, 0f), Vector3.one * 0.065f, Gourd);
            Part("Bottom", gourd, PrimitiveType.Sphere, new Vector3(0f, -0.18f, 0f), new Vector3(0.1f, 0.11f, 0.1f), Gourd);

            // Head: pale face, white topknot with a pin, long hair down the back, long brows, moustache, long beard.
            var head = f.Head;
            Face(head, MasterSkin, WhiteHair);
            Part("Hair", head, PrimitiveType.Sphere, new Vector3(0f, 0.16f, -0.02f), new Vector3(0.205f, 0.16f, 0.21f), WhiteHair);
            Part("Topknot", head, PrimitiveType.Sphere, new Vector3(0f, 0.27f, -0.01f), new Vector3(0.08f, 0.075f, 0.08f), WhiteHair);
            Segment("HairPin", head, PrimitiveType.Cylinder, new Vector3(-0.07f, 0.275f, -0.01f), new Vector3(0.07f, 0.275f, -0.01f), 0.012f, Wood);
            var hair = Loose(f, "LongHair", head, new Vector3(0f, 0.12f, -0.09f), Quaternion.Euler(14f, 0f, 0f), 10f, 4f, 0.9f);
            Part("Strands", hair, PrimitiveType.Cube, new Vector3(0f, -0.16f, -0.005f), new Vector3(0.15f, 0.32f, 0.03f), WhiteHair);
            for (var i = 0; i < 2; i++)
            {
                var side = i == 0 ? -1f : 1f;
                Segment("Brow", head, PrimitiveType.Capsule, new Vector3(side * 0.022f, 0.145f, 0.1f), new Vector3(side * 0.09f, 0.115f, 0.085f), 0.017f, WhiteHair);
                Segment("Moustache", head, PrimitiveType.Capsule, new Vector3(side * 0.012f, 0.07f, 0.105f), new Vector3(side * 0.06f, 0.02f, 0.09f), 0.02f, WhiteHair);
            }

            var beard = Loose(f, "Beard", head, new Vector3(0f, 0.04f, 0.075f), Quaternion.Euler(-8f, 0f, 0f), 14f, 6f, 2.2f);
            Segment("Upper", beard, PrimitiveType.Capsule, Vector3.zero, new Vector3(0f, -0.14f, 0.01f), 0.085f, WhiteHair);
            Segment("Middle", beard, PrimitiveType.Capsule, new Vector3(0f, -0.14f, 0.01f), new Vector3(0f, -0.3f, 0f), 0.05f, WhiteHair);
            Segment("Tip", beard, PrimitiveType.Capsule, new Vector3(0f, -0.3f, 0f), new Vector3(0f, -0.4f, -0.01f), 0.025f, WhiteHair);

            // Legs under the long robe: dark trousers, grey cloth shoes with an upturned toe; patched, frayed hem panels.
            for (var i = 0; i < 2; i++)
            {
                var side = i == 0 ? -1f : 1f;
                var prefix = i == 0 ? "Left" : "Right";
                var thigh = Limb(f, prefix + "Thigh", 0.14f, DarkTrousers);
                var shin = Limb(f, prefix + "Shin", 0.115f, DarkTrousers);
                Band(shin, "LegWrap", 0.85f, 0.28f, 0.122f, OldRobe);
                f.Legs[i] = new Leg { Side = side, Thigh = thigh, Shin = shin, Foot = Shoe(f, prefix + "Shoe", ClothShoe, SoleWhite, true) };
                var front = RobePanels(f, i, s.SkirtLength, OldRobe, 0.75f, 0.55f);
                Part("HemPatch", front, PrimitiveType.Cube, new Vector3(side * 0.03f, -s.SkirtLength + 0.16f, 0.125f), new Vector3(0.08f, 0.1f, 0.01f), RobePatch);
                Rags(front, "HemFray", new Vector3(0f, -s.SkirtLength + 0.01f, 0.11f), 0.08f, 0.005f, 4, 0.03f, 0.06f, RobePatch);
            }

            // Arms: wide sleeves with a dark lining; left hand behind the back, right hand on the staff.
            for (var i = 0; i < 2; i++)
            {
                var side = i == 0 ? -1f : 1f;
                var prefix = i == 0 ? "Left" : "Right";
                var upper = Limb(f, prefix + "UpperArm", 0.13f, OldRobe);
                var fore = Limb(f, prefix + "Forearm", 0.12f, OldRobe);
                Band(fore, "WideSleeve", 0.68f, 0.6f, 0.23f, OldRobe);
                Band(fore, "Lining", 0.985f, 0.03f, 0.215f, RobeInner);
                f.Arms[i] = new Arm
                {
                    Side = side, Mode = i == 0 ? ArmMode.BehindBack : ArmMode.Staff, Target = new Vector3(0f, 0.04f, -0.19f),
                    Upper = upper, Fore = fore, Hand = Hand(f, prefix + "Hand", side, MasterSkin, true),
                };
            }

            // Gnarled staff with a hooked, knobbed top and a red tassel; posed from its ground tip.
            var staff = Empty("Staff", f.Root);
            Segment("Shaft", staff, PrimitiveType.Cylinder, Vector3.zero, new Vector3(0f, 1.62f, 0f), 0.036f, Staff);
            Part("Knot", staff, PrimitiveType.Sphere, new Vector3(0.008f, 0.48f, 0f), new Vector3(0.05f, 0.06f, 0.05f), Staff);
            Part("Knot", staff, PrimitiveType.Sphere, new Vector3(-0.006f, 1.12f, 0.004f), new Vector3(0.048f, 0.055f, 0.048f), Staff);
            Segment("Hook", staff, PrimitiveType.Capsule, new Vector3(0f, 1.6f, 0f), new Vector3(0.06f, 1.73f, 0.02f), 0.032f, Staff);
            Part("Head", staff, PrimitiveType.Sphere, new Vector3(0.07f, 1.75f, 0.02f), new Vector3(0.075f, 0.085f, 0.075f), Staff);
            var tassel = Loose(f, "Tassel", staff, new Vector3(0.02f, 1.58f, 0f), Quaternion.Euler(0f, 0f, 8f), 25f, 18f, 1.6f);
            Part("Cord", tassel, PrimitiveType.Cube, new Vector3(0f, -0.06f, 0f), new Vector3(0.008f, 0.12f, 0.008f), Tassel);
            Part("Tuft", tassel, PrimitiveType.Capsule, new Vector3(0f, -0.16f, 0f), new Vector3(0.025f, 0.05f, 0.025f), Tassel);
            f.Staff = staff;
            return f;
        }

        // ---------------------------------------------------------------- parts

        /// <summary>Face on a head transform (origin at the neck top): skull, nose, ears, eyes, brows.</summary>
        private void Face(Transform head, Color skin, Color brows)
        {
            Part("Face", head, PrimitiveType.Sphere, new Vector3(0f, 0.1f, 0.01f), new Vector3(0.19f, 0.23f, 0.2f), skin);
            Part("Nose", head, PrimitiveType.Sphere, new Vector3(0f, 0.095f, 0.108f), new Vector3(0.03f, 0.042f, 0.035f), skin);
            for (var i = 0; i < 2; i++)
            {
                var side = i == 0 ? -1f : 1f;
                Part("Ear", head, PrimitiveType.Sphere, new Vector3(side * 0.097f, 0.1f, 0f), new Vector3(0.03f, 0.05f, 0.04f), skin);
                Part("Eye", head, PrimitiveType.Sphere, new Vector3(side * 0.04f, 0.122f, 0.098f), new Vector3(0.024f, 0.016f, 0.012f), Eye);
                Part("Brow", head, PrimitiveType.Cube, new Vector3(side * 0.042f, 0.146f, 0.098f), new Vector3(0.046f, 0.01f, 0.012f), brows);
            }
        }

        /// <summary>Hand on a wrist transform, pointing +z with the back of the hand up (+y); relaxed fingers or a fist.</summary>
        private Transform Hand(Figure f, string name, float side, Color skin, bool fist)
        {
            var hand = Empty(name, f.Root);
            Part("Palm", hand, PrimitiveType.Sphere, new Vector3(0f, 0f, 0.045f), new Vector3(0.072f, 0.032f, 0.085f), skin);
            if (fist)
            {
                Part("Fist", hand, PrimitiveType.Sphere, new Vector3(0f, -0.008f, 0.085f), new Vector3(0.076f, 0.05f, 0.06f), skin);
                Segment("Thumb", hand, PrimitiveType.Capsule, new Vector3(-side * 0.03f, -0.01f, 0.035f), new Vector3(-side * 0.025f, -0.022f, 0.085f), 0.022f, skin);
                return hand;
            }

            for (var k = 0; k < 4; k++)
            {
                var root = new Vector3(-side * (0.026f - k * 0.017f), 0f, 0.083f);
                var tip = root + Quaternion.Euler(28f + k * 4f, 0f, 0f) * Vector3.forward * (k == 3 ? 0.05f : 0.064f);
                Segment("Finger" + (k + 1), hand, PrimitiveType.Capsule, root, tip, 0.017f, skin);
            }

            Segment("Thumb", hand, PrimitiveType.Capsule, new Vector3(-side * 0.032f, -0.006f, 0.03f), new Vector3(-side * 0.05f, -0.018f, 0.072f), 0.021f, skin);
            return hand;
        }

        /// <summary>Straw sandal on a bare foot with a grey ankle wrap (origin at the ankle).</summary>
        private Transform Sandal(Figure f, string name)
        {
            var s = f.S;
            var foot = Empty(name, f.Root);
            var mid = (s.ToeAhead - s.HeelBehind) / 2f;
            var length = s.ToeAhead + s.HeelBehind;
            var ground = -s.Ankle;
            Part("Sole", foot, PrimitiveType.Cube, new Vector3(0f, ground + 0.01f, mid), new Vector3(0.1f, 0.02f, length + 0.03f), Straw);
            Part("Foot", foot, PrimitiveType.Sphere, new Vector3(0f, ground + 0.04f, mid + 0.01f), new Vector3(0.088f, 0.06f, length), WorkerSkin);
            Part("ToeLoop", foot, PrimitiveType.Sphere, new Vector3(0f, ground + 0.03f, s.ToeAhead - 0.005f), new Vector3(0.06f, 0.035f, 0.05f), Straw);
            for (var i = 0; i < 2; i++)
            {
                var side = i == 0 ? -1f : 1f;
                Segment("Strap", foot, PrimitiveType.Cylinder, new Vector3(side * 0.045f, ground + 0.02f, -0.03f), new Vector3(0f, ground + 0.065f, 0.05f), 0.012f, Straw);
            }

            Part("Wrap", foot, PrimitiveType.Cylinder, new Vector3(0f, 0.02f, 0f), new Vector3(0.096f, 0.035f, 0.096f), WrapGrey);
            return foot;
        }

        /// <summary>Cloth shoe (origin at the ankle): sole, rounded upper, collar, optional small upturned toe.</summary>
        private Transform Shoe(Figure f, string name, Color upper, Color sole, bool upturned)
        {
            var s = f.S;
            var foot = Empty(name, f.Root);
            var mid = (s.ToeAhead - s.HeelBehind) / 2f;
            var length = s.ToeAhead + s.HeelBehind;
            var ground = -s.Ankle;
            Part("Sole", foot, PrimitiveType.Cube, new Vector3(0f, ground + 0.012f, mid), new Vector3(0.1f, 0.024f, length + 0.03f), sole);
            Part("Upper", foot, PrimitiveType.Sphere, new Vector3(0f, ground + 0.045f, mid - 0.005f), new Vector3(0.098f, 0.075f, length + 0.02f), upper);
            Part("Collar", foot, PrimitiveType.Cylinder, new Vector3(0f, -0.005f, -0.02f), new Vector3(0.092f, 0.03f, 0.1f), upper);
            if (upturned)
            {
                Segment("Toe", foot, PrimitiveType.Capsule, new Vector3(0f, ground + 0.035f, s.ToeAhead - 0.005f), new Vector3(0f, ground + 0.075f, s.ToeAhead + 0.03f), 0.028f, upper);
            }

            return foot;
        }

        /// <summary>
        /// Three robe panels (front, outer side, back) on one leg, hanging from the waist and following the thigh part of
        /// the way, flared a little at the hem. Returns the front panel (for patches).
        /// </summary>
        private Transform RobePanels(Figure f, int leg, float length, Color cloth, float follow, float backFollow)
        {
            var side = leg == 0 ? -1f : 1f;
            var prefix = leg == 0 ? "Left" : "Right";
            Transform front = null;
            for (var k = 0; k < 3; k++)
            {
                var panel = Empty(prefix + (k == 0 ? "RobeFront" : k == 1 ? "RobeSide" : "RobeBack"), f.Root);
                Vector3 offset, size;
                Quaternion flare;
                switch (k)
                {
                    case 0:
                        offset = new Vector3(side * 0.035f, -length / 2f, 0.112f);
                        size = new Vector3(0.2f, length, 0.024f);
                        flare = Quaternion.Euler(-5f, 0f, side * 2f);
                        front = panel;
                        break;
                    case 1:
                        offset = new Vector3(side * 0.1f, -length / 2f, 0f);
                        size = new Vector3(0.024f, length, 0.236f);
                        flare = Quaternion.Euler(0f, 0f, side * 7f);
                        break;
                    default:
                        offset = new Vector3(side * 0.035f, -length / 2f, -0.112f);
                        size = new Vector3(0.2f, length, 0.024f);
                        flare = Quaternion.Euler(5f, 0f, side * 2f);
                        break;
                }

                Part("Cloth", panel, PrimitiveType.Cube, offset, size, cloth);
                f.Panels.Add(new Panel { T = panel, Leg = leg, Follow = k == 2 ? backFollow : follow, Flare = flare });
            }

            return front;
        }

        /// <summary>Pivot of a loose part (registered to trail and flap); its parts hang from it along -y.</summary>
        private Transform Loose(Figure f, string name, Transform parent, Vector3 pivot, Quaternion rest, float trail, float amp, float seed)
        {
            var t = Empty(name, parent);
            t.localPosition = pivot;
            t.localRotation = rest;
            f.Swingers.Add(new Swinger { T = t, Rest = rest, Trail = trail, Amp = amp, Seed = seed });
            return t;
        }

        /// <summary>Limb segment: an empty joint stretched by <see cref="Stretch"/>, a capsule inside (y in limb lengths).</summary>
        private Transform Limb(Figure f, string name, float diameter, Color cloth)
        {
            var limb = Empty(name, f.Root);
            limb.localScale = new Vector3(1f, 0.4f, 1f);
            Part("Cloth", limb, PrimitiveType.Capsule, Vector3.zero, new Vector3(diameter, 0.5f, diameter), cloth);
            return limb;
        }

        /// <summary>A ring around a limb at <paramref name="at"/> (0 joint start .. 1 joint end), <paramref name="height"/> limb lengths tall.</summary>
        private void Band(Transform limb, string name, float at, float height, float diameter, Color color)
        {
            Part(name, limb, PrimitiveType.Cylinder, new Vector3(0f, at - 0.5f, 0f), new Vector3(diameter, height / 2f, diameter), color);
        }

        /// <summary>Frayed strips around an ellipse (radii rx, rz) hanging from <paramref name="centre"/> along -y, uneven lengths.</summary>
        private void Rags(Transform parent, string name, Vector3 centre, float rx, float rz, int count, float shortest, float longest, Color color)
        {
            for (var k = 0; k < count; k++)
            {
                var angle = 0.4f + k * 2f * Mathf.PI / count;
                var length = Mathf.Lerp(shortest, longest, (k * 7 % 3) / 2f);
                var at = centre + new Vector3(Mathf.Cos(angle) * rx, -length / 2f, Mathf.Sin(angle) * rz);
                Part(name, parent, PrimitiveType.Cube, at, new Vector3(0.03f, length, 0.01f), color)
                    .localRotation = Quaternion.Euler(4f * (k % 2 == 0 ? 1f : -1f), -(angle * Mathf.Rad2Deg + 90f), 0f);
            }
        }

        /// <summary>A primitive between two local points (capsules overlap their joints by their radius).</summary>
        private Transform Segment(string name, Transform parent, PrimitiveType shape, Vector3 from, Vector3 to, float diameter, Color color)
        {
            var along = to - from;
            var length = along.magnitude;
            // Unit primitives: capsule and cylinder are 2 tall, a cube 1.
            var height = shape == PrimitiveType.Capsule ? (length + diameter) / 2f : shape == PrimitiveType.Cube ? length : length / 2f;
            var part = Part(name, parent, shape, (from + to) / 2f, new Vector3(diameter, Mathf.Max(0.001f, height), diameter), color);
            part.localRotation = length > 1e-6f ? Quaternion.FromToRotation(Vector3.up, along) : Quaternion.identity;
            return part;
        }

        private static Transform Empty(string name, Transform parent)
        {
            var t = new GameObject(name) { layer = IgnoreRaycastLayer }.transform;
            t.SetParent(parent, false);
            return t;
        }

        /// <summary>A mesh part (no collider), coloured from the default material; casts and takes shadows like the yard.</summary>
        private Transform Part(string name, Transform parent, PrimitiveType shape, Vector3 localPosition, Vector3 size, Color color)
        {
            var go = new GameObject(name) { layer = IgnoreRaycastLayer };
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localScale = size;
            go.AddComponent<MeshFilter>().sharedMesh = MeshOf(shape);
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = Paint(color);
            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = true;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            return go.transform;
        }

        private Material Paint(Color color)
        {
            if (_materials.TryGetValue(color, out var paint))
            {
                return paint;
            }

            paint = _palette.Create("WalkWorker");
            paint.hideFlags = HideFlags.DontSave;
            if (paint.HasProperty("_BaseColor")) paint.SetColor("_BaseColor", color);
            if (paint.HasProperty("_Color")) paint.SetColor("_Color", color);
            var smooth = color == Eye || color == CapBlack ? 0.5f : 0.15f;
            if (paint.HasProperty("_Smoothness")) paint.SetFloat("_Smoothness", smooth);
            if (paint.HasProperty("_Glossiness")) paint.SetFloat("_Glossiness", smooth);
            if (paint.HasProperty("_Metallic")) paint.SetFloat("_Metallic", 0f);
            _materials[color] = paint;
            return paint;
        }

        private Mesh MeshOf(PrimitiveType shape)
        {
            if (!_meshes.TryGetValue(shape, out var mesh))
            {
                var probe = GameObject.CreatePrimitive(shape);
                probe.SetActive(false);
                mesh = probe.GetComponent<MeshFilter>().sharedMesh;
                Release(probe);
                _meshes[shape] = mesh;
            }

            return mesh;
        }

        private static void Release(Object target)
        {
            if (target == null) return;
            if (Application.isPlaying) Object.Destroy(target);
            else Object.DestroyImmediate(target);
        }
    }
}
