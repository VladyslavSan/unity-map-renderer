using NUnit.Framework;
using MapRenderer.App.Menu;

namespace MapRenderer.Tests.EditMode.Menu
{
    /// <summary>Round-trip / lifecycle tests for <see cref="CameraPresetStore"/>, the debug-menu preset persistence.
    /// Non-obvious why: it uses a test-only key prefix because editor PlayerPrefs are shared with Play mode, so
    /// clearing the <see cref="CameraPresetStore.DefaultKeyPrefix"/> slots would delete the user's real presets.
    /// Every slot is cleared before and after each test.</summary>
    public class CameraPresetStoreTests
    {
        // Distinct from CameraPresetStore.DefaultKeyPrefix so this suite's clears never touch a real saved preset.
        private const string TestKeyPrefix = "mapdemo.debugmenu.camslot.TEST.";

        private CameraPresetStore _store;

        [SetUp]
        public void SetUp()
        {
            _store = new CameraPresetStore(10, TestKeyPrefix);
            ClearAll();
        }

        [TearDown]
        public void TearDown() => ClearAll();

        private void ClearAll()
        {
            for (int i = 0; i < _store.SlotCount; i++) _store.Clear(i);
        }

        /// <summary>Save then load reproduces the pose to FULL double precision — the tooth that fails if the
        /// serializer ever drops to a lossy format (e.g. JsonUtility or a fixed-decimal format), which would
        /// silently degrade lat/lon. Exact equality (delta 0), so a single lost digit reds it.</summary>
        [Test]
        public void SaveThenLoad_RoundTripsPose_ToFullDoublePrecision()
        {
            var p = new CameraPreset
            {
                Latitude = 52.516274633761092,
                Longitude = 13.377669811248779,
                Zoom = 14.372510896541,
                Heading = 271.418273645,
                Tilt = 47.913746251,
                FovDeg = 59.999997615814,
            };

            _store.Save(3, p);

            Assert.IsTrue(_store.TryLoad(3, out CameraPreset r), "slot 3 should load after a save");
            Assert.AreEqual(p.Latitude, r.Latitude, 0.0, "latitude");
            Assert.AreEqual(p.Longitude, r.Longitude, 0.0, "longitude");
            Assert.AreEqual(p.Zoom, r.Zoom, 0.0, "zoom");
            Assert.AreEqual(p.Heading, r.Heading, 0.0, "heading");
            Assert.AreEqual(p.Tilt, r.Tilt, 0.0, "tilt");
            Assert.AreEqual(p.FovDeg, r.FovDeg, 0.0, "fov");
        }

        /// <summary>An untouched slot reports empty and fails to load.</summary>
        [Test]
        public void EmptySlot_HasFalse_AndLoadFails()
        {
            Assert.IsFalse(_store.Has(5));
            Assert.IsFalse(_store.TryLoad(5, out _));
        }

        /// <summary>Clear removes a saved slot — Has and TryLoad both go false.</summary>
        [Test]
        public void Clear_RemovesSavedSlot()
        {
            _store.Save(1, new CameraPreset { Zoom = 10.0 });
            Assert.IsTrue(_store.Has(1));

            _store.Clear(1);

            Assert.IsFalse(_store.Has(1));
            Assert.IsFalse(_store.TryLoad(1, out _));
        }

        /// <summary>Two stores on DIFFERENT key prefixes are fully isolated: clearing every slot of one leaves the
        /// other's presets intact. This isolation lets the suite's <c>ClearAll</c> spare the real presets under
        /// <see cref="CameraPresetStore.DefaultKeyPrefix"/>. Both prefixes are synthetic. A store that ignores its
        /// prefix reds this: B's clear wipes A's slot.</summary>
        [Test]
        public void DistinctKeyPrefixes_AreIsolated_ClearOfOneLeavesTheOther()
        {
            var storeA = new CameraPresetStore(10, "mapdemo.debugmenu.camslot.ISOLATION_A.");
            var storeB = new CameraPresetStore(10, "mapdemo.debugmenu.camslot.ISOLATION_B.");
            try
            {
                storeA.Save(2, new CameraPreset { Zoom = 12.0 });
                Assert.IsTrue(storeA.Has(2), "precondition: A's slot 2 is saved");

                for (int i = 0; i < storeB.SlotCount; i++) storeB.Clear(i); // clear ALL of B's namespace

                Assert.IsTrue(storeA.Has(2), "clearing store B must not touch store A's namespace");
                Assert.IsTrue(storeA.TryLoad(2, out CameraPreset r) && r.Zoom == 12.0, "…and A's pose still loads");
            }
            finally
            {
                for (int i = 0; i < storeA.SlotCount; i++) storeA.Clear(i);
                for (int i = 0; i < storeB.SlotCount; i++) storeB.Clear(i);
            }
        }

        /// <summary>A corrupt persisted string degrades to "empty slot" rather than throwing — wrong field count,
        /// non-numeric fields, and empty input all fail cleanly.</summary>
        [Test]
        public void Deserialize_MalformedInput_FailsCleanly()
        {
            Assert.IsFalse(CameraPresetStore.TryDeserialize("", out _), "empty");
            Assert.IsFalse(CameraPresetStore.TryDeserialize("1;2;3", out _), "too few fields");
            Assert.IsFalse(CameraPresetStore.TryDeserialize("a;b;c;d;e;f", out _), "non-numeric fields");
        }

        /// <summary>Serialize→Deserialize is a pure identity independent of PlayerPrefs (the static pair the store
        /// is built on), including a negative longitude and an integer-valued field.</summary>
        [Test]
        public void SerializeDeserialize_IsIdentity()
        {
            var p = new CameraPreset
            {
                Latitude = -33.868820,
                Longitude = 151.209290,
                Zoom = 3.0,
                Heading = 0.0,
                Tilt = 0.0,
                FovDeg = 60.0,
            };

            Assert.IsTrue(CameraPresetStore.TryDeserialize(CameraPresetStore.Serialize(p), out CameraPreset r));
            Assert.AreEqual(p.Latitude, r.Latitude, 0.0);
            Assert.AreEqual(p.Longitude, r.Longitude, 0.0);
            Assert.AreEqual(p.Zoom, r.Zoom, 0.0);
        }
    }
}
