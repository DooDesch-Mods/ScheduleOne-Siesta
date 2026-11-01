using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppScheduleOne.Persistence;
using Siesta.Config;

namespace Siesta.Compat
{
    /// <summary>
    /// Releases the memory of old scenes when a save starts loading.
    ///
    /// A scene that ends leaves many references to its destroyed Unity objects: static events, phone app lists, event
    /// handlers on prefabs, GC handles. A destroyed object stays a managed object, and its fields still point at the
    /// rest of the scene, so one reference is enough to keep the scene in the heap. There are too many independent
    /// holders to unsubscribe one by one, and every game update can add more, so the fix works at the destroyed end:
    /// such an object can no longer do anything useful, so it lets go of its references and only its shell stays.
    ///
    /// The object graph comes from the IL2CPP memory snapshot. The walk follows typed reference fields from statics and
    /// GC handles and stops at destroyed objects, so everything it touches is an object the collector keeps as well.
    /// Scanning the snapshot's memory for class pointers instead also finds old object images in free memory, and
    /// writing to those can damage whatever lives next to them.
    /// </summary>
    internal static unsafe class HeapScrub
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Section { public ulong Start; public uint Size; public byte* Bytes; }

        [StructLayout(LayoutKind.Sequential)]
        private struct Field { public uint Offset; public uint TypeIndex; public byte* Name; public byte IsStatic; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MetaType
        {
            public uint Flags;
            public Field* Fields;
            public uint FieldCount;
            public uint StaticsSize;
            public byte* Statics;
            public uint BaseOrElement;
            public byte* Name;
            public byte* Assembly;
            public ulong TypeInfo;
            public uint Size;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Snapshot
        {
            public uint SectionCount; public Section* Sections;
            public uint StackCount; public Section* Stacks;
            public uint TypeCount; public MetaType* Types;
            public uint HandleCount; public ulong* Handles;
            public uint PointerSize, ObjectHeader, ArrayHeader, BoundsOffset, SizeOffset, Granularity;
            public void* User;
        }

        private const uint FlagValueType = 1, FlagArray = 2;
        private const long Mb = 1024L * 1024L;

        // Debug console switch for the A/B measurement.
        internal static bool Off;

        internal static void Apply(HarmonyLib.Harmony harmony)
        {
            try
            {
                MethodInfo cleanUp = AccessTools.Method(typeof(LoadManager), nameof(LoadManager.CleanUp));
                harmony.Patch(cleanUp, postfix: new HarmonyMethod(typeof(HeapScrub)
                    .GetMethod(nameof(AfterCleanUp), BindingFlags.Static | BindingFlags.NonPublic)));
            }
            catch (Exception e)
            {
                Core.Log?.Warning("Old scene memory fix not applied, the heap grows with every load: " + e.Message);
            }
        }

        // CleanUp runs on all three load paths: the loading screen is open and the next scene has not started.
        private static void AfterCleanUp()
        {
            if (Off || !Preferences.FreeOldSceneMemory)
            {
                Core.Log?.Msg($"Old scene memory: switched off, heap {Il2CppSystem.GC.GetTotalMemory(false) / Mb} MB, nothing released.");
                return;
            }
            Run();
        }

        internal static void Run()
        {
            try
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                long before = Il2CppSystem.GC.GetTotalMemory(false) / Mb;
                Snapshot* snapshot = (Snapshot*)IL2CPP.il2cpp_capture_memory_snapshot();
                if (snapshot == null)
                {
                    Core.Log?.Warning("Old scene memory: the runtime gave no memory snapshot, nothing released.");
                    return;
                }
                string result;
                try
                {
                    var walk = new Walk(snapshot);
                    result = walk.Cut();
                }
                finally
                {
                    IL2CPP.il2cpp_free_captured_memory_snapshot((IntPtr)snapshot);
                }
                Il2CppSystem.GC.Collect();
                // An unreachable object with a finalizer keeps what it points at until its finalizer has run.
                Il2CppSystem.GC.WaitForPendingFinalizers();
                Il2CppSystem.GC.Collect();
                long after = Il2CppSystem.GC.GetTotalMemory(false) / Mb;
                Core.Log?.Msg($"Old scene memory: heap {before} -> {after} MB, {result}, {watch.ElapsedMilliseconds} ms");
            }
            catch (Exception e)
            {
                Core.Log?.Error("Old scene memory: failed, the heap keeps what it holds: " + e);
            }
        }

