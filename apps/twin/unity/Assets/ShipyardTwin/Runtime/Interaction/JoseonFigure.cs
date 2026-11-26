using System.Collections.Generic;
using UnityEngine;

namespace ShipyardTwin
{
    /// <summary>Which reusable figure model (Joseon satire look of the walk avatars).</summary>
    internal enum FigureModel
    {
        /// <summary>사원 (common model, reused in every preset): ragged beige hemp clothes, headband, tired face.</summary>
        Laborer,
        /// <summary>관리자: dark coat and trousers, topknot, dark circles under the eyes, a grin.</summary>
        Overseer,
        /// <summary>임원: stout noble in a purple robe with gold trim and a black gat, laughing.</summary>
        Noble,
    }

    /// <summary>How an arm is driven this frame.</summary>
    internal enum ArmHold
    {
        /// <summary>Free swing counter to the legs (walk/run blend).</summary>
        Swing,
        /// <summary>Two-bone IK to a point in the figure's root space (rod handle, whip grip).</summary>
        Root,
        /// <summary>Two-bone IK to a point in spine space (rope at the chest, a hand on the belly), moves with the torso.</summary>
        Spine,
        /// <summary>Hand planted on the ground (crawling), placed by the crawl gait.</summary>
        Ground,
    }

    internal struct ArmGoal
    {
        public ArmHold Hold;
        public Vector3 Target;
        public Vector3 Pole;
    }

    /// <summary>Speed-driven locomotion blend (what an Animator blend tree would get): moving and running shares.</summary>
    internal struct Locomotion
    {
        /// <summary>Actual ground speed, m/s (from the controller's real displacement, not the input).</summary>
        public float Speed;
        /// <summary>0 idle .. 1 moving (smoothed).</summary>
        public float Move;
        /// <summary>0 walk .. 1 run (smoothed).</summary>
        public float Run;
        /// <summary>Forward distance this frame (signed, along the root's facing).</summary>
        public float Distance;
        public float Time;
    }

    /// <summary>
    /// Procedural humanoid of the walk avatars: a rigid-bone skeleton (hips, spine, head, thigh/shin/foot, upper arm,
    /// forearm with the hand) posed every frame by analytic two-bone IK, so feet and hands land exactly where the gait
    /// puts them. Each bone is one baked mesh (<see cref="AvatarKit"/>).
    /// Gait: the figure keeps its own phase, advanced by the distance actually travelled divided by its own step length,
    /// and a stance foot slides back exactly as far as the root moves forward (duty factor from walk 0.6 to run 0.36),
    /// so the planted foot does not slip at any speed; the step length grows with speed up to the leg's reach, after
    /// which the cadence rises. Several figures on one root (the cart crew) share the root's speed but each has its
    /// own phase offset and step length.
    /// Poses: <see cref="Locomote"/> (idle / walk / run blended by <see cref="Locomotion"/>), <see cref="Crawl"/> (on
    /// hands and knees, carrying a rider) and <see cref="Sit"/> (riding or seated).
    /// Root space: +z forward, +y up, origin on the ground under the hips (seated figures: at the seat).
    /// </summary>
    internal sealed class JoseonFigure
    {
        // Laborer (사원).
        private static readonly Color Skin = new Color(0.69f, 0.5f, 0.36f);
        private static readonly Color Hemp = new Color(0.74f, 0.66f, 0.52f);
        private static readonly Color HempDark = new Color(0.6f, 0.52f, 0.4f);
        private static readonly Color Vest = new Color(0.5f, 0.41f, 0.3f);
        private static readonly Color Patch = new Color(0.4f, 0.37f, 0.34f);
        private static readonly Color Rope = new Color(0.66f, 0.52f, 0.3f);
        private static readonly Color Straw = new Color(0.78f, 0.64f, 0.38f);
        private static readonly Color Headband = new Color(0.86f, 0.8f, 0.68f);
        private static readonly Color Hair = new Color(0.07f, 0.06f, 0.05f);
        private static readonly Color Stubble = new Color(0.45f, 0.34f, 0.26f);
        private static readonly Color Mouth = new Color(0.25f, 0.08f, 0.07f);
        private static readonly Color Eye = new Color(0.05f, 0.04f, 0.04f);

        // Overseer (관리자).
        private static readonly Color OverseerSkin = new Color(0.72f, 0.54f, 0.42f);
        private static readonly Color Coat = new Color(0.17f, 0.17f, 0.24f);
        private static readonly Color CoatTrim = new Color(0.62f, 0.5f, 0.24f);
        private static readonly Color Under = new Color(0.62f, 0.56f, 0.46f);
        private static readonly Color Trousers = new Color(0.27f, 0.2f, 0.29f);
        private static readonly Color Boot = new Color(0.09f, 0.08f, 0.08f);
        private static readonly Color DarkCircle = new Color(0.33f, 0.2f, 0.24f);
        private static readonly Color Teeth = new Color(0.92f, 0.9f, 0.82f);

        // Noble (임원).
        private static readonly Color NobleSkin = new Color(0.86f, 0.66f, 0.52f);
        private static readonly Color Cheek = new Color(0.86f, 0.5f, 0.45f);
        private static readonly Color Purple = new Color(0.38f, 0.16f, 0.4f);
        private static readonly Color PurpleDark = new Color(0.26f, 0.1f, 0.28f);
        private static readonly Color Gold = new Color(0.86f, 0.66f, 0.24f);
        private static readonly Color Collar = new Color(0.94f, 0.92f, 0.86f);
        private static readonly Color GatBlack = new Color(0.06f, 0.06f, 0.07f);
        private static readonly Color Bead = new Color(0.55f, 0.3f, 0.14f);

        public readonly FigureModel Model;
        public readonly Style S;
        public readonly Transform Root, Hips, Spine, Head;
        private readonly Leg[] _legs = new Leg[2];
        private readonly Arm[] _arms = new Arm[2];
        private readonly List<Swinger> _loose = new List<Swinger>();

        /// <summary>Own gait phase 0..1 (0 left heel strike, 0.5 right).</summary>
        public float Phase;
        public readonly ArmGoal[] Arms = { new ArmGoal(), new ArmGoal() };
        /// <summary>Extra torso pitch (forward +) and roll (right +), degrees, e.g. from acceleration or pulling.</summary>
        public float ExtraLean, ExtraRoll;
        /// <summary>Extra head pitch (down +) and yaw, degrees.</summary>
        public float HeadPitch, HeadYaw;

