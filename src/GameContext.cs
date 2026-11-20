using System;
using System.Collections.Generic;
using Game;
using HarmonyLib;
using Model;
using UI.Common;
using UnityEngine;

namespace MHPerformanceAnalyzer
{
    /// <summary>Game state read only when a spike is recorded (or once per second for loading).</summary>
    internal static class GameContext
    {
        public struct Snapshot
        {
            public string? GameTime;
            public string? Camera;
            public bool HasCameraPos;
            public Vector3 CameraPos;
            public int Cars;
            public int Locomotives;
            public int Trains;
            public View View;
        }

        /// <summary>What the camera is looking at: enough to tell whether two runs had the same view.</summary>
        public struct View
        {
            public bool Valid;
            public int Mode;          // CameraSelector.CameraIdentifier, -1 if unknown
            public Vector3 Pos;       // game coordinates
            public float Yaw, Pitch;  // degrees
            public float Fov;
            public float Distance;    // strategy camera zoom distance, -1 if not strategy
        }

        private static AccessTools.FieldRef<CameraSelector, Camera>? selectorCamera;
        private static AccessTools.FieldRef<global::Cameras.StrategyCameraController, float>? strategyDistance;
        private static bool refsTried;

        private static Camera? ActiveCamera(CameraSelector? selector)
        {
            if (!refsTried)
            {
                refsTried = true;
                try
                {
                    selectorCamera = AccessTools.FieldRefAccess<CameraSelector, Camera>("_camera");
                    strategyDistance = AccessTools.FieldRefAccess<global::Cameras.StrategyCameraController, float>("_distance");
                }
                catch (Exception)
                {
                }
            }
            Camera? cam = null;
            if (selector != null && selectorCamera != null)
            {
                cam = selectorCamera(selector);
            }
            return cam != null ? cam : UnityEngine.Camera.main;
        }

        /// <summary>No allocation; called once per second and on spikes.</summary>
        public static View CaptureView()
        {
            var v = new View { Mode = -1, Distance = -1f };
            try
            {
                var selector = CameraSelector.shared;
                var cam = ActiveCamera(selector);
                if (cam == null)
                {
                    return v;
                }
                var t = cam.transform;
                v.Valid = true;
                v.Pos = Helpers.TransformExtensions.GamePosition(t);
                var e = t.rotation.eulerAngles;
                v.Yaw = e.y;
                v.Pitch = e.x > 180f ? e.x - 360f : e.x;
                v.Fov = cam.fieldOfView;
                if (selector != null)
                {
                    v.Mode = (int)selector.CurrentCameraIdentifier;
                    if (selector.CurrentCameraIdentifier == CameraSelector.CameraIdentifier.Strategy && strategyDistance != null && selector.strategyCamera != null)
                    {
                        v.Distance = strategyDistance(selector.strategyCamera);
                    }
                }
            }
            catch (Exception)
            {
            }
            return v;
        }

        public static bool IsLoading()
        {
            try
            {
                var ps = ProgressScreen.Shared;
                return ps != null && ps.IsVisible;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static Snapshot Capture(bool worldLoaded, HashSet<object> setScratch)
        {
            var s = new Snapshot();
            if (!worldLoaded)
            {
                return s;
            }
            try
            {
                s.GameTime = TimeWeather.Now.ToString();
            }
            catch (Exception)
            {
            }
            try
            {
                var selector = CameraSelector.shared;
                if (selector != null)
                {
                    s.Camera = selector.CurrentCameraIdentifier.ToString();
                }
                s.View = CaptureView();
                if (s.View.Valid)
                {
                    s.HasCameraPos = true;
                    s.CameraPos = s.View.Pos;
                }
            }
            catch (Exception)
            {
            }
            try
            {
                var tc = TrainController.Shared;
                if (tc != null)
                {
                    setScratch.Clear();
                    foreach (Car car in tc.Cars)
                    {
                        s.Cars++;
                        if (car.IsLocomotive)
                        {
                            s.Locomotives++;
                        }
                        var set = car.set;
                        if (set != null)
                        {
                            setScratch.Add(set);
                        }
                    }
                    s.Trains = setScratch.Count;
                    setScratch.Clear();
                }
            }
            catch (Exception)
            {
            }
            return s;
        }
    }
}
