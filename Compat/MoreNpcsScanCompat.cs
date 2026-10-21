using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine.SceneManagement;
using Il2CppScheduleOne.Economy;

namespace Siesta.Compat
{
    /// <summary>
    /// Replaces two whole-scene searches of "More NPCs" (and its fork) with searches that return the same objects
    /// for a fraction of the cost. Both run inside <c>MoreNPCs.Core.OnUpdate</c>, so their cost lands in one frame
    /// and shows as a freeze.
    ///
    /// <c>GameDealerFinder.GetRootTransforms</c> lists every active Transform of the scene
    /// (<c>Object.FindObjectsOfType&lt;Transform&gt;</c>, about 90 000 of them) and keeps the hierarchy roots. Its
    /// only caller then keeps the roots that carry a <c>Dealer</c> component. That is 160-370 ms, again each time
    /// the 30 s dealer cache has expired. A Dealer is an NPC, and every NPC is in the game's NPC registry, so the
    /// replacement reads the registry and returns the active root objects that are dealers: the same dealers for
    /// the caller, in well under a millisecond.
    ///
    /// <c>ReOfficeWhiteboardDisplay.FindPropertyListingTransform</c> looks a listing board up by name in
    /// <c>Resources.FindObjectsOfTypeAll(Transform)</c>: every Transform in memory, prefabs and assets included
    /// (about 330 000), with one name read per object. That is 0.9-1.5 s per name, and the first board sync looks
    /// up four names in one frame: a freeze of about 6 s. The replacement asks Unity for an active object of that
    /// name first (<c>GameObject.Find</c>, native). Only when there is none does it walk the loaded scenes once,
    /// inactive objects included, which is the set the original accepts a match from, and it keeps every board of
    /// the same name family it passes so the next names need no walk. It keeps the original's hit cache and its
    /// 60 s retry for a name that is not there.
    ///
    /// Each prefix falls back to the original on any exception.
    /// </summary>
    internal static class MoreNpcsScanCompat
    {
        private const float ListingRetrySeconds = 60f;   // MoreNPCs' own retry window for a missed name

        private static readonly Dictionary<string, Transform> _listingHit = new Dictionary<string, Transform>(StringComparer.Ordinal);
        private static readonly Dictionary<string, float> _listingMiss = new Dictionary<string, float>(StringComparer.Ordinal);
        private static Scene _ddolScene;
        private static bool _ddolKnown;
        private static bool _rootsFailed, _listingFailed;

        private static MethodInfo _rootsTarget, _listingTarget, _dealersTarget;

        // Debug console switch: let MoreNPCs' own searches run, for an A/B measurement.
        internal static bool Off = false;

        internal static void Apply(HarmonyLib.Harmony harmony)
        {
            Assembly asm = FindAssembly("MoreNPCs_Il2cpp") ?? FindAssembly("MoreNPCs");
            if (asm == null)
            {
                Core.Log?.Msg("More NPCs scan compat: More NPCs is not installed, nothing to patch.");
                return;
            }

            Type finder = asm.GetType("MoreNPCs.Utils.GameDealerFinder", false);
            _rootsTarget = finder?.GetMethod("GetRootTransforms", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            _dealersTarget = finder?.GetMethod("GetRecruitedDealersFromGame", BindingFlags.Static | BindingFlags.Public);
            if (_rootsTarget != null && (_rootsTarget.ReturnType != typeof(Transform[]) || _rootsTarget.GetParameters().Length != 0))
            {
                Core.Log?.Warning("More NPCs scan compat: GameDealerFinder.GetRootTransforms has another signature, left untouched.");
                _rootsTarget = null;
            }
            else
            {
                Patch(harmony, _rootsTarget, nameof(RootsPrefix), "GameDealerFinder.GetRootTransforms");
            }

            Type board = asm.GetType("MoreNPCs.Manager.ReOfficeWhiteboardDisplay", false);
            _listingTarget = board?.GetMethod("FindPropertyListingTransform", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (_listingTarget != null && (_listingTarget.ReturnType != typeof(Transform)
                || _listingTarget.GetParameters().Length != 1 || _listingTarget.GetParameters()[0].ParameterType != typeof(string)))
            {
                Core.Log?.Warning("More NPCs scan compat: ReOfficeWhiteboardDisplay.FindPropertyListingTransform has another signature, left untouched.");
                _listingTarget = null;
                return;
            }
            Patch(harmony, _listingTarget, nameof(ListingPrefix), "ReOfficeWhiteboardDisplay.FindPropertyListingTransform");
        }

        private static void Patch(HarmonyLib.Harmony harmony, MethodInfo target, string prefix, string label)
        {
            if (target == null)
            {
                Core.Log?.Warning("More NPCs scan compat: " + label + " not found, More NPCs keeps its own search.");
                return;
            }
            try
            {
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(MoreNpcsScanCompat)
                    .GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic)));
                Core.Log?.Msg("More NPCs scan compat: " + label + " replaced.");
            }
            catch (Exception e)
            {
                Core.Log?.Error("More NPCs scan compat: patching " + label + " failed, More NPCs keeps its own search: " + e.Message);
            }
        }

