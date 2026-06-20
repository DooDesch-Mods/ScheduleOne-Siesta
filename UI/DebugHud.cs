using System.Text;
using UnityEngine;
using Siesta.Compat;
using Siesta.Config;
using Siesta.Lod;

namespace Siesta.UI
{
    /// <summary>
    /// DEBUG-only on-screen readout (excluded from Release). Big colour-coded FPS plus the live LOD tier
    /// counts, so a tester can watch culling react in real time. Rebuilt at ~10 Hz to stay negligible.
    /// </summary>
    internal static class DebugHud
    {
        private static string _cached = "";
        private static float _nextRebuild;
        private static float _minFps = 9999f;
        private static GUIStyle _box;
        private static GUIStyle _fps;
        private static readonly Color _green = new Color(0.40f, 1f, 0.45f);
        private static readonly Color _yellow = new Color(1f, 0.85f, 0.30f);
        private static readonly Color _red = new Color(1f, 0.40f, 0.40f);

        internal static void Draw()
        {
            if (!Preferences.ShowHud)
            {
                return;
            }

            EnsureStyles();

            float fps = 1f / Mathf.Max(0.0001f, Time.smoothDeltaTime);
            if (fps < _minFps) _minFps = fps;
            _fps.normal.textColor = fps >= 50f ? _green : (fps >= 30f ? _yellow : _red);
            float w = 240f;
            GUI.Box(new Rect(Screen.width * 0.5f - w * 0.5f, 6f, w, 56f), $"{fps:F0} FPS", _fps);

            if (Time.unscaledTime >= _nextRebuild)
            {
                _nextRebuild = Time.unscaledTime + 0.1f;
                _cached = Build(fps);
            }

            GUI.Box(new Rect(8, 8, 330, 150), _cached, _box);
        }

        private static string Build(float fps)
        {
            var sb = new StringBuilder(384);
            sb.AppendLine("<b>Siesta - NPC LOD</b>");

            LodRegistry.CountByTier(out int f, out int c, out int d);
            sb.AppendLine($"FPS {fps:F0}  (min {_minFps:F0})");
            sb.AppendLine($"tracked {f + c + d}   full {f}  cosmetic {c}  deep {d}");
            sb.AppendLine($"mp {(Net.IsMultiplayer() ? "yes" : "no")}   host {(Net.IsAuthoritative() ? "yes" : "no")}");
            sb.AppendLine($"cosmetic@{Preferences.CosmeticDistance:F0}m  deep@{Preferences.DeepDistance:F0}m  budget {Preferences.BudgetPerFrame}/f");
            sb.AppendLine($"levers: cosmetic={(Preferences.UseCosmeticCull ? "on" : "off")}  deep={(Preferences.UseDeepCull ? "on" : "off")}");
            sb.Append("<b>keys</b> F6 hud  F7 full  F8 cosmetic  F9 deep  F10 restore");
            return sb.ToString();
        }

        private static void EnsureStyles()
        {
            if (_box != null)
            {
                return;
            }
            _box = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.UpperLeft,
                richText = true,
                fontSize = 12,
                wordWrap = true,
            };
            _box.normal.textColor = Color.white;
            _box.padding = new RectOffset(8, 8, 6, 6);

            _fps = new GUIStyle(GUI.skin.box)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 36,
                fontStyle = FontStyle.Bold,
            };
        }
    }
}
