using UnityEngine;

namespace ShipyardTwin
{
    /// <summary>Ground that can support the visitor camera. Its existing collider still serves object picking.</summary>
    internal sealed class YardWalkSurface : MonoBehaviour
    {
        public static void Mark(GameObject ground) => ground.AddComponent<YardWalkSurface>();
    }
}
