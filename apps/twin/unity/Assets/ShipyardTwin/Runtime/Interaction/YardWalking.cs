using UnityEngine;

namespace ShipyardTwin
{
    /// <summary>Moves a ground-level camera across the visible yard's walkable surfaces.</summary>
    internal sealed class YardWalking
    {
        private const float BodyHeight = 1.82f;
        private const float Radius = 0.26f, StepUp = 0.45f, StepDown = 0.6f;
        private static readonly float[] SpawnRings = { 0f, 6f, 14f, 28f, 50f, 80f, 120f, 180f, 260f };

        private Vector3 _feet;
        private float _yaw, _pitch;
        public Vector3 Feet => _feet;
        public float Yaw => _yaw;
        public float Pitch => _pitch;

        public bool TryBegin(Vector3 preferred, Vector3 fallback, float heading)
        {
            // Geometry is built by scripts and may not have been published to Physics yet.
            Physics.SyncTransforms();
            var target = preferred;
            if (!TryFindSpawn(target, heading, out _feet))
            {
                target = fallback;
                if (!TryFindSpawn(target, heading, out _feet)) return false;
            }
            var toward = new Vector2(target.x - _feet.x, target.z - _feet.z);
            _yaw = toward.sqrMagnitude > 1f ? Mathf.Atan2(toward.x, toward.y) * Mathf.Rad2Deg : heading;
            _pitch = 18f;
            return true;
        }

        public float Move(Vector2 input, float speed, float seconds)
        {
            if (input.sqrMagnitude <= 1e-6f || seconds <= 0f) return 0f;
            var before = _feet;
            var distance = speed * Mathf.Min(seconds, 0.05f);
            var offset = Quaternion.Euler(0f, _yaw, 0f) * new Vector3(input.x, 0f, input.y) * distance;
            var steps = Mathf.CeilToInt(offset.magnitude / Radius);
            var step = offset / steps;
            for (var i = 0; i < steps; i++)
            {
                if (TryStep(step)) continue;
                // Slide along a wall when approaching it diagonally.
                if (step.x != 0f) TryStep(new Vector3(step.x, 0f, 0f));
                if (step.z != 0f) TryStep(new Vector3(0f, 0f, step.z));
            }
            return new Vector2(_feet.x - before.x, _feet.z - before.z).magnitude;
        }

        public void Look(Vector2 pixels, float degreesPerPixel)
        {
            _yaw += pixels.x * degreesPerPixel;
            _pitch = Mathf.Clamp(_pitch - pixels.y * degreesPerPixel, -5f, 65f);
        }

        private bool TryStep(Vector3 offset)
        {
            if (!FindStep(_feet, offset, out var ground)) return false;
            _feet = ground;
            return true;
        }

        private static bool FindStep(Vector3 feet, Vector3 offset, out Vector3 ground)
        {
            var candidate = feet + offset;
            return TryGround(candidate + Vector3.up * StepUp, StepUp + StepDown, out ground) &&
                   !Physics.CapsuleCast(LowerBody(feet, StepUp), UpperBody(feet), Radius, offset.normalized,
                       offset.magnitude, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) && HasRoom(ground);
        }

        private static bool TryFindSpawn(Vector3 near, float heading, out Vector3 ground)
        {
            var origin = new Vector3(near.x, YardLayout.QuayLevel + 155f, near.z);
            foreach (var radius in SpawnRings)
            {
                var directions = radius == 0f ? 1 : 16;
                for (var i = 0; i < directions; i++)
                {
                    var offset = Quaternion.Euler(0f, heading + 180f + i * 360f / directions, 0f) * Vector3.forward * radius;
                    if (TryGround(origin + offset, 162f, out ground) && HasRoom(ground) && HasWalkingSpace(ground, heading))
                        return true;
                }
            }
            ground = default;
            return false;
        }

        private static bool HasWalkingSpace(Vector3 ground, float heading)
        {
            const float clearDistance = 1.25f;
            var rotation = Quaternion.Euler(0f, heading, 0f);
            var forward = rotation * Vector3.forward * clearDistance;
            var right = rotation * Vector3.right * clearDistance;
            return (FindStep(ground, forward, out _) || FindStep(ground, -forward, out _)) &&
                   (FindStep(ground, right, out _) || FindStep(ground, -right, out _));
        }

        private static bool TryGround(Vector3 origin, float distance, out Vector3 ground)
        {
            ground = default;
            if (!Physics.Raycast(origin, Vector3.down, out var hit, distance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) ||
                hit.normal.y < 0.72f || hit.point.y < YardLayout.SeaLevel + 0.5f ||
                !hit.collider.TryGetComponent<YardWalkSurface>(out _)) return false;
            ground = hit.point;
            return true;
        }

        private static bool HasRoom(Vector3 feet) => !Physics.CheckCapsule(LowerBody(feet, 0.12f), UpperBody(feet),
            Radius, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);

        private static Vector3 LowerBody(Vector3 feet, float clearance) => feet + Vector3.up * (Radius + clearance);
        private static Vector3 UpperBody(Vector3 feet) => feet + Vector3.up * (BodyHeight - Radius);
    }
}
