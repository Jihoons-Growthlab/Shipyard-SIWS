using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace ShipyardTwin.Tests
{
    /// <summary>Selected-model outline (<see cref="SelectionHighlight"/>) and heavy equipment zone (<see cref="EquipmentZone"/>).</summary>
    public sealed class SelectionHighlightTests
    {
        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (var o in _created.Where(o => o != null)) Object.DestroyImmediate(o);
            _created.Clear();
        }

        private HoverTarget Machine(string name, Vector3 size, bool heavy, TargetCategory category = TargetCategory.Vehicle)
        {
            var root = new GameObject(name);
            _created.Add(root);
            var body = GameObject.CreatePrimitive(PrimitiveType.Cube);   // split (hard-edge) normals, readable
            body.transform.SetParent(root.transform, false);
            body.transform.localScale = size;
            body.transform.localPosition = new Vector3(0f, size.y / 2f, 0f);
            var target = HoverTarget.Attach(root, "장비", name).Identify(category, name);
            target.IsHeavyEquipment = heavy;
            return target;
        }

        [Test]
        public void SmoothedNormals_CloseHardEdgesWithoutTouchingTheModel()
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _created.Add(cube);
            var source = cube.GetComponent<MeshFilter>().sharedMesh;
            var normals = source.normals;
            var outline = SelectionHighlight.SmoothedCopy(source);
            _created.Add(outline);
            Assert.AreNotSame(source, outline, "A copy: the model's own mesh keeps its normals.");
            CollectionAssert.AreEqual(normals, source.normals);
            CollectionAssert.AreEqual(source.vertices, outline.vertices);
            Assert.AreEqual(source.subMeshCount, outline.subMeshCount);
            var byCorner = outline.vertices.Select((v, i) => (v, n: outline.normals[i])).GroupBy(p => p.v);
            foreach (var corner in byCorner)
            {
                Assert.AreEqual(1, corner.Select(p => p.n).Distinct().Count(), "One normal per corner: no gap in the outline.");
                Assert.AreEqual(1f, corner.First().n.magnitude, 1e-4f);
                Assert.Greater(Vector3.Dot(corner.First().n, corner.Key.normalized), 0.99f, "Cube corners point outward.");
            }

            var fan = SelectionHighlight.SmoothNormals(new[] { Vector3.zero, Vector3.right, Vector3.forward }, null);
            Assert.IsTrue(fan.All(n => Mathf.Abs(n.magnitude - 1f) < 1e-4f), "No normals: from the centre, unit length.");
        }

        [Test]
        public void Highlight_FollowsTargetAndClearsOnHideReplaceAndDestroy()
        {
            var a = Machine("FL-1", new Vector3(1.2f, 2f, 3f), heavy: true);
            var b = Machine("CAR-1", new Vector3(1.8f, 1.5f, 4.5f), heavy: false);
            var children = a.transform.childCount;
            var colliders = a.GetComponentsInChildren<Collider>(true).Length;
            var materials = a.GetComponentInChildren<Renderer>().sharedMaterials;
            var highlight = new SelectionHighlight();
            try
            {
                highlight.Show(a);
                Assert.AreEqual(1, highlight.PartCount);
                highlight.Render();
                Assert.AreEqual(1, highlight.Drawn);
                Assert.AreEqual(children, a.transform.childCount, "Nothing is added under the model.");
                Assert.AreEqual(colliders, a.GetComponentsInChildren<Collider>(true).Length, "Picking unchanged.");
                CollectionAssert.AreEqual(materials, a.GetComponentInChildren<Renderer>().sharedMaterials, "Materials unchanged.");

                a.transform.GetChild(0).gameObject.SetActive(false);
                highlight.Render();
                Assert.AreEqual(0, highlight.Drawn, "Hidden: not drawn in the same frame.");
                a.transform.GetChild(0).gameObject.SetActive(true);

                highlight.Show(b);
                Assert.AreSame(b, highlight.Target, "Replaced, not added.");
                highlight.Render();
                Assert.AreEqual(1, highlight.Drawn);

                Object.DestroyImmediate(b.gameObject);
                highlight.Render();
                Assert.IsFalse(highlight.IsShowing, "Destroyed: cleared.");
                Assert.AreEqual(0, highlight.PartCount);
            }
            finally
            {
                highlight.Dispose();
            }
        }

        [Test]
        public void Highlight_TakesLateAndSwappedMeshesAndFreesCopiesWithTheSelection()
        {
            var block = Machine("B-1", new Vector3(10f, 4f, 12f), heavy: false, TargetCategory.Block);
            var filter = block.GetComponentInChildren<MeshFilter>();
            var renderer = filter.GetComponent<MeshRenderer>();
            var loaded = filter.sharedMesh;
            filter.sharedMesh = null;   // model still loading when clicked
            var highlight = new SelectionHighlight();
            try
            {
                highlight.Show(block);
                highlight.Render();
                Assert.AreEqual(1, highlight.PartCount, "Known before its mesh arrives.");
                Assert.AreEqual(0, highlight.Drawn);

                filter.sharedMesh = loaded;   // arrives
                highlight.Render();
                Assert.AreEqual(1, highlight.Drawn, "Outlined from the frame its mesh arrives.");
                var first = highlight.OutlineOf(renderer);
                CollectionAssert.AreEqual(loaded.vertices, first.vertices);
                Assert.AreEqual(1, highlight.CachedMeshes);
                highlight.Render();
                Assert.AreSame(first, highlight.OutlineOf(renderer), "Reused within the selection.");

                var swapped = Object.Instantiate(loaded);   // replaced by a new model (another LOD or a reload)
                _created.Add(swapped);
                swapped.vertices = swapped.vertices.Select(v => v * 2f).ToArray();
                filter.sharedMesh = swapped;
                Object.DestroyImmediate(first);   // even a lost copy is rebuilt, never drawn stale
                highlight.Render();
                var second = highlight.OutlineOf(renderer);
                Assert.AreNotSame(first, second);
                CollectionAssert.AreEqual(swapped.vertices, second.vertices, "The new mesh, not the old outline.");

                var source = swapped;
                filter.sharedMesh = loaded;
                Object.DestroyImmediate(source);   // the swapped-out mesh is unloaded: its copy goes with it
                highlight.Render();
                Assert.AreEqual(1, highlight.CachedMeshes);

                var unloaded = Object.Instantiate(loaded);
                _created.Add(unloaded);
                filter.sharedMesh = unloaded;
                highlight.Render();
                Object.DestroyImmediate(unloaded);
                filter.sharedMesh = null;
                highlight.Render();
                Assert.AreEqual(0, highlight.Drawn, "Unloading a mesh cannot leave its previous outline visible.");
                Assert.AreEqual(1, highlight.CachedMeshes, "The unloaded mesh's outline was released.");
                filter.sharedMesh = loaded;

                var late = GameObject.CreatePrimitive(PrimitiveType.Sphere);   // a part added after the click
                late.transform.SetParent(block.transform, false);
                Object.DestroyImmediate(late.GetComponent<Collider>());
                highlight.Show(block);   // same selection again: a fresh scan (Render rescans every 0.5 s)
                Assert.AreEqual(2, highlight.PartCount);

                var kept = highlight.OutlineOf(renderer);
                highlight.Clear();
                Assert.AreEqual(0, highlight.CachedMeshes, "No copy outlives the selection.");
                Assert.IsTrue(kept == null, "Copies destroyed on clear.");
                Assert.IsTrue(loaded != null, "The model's own mesh is never destroyed.");
            }
            finally
            {
                highlight.Dispose();
            }
        }

        [Test]
        public void EquipmentZone_OnlyForHeavyEquipmentAndEstimatedFromFootprint()
        {
            var forklift = Machine("FL-1", new Vector3(1.2f, 2f, 3f), heavy: true);
            var transporter = Machine("TP-1", new Vector3(6f, 2.5f, 25f), heavy: true);
            var car = Machine("CAR-1", new Vector3(1.8f, 1.5f, 4.5f), heavy: false);
            var crane = Machine("JC-1", new Vector3(10f, 40f, 10f), heavy: true, TargetCategory.Crane);
            Assert.IsTrue(EquipmentZone.Applies(forklift) && EquipmentZone.Applies(transporter));
            Assert.IsFalse(EquipmentZone.Applies(car), "Cars and trucks: no ring.");
            Assert.IsFalse(EquipmentZone.Applies(crane), "Cranes keep their ERP danger radius (CraneSafetyView).");
            Assert.IsFalse(EquipmentZone.Applies(null));

            Assert.AreEqual(3.5f, EquipmentZone.EstimateRadius(0.6f, 1.5f), "Forklift: 1.6 m half diagonal + 1.5 m, up to 0.5 m.");
            Assert.AreEqual(17.5f, EquipmentZone.EstimateRadius(3f, 12.5f), "Transporter: 12.9 m + 35 % margin.");
            Assert.AreEqual(EquipmentZone.MaxMargin, EquipmentZone.EstimateRadius(0f, 40f) - 40f, 1e-4f, "Margin capped at 5 m.");
            StringAssert.Contains("모의 위험반경", EquipmentZone.LabelText(3.5f));
            StringAssert.Contains("추정", EquipmentZone.LabelText(3.5f));
            StringAssert.Contains("ERP", EquipmentZone.Notice);

            // Heading does not change the footprint (yaw frame), so the radius is the same at any rotation.
            transporter.transform.rotation = Quaternion.Euler(0f, 37f, 0f);
            transporter.transform.position = new Vector3(100f, 0f, -40f);
            var zone = new EquipmentZone();
            try
            {
                Assert.IsTrue(zone.Show(transporter, null));
                Assert.AreEqual(EquipmentZone.EstimateRadius(3f, 12.5f), zone.Radius, 1e-3f);
                zone.Render(null);
                Assert.IsTrue(zone.Drawn);
                transporter.gameObject.SetActive(false);
                zone.Render(null);
                Assert.IsFalse(zone.Drawn, "Hidden machine: no ring.");
                transporter.gameObject.SetActive(true);

                Assert.IsFalse(zone.Show(car, null), "Switching to a car clears the ring.");
                Assert.IsFalse(zone.IsShowing);
                Assert.IsTrue(zone.Show(forklift, null));
                Object.DestroyImmediate(forklift.gameObject);
                zone.Render(null);
                Assert.IsFalse(zone.IsShowing, "Destroyed machine: cleared.");
            }
            finally
            {
                zone.Dispose();
            }
        }
    }
}