        internal sealed class Style
        {
            public float HipHeight, HipHalfWidth, Thigh, Shin, Ankle = 0.07f, ToeAhead = 0.13f, HeelBehind = 0.09f, FootLift = 0.09f;
            public float WaistUp = 0.08f, SpineLength, ShoulderHalfWidth, ShoulderDrop = 0.06f, UpperArm, Forearm;
            public float Stoop, WalkLean = 4f, RunLean = 14f, HeadCompensation = 0.7f;
            public float ArmSwing = 20f, RunArmSwing = 42f, Elbow = 14f, RunElbow = 85f, Twist = 6f, LookAround = 12f, Seed;
            public float Bob = 0.022f, RunBob = 0.05f, RunCrouch = 0.06f;
        }

        private sealed class Leg
        {
            public Transform Thigh, Shin, Foot;
            public float Side;
        }

        private sealed class Arm
        {
            public Transform Upper, Fore;
            public float Side;
        }

        /// <summary>Loose part (headband tails, coat tails) that trails while moving and settles when standing.</summary>
        private sealed class Swinger
        {
            public Transform T;
            public Quaternion Rest;
            public float Trail, Amp, Seed;
        }

        private JoseonFigure(FigureModel model, Style style, Transform parent, string name)
        {
            Model = model;
            S = style;
            Root = AvatarKit.Empty(name, parent);
            Hips = AvatarKit.Empty("Hips", Root);
            Spine = AvatarKit.Empty("Spine", Root);
            Head = AvatarKit.Empty("Head", Spine);
            Head.localPosition = new Vector3(0f, S.SpineLength + 0.06f, 0.02f);
            for (var i = 0; i < 2; i++)
            {
                var prefix = i == 0 ? "Left" : "Right";
                _legs[i] = new Leg
                {
                    Side = i == 0 ? -1f : 1f,
                    Thigh = AvatarKit.Empty(prefix + "Thigh", Root),
                    Shin = AvatarKit.Empty(prefix + "Shin", Root),
                    Foot = AvatarKit.Empty(prefix + "Foot", Root),
                };
                _arms[i] = new Arm
                {
                    Side = i == 0 ? -1f : 1f,
                    Upper = AvatarKit.Empty(prefix + "UpperArm", Root),
                    Fore = AvatarKit.Empty(prefix + "Forearm", Root),
                };
            }
        }

        // ================================================================ gait

        /// <summary>Stance share of the cycle: 0.6 walking (double support) .. 0.36 running (flight).</summary>
        public static float Duty(float run) => Mathf.Lerp(0.6f, 0.36f, run);

        /// <summary>Step length (half a cycle) at this speed, capped by what the leg can reach in stance.</summary>
        public float StepLength(float speed, float run)
        {
            var walk = 0.32f + 0.28f * speed;
            var sprint = 0.5f + 0.16f * speed;
            var desired = Mathf.Lerp(walk, sprint, run);
            var leg = S.Thigh + S.Shin + S.Ankle;
            var hip = S.HipHeight - S.RunCrouch * run;
            var reach = 0.9f * Mathf.Sqrt(Mathf.Max(0.01f, leg * leg - hip * hip));
            return Mathf.Clamp(desired, 0.25f, reach / Duty(run));
        }

        /// <summary>Advances the phase by the distance actually travelled (backwards walking runs it backwards).</summary>
        public void Advance(Locomotion motion) =>
            Phase = Mathf.Repeat(Phase + motion.Distance / (2f * StepLength(Mathf.Abs(motion.Speed), motion.Run)), 1f);

        /// <summary>
        /// Upright idle / walk / run. Stance foot travel equals the root's travel; the swing foot lifts higher when running.
        /// Standing (move 0): feet together under the hips, breathing and a slow weight shift, the head looks around.
        /// </summary>
        public void Locomote(Locomotion m)
        {
            var w = m.Move;
            var run = m.Run;
            var idle = 1f - w;
            var cycle = 2f * Mathf.PI * Phase;
            var duty = Duty(run);
            var amplitude = StepLength(Mathf.Abs(m.Speed), run) * duty * w;
            var breath = Mathf.Sin(m.Time * 1.45f + S.Seed);

            // Hips: walking, lowest at heel strike; running, lowest mid-stance and crouched a little.
            var walkBob = -S.Bob * Mathf.Cos(2f * cycle);
            var runBob = -S.RunBob * Mathf.Cos(2f * cycle - 2f * Mathf.PI * duty);
            var hipY = S.HipHeight - S.RunCrouch * run * w + w * Mathf.Lerp(walkBob, runBob, run);
            var sway = 0.018f * w * Mathf.Sin(cycle) * (1f - 0.5f * run) + 0.012f * idle * Mathf.Sin(m.Time * 0.45f + S.Seed);
            Hips.localPosition = new Vector3(sway, hipY, 0f);
            Hips.localRotation = Quaternion.Euler(0f, S.Twist * 0.6f * w * Mathf.Cos(cycle), 0f);

            for (var i = 0; i < 2; i++)
            {
                var u = Mathf.Repeat(Phase + (i == 0 ? 0f : 0.5f), 1f);
                PoseStep(_legs[i], u, duty, amplitude, w, run, new Vector3(sway + _legs[i].Side * S.HipHalfWidth, hipY, 0f));
            }

            var twist = -S.Twist * w * Mathf.Cos(cycle);
            var lean = S.Stoop + S.WalkLean * w * (1f - run) + S.RunLean * w * run + ExtraLean;
            Spine.localPosition = new Vector3(sway, hipY + S.WaistUp, 0f);
            Spine.localRotation = Quaternion.Euler(lean + 1.3f * breath * (0.3f + 0.7f * idle), twist, ExtraRoll);

            var look = S.LookAround * idle * Mathf.Sin(m.Time * 0.31f + S.Seed);
            Head.localRotation = Quaternion.Euler(-lean * S.HeadCompensation + HeadPitch + 2f * w * Mathf.Cos(2f * cycle),
                look - twist * 0.7f + HeadYaw, -ExtraRoll * 0.5f);

            for (var i = 0; i < 2; i++) PoseArm(_arms[i], Arms[i], cycle, w, run);
            PoseLoose(w, run, cycle, m.Time);
        }