        /// <summary>
        /// What a delegate aimed at a destroyed object runs instead. Callers clean the stack on x64, so it fits every
        /// signature that returns in a register.
        /// </summary>
        [UnmanagedCallersOnly]
        private static nint Nothing() => 0;

        private static string Str(byte* p) => p == null ? "" : Marshal.PtrToStringUTF8((IntPtr)p) ?? "";

        private sealed class Walk
        {
            private readonly Snapshot* _snap;
            private readonly int _typeCount;
            private readonly int _header;
            private readonly Dictionary<ulong, int> _byInfo;
            private readonly ulong[] _sectionStarts;
            private readonly int[] _sectionIndex;
            private readonly bool[] _isUnity, _isDelegate, _isClosure;
            private readonly uint[] _multicastOffset;
            private readonly int[][] _instanceSlots, _elementSlots;
            private readonly Dictionary<ulong, bool> _returnsInMemory = new Dictionary<ulong, bool>();
            private readonly HashSet<ulong> _seen = new HashSet<ulong>();
            private readonly Stack<ulong> _todo = new Stack<ulong>();
            private readonly HashSet<ulong> _dead = new HashSet<ulong>();
            private readonly List<ulong> _delegates = new List<ulong>();
            private Section* _last;
            private ulong _cached, _target, _invoke, _methodPtr, _code, _method;
            private long _reached, _unknown, _closures;

            internal Walk(Snapshot* snap)
            {
                _snap = snap;
                _typeCount = (int)snap->TypeCount;
                _header = (int)snap->ObjectHeader;
                _byInfo = new Dictionary<ulong, int>(_typeCount);
                _isUnity = new bool[_typeCount];
                _isDelegate = new bool[_typeCount];
                _isClosure = new bool[_typeCount];
                _multicastOffset = new uint[_typeCount];
                _instanceSlots = new int[_typeCount][];
                _elementSlots = new int[_typeCount][];
                _sectionStarts = new ulong[snap->SectionCount];
                _sectionIndex = new int[snap->SectionCount];
                for (uint i = 0; i < snap->SectionCount; i++)
                {
                    _sectionStarts[i] = snap->Sections[i].Start;
                    _sectionIndex[i] = (int)i;
                }
                Array.Sort(_sectionStarts, _sectionIndex);
            }

            internal string Cut()
            {
                if (!FindTypes()) return "no change, the runtime's type table has an unexpected layout";
                Roots();
                Follow();
                long stubbed = 0, inMemory = 0;
                nint stub = (nint)(delegate* unmanaged<nint>)&Nothing;
                foreach (ulong start in _delegates)
                {
                    uint multi = _multicastOffset[_byInfo[Peek(start)]];
                    if (multi != uint.MaxValue && Peek(start + multi) != 0) continue;
                    if (!Doomed(Peek(start + _target), 2)) continue;
                    if (ReturnsInMemory(Peek(start + _method))) { inMemory++; continue; }
                    *(ulong*)(start + _target) = 0;
                    *(ulong*)(start + _code) = 0;
                    *(ulong*)(start + _invoke) = (ulong)stub;
                    *(ulong*)(start + _methodPtr) = (ulong)stub;
                    stubbed++;
                }
                long refs = 0;
                foreach (ulong start in _dead)
                    foreach (int offset in _instanceSlots[_byInfo[Peek(start)]])
                    {
                        ulong* slot = (ulong*)(start + (ulong)offset);
                        if (*slot != 0) { *slot = 0; refs++; }
                    }
                return $"{_dead.Count} destroyed objects let go of {refs} references, {stubbed} handlers off " +
                       $"({_closures} through a closure, {inMemory} left on because of their return type), " +
                       $"{_reached} objects walked, {_unknown} references without a known type";
            }

