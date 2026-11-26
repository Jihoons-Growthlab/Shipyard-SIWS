using UnityEngine;

namespace ShipyardTwin
{
    internal sealed class WalkFootsteps
    {
        private AudioSource _source;
        private AudioClip[] _clips;
        private int _next;

        public void Play(Transform camera, float weight)
        {
            if (_source == null)
            {
                _clips ??= Resources.LoadAll<AudioClip>("ShipyardTwin/Audio/BootSteps");
                if (_clips.Length == 0) return;
                _source = camera.gameObject.AddComponent<AudioSource>();
                _source.playOnAwake = false;
                _source.spatialBlend = 0f;
            }
            _source.PlayOneShot(_clips[_next], Mathf.Lerp(0.19f, 0.37f, weight));
            _next = (_next + 1) % _clips.Length;
        }

        public void Stop()
        {
            if (_source != null) _source.Stop();
        }

        public void Dispose()
        {
            if (_source != null) Object.Destroy(_source);
        }
    }
}