        /// <summary>
        /// On hands and knees (the 관리자 preset's carrier): diagonal pairs (left hand with right knee), knees and hands
        /// slide back with the ground during stance; the back sags and bobs more under the rider's
        /// <paramref name="load"/>; the head is raised towards the coin.
        /// </summary>
        public void Crawl(Locomotion m, float load)
        {
            var w = m.Move;
            var run = m.Run;
            var cycle = 2f * Mathf.PI * Phase;
            var duty = Mathf.Lerp(0.62f, 0.45f, run);
            var step = Mathf.Clamp(0.22f + 0.14f * Mathf.Abs(m.Speed), 0.22f, 0.5f);
            var amplitude = step * duty * w;
            var breath = Mathf.Sin(m.Time * 2.2f + S.Seed);   // panting

            var bob = (0.02f + 0.02f * load) * w * Mathf.Cos(2f * cycle);
            var hipY = 0.5f - 0.03f * load - bob + 0.006f * breath;
            const float hipZ = -0.3f;
            Hips.localPosition = new Vector3(0f, hipY, hipZ);
            Hips.localRotation = Quaternion.Euler(70f, 0f, 3f * w * Mathf.Sin(cycle));

            // Knees: left with phase 0.5 (opposite the left hand), right with phase 0.
            for (var i = 0; i < 2; i++)
            {
                var leg = _legs[i];
                var u = Mathf.Repeat(Phase + (i == 0 ? 0.5f : 0f), 1f);
                var (z, lift) = Cycle(u, duty, amplitude, 0.06f * w * (1f + run));
                var hip = new Vector3(leg.Side * S.HipHalfWidth, hipY, hipZ);
                var knee = new Vector3(leg.Side * (S.HipHalfWidth + 0.03f), 0.055f + lift, hipZ + 0.06f + z);
                var thighDir = knee - hip;
                knee = hip + thighDir.normalized * S.Thigh;   // rigid thigh: the knee may hover slightly at the ends
                Place(leg.Thigh, hip, knee, Vector3.forward);
                var ankle = knee + new Vector3(0f, 0.05f + 0.6f * lift, -S.Shin);
                Place(leg.Shin, knee, ankle, Vector3.down);
                leg.Foot.localPosition = ankle;
                leg.Foot.localRotation = Quaternion.Euler(95f - 25f * lift / 0.06f, 0f, 0f);   // toes tucked under, sole back
            }

            var lean = 88f + 5f * load + ExtraLean;
            Spine.localPosition = new Vector3(0f, hipY + 0.02f, hipZ + 0.04f);
            Spine.localRotation = Quaternion.Euler(lean + 2f * breath, -4f * w * Mathf.Cos(cycle), 3f * w * Mathf.Sin(cycle));
            Head.localRotation = Quaternion.Euler(-lean * 0.85f + HeadPitch + 3f * w * Mathf.Cos(2f * cycle), HeadYaw, 0f);

            // Hands: right with phase 0.5, left with phase 0 (diagonal to the knees).
            for (var i = 0; i < 2; i++)
            {
                var arm = _arms[i];
                var u = Mathf.Repeat(Phase + (i == 0 ? 0f : 0.5f), 1f);
                var (z, lift) = Cycle(u, duty, amplitude * 1.1f, 0.08f * w * (1f + run));
                var shoulder = SpinePoint(new Vector3(arm.Side * S.ShoulderHalfWidth, S.SpineLength - S.ShoulderDrop, 0f));
                var hand = new Vector3(arm.Side * (S.ShoulderHalfWidth + 0.04f), 0.035f + lift, shoulder.z + 0.06f + z);
                var elbow = Joint(shoulder, hand, S.UpperArm, S.Forearm, new Vector3(arm.Side * 0.4f, 0f, -1f));
                var wrist = elbow + (hand - elbow).normalized * S.Forearm;
                Place(arm.Upper, shoulder, elbow, Vector3.forward);
                Place(arm.Fore, elbow, wrist, Vector3.forward);
            }

            PoseLoose(w * 0.6f, run, cycle, m.Time);
        }

        /// <summary>
        /// Seated with the hips at the root origin (riding, or on the cart's seat): legs reach the given ankle points
        /// (root space), the torso leans by <see cref="ExtraLean"/> / <see cref="ExtraRoll"/>, arms follow their goals.
        /// </summary>
        public void Sit(Vector3 leftAnkle, Vector3 rightAnkle, float time, float bounce)
        {
            var breath = Mathf.Sin(time * 1.3f + S.Seed);
            Hips.localPosition = new Vector3(0f, bounce, 0f);
            Hips.localRotation = Quaternion.identity;
            for (var i = 0; i < 2; i++)
            {
                var leg = _legs[i];
                var hip = new Vector3(leg.Side * S.HipHalfWidth, bounce, 0.02f);
                var ankle = i == 0 ? leftAnkle : rightAnkle;
                var knee = Joint(hip, ankle, S.Thigh, S.Shin, new Vector3(leg.Side * 0.5f, 0.3f, 1f));
                var reach = ankle - knee;
                var foot = knee + (reach.sqrMagnitude > 1e-8f ? reach.normalized : Vector3.down) * S.Shin;
                Place(leg.Thigh, hip, knee, Vector3.up);
                Place(leg.Shin, knee, foot, Vector3.forward);
                leg.Foot.localPosition = foot;
                leg.Foot.localRotation = Quaternion.Euler(20f, leg.Side * 12f, 0f);
            }

            Spine.localPosition = new Vector3(0f, bounce + S.WaistUp, 0f);
            Spine.localRotation = Quaternion.Euler(S.Stoop + ExtraLean + 1.2f * breath, 0f, ExtraRoll);
            Head.localRotation = Quaternion.Euler(-(S.Stoop + ExtraLean) * S.HeadCompensation + HeadPitch, HeadYaw, -ExtraRoll * 0.4f);
            for (var i = 0; i < 2; i++) PoseArm(_arms[i], Arms[i], 0f, 0f, 0f);
            PoseLoose(0.2f, 0f, 0f, time);
        }

