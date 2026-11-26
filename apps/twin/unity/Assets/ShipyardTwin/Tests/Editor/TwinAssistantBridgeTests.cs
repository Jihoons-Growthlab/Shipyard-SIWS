using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace ShipyardTwin.Tests
{
    /// <summary>Browser assistant ↔ scene link (<see cref="TwinAssistantBridge"/>) without a loaded yard.</summary>
    public sealed class TwinAssistantBridgeTests
    {
        private GameObject _host;
        private TwinAssistantBridge _bridge;
        private readonly List<TwinAssistantBridge.Reply> _replies = new List<TwinAssistantBridge.Reply>();

        [SetUp]
        public void SetUp()
        {
            _host = new GameObject("assistant-bridge-test");
            _bridge = _host.AddComponent<TwinAssistantBridge>();
            _replies.Clear();
            _bridge.Replied += _replies.Add;
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_host);

        [Test]
        public void Clean_TrimsControlCharactersAndLength()
        {
            Assert.AreEqual(string.Empty, TwinAssistantBridge.Clean(null));
            Assert.AreEqual(string.Empty, TwinAssistantBridge.Clean("   "));
            Assert.AreEqual("J안벽", TwinAssistantBridge.Clean("  J안벽\n"));
            Assert.AreEqual("a b", TwinAssistantBridge.Clean("a\tb"));
            Assert.AreEqual(80, TwinAssistantBridge.Clean(new string('x', 300)).Length);
        }

        [Test]
        public void OffScreenReason_KeepsTheDateAndNamesTheCase()
        {
            StringAssert.Contains("탑재 전", TwinAssistantBridge.OffScreenReason(TargetCategory.Block));
            StringAssert.Contains("호선", TwinAssistantBridge.OffScreenReason(TargetCategory.Hull));
            foreach (var category in new[] { TargetCategory.Block, TargetCategory.Hull, TargetCategory.Facility, TargetCategory.Crane })
                StringAssert.Contains("재생 일자는 바꾸지 않습니다", TwinAssistantBridge.OffScreenReason(category));
        }

        [Test]
        public void Find_WithoutYard_AnswersNotReady()
        {
            _bridge.Handle("{\"id\":3,\"op\":\"find\",\"query\":\"J안벽\"}");
            Assert.AreEqual(1, _replies.Count);
            Assert.AreEqual(3, _replies[0].id);
            Assert.AreEqual("find", _replies[0].op);
            Assert.AreEqual("notready", _replies[0].state);
        }

        [Test]
        public void Open_WithoutCandidates_FailsWithoutMovingTheCamera()
        {
            _bridge.Handle("{\"id\":4,\"op\":\"open\",\"pick\":0}");
            Assert.AreEqual(1, _replies.Count);
            Assert.AreEqual("failed", _replies[0].state);
            StringAssert.Contains("다시 요청", _replies[0].reason);
        }

        [Test]
        public void Lift_WithoutYard_FailsWithAReason()
        {
            _bridge.Handle("{\"id\":7,\"op\":\"lift\"}");
            Assert.AreEqual(1, _replies.Count);
            Assert.AreEqual("failed", _replies[0].state);
            StringAssert.Contains("인양 중인 블록이 없습니다", _replies[0].reason);
        }

        [Test]
        public void UnknownOrBrokenRequests_AreIgnored()
        {
            _bridge.Handle("not json");
            _bridge.Handle("{\"id\":5,\"op\":\"teleport\",\"query\":\"0,0,0\"}");
            _bridge.Handle("{\"id\":6,\"op\":\"state\"}");
            Assert.AreEqual(1, _replies.Count, "Only the state request answers.");
            Assert.AreEqual("notready", _replies[0].state);
        }
    }
}
