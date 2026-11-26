using UnityEngine;

namespace ShipyardTwin
{
    /// <summary>
    /// Exit button action: stops Play Mode in the Unity editor, returns to the Portal in the browser build,
    /// quits the built app otherwise.
    /// </summary>
    public static class DashboardExit
    {
        public static void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.ExitPlaymode();
#elif UNITY_WEBGL
            WebHost.Leave();
#else
            Application.Quit();
#endif
        }
    }
}
