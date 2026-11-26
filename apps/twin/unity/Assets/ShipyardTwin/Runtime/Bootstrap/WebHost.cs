using System;
using System.Threading.Tasks;
using UnityEngine;

namespace ShipyardTwin
{
    /// <summary>
    /// Differences between the desktop player and the browser (WebGL) build served by the Portal at /twin/.
    /// In the browser there are no environment variables and no worker threads: the ERP is the page's own origin
    /// (the Portal proxies /api/*) and the token is the Portal login kept in the tab's sessionStorage.
    /// </summary>
    public static class WebHost
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        [System.Runtime.InteropServices.DllImport("__Internal")]
        private static extern string ShipyardWeb_Origin();

        [System.Runtime.InteropServices.DllImport("__Internal")]
        private static extern string ShipyardWeb_Token();

        [System.Runtime.InteropServices.DllImport("__Internal")]
        private static extern void ShipyardWeb_Leave();

        [System.Runtime.InteropServices.DllImport("__Internal")]
        private static extern string ShipyardWeb_Query(string name);

        /// <summary>A query parameter of the page address (the Portal opens /twin/?hull=…&amp;block=…); null when absent.</summary>
        public static string Query(string name)
        {
            var value = ShipyardWeb_Query(name);
            return string.IsNullOrEmpty(value) ? null : value;
        }

        [System.Runtime.InteropServices.DllImport("__Internal")]
        private static extern double ShipyardWeb_PixelRatio();

        /// <summary>Canvas pixels per CSS pixel: UI scaled by this matches the size of ordinary web pages.</summary>
        public static float PixelRatio => Mathf.Clamp((float)ShipyardWeb_PixelRatio(), 1f, 4f);

        /// <summary>Back to the Portal home in the same tab (Application.Quit does nothing in a browser).</summary>
        public static void Leave() => ShipyardWeb_Leave();

        public static bool IsBrowser => true;

        public static string ErpUrl => ShipyardWeb_Origin() ?? string.Empty;

        public static string Token => ShipyardWeb_Token() ?? string.Empty;
#else
        public static bool IsBrowser => false;

        public static float PixelRatio => 1f;

        public static void Leave()
        {
        }

        public static string Query(string name) => null;

        public static string ErpUrl => (Environment.GetEnvironmentVariable("SHIPYARD_API_URL") ?? string.Empty).TrimEnd('/');

        public static string Token => Environment.GetEnvironmentVariable("SHIPYARD_API_TOKEN");
#endif

        /// <summary>Task.Run on desktop; inline on WebGL, which has no thread pool.</summary>
        public static Task<T> Offload<T>(Func<T> work)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            try
            {
                return Task.FromResult(work());
            }
            catch (Exception exception)
            {
                return Task.FromException<T>(exception);
            }
#else
            return Task.Run(work);
#endif
        }
    }
}
