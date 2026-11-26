using UnityEngine;

namespace ShipyardTwin
{
    public enum WalkPace { Slow, Normal, Fast }

    /// <summary>Alternating footsteps driven by distance actually walked.</summary>
    internal sealed class WalkGait
    {
        private const float SlowSpeed = 1f, NormalSpeed = 1.8f, FastSpeed = 3.2f;
        public const float SprintSpeed = 8f;
        public float Phase { get; private set; }
        public float Weight { get; private set; }

        public static float Speed(WalkPace pace) => pace switch
        {
            WalkPace.Slow => SlowSpeed,
            WalkPace.Fast => FastSpeed,
            _ => NormalSpeed,
        };

        public void Reset()
        {
            Phase = 0.38f;
            Weight = 0f;
        }

        /// <summary>Phases 0 and 0.5 are left and right foot contact. Returns true on a contact.</summary>
        public bool Advance(float distance, float seconds)
        {
            if (seconds <= 0f) return false;
            var speed = distance / seconds;
            var target = Mathf.Clamp01(speed / NormalSpeed);
            Weight = Mathf.MoveTowards(Weight, target, seconds * 6f);

            var walkStep = Mathf.Lerp(0.4f, 0.8f, target);
            var stepLength = Mathf.Lerp(walkStep, 2.15f, Mathf.InverseLerp(NormalSpeed, SprintSpeed, speed));
            var next = Phase + distance / (2f * stepLength);
            var contact = Mathf.FloorToInt(next * 2f) != Mathf.FloorToInt(Phase * 2f);
            Phase = Mathf.Repeat(next, 1f);
            return contact;
        }

    }
}