        /// <summary>Position (z, lift) of a limb end at cycle position u: stance slides back, swing carries it forward raised.</summary>
        private static (float z, float lift) Cycle(float u, float duty, float amplitude, float lift)
        {
            if (u < duty) return (amplitude * (1f - 2f * u / duty), 0f);
            var k = (u - duty) / (1f - duty);
            return (-amplitude * Mathf.Cos(Mathf.PI * k), lift * Mathf.Sin(Mathf.PI * k));
        }

        /// <summary>One upright leg: the foot rolls over the toe at push-off and lands heel first; two-bone knee.</summary>
        private void PoseStep(Leg leg, float u, float duty, float amplitude, float w, float run, Vector3 hip)
        {
            var (z, lift) = Cycle(u, duty, amplitude, S.FootLift * w * (1f + 2.2f * run));
            float pitch;
            if (u < duty)
            {
                pitch = 18f * w * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(duty * 0.65f, duty, u));   // heel rises before toe-off
            }
            else
            {
                var k = (u - duty) / (1f - duty);
                var toeUp = Mathf.Lerp(8f, 2f, run);
                pitch = (k < 0.6f ? Mathf.Lerp(18f + 20f * run, -toeUp, Mathf.SmoothStep(0f, 1f, k / 0.6f))
                                  : Mathf.Lerp(-toeUp, 0f, Mathf.SmoothStep(0f, 1f, (k - 0.6f) / 0.4f))) * w;
            }

            var ground = new Vector3(leg.Side * S.HipHalfWidth, lift, z);
            var roll = Quaternion.Euler(pitch, 0f, 0f);
            var pivot = pitch >= 0f ? ground + Vector3.forward * S.ToeAhead : ground - Vector3.forward * S.HeelBehind;
            var ankle = pivot + roll * (ground + Vector3.up * S.Ankle - pivot);
            var knee = Joint(hip, ankle, S.Thigh, S.Shin, Vector3.forward);
            var reach = ankle - knee;
            var foot = knee + (reach.sqrMagnitude > 1e-8f ? reach.normalized : Vector3.down) * S.Shin;
            Place(leg.Thigh, hip, knee, Vector3.forward);
            Place(leg.Shin, knee, foot, Vector3.forward);
            leg.Foot.localPosition = foot;
            leg.Foot.localRotation = roll;
        }

        private void PoseArm(Arm arm, ArmGoal goal, float cycle, float w, float run)
        {
            var side = arm.Side;
            var shoulder = SpinePoint(new Vector3(side * S.ShoulderHalfWidth, S.SpineLength - S.ShoulderDrop, 0f));
            Vector3 elbow, wrist;
            if (goal.Hold == ArmHold.Swing || goal.Hold == ArmHold.Ground)
            {
                var forward = side * Mathf.Cos(cycle);   // +1: this arm furthest forward (right arm with the left foot)
                var swingDeg = Mathf.Lerp(S.ArmSwing, S.RunArmSwing, run) * w;
                var swing = swingDeg * forward * Mathf.Deg2Rad;
                var abduct = 8f * Mathf.Deg2Rad;
                var spin = Spine.localRotation;
                var upper = spin * new Vector3(side * Mathf.Sin(abduct), -Mathf.Cos(swing), Mathf.Sin(swing)).normalized;
                elbow = shoulder + upper * S.UpperArm;
                var bend = swing + (Mathf.Lerp(S.Elbow, S.RunElbow, run * w) + 0.5f * swingDeg * Mathf.Max(0f, forward)) * Mathf.Deg2Rad;
                var fore = spin * new Vector3(side * Mathf.Sin(abduct * 0.5f), -Mathf.Cos(bend), Mathf.Sin(bend)).normalized;
                wrist = elbow + fore * S.Forearm;
            }
            else
            {
                var target = goal.Hold == ArmHold.Spine ? SpinePoint(goal.Target) : goal.Target;
                var pole = goal.Pole.sqrMagnitude > 1e-6f ? goal.Pole : new Vector3(side, -0.4f, -0.6f);
                elbow = Joint(shoulder, target, S.UpperArm, S.Forearm, pole);
                var reach = target - elbow;
                wrist = elbow + (reach.sqrMagnitude > 1e-8f ? reach.normalized : Vector3.down) * S.Forearm;
            }

            Place(arm.Upper, shoulder, elbow, Vector3.forward);
            Place(arm.Fore, elbow, wrist, new Vector3(side, 0f, 0f));
        }

        private void PoseLoose(float w, float run, float cycle, float time)
        {
            foreach (var loose in _loose)
            {
                var flap = loose.Amp * (w * (1f + run) * Mathf.Sin(2f * cycle + loose.Seed) + 0.25f * Mathf.Sin(time * 1.7f + loose.Seed));
                loose.T.localRotation = loose.Rest * Quaternion.Euler(loose.Trail * w * (1f + 0.6f * run) + flap, 0f,
                    0.5f * loose.Amp * w * Mathf.Cos(cycle + loose.Seed));
            }
        }

        /// <summary>Point in spine space → root space.</summary>
        public Vector3 SpinePoint(Vector3 local) => Spine.localPosition + Spine.localRotation * local;

        /// <summary>World position of the hand (wrist end of the forearm), i = 0 left, 1 right.</summary>
        public Vector3 HandWorld(int i) => _arms[i].Fore.TransformPoint(new Vector3(0f, -S.Forearm - 0.06f, 0f));

        /// <summary>Bone from a to b along its local -y; <paramref name="front"/> sets the twist (bone +z towards it).</summary>
        private static void Place(Transform bone, Vector3 from, Vector3 to, Vector3 front)
        {
            var dir = to - from;
            bone.localPosition = from;
            if (dir.sqrMagnitude < 1e-10f) return;
            dir.Normalize();
            var hint = Vector3.ProjectOnPlane(front, dir);
            if (hint.sqrMagnitude < 1e-6f) hint = Vector3.ProjectOnPlane(Vector3.up, dir);
            if (hint.sqrMagnitude < 1e-6f) hint = Vector3.ProjectOnPlane(Vector3.right, dir);
            bone.localRotation = Quaternion.LookRotation(hint.normalized, -dir);
        }

        /// <summary>Middle joint of a two-bone chain from a to b, bent towards the pole (analytic IK).</summary>
        public static Vector3 Joint(Vector3 a, Vector3 b, float first, float second, Vector3 pole)
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