        // ----- dealers -----

        private static bool RootsPrefix(ref Transform[] __result)
        {
            if (Off || _rootsFailed) return true;
            try
            {
                __result = DealerRoots();
                return false;
            }
            catch (Exception e)
            {
                _rootsFailed = true;
                Core.Log?.Warning("More NPCs scan compat: dealer search threw " + e.GetType().Name + " (" + e.Message
                    + "), More NPCs uses its own search for the rest of the session.");
                return true;
            }
        }

        /// <summary>Active hierarchy roots that are dealers. A dealer below another object is not returned: the
        /// caller reads components on the roots only, so the original never found it either.</summary>
        internal static Transform[] DealerRoots()
        {
            var list = new List<Transform>();
            var reg = NPCManager.NPCRegistry;
            if (reg == null) return list.ToArray();
            int n = reg.Count;
            for (int i = 0; i < n; i++)
            {
                NPC npc = reg[i];
                if (npc == null || !npc.gameObject.activeInHierarchy) continue;
                Transform t = npc.transform;
                if (t.parent != null) continue;
                if (npc.TryCast<Dealer>() == null) continue;
                list.Add(t);
            }
            return list.ToArray();
        }

        // ----- listing boards -----

        private static bool ListingPrefix(string listingName, ref Transform __result)
        {
            if (Off || _listingFailed) return true;
            try
            {
                __result = FindListing(listingName);
                return false;
            }
            catch (Exception e)
            {
                _listingFailed = true;
                Core.Log?.Warning("More NPCs scan compat: listing search threw " + e.GetType().Name + " (" + e.Message
                    + "), More NPCs uses its own search for the rest of the session.");
                return true;
            }
        }

        private static Transform FindListing(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_listingHit.TryGetValue(name, out Transform hit) && hit != null) return hit;
            if (_listingMiss.TryGetValue(name, out float missedAt) && Time.time - missedAt < ListingRetrySeconds) return null;

            // GameObject.Find returns only active scene objects; a name without '/' matches at any depth.
            GameObject active = name.IndexOf('/') < 0 ? GameObject.Find(name) : null;
            Transform found = active != null ? active.transform : SweepLoadedScenes(name);
            if (found != null)
            {
                _listingHit[name] = found;
                _listingMiss.Remove(name);
            }
            else
            {
                _listingMiss[name] = Time.time;
            }
            return found;
        }

        /// <summary>One walk over the loaded scenes, inactive objects included. Returns the first object with this
        /// name and caches every object whose name starts with the same first word ("PropertyListing ..."), so
        /// the other boards are found without another walk.</summary>
        private static Transform SweepLoadedScenes(string name)
        {
            int space = name.IndexOf(' ');
            string family = space > 0 ? name.Substring(0, space + 1) : null;
            Transform found = null;

            int n = SceneManager.sceneCount;
            for (int i = 0; i < n; i++) Sweep(SceneManager.GetSceneAt(i), name, family, ref found);
            Sweep(DontDestroyScene(), name, family, ref found);
            return found;
        }

        private static void Sweep(Scene scene, string name, string family, ref Transform found)
        {
            if (!scene.IsValid() || !scene.isLoaded) return;
            var roots = scene.GetRootGameObjects();
            if (roots == null) return;
            foreach (GameObject root in roots)
            {
                if (root == null) continue;
                var all = root.GetComponentsInChildren<Transform>(true);
                foreach (Transform t in all)
                {
                    string tn = t.name;
                    if (found == null && tn == name) found = t;
                    if (family != null && tn.StartsWith(family, StringComparison.Ordinal) && !_listingHit.ContainsKey(tn))
                        _listingHit[tn] = t;
                }
            }
        }

        // The DontDestroyOnLoad scene is not in SceneManager's list; the original search covers it.
        private static Scene DontDestroyScene()
        {
            if (_ddolKnown) return _ddolScene;
            var probe = new GameObject("Siesta_DontDestroyProbe");
            UnityEngine.Object.DontDestroyOnLoad(probe);
            _ddolScene = probe.scene;
            UnityEngine.Object.Destroy(probe);
            _ddolKnown = true;
            return _ddolScene;
        }

        internal static void ClearCaches()
        {
            _listingHit.Clear();
            _listingMiss.Clear();
        }

        private static Assembly FindAssembly(string simpleName)
        {
            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                try
                {
                    if (asms[i].GetName().Name == simpleName) return asms[i];
                }
                catch (Exception e) { Core.Log?.Warning("More NPCs scan compat: skipped an assembly whose name cannot be read: " + e.Message); }
            }
            return null;
        }

