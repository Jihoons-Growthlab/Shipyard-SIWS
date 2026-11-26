using System.Collections.Generic;
using UnityEngine;

namespace ShipyardTwin
{
    public enum YardDisplayLayer { Cranes, Vehicles, Buildings, Hulls, Blocks }

    /// <summary>Groups scene objects beneath their owners so their simulation components stay active when hidden.</summary>
    public sealed class YardDisplayLayers
    {
        private readonly HashSet<YardDisplayLayer> _hidden = new HashSet<YardDisplayLayer>();
        private readonly Dictionary<(Transform, YardDisplayLayer), Transform> _groups =
            new Dictionary<(Transform, YardDisplayLayer), Transform>();

        public bool IsVisible(YardDisplayLayer layer) => !_hidden.Contains(layer);

        public Transform ParentFor(Transform owner, YardDisplayLayer layer)
        {
            var key = (owner, layer);
            if (_groups.TryGetValue(key, out var group) && group != null)
            {
                return group;
            }

            group = new GameObject(layer.ToString()).transform;
            group.SetParent(owner, false);
            group.gameObject.SetActive(IsVisible(layer));
            _groups[key] = group;
            return group;
        }

        public void SetVisible(YardDisplayLayer layer, bool visible)
        {
            if (visible) _hidden.Remove(layer);
            else _hidden.Add(layer);

            foreach (var pair in _groups)
            {
                if (pair.Key.Item2 == layer && pair.Value != null)
                {
                    pair.Value.gameObject.SetActive(visible);
                }
            }
        }
    }
}