        // ================================================================ models

        public static JoseonFigure Build(AvatarKit kit, FigureModel model, Transform parent, string name)
        {
            switch (model)
            {
                case FigureModel.Overseer: return BuildOverseer(kit, parent, name);
                case FigureModel.Noble: return BuildNoble(kit, parent, name);
                default: return BuildLaborer(kit, parent, name);
            }
        }

        /// <summary>사원: thin, stooped, ragged beige hemp jacket under a torn vest, rope belt, cropped trousers, straw sandals.</summary>
        private static JoseonFigure BuildLaborer(AvatarKit kit, Transform parent, string name)
        {
            var s = new Style
            {
                HipHeight = 0.86f, HipHalfWidth = 0.095f, Thigh = 0.43f, Shin = 0.42f,
                SpineLength = 0.5f, ShoulderHalfWidth = 0.19f, UpperArm = 0.29f, Forearm = 0.26f,
                Stoop = 9f, WalkLean = 5f, RunLean = 16f, HeadCompensation = 0.75f, ArmSwing = 22f, Seed = 0.3f,
            };
            var f = new JoseonFigure(FigureModel.Laborer, s, parent, name);

            kit.Part(f.Hips, PrimitiveType.Cube, new Vector3(0f, 0.03f, 0f), new Vector3(0.31f, 0.2f, 0.2f), Hemp);
            kit.Part(f.Hips, PrimitiveType.Cube, new Vector3(-0.07f, 0.0f, -0.102f), new Vector3(0.08f, 0.08f, 0.008f), Patch);

            var spine = f.Spine;
            kit.Part(spine, PrimitiveType.Capsule, new Vector3(0f, 0.22f, 0f), new Vector3(0.3f, 0.32f, 0.2f), Hemp);
            kit.Segment(spine, PrimitiveType.Capsule, new Vector3(-0.18f, 0.43f, 0f), new Vector3(0.18f, 0.43f, 0f), 0.11f, Hemp);
            kit.Part(spine, PrimitiveType.Cube, new Vector3(0f, 0.26f, -0.092f), new Vector3(0.33f, 0.42f, 0.035f), Vest);
            for (var i = 0; i < 2; i++)
            {
                var side = i == 0 ? -1f : 1f;
                kit.Part(spine, PrimitiveType.Cube, new Vector3(side * 0.11f, 0.24f, 0.092f), Quaternion.Euler(0f, 0f, side * 5f),
                    new Vector3(0.1f, 0.4f - i * 0.05f, 0.035f), Vest);
                kit.Segment(spine, PrimitiveType.Cube, new Vector3(side * 0.06f, 0.47f, 0.1f), new Vector3(-side * 0.04f, 0.24f, 0.11f), 0.04f, HempDark);
            }

            kit.Part(spine, PrimitiveType.Cube, new Vector3(0.07f, 0.12f, -0.112f), Quaternion.Euler(0f, 0f, -6f), new Vector3(0.09f, 0.11f, 0.01f), Patch);
            kit.Part(spine, PrimitiveType.Cube, new Vector3(-0.12f, 0.31f, 0.112f), Quaternion.Euler(0f, 0f, 9f), new Vector3(0.07f, 0.07f, 0.01f), HempDark);
            kit.Part(spine, PrimitiveType.Cylinder, new Vector3(0f, 0.0f, 0f), new Vector3(0.33f, 0.022f, 0.225f), Rope);   // rope belt
            kit.Part(spine, PrimitiveType.Sphere, new Vector3(0.06f, 0.0f, 0.11f), Vector3.one * 0.05f, Rope);
            kit.Segment(spine, PrimitiveType.Cube, new Vector3(0.06f, -0.01f, 0.115f), new Vector3(0.075f, -0.15f, 0.12f), 0.018f, Rope);
            Rags(kit, spine, new Vector3(0f, -0.07f, 0f), 0.16f, 0.105f, 7, 0.05f, 0.11f, Hemp);
            kit.Part(spine, PrimitiveType.Cylinder, new Vector3(0f, 0.53f, 0.01f), new Vector3(0.09f, 0.05f, 0.09f), Skin);

            var head = f.Head;
            Face(kit, head, Skin, Hair, tired: true);
            kit.Part(head, PrimitiveType.Sphere, new Vector3(0f, 0.04f, 0.075f), new Vector3(0.13f, 0.06f, 0.08f), Stubble);
            kit.Part(head, PrimitiveType.Sphere, new Vector3(0f, 0.06f, 0.104f), new Vector3(0.045f, 0.032f, 0.02f), Mouth);   // open, panting
            kit.Part(head, PrimitiveType.Sphere, new Vector3(0f, 0.17f, -0.015f), new Vector3(0.2f, 0.15f, 0.2f), Hair);
            kit.Part(head, PrimitiveType.Sphere, new Vector3(0.01f, 0.25f, -0.035f), new Vector3(0.075f, 0.065f, 0.075f), Hair);
            kit.Part(head, PrimitiveType.Cylinder, new Vector3(0f, 0.16f, 0f), new Vector3(0.21f, 0.02f, 0.214f), Headband);
            kit.Part(head, PrimitiveType.Sphere, new Vector3(0f, 0.16f, -0.108f), Vector3.one * 0.045f, Headband);
            var tails = f.Loose(head, "HeadbandTails", new Vector3(0f, 0.16f, -0.115f), Quaternion.Euler(20f, 0f, 0f), 45f, 14f, 0.7f);
            kit.Part(tails, PrimitiveType.Cube, new Vector3(-0.02f, -0.08f, 0f), Quaternion.Euler(0f, 0f, -10f), new Vector3(0.035f, 0.16f, 0.008f), Headband);
            kit.Part(tails, PrimitiveType.Cube, new Vector3(0.02f, -0.07f, 0f), Quaternion.Euler(0f, 0f, 12f), new Vector3(0.035f, 0.13f, 0.008f), Headband);

            for (var i = 0; i < 2; i++)
            {
                var leg = f._legs[i];
                // Thigh and knee in baggy hemp trousers, torn above the shin; bare shin; straw sandal.
                Limb(kit, leg.Thigh, s.Thigh, 0.15f, Hemp);
                Rags(kit, leg.Thigh, new Vector3(0f, -s.Thigh - 0.02f, 0f), 0.075f, 0.075f, 5, 0.05f, 0.12f, Hemp);
                if (i == 1) kit.Part(leg.Thigh, PrimitiveType.Cube, new Vector3(0f, -0.25f, 0.074f), new Vector3(0.08f, 0.12f, 0.01f), Patch);
                kit.Segment(leg.Shin, PrimitiveType.Cylinder, new Vector3(0f, -0.02f, 0f), new Vector3(0f, -0.14f, 0f), 0.13f, HempDark);
                Limb(kit, leg.Shin, s.Shin, 0.09f, Skin);
                Sandal(kit, leg.Foot, s);
                var arm = f._arms[i];
                Limb(kit, arm.Upper, s.UpperArm, 0.11f, Hemp);
                Rags(kit, arm.Upper, new Vector3(0f, -0.17f, 0f), 0.055f, 0.055f, 4, 0.04f, 0.08f, Hemp);
                kit.Segment(arm.Upper, PrimitiveType.Capsule, new Vector3(0f, -0.18f, 0f), new Vector3(0f, -s.UpperArm, 0f), 0.08f, Skin);
                Limb(kit, arm.Fore, s.Forearm, 0.075f, Skin);
                Hand(kit, arm.Fore, s.Forearm, arm.Side, Skin);
            }

            return f;
        }