#if DEBUG
        private static readonly string[] BoardNames =
            { "PropertyListing Laundromat", "PropertyListing Post Office", "PropertyListing Car Wash", "PropertyListing Taco Ticklers" };

        /// <summary>Times the original and the replacement of both searches once and logs whether they agree.</summary>
        internal static void Probe(Action<string> log)
        {
            var sw = new System.Diagnostics.Stopwatch();
            bool wasOff = Off;
            try
            {
                if (_rootsTarget != null)
                {
                    Off = true;
                    sw.Restart();
                    var orig = (Transform[])_rootsTarget.Invoke(null, null);
                    sw.Stop();
                    Off = wasOff;
                    var origDealers = new List<string>();
                    foreach (Transform t in orig) if (t != null && t.GetComponent<Dealer>() != null) origDealers.Add(t.name);
                    double origMs = sw.Elapsed.TotalMilliseconds;
                    sw.Restart();
                    var mine = DealerRoots();
                    sw.Stop();
                    var mineNames = new List<string>();
                    foreach (Transform t in mine) mineNames.Add(t.name);
                    origDealers.Sort(); mineNames.Sort();
                    log($"mnf: dealer roots original {orig.Length} roots, dealers [{string.Join(",", origDealers)}] in {origMs:F1} ms; "
                        + $"replacement dealers [{string.Join(",", mineNames)}] in {sw.Elapsed.TotalMilliseconds:F2} ms; same={string.Join(",", origDealers) == string.Join(",", mineNames)}");
                }
                if (_dealersTarget != null)
                {
                    foreach (bool off in new[] { true, false })
                    {
                        Off = off;
                        sw.Restart();
                        object list = _dealersTarget.Invoke(null, new object[] { true });
                        sw.Stop();
                        int c = list is System.Collections.ICollection col ? col.Count : -1;
                        log($"mnf: GetRecruitedDealersFromGame(force) {(off ? "original" : "replacement")} = {c} recruited, {sw.Elapsed.TotalMilliseconds:F1} ms");
                    }
                    Off = wasOff;
                }
                if (_listingTarget != null)
                {
                    // A never-seen name forces the original's full search once.
                    Off = true;
                    sw.Restart();
                    _listingTarget.Invoke(null, new object[] { BoardNames[0] + " #" + Guid.NewGuid().ToString("N") });
                    sw.Stop();
                    Off = wasOff;
                    log($"mnf: listing full search original {sw.Elapsed.TotalMilliseconds:F1} ms (one name)");

                    // Cold replacement: all four boards from empty caches, as the first board sync asks for them.
                    var active = new List<string>();
                    foreach (string b in BoardNames) active.Add(GameObject.Find(b) != null ? "active" : "inactive");
                    ClearCaches();
                    sw.Restart();
                    var found = new List<string>();
                    foreach (string b in BoardNames) found.Add(FindListing(b) != null ? "found" : "missing");
                    sw.Stop();
                    log($"mnf: listing replacement, four boards cold: {sw.Elapsed.TotalMilliseconds:F1} ms; {string.Join(",", found)}; {string.Join(",", active)}");

                    Off = true;
                    var origFound = new List<string>();
                    foreach (string b in BoardNames)
                    {
                        Transform o = (Transform)_listingTarget.Invoke(null, new object[] { b });
                        Transform m = FindListing(b);
                        origFound.Add(o == null ? (m == null ? "both-missing" : "only-replacement") : (o.Pointer == m?.Pointer ? "same" : "different"));
                    }
                    Off = wasOff;
                    log($"mnf: listing original vs replacement: {string.Join(",", origFound)}");
                }
            }
            catch (Exception e)
            {
                log("mnf: probe threw " + e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                Off = wasOff;
            }
        }
#endif
    }
}
