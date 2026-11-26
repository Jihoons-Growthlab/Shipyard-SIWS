using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ShipyardTwin.Tests
{
    /// <summary>
    /// The whole flow in the generated scene with the bundled yard snapshot (2 hulls) on plan day 2026-11-17:
    /// startup yard placement → hull selection (no autoplay) → manual play → slot → crane → hull → switching hulls.
    /// </summary>
    public sealed class YardFlowPlayTests
    {
        private ShipView _ship;

        [UnitySetUp]
        public IEnumerator LoadScene()
        {
            ShipyardTwinBootstrap.AsOfOverride = "2026-11-17";
            SceneManager.LoadScene("ShipyardTwin");
            var deadline = Time.realtimeSinceStartup + 60f;
            for (var i = 0; Time.realtimeSinceStartup < deadline && (_ship == null || !_ship.Ready); i++)
            {
                yield return null;
                _ship = Object.FindFirstObjectByType<ShipView>();
            }

            Assert.IsTrue(_ship != null && _ship.Ready, "Yard blocks placed. ship=" + (_ship != null) + " actors=" + _ship?.ActorCount +
                                                        " meshes=" + _ship?.LoadedMeshes + " hulls=" + _ship?.Model?.Hulls.Count + " source=" + _ship?.Source?.Status);
        }

        [TearDown]
        public void Reset() => ShipyardTwinBootstrap.AsOfOverride = null;

        private Transform Block(string id) => _ship.transform.Find(id);

        private static void Near(Vector3 expected, Vector3 actual, string message, float tolerance = 0.05f) =>
            Assert.Less(Vector3.Distance(expected, actual), tolerance, message + " expected " + expected + " got " + actual);

        [UnityTest]
        public IEnumerator Goliath_TrolleyAndHooksStayAboveTheBlockWhileItLands()
        {
            var crane = Object.FindObjectsByType<HoverTarget>(FindObjectsSortMode.None)
                .Single(target => target.Category == TargetCategory.Crane && target.Id == "FLEET-GC");
            var trolley = crane.transform.Find("UpperTrolley");
            var hooks = crane.transform.Cast<Transform>().Where(part => part.name == "Hook").ToArray();
            Assert.IsNotNull(trolley);
            Assert.AreEqual(2, hooks.Length);

            // The same crane serves two docks on opposite sides of its rail centre line.
            foreach (var hull in new[] { "SN2051", "SN2054" })
            {
                _ship.Select(hull);
                var playback = _ship.Playback;
                Assert.IsNotNull(playback);
                var entry = playback.Schedule.Entries.First(e => _ship.LiftCrane(e.Block.block) == "FLEET-GC");
                var previousHeight = float.PositiveInfinity;
                foreach (var progress in new[] { 0.85f, 0.95f })
                {
                    playback.Seek(entry.FinishDay - entry.LiftDays * (1f - progress));
                    yield return null;
                    yield return null;
                    Assert.IsTrue(_ship.CurrentLiftHooked, hull);
                    Assert.AreEqual(entry.Block.block, _ship.CurrentLiftBlock.block);

                    var block = _ship.CurrentLift.position;
                    var hook = (hooks[0].position + hooks[1].position) / 2f;
                    Assert.Less(Vector2.Distance(BlockMotion.YardOf(block), BlockMotion.YardOf(trolley.position)), 0.05f,
                        "The upper trolley and goliath stand directly above the block.");
                    Assert.Less(Vector2.Distance(BlockMotion.YardOf(block), BlockMotion.YardOf(hook)), 0.05f,
                        "The hooks lower vertically below the trolley.");
                    Assert.Greater(hook.y, _ship.CurrentLiftBounds.max.y);
                    Assert.Less(hook.y, previousHeight);
                    previousHeight = hook.y;
                }
            }
        }

        [UnityTest]
        public IEnumerator Yard_PlacedAtStartupThenSimulatedPerHull()
        {
            var model = _ship.Model;
            Assert.AreEqual(new[] { "SN2040", "SN2046" }, model.Hulls.Select(h => h.Id).ToArray());
            Assert.IsNull(_ship.Selected, "No hull selected at startup.");
            Assert.IsNull(_ship.Playback, "Nothing to play before a hull is chosen.");

            // 1. Startup: every block where the plan puts it on the plan day, before any hull is chosen.
            var stocked = 0;
            foreach (var plan in model.Blocks.Values)
            {
                var block = Block(plan.Id);
                Assert.IsNotNull(block, plan.Id);
                var state = YardModel.StateAt(plan, model.AsOf);
                var renderer = block.GetComponent<Renderer>();
                switch (state)
                {
                    case BlockState.Stocked:
                        stocked++;
                        Near(BlockMotion.InYard(plan.Record, plan.StayAt(model.AsOf)).position, block.position, plan.Id + " in its slot");
                        Assert.IsTrue(renderer.enabled, plan.Id + " drawn");
                        break;
                    case BlockState.Erected:
                        Near(BlockMotion.Landed(model.Blocks[plan.Id].Record.hull == "SN2040" ? model.Hull("SN2040").Placement : model.Hull("SN2046").Placement,
                            plan.Record).position, block.position, plan.Id + " on its hull");
                        Assert.IsTrue(renderer.enabled, plan.Id + " drawn");
                        break;
                    default:
                        Assert.IsFalse(renderer.enabled, plan.Id + " (" + state + ") not drawn");
                        Assert.IsFalse(block.GetComponent<Collider>().enabled);
                        break;
                }
            }

            Assert.Greater(stocked, 10, "SN2046's next blocks wait in the yard on Nov 17.");
            var unplaced = model.Blocks.Values.Where(b => b.Stays.Count == 0).Select(b => b.Id).ToList();
            Assert.AreEqual(4, unplaced.Count, "SN2040 blocks without a stocking slot (미배치).");
            var before = model.Hull("SN2040").Blocks.ToDictionary(b => b.Id, b => (Block(b.Id).position, Block(b.Id).GetComponent<Renderer>().enabled));

            // 2. Choosing SN2046 prepares its simulation at day 0 but does not play.
            _ship.Select("SN2046");
            var playback = _ship.Playback;
            Assert.IsNotNull(playback);
            Assert.IsFalse(playback.Playing);
            for (var f = 0; f < 30; f++) yield return null;
            Assert.AreEqual(0f, playback.Schedule.Cursor, "Selecting does not start playback.");
            Assert.AreEqual("GY-DOCK2", _ship.ActiveDock);
            foreach (var pair in before)
            {
                Assert.AreEqual(pair.Value.position, Block(pair.Key).position, pair.Key + " (other hull) unchanged");
                Assert.AreEqual(pair.Value.enabled, Block(pair.Key).GetComponent<Renderer>().enabled);
            }

            // Erection order follows the ERP plan and the predecessors land first.
            var schedule = playback.Schedule;
            var finish = schedule.Entries.ToDictionary(e => e.Block.block, e => e.FinishDay);
            foreach (var entry in schedule.Entries)
            {
                foreach (var predecessor in entry.Block.predecessors.Where(finish.ContainsKey))
                {
                    Assert.LessOrEqual(finish[predecessor], entry.FinishDay, predecessor + " before " + entry.Block.block);
                }
            }

            // 3. One lift: in its PE-yard slot just before, high on the hook midway, on the hull at the end.
            var hull = model.Hull("SN2046");
            var reach = BlockMotion.CraneReach(hull.Dock);
            var lift = schedule.Entries.First(e =>
            {
                var stay = model.Blocks[e.Block.block].LiftSource(schedule.Start.AddDays(e.FinishDay - e.LiftDays).Date);
                return stay != null && reach.Contains(new Vector2(stay.Row.x, stay.Row.y));
            });
            var plan2 = model.Blocks[lift.Block.block];
            var slot = BlockMotion.InYard(plan2.Record, plan2.LiftSource(schedule.Start.AddDays(lift.FinishDay - lift.LiftDays).Date));
            var landed = BlockMotion.Landed(hull.Placement, plan2.Record);
            var moved = Block(plan2.Id);
            playback.Seek(lift.FinishDay - lift.LiftDays - 0.01f);
            yield return null;
            Near(slot.position, moved.position, "Waiting in its slot");
            playback.Seek(lift.FinishDay - lift.LiftDays * 0.5f);
            yield return null;
            Assert.Greater(moved.position.y, slot.position.y + 10f, "On the hook midway: cursor " + schedule.Cursor + " lift " +
                (lift.FinishDay - lift.LiftDays) + ".." + lift.FinishDay + " progress " + schedule.Progress(lift) + " detail " +
                moved.GetComponent<HoverTarget>().Detail + " current " + _ship.CurrentLift?.name + " entries " + (_ship.Playback == playback));
            Assert.AreEqual(moved, _ship.CurrentLift);
            Assert.IsTrue(_ship.CurrentLiftHooked);
            playback.Seek(lift.FinishDay);
            yield return null;
            Near(landed.position, moved.position, "Landed on the hull");

            // A block stocked out of the crane's reach is driven to the rail head first.
            var far = schedule.Entries.FirstOrDefault(e =>
            {
                var stay = model.Blocks[e.Block.block].LiftSource(schedule.Start.AddDays(e.FinishDay - e.LiftDays).Date);
                return stay != null && !reach.Contains(new Vector2(stay.Row.x, stay.Row.y));
            });
            if (far != null)
            {
                playback.Seek(far.FinishDay - far.LiftDays * (1f - BlockMotion.GroundShare * 0.5f));
                yield return null;
                Assert.IsFalse(_ship.CurrentLiftHooked, "On a transporter before the crane takes it.");
            }

            // Manual play advances; pause holds.
            playback.Seek(0f);
            yield return null;
            playback.Playing = true;
            yield return new WaitForSeconds(0.5f);
            playback.Playing = false;
            var held = schedule.Cursor;
            Assert.Greater(held, 0f);
            for (var f = 0; f < 10; f++) yield return null;
            Assert.AreEqual(held, schedule.Cursor, "Paused.");

            // 4. Switching hulls: the previous playback stops, its blocks return to the plan day, no duplicates.
            playback.Playing = true;
            _ship.Select("SN2040");
            Assert.IsFalse(playback.Playing, "Previous playback stopped.");
            Assert.AreNotSame(playback, _ship.Playback);
            Assert.AreEqual(0f, _ship.Playback.Schedule.Cursor, "New hull from day 0.");
            yield return null;
            foreach (var plan in hull.Blocks)
            {
                var state = YardModel.StateAt(plan, model.AsOf);
                if (state == BlockState.Stocked)
                {
                    Near(BlockMotion.InYard(plan.Record, plan.StayAt(model.AsOf)).position, Block(plan.Id).position, plan.Id + " back in its slot");
                }
            }

            var blocks = _ship.GetComponentsInChildren<HoverTarget>(true).Count(t => t.Kind == "블록");
            var transforms = Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).Length;
            var meshes = new List<int>();
            for (var i = 0; i < 6; i++)
            {
                _ship.Select(i % 2 == 0 ? "SN2046" : "SN2040");
                for (var f = 0; f < 20; f++) yield return null;
                meshes.Add(_ship.LoadedMeshes);
            }

            Assert.AreEqual(model.Blocks.Count, blocks, "One object per block.");
            Assert.AreEqual(blocks, _ship.GetComponentsInChildren<HoverTarget>(true).Count(t => t.Kind == "블록"), "No duplicates after switching.");
            Assert.AreEqual(transforms, Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).Length, "Object count stable.");
            Assert.LessOrEqual(meshes.Max(), model.Blocks.Count);
            Assert.AreEqual(meshes[2], meshes[4], "Meshes do not pile up across switches: " + string.Join(",", meshes));
        }
    }
}