            private bool FindTypes()
            {
                int unityRoot = -1, delegateRoot = -1;
                for (int i = 0; i < _typeCount; i++)
                {
                    _byInfo[_snap->Types[i].TypeInfo] = i;
                    string name = Str(_snap->Types[i].Name);
                    if (name == "UnityEngine.Object") unityRoot = i;
                    else if (name == "System.Delegate") delegateRoot = i;
                }
                if (unityRoot < 0 || delegateRoot < 0)
                {
                    Core.Log?.Warning($"Old scene memory: base types missing, UnityEngine.Object {unityRoot}, System.Delegate {delegateRoot}.");
                    return false;
                }
                _cached = Offset(unityRoot, "m_CachedPtr");
                _target = Offset(delegateRoot, "m_target");
                _invoke = Offset(delegateRoot, "invoke_impl");
                _methodPtr = Offset(delegateRoot, "method_ptr");
                _code = Offset(delegateRoot, "method_code");
                _method = Offset(delegateRoot, "method");
                if (_cached == uint.MaxValue || _target == uint.MaxValue || _invoke == uint.MaxValue ||
                    _methodPtr == uint.MaxValue || _code == uint.MaxValue || _method == uint.MaxValue)
                {
                    Core.Log?.Warning($"Old scene memory: fields missing, m_CachedPtr {_cached}, m_target {_target}, invoke_impl {_invoke}, method_ptr {_methodPtr}, method_code {_code}, method {_method}.");
                    return false;
                }
                for (int i = 0; i < _typeCount; i++)
                {
                    var t = _snap->Types + i;
                    _multicastOffset[i] = uint.MaxValue;
                    if ((t->Flags & (FlagValueType | FlagArray)) != 0) continue;
                    _isClosure[i] = Str(t->Name).Contains("<>c__DisplayClass");
                    var offsets = new List<int>();
                    int cur = i, hops = 0;
                    while (cur >= 0 && cur < _typeCount && hops++ < 64)
                    {
                        var c = _snap->Types + cur;
                        if (cur == unityRoot) _isUnity[i] = true;
                        if (cur == delegateRoot) _isDelegate[i] = true;
                        for (uint k = 0; k < c->FieldCount; k++)
                        {
                            var x = c->Fields + k;
                            if (x->IsStatic != 0 || x->TypeIndex >= (uint)_typeCount) continue;
                            if (Str(x->Name) == "delegates") _multicastOffset[i] = x->Offset;
                            if ((_snap->Types[x->TypeIndex].Flags & FlagValueType) != 0) AddStruct((int)x->TypeIndex, (int)x->Offset, offsets, 0);
                            else offsets.Add((int)x->Offset);
                        }
                        if (c->BaseOrElement == (uint)cur || c->BaseOrElement >= (uint)_typeCount) break;
                        cur = (int)c->BaseOrElement;
                    }
                    _instanceSlots[i] = offsets.ToArray();
                }
                return true;
            }

            private uint Offset(int type, string field)
            {
                for (uint k = 0; k < _snap->Types[type].FieldCount; k++)
                    if (Str(_snap->Types[type].Fields[k].Name) == field) return _snap->Types[type].Fields[k].Offset;
                return uint.MaxValue;
            }

            /// <summary>Reference fields of a value type. Its field offsets count the object header it only has when boxed.</summary>
            private void AddStruct(int type, int at, List<int> into, int depth)
            {
                if (depth > 8) return;
                var t = _snap->Types + type;
                for (uint k = 0; k < t->FieldCount; k++)
                {
                    var x = t->Fields + k;
                    if (x->IsStatic != 0 || x->TypeIndex >= (uint)_typeCount || x->TypeIndex == (uint)type) continue;
                    if ((_snap->Types[x->TypeIndex].Flags & FlagValueType) != 0) AddStruct((int)x->TypeIndex, at + (int)x->Offset - _header, into, depth + 1);
                    else into.Add(at + (int)x->Offset - _header);
                }
            }

            /// <summary>Reads through the snapshot's copy. An address outside every section reads as zero.</summary>
            private ulong Peek(ulong address)
            {
                if (_last == null || address < _last->Start || address + 8 > _last->Start + _last->Size)
                {
                    int at = Array.BinarySearch(_sectionStarts, address);
                    if (at < 0) at = ~at - 1;
                    if (at < 0) return 0;
                    _last = _snap->Sections + _sectionIndex[at];
                    if (address + 8 > _last->Start + _last->Size) return 0;
                }
                return *(ulong*)(_last->Bytes + (address - _last->Start));
            }