        /// <summary>관리자: dark coat with an ochre trim, dark trousers bound at the shin, black boots, dark circles, grin.</summary>
        private static JoseonFigure BuildOverseer(AvatarKit kit, Transform parent, string name)
        {
            var s = new Style
            {
                HipHeight = 0.88f, HipHalfWidth = 0.1f, Thigh = 0.44f, Shin = 0.43f,
                SpineLength = 0.52f, ShoulderHalfWidth = 0.2f, UpperArm = 0.29f, Forearm = 0.27f,
                Stoop = 3f, WalkLean = 4f, RunLean = 15f, HeadCompensation = 0.6f, ArmSwing = 18f, Seed = 1.9f,
            };
            var f = new JoseonFigure(FigureModel.Overseer, s, parent, name);
            kit.Part(f.Hips, PrimitiveType.Cube, new Vector3(0f, 0.03f, 0f), new Vector3(0.33f, 0.2f, 0.21f), Trousers);

            var spine = f.Spine;
            kit.Part(spine, PrimitiveType.Capsule, new Vector3(0f, 0.22f, 0f), new Vector3(0.34f, 0.33f, 0.23f), Coat);
            kit.Segment(spine, PrimitiveType.Capsule, new Vector3(-0.19f, 0.45f, 0f), new Vector3(0.19f, 0.45f, 0f), 0.13f, Coat);
            kit.Segment(spine, PrimitiveType.Cube, new Vector3(0.08f, 0.5f, 0.07f), new Vector3(-0.09f, 0.18f, 0.12f), 0.04f, Under);
            kit.Segment(spine, PrimitiveType.Cube, new Vector3(0.09f, 0.48f, 0.08f), new Vector3(-0.08f, 0.17f, 0.125f), 0.012f, CoatTrim);
            kit.Part(spine, PrimitiveType.Cube, new Vector3(0f, -0.01f, 0f), new Vector3(0.36f, 0.06f, 0.245f), CoatTrim);   // sash
            kit.Part(spine, PrimitiveType.Cylinder, new Vector3(0f, 0.55f, 0.01f), new Vector3(0.095f, 0.05f, 0.095f), OverseerSkin);
            var coatTails = f.Loose(spine, "CoatTails", new Vector3(0f, -0.03f, -0.1f), Quaternion.Euler(4f, 0f, 0f), 30f, 8f, 1.1f);
            kit.Part(coatTails, PrimitiveType.Cube, new Vector3(0f, -0.22f, 0f), new Vector3(0.32f, 0.44f, 0.02f), Coat);
            kit.Part(coatTails, PrimitiveType.Cube, new Vector3(0f, -0.43f, 0.002f), new Vector3(0.32f, 0.025f, 0.022f), CoatTrim);
            var coatFront = f.Loose(spine, "CoatFront", new Vector3(0f, -0.03f, 0.115f), Quaternion.Euler(-3f, 0f, 0f), -12f, 6f, 2.3f);
            kit.Part(coatFront, PrimitiveType.Cube, new Vector3(-0.07f, -0.2f, 0f), new Vector3(0.18f, 0.4f, 0.02f), Coat);

            var head = f.Head;
            Face(kit, head, OverseerSkin, Hair, tired: false);
            for (var i = 0; i < 2; i++)
            {
                var side = i == 0 ? -1f : 1f;
                kit.Part(head, PrimitiveType.Sphere, new Vector3(side * 0.04f, 0.1f, 0.094f), new Vector3(0.05f, 0.03f, 0.02f), DarkCircle);
            }
            kit.Part(head, PrimitiveType.Cube, new Vector3(0f, 0.058f, 0.1f), new Vector3(0.07f, 0.022f, 0.02f), Teeth);   // wide grin
            kit.Part(head, PrimitiveType.Cube, new Vector3(0f, 0.044f, 0.098f), new Vector3(0.062f, 0.012f, 0.02f), Mouth);
            kit.Part(head, PrimitiveType.Sphere, new Vector3(0f, 0.16f, -0.02f), new Vector3(0.2f, 0.15f, 0.2f), Hair);
            kit.Part(head, PrimitiveType.Sphere, new Vector3(0f, 0.255f, -0.03f), new Vector3(0.08f, 0.075f, 0.08f), Hair);
            kit.Segment(head, PrimitiveType.Cylinder, new Vector3(-0.06f, 0.26f, -0.03f), new Vector3(0.06f, 0.26f, -0.03f), 0.012f, CoatTrim);

            for (var i = 0; i < 2; i++)
            {
                var leg = f._legs[i];
                Limb(kit, leg.Thigh, s.Thigh, 0.155f, Trousers);
                Limb(kit, leg.Shin, s.Shin, 0.125f, Trousers);
                kit.Segment(leg.Shin, PrimitiveType.Cylinder, new Vector3(0f, -0.2f, 0f), new Vector3(0f, -s.Shin + 0.02f, 0f), 0.13f, Under);   // leg wrap
                Shoe(kit, leg.Foot, s, Boot);
                var arm = f._arms[i];
                Limb(kit, arm.Upper, s.UpperArm, 0.125f, Coat);
                Limb(kit, arm.Fore, s.Forearm, 0.11f, Coat);
                kit.Segment(arm.Fore, PrimitiveType.Cylinder, new Vector3(0f, -s.Forearm + 0.03f, 0f), new Vector3(0f, -s.Forearm + 0.01f, 0f), 0.115f, CoatTrim);
                Hand(kit, arm.Fore, s.Forearm, arm.Side, OverseerSkin);
            }

            return f;
        }

