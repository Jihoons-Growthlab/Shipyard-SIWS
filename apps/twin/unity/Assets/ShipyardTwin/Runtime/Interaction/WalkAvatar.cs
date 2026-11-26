using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ShipyardTwin.Tests.PlayMode")]

namespace ShipyardTwin
{
    public enum WalkAvatar { Worker, Steward, Master }

    /// <summary>Maps the authenticated account to an appearance for yard exploration.</summary>
    public static class WalkAvatarRoles
    {
        public static WalkAvatar ForGroup(string groupId) => groupId?.Trim().ToUpperInvariant() switch
        {
            "ADMIN" or "관리자" => WalkAvatar.Steward,
            "EXEC" or "EXECUTIVE" or "임원" => WalkAvatar.Master,
            _ => WalkAvatar.Worker,
        };

        internal static bool CanChoose(string groupId) => string.Equals(groupId, "ADMIN", StringComparison.Ordinal);

        internal static IEnumerator Resolve(string url, string token, Action<string> resolved)
        {
            using var request = UnityWebRequest.Get(url + "/api/me");
            request.SetRequestHeader("Authorization", "Bearer " + token);
            request.timeout = 6;
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                resolved(null);
                yield break;
            }
            Account account = null;
            try
            {
                account = JsonUtility.FromJson<Account>(request.downloadHandler.text);
            }
            catch (ArgumentException) { }
            resolved(account?.group_id);
        }

        [Serializable]
        private sealed class Account
        {
            public string group_id;
        }
    }
}