            private void Visit(ulong address)
            {
                if (address != 0 && (address & 7) == 0 && _seen.Add(address)) _todo.Push(address);
            }

            private void Roots()
            {
                for (uint i = 0; i < _snap->HandleCount; i++) Visit(_snap->Handles[i]);
                var slots = new List<int>();
                for (int i = 0; i < _typeCount; i++)
                {
                    var t = _snap->Types + i;
                    if (t->StaticsSize == 0 || t->Statics == null) continue;
                    for (uint k = 0; k < t->FieldCount; k++)
                    {
                        var x = t->Fields + k;
                        if (x->IsStatic == 0 || x->TypeIndex >= (uint)_typeCount) continue;
                        slots.Clear();
                        if ((_snap->Types[x->TypeIndex].Flags & FlagValueType) != 0) AddStruct((int)x->TypeIndex, (int)x->Offset, slots, 0);
                        else slots.Add((int)x->Offset);
                        foreach (int offset in slots)
                            if (offset >= 0 && offset + 8 <= t->StaticsSize) Visit(*(ulong*)(t->Statics + offset));
                    }
                }
            }

            /// <summary>A destroyed object ends a path, so what only it holds stays out of reach and gets collected.</summary>
            private void Follow()
            {
                while (_todo.Count > 0)
                {
                    ulong at = _todo.Pop();
                    if (!_byInfo.TryGetValue(Peek(at), out int ti)) { _unknown++; continue; }
                    var t = _snap->Types + ti;
                    _reached++;
                    if ((t->Flags & FlagArray) != 0)
                    {
                        FollowArray(at, t);
                        continue;
                    }
                    var mine = _instanceSlots[ti];
                    if (mine == null) continue;
                    if (_isUnity[ti] && Peek(at + _cached) == 0) { _dead.Add(at); continue; }
                    if (_isDelegate[ti]) _delegates.Add(at);
                    foreach (int offset in mine) Visit(Peek(at + (ulong)offset));
                }
            }

            private void FollowArray(ulong at, MetaType* t)
            {
                ulong length = Peek(at + _snap->SizeOffset);
                int element = (int)t->BaseOrElement;
                if (length >= 1UL << 31 || element < 0 || element >= _typeCount) return;
                ulong first = at + _snap->ArrayHeader;
                if ((_snap->Types[element].Flags & FlagValueType) == 0)
                {
                    for (ulong n = 0; n < length; n++) Visit(Peek(first + n * 8));
                    return;
                }
                var inner = _elementSlots[element];
                if (inner == null)
                {
                    var list = new List<int>();
                    AddStruct(element, 0, list, 0);
                    inner = _elementSlots[element] = list.ToArray();
                }
                if (inner.Length == 0) return;
                ulong stride = _snap->Types[element].Size;
                for (ulong n = 0; n < length; n++)
                    foreach (int offset in inner) Visit(Peek(first + n * stride + (ulong)offset));
            }

            private bool Doomed(ulong target, int depth)
            {
                if (target == 0) return false;
                if (_dead.Contains(target)) return true;
                if (depth == 0) return false;
                if (!_byInfo.TryGetValue(Peek(target), out int ti) || !_isClosure[ti]) return false;
                foreach (int offset in _instanceSlots[ti])
                    if (Doomed(Peek(target + (ulong)offset), depth - 1)) { _closures++; return true; }
                return false;
            }

            /// <summary>
            /// A struct too large for a register comes back through a hidden pointer that the caller then reads from
            /// the return register, so the stub's zero would crash there. Such a delegate stays as it is and throws
            /// the usual exception if anyone still invokes it.
            /// </summary>
            private bool ReturnsInMemory(ulong method)
            {
                if (_returnsInMemory.TryGetValue(method, out bool known)) return known;
                bool result = true;
                if (method != 0)
                {
                    IntPtr type = IL2CPP.il2cpp_method_get_return_type((IntPtr)method);
                    IntPtr klass = type == IntPtr.Zero ? IntPtr.Zero : IL2CPP.il2cpp_class_from_type(type);
                    if (klass != IntPtr.Zero)
                        result = IL2CPP.il2cpp_class_is_valuetype(klass) && IL2CPP.il2cpp_class_instance_size(klass) - _header > 8;
                }
                _returnsInMemory[method] = result;
                return result;
            }
        }
    }
}