        /// <summary>임원: stout, seated, purple robe with gold trim and white collar, black gat with a bead string, laughing.</summary>
        private static JoseonFigure BuildNoble(AvatarKit kit, Transform parent, string name)
        {
            var s = new Style
            {
                HipHeight = 0.88f, HipHalfWidth = 0.13f, Thigh = 0.42f, Shin = 0.42f,
                SpineLength = 0.54f, ShoulderHalfWidth = 0.23f, UpperArm = 0.29f, Forearm = 0.27f,
                Stoop = -10f, HeadCompensation = 0.6f, Seed = 3.4f, LookAround = 8f,
            };
            var f = new JoseonFigure(FigureModel.Noble, s, parent, name);
            kit.Part(f.Hips, PrimitiveType.Cube, new Vector3(0f, 0.04f, 0f), new Vector3(0.42f, 0.22f, 0.3f), Purple);

            var spine = f.Spine;
            kit.Part(spine, PrimitiveType.Sphere, new Vector3(0f, 0.18f, 0.06f), new Vector3(0.46f, 0.44f, 0.42f), Purple);   // belly
            kit.Part(spine, PrimitiveType.Capsule, new Vector3(0f, 0.32f, -0.01f), new Vector3(0.42f, 0.24f, 0.3f), Purple);
            kit.Segment(spine, PrimitiveType.Capsule, new Vector3(-0.22f, 0.47f, 0f), new Vector3(0.22f, 0.47f, 0f), 0.15f, Purple);
            kit.Part(spine, PrimitiveType.Cylinder, new Vector3(0f, 0.17f, 0.05f), new Vector3(0.47f, 0.035f, 0.44f), Gold);   // belt
            kit.Segment(spine, PrimitiveType.Cube, new Vector3(0.1f, 0.52f, 0.1f), new Vector3(-0.08f, 0.26f, 0.25f), 0.05f, Collar);
            kit.Segment(spine, PrimitiveType.Cube, new Vector3(0.11f, 0.5f, 0.11f), new Vector3(-0.07f, 0.24f, 0.26f), 0.014f, Gold);
            kit.Part(spine, PrimitiveType.Cube, new Vector3(0.08f, 0.34f, 0.2f), Quaternion.Euler(-20f, 0f, 0f), new Vector3(0.12f, 0.12f, 0.01f), Gold);   // embroidered badge
            kit.Part(spine, PrimitiveType.Cylinder, new Vector3(0f, 0.57f, 0.01f), new Vector3(0.12f, 0.05f, 0.12f), NobleSkin);

            var head = f.Head;
            kit.Part(head, PrimitiveType.Sphere, new Vector3(0f, 0.09f, 0.01f), new Vector3(0.23f, 0.24f, 0.22f), NobleSkin);   // round face
            Face(kit, head, NobleSkin, Hair, tired: false);
            for (var i = 0; i < 2; i++)
            {
                var side = i == 0 ? -1f : 1f;
                kit.Part(head, PrimitiveType.Sphere, new Vector3(side * 0.065f, 0.075f, 0.09f), new Vector3(0.05f, 0.035f, 0.03f), Cheek);
                kit.Segment(head, PrimitiveType.Capsule, new Vector3(side * 0.012f, 0.062f, 0.112f), new Vector3(side * 0.07f, 0.075f, 0.098f), 0.016f, Hair);
                kit.Segment(head, PrimitiveType.Cylinder, new Vector3(side * 0.12f, 0.19f, 0f), new Vector3(side * 0.09f, -0.02f, 0.04f), 0.008f, Bead);   // gat string
            }
            kit.Part(head, PrimitiveType.Sphere, new Vector3(0f, 0.035f, 0.105f), new Vector3(0.08f, 0.05f, 0.04f), Mouth);   // laughing
            kit.Part(head, PrimitiveType.Cube, new Vector3(0f, 0.052f, 0.118f), new Vector3(0.06f, 0.014f, 0.012f), Teeth);
            kit.Segment(head, PrimitiveType.Capsule, new Vector3(0f, 0.0f, 0.1f), new Vector3(0f, -0.07f, 0.09f), 0.03f, Hair);   // goatee
            kit.Part(head, PrimitiveType.Sphere, new Vector3(0f, 0.16f, -0.02f), new Vector3(0.21f, 0.14f, 0.2f), Hair);
            kit.Part(head, PrimitiveType.Cylinder, new Vector3(0f, 0.2f, 0f), new Vector3(0.5f, 0.006f, 0.5f), GatBlack);   // gat brim
            kit.Part(head, PrimitiveType.Cylinder, new Vector3(0f, 0.28f, 0f), new Vector3(0.17f, 0.08f, 0.17f), GatBlack);
            kit.Part(head, PrimitiveType.Cylinder, new Vector3(0f, 0.215f, 0f), new Vector3(0.175f, 0.012f, 0.175f), PurpleDark);

            for (var i = 0; i < 2; i++)
            {
                var leg = f._legs[i];
                Limb(kit, leg.Thigh, s.Thigh, 0.2f, Purple);
                Limb(kit, leg.Shin, s.Shin, 0.15f, PurpleDark);
                kit.Segment(leg.Shin, PrimitiveType.Cylinder, new Vector3(0f, -s.Shin + 0.06f, 0f), new Vector3(0f, -s.Shin + 0.04f, 0f), 0.16f, Gold);
                Shoe(kit, leg.Foot, s, GatBlack);
                var arm = f._arms[i];
                Limb(kit, arm.Upper, s.UpperArm, 0.15f, Purple);
                Limb(kit, arm.Fore, s.Forearm, 0.15f, Purple);
                kit.Segment(arm.Fore, PrimitiveType.Cylinder, new Vector3(0f, -s.Forearm + 0.05f, 0f), new Vector3(0f, -s.Forearm + 0.02f, 0f), 0.2f, Gold);   // wide cuff
                Hand(kit, arm.Fore, s.Forearm, arm.Side, NobleSkin);
            }

            return f;
        }

