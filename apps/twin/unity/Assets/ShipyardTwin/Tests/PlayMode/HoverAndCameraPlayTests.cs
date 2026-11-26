using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace ShipyardTwin.Tests
{
    /// <summary>
    /// Play Mode checks in the generated scene: what the hover tooltip names under a screen point (the same
    /// <see cref="HoverTooltipView.Pick"/> the tooltip uses every frame) and how mouse buttons move the camera.
    /// </summary>
    public sealed class HoverAndCameraPlayTests
    {
        private Camera _camera;
        private OrbitCameraRig _rig;
        private ShipView _ship;

        [UnitySetUp]
        public IEnumerator LoadScene()
        {
            ShipyardTwinBootstrap.AsOfOverride = "2026-11-17";
            SceneManager.LoadScene("ShipyardTwin");
            _ship = null;
            _rig = null;
            var deadline = Time.realtimeSinceStartup + 60f;
            for (var i = 0; Time.realtimeSinceStartup < deadline && (_ship == null || !_ship.Ready || _rig == null); i++)
            {
                yield return null;
                _ship = Object.FindFirstObjectByType<ShipView>();
                _rig = Object.FindFirstObjectByType<OrbitCameraRig>();
            }

            _camera = Camera.main;
            Assert.IsTrue(_ship != null && _ship.Ready, "Scene constructed and yard blocks placed.");
            Assert.IsNotNull(_camera);
        }

        private HoverTarget PickAt(Vector3 world)
        {
            var screen = _camera.WorldToScreenPoint(world);
            Assert.Greater(screen.z, 0f, "In front of the camera.");
            return HoverTooltipView.Pick(_camera, screen);
        }

        private IEnumerator Look(Vector3 pivot, float yaw, float pitch, float distance)
        {
            _camera.transform.rotation = Quaternion.Euler(pitch, yaw, 0);
            _camera.transform.position = pivot - _camera.transform.rotation * Vector3.forward * distance;
            _rig.enabled = false;   // hold the pose
            yield return null;
        }

        [UnityTest]
        public IEnumerator WalkButton_FollowsThirdPersonAvatarAndRestoresOrbit()
        {
            var dashboard = Object.FindFirstObjectByType<DashboardShellView>().GetComponent<UIDocument>().rootVisualElement;
            var button = dashboard.Q("top-nav").Q<Button>("nav-walk");
            Assert.IsNotNull(button);

            var position = _camera.transform.position;
            var rotation = _camera.transform.rotation;
            var projection = _camera.projectionMatrix;
            Submit(button);
            Assert.IsTrue(_rig.IsWalking);
            Assert.AreEqual("지도 시점", button.Q<Label>("nav-walk-label").text);
            Assert.AreEqual(WalkPace.Normal, _rig.Pace);
            Assert.That(_rig.PaceMetersPerSecond, Is.EqualTo(1.8f));
            var worker = GameObject.Find("WalkWorker");
            Assert.IsNotNull(worker);
            Assert.IsNull(_camera.transform.Find("WalkWorkerArms"));
            Assert.IsEmpty(worker.GetComponentsInChildren<Collider>());
            Assert.IsNotEmpty(worker.GetComponentsInChildren<MeshRenderer>());
            Assert.IsTrue(Physics.Raycast(worker.transform.position + Vector3.up, Vector3.down, out var ground, 2f));
            Assert.That(ground.distance, Is.EqualTo(1f).Within(0.03f));
            Assert.That(Vector3.Dot(_camera.transform.forward, worker.transform.position + Vector3.up * 1.15f - _camera.transform.position),
                Is.GreaterThan(0.1f), "The camera looks at the avatar from behind.");
            var aim = worker.transform.position + Vector3.up * 1.15f;
            var cameraOffset = _camera.transform.position - aim;
            Assert.Greater(cameraOffset.magnitude, 0.9f);
            var cameraWall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cameraWall.transform.SetPositionAndRotation(aim + cameraOffset.normalized * 0.7f, Quaternion.LookRotation(cameraOffset));
            cameraWall.transform.localScale = new Vector3(2f, 2f, 0.1f);
            Physics.SyncTransforms();
            yield return null;
            yield return null;
            Assert.Less(Vector3.Distance(_camera.transform.position, aim), cameraOffset.magnitude - 0.3f, "A wall pulls the follow camera in.");
            Object.Destroy(cameraWall);
            yield return null;
            var spawn = worker.transform.position;
            var travelled = 0f;
            var walkDirection = Vector2.zero;
            foreach (var direction in new[] { Vector2.down, Vector2.right, Vector2.left, Vector2.up })
            {
                var start = worker.transform.position;
                _rig.SetWalkInput(direction);
                var deadline = Time.realtimeSinceStartup + 1.25f;
                while (Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                    travelled = Mathf.Max(travelled, Vector3.Distance(worker.transform.position, start));
                    if (travelled > 0.4f) break;
                }
                if (travelled <= 0.4f) continue;
                walkDirection = direction;
                break;
            }
            _rig.SetWalkInput(Vector2.zero);
            Assert.Greater(travelled, 0.4f, "Walk input moves the avatar along the yard at " + spawn);
            Assert.IsNotNull(_camera.GetComponent<AudioSource>(), "A foot contact while walking plays a boot step.");
            Assert.IsTrue(Physics.Raycast(worker.transform.position + Vector3.up, Vector3.down, out ground, 2f));
            Assert.That(ground.distance, Is.EqualTo(1f).Within(0.03f));

            var beforeBarrier = worker.transform.position;
            var flatYaw = Quaternion.Euler(0f, _camera.transform.eulerAngles.y, 0f);
            var forward = flatYaw * new Vector3(walkDirection.x, 0f, walkDirection.y);
            var barrier = GameObject.CreatePrimitive(PrimitiveType.Cube);
            barrier.transform.SetPositionAndRotation(beforeBarrier + 0.8f * forward + 1.5f * Vector3.up, Quaternion.LookRotation(forward));
            barrier.transform.localScale = new Vector3(4f, 3f, 0.2f);
            Physics.SyncTransforms();
            _rig.SetWalkInput(walkDirection);
            yield return new WaitForSecondsRealtime(0.75f);
            _rig.SetWalkInput(Vector2.zero);
            Assert.Less(Vector3.Dot(worker.transform.position - beforeBarrier, forward), 0.55f,
                "The avatar stops before a wall.");
            Object.Destroy(barrier);

            var slow = dashboard.Q<Button>("walk-pace-slow");
            var normal = dashboard.Q<Button>("walk-pace-normal");
            var fast = dashboard.Q<Button>("walk-pace-fast");
            Assert.IsNotNull(slow);
            Assert.IsNotNull(normal);
            Assert.IsNotNull(fast);
            Submit(slow);
            Assert.AreEqual(WalkPace.Slow, _rig.Pace);
            Assert.That(_rig.PaceMetersPerSecond, Is.EqualTo(1f));
            Submit(fast);
            Assert.AreEqual(WalkPace.Fast, _rig.Pace);
            Assert.That(_rig.PaceMetersPerSecond, Is.EqualTo(3.2f));
            Submit(normal);
            Assert.AreEqual(WalkPace.Normal, _rig.Pace);
            Submit(fast);

            Submit(button);
            Assert.IsFalse(_rig.IsWalking);
            Assert.AreEqual("걸어보기", button.Q<Label>("nav-walk-label").text);
            Assert.IsFalse(worker.activeInHierarchy);
            Assert.That(Vector3.Distance(position, _camera.transform.position), Is.LessThan(0.01f));
            Assert.That(Quaternion.Angle(rotation, _camera.transform.rotation), Is.LessThan(0.01f));
            Assert.AreEqual(projection, _camera.projectionMatrix);
            Submit(button);
            Assert.AreEqual(WalkPace.Fast, _rig.Pace, "Returning to third person keeps the chosen pace.");
        }

        [UnityTest]
        public IEnumerator WalkAvatar_ShowsEachFigureAndHidesThePreviousOne()
        {
            var palette = GrowthLab.Core.CoreBootstrap.Active.Context.Services.Get<ShipPalette>();
            var yardShader = palette.Get("concrete").shader;
            using var view = new WalkWorkerView(palette);
            Transform previous = null;
            foreach (var avatar in new[] { WalkAvatar.Worker, WalkAvatar.Steward, WalkAvatar.Master })
            {
                view.Show(avatar);
                view.Update(Vector3.zero, 30f, 0.35f, 1f);
                yield return null;
                var root = GameObject.Find(WalkWorkerView.BodyRootName);
                var active = root.transform.Find(avatar.ToString());
                Assert.IsNotNull(active, avatar.ToString());
                Assert.IsTrue(active.gameObject.activeInHierarchy, avatar.ToString());
                Assert.IsNotEmpty(active.GetComponentsInChildren<MeshRenderer>());
                foreach (var renderer in active.GetComponentsInChildren<MeshRenderer>())
                    Assert.AreSame(yardShader, renderer.sharedMaterial.shader);
                Assert.IsEmpty(active.GetComponentsInChildren<Collider>());
                if (previous != null) Assert.IsFalse(previous.gameObject.activeInHierarchy);
                previous = active;
            }
            view.Hide();
            Assert.IsFalse(previous.gameObject.activeInHierarchy);
        }

        [UnityTest]
        public IEnumerator WalkAvatar_OnlyTheAdminAccountCanChooseACharacter()
        {
            var dashboard = Object.FindFirstObjectByType<DashboardShellView>().GetComponent<UIDocument>().rootVisualElement;
            var toggle = dashboard.Q<Button>("walk-avatar-toggle");
            var menu = dashboard.Q("walk-avatar-menu");
            Assert.IsNotNull(toggle);
            Assert.IsNotNull(menu);
            Assert.IsTrue(_rig.SetWalking(true));
            _rig.ApplyWalkAccount("PROD");
            yield return null;
            Assert.IsFalse(_rig.CanChooseWalkAvatar);
            Assert.IsFalse(dashboard.ClassListContains("tw-avatar-allowed"));
            Assert.IsFalse(_rig.SetWalkAvatar(WalkAvatar.Master));
            Assert.AreEqual(WalkAvatar.Worker, _rig.CurrentWalkAvatar);

            _rig.ApplyWalkAccount("ADMIN");
            yield return null;
            Assert.IsTrue(_rig.CanChooseWalkAvatar);
            Assert.IsTrue(dashboard.ClassListContains("tw-avatar-allowed"));
            Assert.AreEqual(WalkAvatar.Steward, _rig.CurrentWalkAvatar);
            Submit(toggle);
            Assert.IsTrue(menu.ClassListContains("tw-avatar-menu--open"));
            Submit(menu.Q<Button>("walk-avatar-master"));
            Assert.AreEqual(WalkAvatar.Master, _rig.CurrentWalkAvatar);
            Assert.AreEqual("무림고수", toggle.text);
            Assert.IsFalse(menu.ClassListContains("tw-avatar-menu--open"));
            var root = GameObject.Find(WalkWorkerView.BodyRootName).transform;
            Assert.IsTrue(root.Find("Master").gameObject.activeInHierarchy);
            Assert.IsFalse(root.Find("Steward").gameObject.activeInHierarchy);
            Assert.IsTrue(_rig.SetWalking(false));
            Assert.IsFalse(_rig.SetWalkAvatar(WalkAvatar.Worker));
            Assert.IsTrue(_rig.SetWalking(true));
            Assert.AreEqual(WalkAvatar.Master, _rig.CurrentWalkAvatar);

            _rig.ApplyWalkAccount("EXEC");
            yield return null;
            Assert.IsFalse(_rig.CanChooseWalkAvatar);
            Assert.IsFalse(dashboard.ClassListContains("tw-avatar-allowed"));
            Assert.IsFalse(_rig.SetWalkAvatar(WalkAvatar.Worker));
            Assert.AreEqual(WalkAvatar.Master, _rig.CurrentWalkAvatar);
        }

        private static void Submit(Button button)
        {
            using var submit = NavigationSubmitEvent.GetPooled();
            submit.target = button;
            button.SendEvent(submit);
        }

        [UnityTest]
        public IEnumerator WalkHazard_ShowsForApproachingEquipmentAndClearsOnLeaving()
        {
            var dashboard = Object.FindFirstObjectByType<DashboardShellView>().GetComponent<UIDocument>().rootVisualElement;
            var warning = dashboard.Q("walk-warning");
            Assert.IsNotNull(warning);
            Assert.AreEqual(PickingMode.Ignore, warning.pickingMode);
            _ship.Calendar.Playing = false;
            Assert.IsTrue(_rig.SetWalking(true));
            var feet = GameObject.Find("WalkWorker").transform.position;
            var baseline = _rig.WalkHazards.Count;
            var vehicle = GameObject.CreatePrimitive(PrimitiveType.Cube);
            vehicle.transform.localScale = new Vector3(3f, 2f, 4f);
            vehicle.transform.position = feet + Vector3.up + Vector3.forward * 200f;
            var target = HoverTarget.Attach(vehicle, "중장비", "테스트 지게차").Identify(TargetCategory.Vehicle, "TEST-FL");
            target.IsHeavyEquipment = true;
            yield return null;
            Assert.AreEqual(baseline, _rig.WalkHazards.Count);

            vehicle.transform.position = feet + Vector3.up + Vector3.forward * 1.5f;
            yield return null;
            yield return null;
            Assert.AreEqual(baseline + 1, _rig.WalkHazards.Count);
            Assert.IsTrue(_rig.WalkHazards.IsEstimated);
            Assert.AreEqual("테스트 지게차", _rig.WalkHazards.Name);
            Assert.IsTrue(warning.ClassListContains("tw-walk-warning--show"));
            StringAssert.Contains("테스트 지게차", warning.Q<Label>("walk-warning-name").text);
            StringAssert.Contains("위험반경 4 m", warning.Q<Label>("walk-warning-range").text);
            StringAssert.Contains("추정", warning.Q<Label>("walk-warning-sim").text);

            vehicle.transform.position = feet + Vector3.up + Vector3.forward * 200f;
            yield return null;
            yield return null;
            Assert.AreEqual(baseline, _rig.WalkHazards.Count);
            Assert.AreEqual(baseline > 0, warning.ClassListContains("tw-walk-warning--show"));

            vehicle.transform.position = feet + Vector3.up + Vector3.forward * 1.5f;
            yield return null;
            yield return null;
            Assert.AreEqual(baseline + 1, _rig.WalkHazards.Count);
            Assert.IsTrue(_rig.SetWalking(false));
            yield return null;
            Assert.Zero(_rig.WalkHazards.Count);
            Assert.IsFalse(warning.ClassListContains("tw-walk-warning--show"));
            Object.Destroy(vehicle);
        }

        [UnityTest]
        public IEnumerator YardSearch_FramesAFacilityAndOpensItsDetailWithoutSeeking()
        {
            var dock = _ship.Model.Facilities.First(f => f.kind == "도크" && f.HasFootprint);
            var shell = Object.FindFirstObjectByType<DashboardShellView>();
            var root = shell.GetComponent<UIDocument>().rootVisualElement;
            Assert.IsTrue(_rig.SetWalking(true));
            shell.OpenSearch();
            root.Q<TextField>("search-field").value = dock.location;
            var results = root.Q("search-results");
            var deadline = Time.realtimeSinceStartup + 3f;
            while (results.childCount == 0 && Time.realtimeSinceStartup < deadline) yield return null;

            Assert.Greater(results.childCount, 0, "Typing shows matching cards without submitting the search.");
            var card = results[0];
            Assert.IsNull(card.Q<Button>(), "The whole card opens the result.");
            Assert.IsFalse(card.ClassListContains("tw-search-card--off"));
            var before = _ship.CurrentDate;
            var selected = _ship.Selected;

            using (var down = PointerDownEvent.GetPooled(new Event { type = EventType.MouseDown, button = 0 }))
            {
                down.target = card;
                card.SendEvent(down);
            }
            using (var up = PointerUpEvent.GetPooled(new Event { type = EventType.MouseUp, button = 0 }))
            {
                up.target = card;
                card.SendEvent(up);
            }

            Assert.IsFalse(shell.IsSearchOpen);
            Assert.IsFalse(_rig.IsWalking, "Opening a map search result restores the map camera.");
            Assert.IsTrue(ObjectDetailView.Active.IsOpen);
            Assert.AreEqual(before, _ship.CurrentDate);
            Assert.AreSame(selected, _ship.Selected);
            Assert.That(_rig.PivotGoal.x, Is.EqualTo(dock.x).Within(dock.size_x_m));
            Assert.That(_rig.PivotGoal.z, Is.EqualTo(dock.y).Within(dock.size_x_m));
            yield return null;
        }

        [UnityTest]
        public IEnumerator Hover_NamesBlocksFacilitiesAndTheShip()
        {
            var twin = _ship.Model.ToTwinFile(_ship.Model.Hull("SN2040"));

            var dock = twin.yard.facilities.First(f => f.location == twin.placement.dock);
            var dockTop = ShipCoordinates.YardToUnity(dock.x, dock.y, YardLayout.QuayLevel);

            // Day 0: no block erected. From straight above the dock the keel blocks name the ship, and no point
            // over a (hidden) block's landed place names a block.
            _ship.Select("SN2040");
            _ship.Playback.Seek(0f);
            yield return Look(dockTop, 0f, 89f, 520f);
            var keel = Object.FindObjectsByType<Transform>(FindObjectsSortMode.None).First(t => t.name == "KeelBlocks SN2040");
            var keelPicks = keel.GetComponentsInChildren<Renderer>().Where(r => r.name == "Keel")
                .Select(r => PickAt(r.bounds.center + Vector3.up * r.bounds.extents.y)).Where(t => t != null).ToList();
            Assert.IsTrue(keelPicks.Any(t => t.Kind == "선박"), "Keel blocks name the ship.");
            var ship = keelPicks.First(t => t.Kind == "선박");
            Assert.AreEqual(twin.hull.vessel_name, ship.Title);
            StringAssert.Contains(twin.hull.hull, ship.Detail);
            var blockTargets = _ship.GetComponentsInChildren<HoverTarget>().Where(t => t.Kind == "블록" && t.Title.StartsWith("SN2040-")).ToList();
            Assert.Greater(blockTargets.Count, 100);
            var hidden = blockTargets.Where(b => !b.GetComponent<Collider>().enabled).ToList();
            Assert.Greater(hidden.Count, 0, "Some blocks are still in production on day 0.");
            Assert.IsFalse(hidden.Select(b => PickAt(_ship.transform.TransformPoint(b.transform.localPosition))).Any(t => hidden.Contains(t)),
                "Hidden blocks are never picked.");

            // All erected: the top face of each block, seen from above, names that block unless the goliath is over it.
            _ship.Playback.Seek(_ship.Playback.Schedule.Duration + 10f);
            yield return null;
            yield return Look(dockTop, 0f, 89f, 520f);
            var own = 0;
            foreach (var target in blockTargets)
            {
                var r = target.GetComponent<Renderer>();
                var hit = PickAt(new Vector3(r.bounds.center.x, r.bounds.max.y, r.bounds.center.z));
                if (hit == target) own++;
                if (hit != null) Assert.AreNotEqual("시설", hit.Kind, "No facility picked through the ship.");
            }

            Assert.Greater(own, blockTargets.Count / 4, "Blocks name themselves from above.");
            var block = blockTargets.First(t => PickAt(new Vector3(t.GetComponent<Renderer>().bounds.center.x,
                t.GetComponent<Renderer>().bounds.max.y, t.GetComponent<Renderer>().bounds.center.z)) == t);
            StringAssert.StartsWith(twin.hull.hull + "-", block.Title, "Block ID.");
            StringAssert.Contains("호선 " + twin.hull.hull, block.Detail);

            // The goliath crane occludes: its girder top is never named, nor anything under it.
            var gantry = Object.FindObjectsByType<GantryCraneView>(FindObjectsSortMode.None)
                .SelectMany(g => g.GetComponentsInChildren<Renderer>()).OrderByDescending(r => r.bounds.max.y).First();
            Assert.IsNull(PickAt(new Vector3(gantry.bounds.center.x, gantry.bounds.max.y, gantry.bounds.center.z)), "Crane is an occluder.");

            // A shop roof: the ERP facility name.
            var shop = twin.yard.facilities.First(f => f.location == "GY-ASSY1");
            var roof = ShipCoordinates.YardToUnity(shop.x, shop.y, YardLayout.QuayLevel + 20f);
            yield return Look(roof, 0f, 89f, 200f);
            var hall = PickAt(ShipCoordinates.YardToUnity(shop.x, shop.y, YardLayout.QuayLevel));
            Assert.IsNotNull(hall);
            Assert.AreEqual("시설", hall.Kind);
            Assert.AreEqual(shop.name, hall.Title);

            // Open sea and the paved road: nothing.
            Assert.IsNull(PickAt(ShipCoordinates.YardToUnity(shop.x - 1500f, shop.y + 3000f, YardLayout.SeaLevel)), "Far sea.");
            yield return Look(ShipCoordinates.YardToUnity(dock.x + dock.size_x_m / 2f + 300f, dock.y, YardLayout.SeaLevel), 0f, 89f, 200f);
            Assert.IsNull(PickAt(ShipCoordinates.YardToUnity(dock.x + dock.size_x_m / 2f + 300f, dock.y, YardLayout.SeaLevel)), "Sea off the gate.");

            // A dock (ERP facility) wall top, sampled along its length (the goliath may stand over part of it).
            yield return Look(dockTop, 0f, 89f, 520f);
            var wall = Enumerable.Range(0, 9).Select(i => dock.x - dock.size_x_m / 2f + 20f + i * (dock.size_x_m - 40f) / 8f)
                .Select(x => PickAt(ShipCoordinates.YardToUnity(x, dock.y + dock.size_y_m / 2f - 3f, YardLayout.QuayLevel)))
                .Where(t => t != null).ToList();
            Assert.IsTrue(wall.Any(t => t.Title == dock.name && t.Kind == "시설"), "Dock wall names the dock.");
        }

        [UnityTest]
        public IEnumerator Hover_HiddenOverUiWhileDraggingAndOffScreen()
        {
            var size = new Vector2(1600, 900);
            Assert.IsTrue(HoverTooltipView.Allowed(false, false, new Vector2(800, 450), size));
            Assert.IsFalse(HoverTooltipView.Allowed(true, false, new Vector2(800, 450), size), "Over the dashboard.");
            Assert.IsFalse(HoverTooltipView.Allowed(false, true, new Vector2(800, 450), size), "Camera drag.");
            Assert.IsFalse(HoverTooltipView.Allowed(false, false, new Vector2(-5, 450), size), "Off screen.");
            var tooltip = Object.FindFirstObjectByType<HoverTooltipView>();
            Assert.IsNotNull(tooltip, "Tooltip view in the scene.");
            yield return null;
        }

        [UnityTest]
        public IEnumerator Camera_LeftPansRightOrbitsMiddlePansWheelZooms()
        {
            _rig.SetView("overview", snap: true);
            yield return null;
            Vector3 pivot = _rig.PivotGoal;
            float yaw = _rig.YawGoal, pitch = _rig.PitchGoal, distance = _rig.DistanceGoal;

            // Left drag sideways: the ground follows the cursor, so the pivot slides against the camera's right axis, no orbit.
            _rig.ApplyMouse(true, false, false, new Vector2(40f, 0f), new Vector2(3f, 2f), 0f);
            var moved = _rig.PivotGoal - pivot;
            Assert.Greater(moved.magnitude, 1f, "Left drag pans.");
            Assert.AreEqual(yaw, _rig.YawGoal, 1e-4f, "Left drag does not orbit.");
            Assert.AreEqual(pitch, _rig.PitchGoal, 1e-4f);
            var right = Quaternion.Euler(pitch, yaw, 0) * Vector3.right;
            Assert.AreEqual(-1f, Vector3.Dot(moved.normalized, right), 1e-3f, "Against the drag along the screen's x axis.");
            // Left drag up/down moves over the ground (kept above it).
            pivot = _rig.PivotGoal;
            _rig.ApplyMouse(true, false, false, new Vector2(0f, -3f), Vector2.zero, 0f);
            Assert.Greater(Vector3.Distance(_rig.PivotGoal, pivot), 0.01f, "Vertical left drag pans.");
            Assert.GreaterOrEqual(_rig.PivotGoal.y, 0f);

            pivot = _rig.PivotGoal;
            _rig.ApplyMouse(false, true, false, new Vector2(40f, 25f), new Vector2(3f, 2f), 0f);
            Assert.AreNotEqual(yaw, _rig.YawGoal, "Right drag orbits.");
            Assert.AreEqual(0f, Vector3.Distance(_rig.PivotGoal, pivot), 1e-4f, "Right drag does not pan.");

            yaw = _rig.YawGoal;
            _rig.ApplyMouse(false, false, true, new Vector2(30f, 20f), Vector2.zero, 0f);
            Assert.Greater(Vector3.Distance(_rig.PivotGoal, pivot), 1f, "Middle drag pans.");
            Assert.AreEqual(pivot.y, _rig.PivotGoal.y, 1e-4f, "Middle drag stays on the ground plane.");
            Assert.AreEqual(yaw, _rig.YawGoal, 1e-4f);

            _rig.ApplyMouse(false, false, false, Vector2.zero, Vector2.zero, 1f);
            Assert.Less(_rig.DistanceGoal, distance, "Wheel up zooms in.");
            distance = _rig.DistanceGoal;
            _rig.ApplyMouse(false, false, false, Vector2.zero, Vector2.zero, 400f);
            Assert.Greater(_rig.DistanceGoal, distance * 0.5f, "A huge browser wheel delta zooms a few notches, not to the limit.");
            pivot = _rig.PivotGoal;
            _rig.ApplyMouse(false, false, false, Vector2.zero, Vector2.zero, 1f, new Vector2(Screen.width * 0.8f, Screen.height * 0.3f));
            Assert.Greater(Vector3.Distance(_rig.PivotGoal, pivot), 0.5f, "Zooming in off-centre moves towards the cursor.");

            // A press over the UI blocks the whole drag until release; a press in the scene does not.
            Assert.IsTrue(OrbitCameraRig.DragBlocked(false, pressed: true, held: true, overUi: true));
            Assert.IsTrue(OrbitCameraRig.DragBlocked(true, pressed: false, held: true, overUi: false), "Still blocked off the UI.");
            Assert.IsFalse(OrbitCameraRig.DragBlocked(true, pressed: false, held: false, overUi: false), "Released.");
            Assert.IsFalse(OrbitCameraRig.DragBlocked(false, pressed: true, held: true, overUi: false));
        }
    }
}
