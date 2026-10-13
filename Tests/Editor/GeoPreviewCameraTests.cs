using NUnit.Framework;
using UnityEngine;

namespace GeoWorld.Editor.Tests
{
    // Switching the Play-mode camera between orbit and fly (no clip) never makes the view jump.
    public class GeoPreviewCameraTests
    {
        GeoPreviewCamera camera;

        [SetUp] public void Setup()
        {
            camera = new GameObject("Preview camera test").AddComponent<GeoPreviewCamera>();
            camera.focus = new Vector3(100, 5, -40); camera.distance = 200; camera.yaw = -25; camera.pitch = 52;
            camera.Apply();
        }

        [TearDown] public void Cleanup() => Object.DestroyImmediate(camera.gameObject);

        [Test] public void FlyStartsWhereTheCameraIsLookingWhereItLooks()
        {
            var position = camera.transform.position;
            var forward = camera.transform.forward;
            camera.SetFlying(true);
            Assert.IsTrue(camera.Flying);
            Assert.That(Vector3.Distance(position, camera.transform.position), Is.LessThan(1e-4f));
            Assert.That(Vector3.Angle(forward, camera.transform.forward), Is.LessThan(0.01f));
            Assert.AreEqual(52f, camera.FlyPitch, 0.01f);
        }

        [Test] public void OrbitComesBackAroundAPointAheadWithoutMovingTheCamera()
        {
            camera.SetFlying(true);
            camera.transform.position = new Vector3(-300, 40, 820);   // flown somewhere else
            var forward = camera.transform.forward;
            camera.SetFlying(false);
            Assert.IsFalse(camera.Flying);
            Assert.That(Vector3.Distance(new Vector3(-300, 40, 820), camera.transform.position), Is.LessThan(1e-3f));
            Assert.That(Vector3.Angle(forward, camera.transform.forward), Is.LessThan(0.01f));
            Assert.That(Vector3.Dot(camera.focus - camera.transform.position, forward), Is.GreaterThan(0));
        }

        [Test] public void ALevelViewComesBackToTheOrbitsLowestPitch()
        {
            camera.transform.rotation = Quaternion.Euler(0, 70, 0);   // looking at the horizon, as a flyer may
            camera.SetFlying(true);
            Assert.AreEqual(0f, camera.FlyPitch, 0.01f);
            camera.SetFlying(false);
            Assert.AreEqual(12f, camera.pitch, 0.01f);
            Assert.AreEqual(70f, Mathf.Repeat(camera.yaw, 360), 0.01f);
        }
    }
}