        private Transform Loose(Transform parent, string name, Vector3 pivot, Quaternion rest, float trail, float amp, float seed)
        {
            var t = AvatarKit.Empty(name, parent);
            t.localPosition = pivot;
            t.localRotation = rest;
            _loose.Add(new Swinger { T = t, Rest = rest, Trail = trail, Amp = amp, Seed = seed });
            return t;
        }

        // ---------------------------------------------------------------- parts

        private static void Face(AvatarKit kit, Transform head, Color skin, Color hair, bool tired)
        {
            kit.Part(head, PrimitiveType.Sphere, new Vector3(0f, 0.1f, 0.01f), new Vector3(0.19f, 0.23f, 0.2f), skin);
            kit.Part(head, PrimitiveType.Sphere, new Vector3(0f, 0.095f, 0.108f), new Vector3(0.032f, 0.045f, 0.036f), skin);
            for (var i = 0; i < 2; i++)
            {
                var side = i == 0 ? -1f : 1f;
                kit.Part(head, PrimitiveType.Sphere, new Vector3(side * 0.097f, 0.1f, 0f), new Vector3(0.03f, 0.05f, 0.04f), skin);
                kit.Part(head, PrimitiveType.Sphere, new Vector3(side * 0.04f, 0.124f, 0.098f), new Vector3(0.024f, tired ? 0.011f : 0.015f, 0.012f), Eye);
                // Tired: inner brow ends raised (worried); others: level or angry-low inner ends.
                var tilt = tired ? -side * 16f : side * 10f;
                kit.Part(head, PrimitiveType.Cube, new Vector3(side * 0.042f, 0.148f, 0.098f), Quaternion.Euler(0f, 0f, tilt), new Vector3(0.048f, 0.011f, 0.012f), hair);
            }
        }

        /// <summary>Limb mesh along the bone's -y, from the joint to <paramref name="length"/>.</summary>
        private static void Limb(AvatarKit kit, Transform bone, float length, float diameter, Color color) =>
            kit.Segment(bone, PrimitiveType.Capsule, Vector3.zero, new Vector3(0f, -length, 0f), diameter, color);

        /// <summary>Hand at the forearm's end (relaxed fingers along the forearm).</summary>
        private static void Hand(AvatarKit kit, Transform fore, float forearm, float side, Color skin)
        {
            var wrist = new Vector3(0f, -forearm, 0f);
            kit.Part(fore, PrimitiveType.Sphere, wrist + new Vector3(0f, -0.045f, 0f), new Vector3(0.075f, 0.09f, 0.035f), skin);
            kit.Segment(fore, PrimitiveType.Capsule, wrist + new Vector3(0f, -0.07f, 0.005f), wrist + new Vector3(0f, -0.12f, 0.02f), 0.06f, skin);
            kit.Segment(fore, PrimitiveType.Capsule, wrist + new Vector3(-side * 0.03f, -0.03f, 0.02f), wrist + new Vector3(-side * 0.04f, -0.075f, 0.035f), 0.022f, skin);
        }

        /// <summary>Straw sandal on a bare foot (bone at the ankle, +z toes).</summary>
        private static void Sandal(AvatarKit kit, Transform foot, Style s)
        {
            var mid = (s.ToeAhead - s.HeelBehind) / 2f;
            var length = s.ToeAhead + s.HeelBehind;
            var ground = -s.Ankle;
            kit.Part(foot, PrimitiveType.Cube, new Vector3(0f, ground + 0.01f, mid), new Vector3(0.1f, 0.02f, length + 0.03f), Straw);
            kit.Part(foot, PrimitiveType.Sphere, new Vector3(0f, ground + 0.04f, mid + 0.01f), new Vector3(0.088f, 0.06f, length), Skin);
            kit.Part(foot, PrimitiveType.Sphere, new Vector3(0f, ground + 0.03f, s.ToeAhead - 0.01f), new Vector3(0.06f, 0.035f, 0.05f), Straw);
            kit.Segment(foot, PrimitiveType.Cylinder, new Vector3(-0.045f, ground + 0.02f, -0.03f), new Vector3(0.045f, ground + 0.02f, -0.03f), 0.014f, Straw);
            kit.Part(foot, PrimitiveType.Cylinder, new Vector3(0f, 0.01f, 0f), new Vector3(0.095f, 0.03f, 0.095f), Straw);
        }

        private static void Shoe(AvatarKit kit, Transform foot, Style s, Color upper)
        {
            var mid = (s.ToeAhead - s.HeelBehind) / 2f;
            var length = s.ToeAhead + s.HeelBehind;
            var ground = -s.Ankle;
            kit.Part(foot, PrimitiveType.Cube, new Vector3(0f, ground + 0.012f, mid), new Vector3(0.1f, 0.024f, length + 0.03f), Collar);
            kit.Part(foot, PrimitiveType.Sphere, new Vector3(0f, ground + 0.045f, mid), new Vector3(0.1f, 0.08f, length + 0.02f), upper);
            kit.Part(foot, PrimitiveType.Cylinder, new Vector3(0f, -0.005f, -0.015f), new Vector3(0.095f, 0.035f, 0.1f), upper);
        }

        /// <summary>Frayed strips around an ellipse hanging from <paramref name="centre"/> along -y, uneven lengths.</summary>
        private static void Rags(AvatarKit kit, Transform bone, Vector3 centre, float rx, float rz, int count, float shortest, float longest, Color color)
        {
            for (var k = 0; k < count; k++)
            {
                var angle = 0.4f + k * 2f * Mathf.PI / count;
                var length = Mathf.Lerp(shortest, longest, (k * 7 % 3) / 2f);
                var at = centre + new Vector3(Mathf.Cos(angle) * rx, -length / 2f, Mathf.Sin(angle) * rz);
                kit.Part(bone, PrimitiveType.Cube, at, Quaternion.Euler(4f * (k % 2 == 0 ? 1f : -1f), -(angle * Mathf.Rad2Deg + 90f), 0f),
                    new Vector3(0.03f, length, 0.01f), color);
            }
        }
    }
}
