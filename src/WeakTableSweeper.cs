using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace FixMemoryLeaks;

public static class WeakTableSweeper
{
    public sealed class Result
    {
        public int Tables;
        public int Entries;
        public int Removed;
        public long Milliseconds;
    }

    public sealed class Detached
    {
        internal readonly List<Held> Entries = [];

        public int Count => Entries.Count;
        public long Milliseconds;
    }

    public sealed class Reattached
    {
        public int Released;
        public int Restored;
        public int Conflicts;
        public int SharedTables;
        public long Milliseconds;
    }

    internal sealed class Held(object table, TableAccess access, object key, object value, List<FieldInfo> cut)
    {
        public readonly object Table = table;
        public readonly TableAccess Access = access;
        public readonly WeakReference Key = new(key);
        public readonly object Value = value;
        public readonly List<FieldInfo> Cut = cut;
    }

    private const int MaxDepth = 6;
    private const int ArrayProbe = 16;
    private const int VisitBudget = 50000;

    private const BindingFlags StaticFields = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private static readonly string[] SkippedAssemblies =
    [
        "mscorlib", "System", "Mono.", "MonoMod", "UnityEngine", "Unity.", "Assembly-CSharp", "HOOKS-",
        "0Harmony", "HarmonyX", "BepInEx", "Newtonsoft", "Rewired", "netstandard", "Microsoft.",
        "GalaxyCSharp", "GoKit", "StovePCSDK", "Sony", "Accessibility", "Novell"
    ];

    private static List<FieldInfo> tableFields;
    private static List<KeyValuePair<FieldInfo, FieldInfo[]>> hostFields;
    private static List<FieldInfo> dictionaryFields;

    private static readonly Dictionary<Type, FieldInfo[]> tableFieldsOf = [];
    private static readonly Dictionary<Type, FieldInfo[]> referenceFields = [];
    private static readonly Dictionary<Type, bool> carriesReferences = [];
    private static readonly Dictionary<Type, TableAccess> tableAccess = [];
    private static readonly Dictionary<Type, bool> clonable = [];
    private static readonly MethodInfo memberwiseClone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly HashSet<object> sharedValueTables = new(ReferenceComparer.Instance);
    private static readonly List<KeyValuePair<object, WeakReference>> releasedCutValues = [];

    public static Result Sweep(object dead, Func<object, bool> barrier, Func<Type, bool> scopedKey, Func<object, bool> prune = null)
    {
        Stopwatch watch = Stopwatch.StartNew();
        Result result = new();

        if (tableFields == null)
            Discover(scopedKey);

        Reach reach = new(dead, barrier, prune);

        foreach (object table in Tables())
        {
            TableAccess access = Access(table.GetType());
            if (access == null)
                continue;

            result.Tables++;

            foreach (KeyValuePair<object, object> entry in access.Entries(table))
            {
                result.Entries++;

                if (reach.Doomed(entry.Key, entry.Value))
                {
                    access.Remove.Invoke(table, [entry.Key]);
                    result.Removed++;
                }
            }
        }

        foreach (FieldInfo field in dictionaryFields)
        {
            if (Read(field, null) is not IDictionary dictionary)
                continue;

            result.Tables++;

            List<object> doomed = [];
            foreach (DictionaryEntry entry in dictionary)
            {
                result.Entries++;
                if (reach.Doomed(entry.Key, entry.Value))
                    doomed.Add(entry.Key);
            }

            foreach (object key in doomed)
                dictionary.Remove(key);

            result.Removed += doomed.Count;
        }

        result.Milliseconds = watch.ElapsedMilliseconds;
        return result;
    }

    public static Detached Detach(Func<Type, bool> scopedKey)
    {
        Stopwatch watch = Stopwatch.StartNew();
        Detached detached = new();

        if (tableFields == null)
            Discover(scopedKey);

        foreach (object table in Tables())
        {
            TableAccess access = Access(table.GetType());
            if (access?.Add == null)
                continue;

            bool cut = !sharedValueTables.Contains(table);
            foreach (KeyValuePair<object, object> entry in access.Entries(table))
            {
                List<FieldInfo> fields = cut ? CutBackReferences(entry.Value, entry.Key) : null;
                try
                {
                    access.Remove.Invoke(table, [entry.Key]);
                }
                catch
                {
                    Restore(entry.Value, fields, entry.Key);
                    continue;
                }
                detached.Entries.Add(new Held(table, access, entry.Key, entry.Value, fields));
            }
        }

        detached.Milliseconds = watch.ElapsedMilliseconds;
        return detached;
    }

    public static Reattached Reattach(Detached detached)
    {
        Stopwatch watch = Stopwatch.StartNew();
        Reattached result = new();

        foreach (KeyValuePair<object, WeakReference> released in releasedCutValues)
            if (released.Value.IsAlive)
                sharedValueTables.Add(released.Key);
        releasedCutValues.Clear();

        foreach (Held held in detached.Entries)
        {
            object key = held.Key.Target;
            if (key == null)
            {
                result.Released++;
                if (held.Cut != null)
                    releasedCutValues.Add(new(held.Table, new WeakReference(held.Value)));
                continue;
            }

            Restore(held.Value, held.Cut, key);
            try
            {
                held.Access.Add.Invoke(held.Table, [key, held.Value]);
                result.Restored++;
            }
            catch
            {
                result.Conflicts++;
            }
        }

        detached.Entries.Clear();
        result.SharedTables = sharedValueTables.Count;
        result.Milliseconds = watch.ElapsedMilliseconds;
        return result;
    }

    private static List<FieldInfo> CutBackReferences(object value, object key)
    {
        if (value == null || ReferenceEquals(value, key) || value is string || value is Array || !Clonable(key.GetType()))
            return null;

        List<FieldInfo> cut = null;
        object stand = null;
        foreach (FieldInfo field in ReferenceFields(value.GetType()))
        {
            if (field.FieldType.IsValueType)
                continue;

            try
            {
                if (!ReferenceEquals(field.GetValue(value), key))
                    continue;
                stand ??= memberwiseClone.Invoke(key, null);
                field.SetValue(value, stand);
                (cut ??= []).Add(field);
            }
            catch
            {
            }
        }
        return cut;
    }

    private static bool Clonable(Type type)
    {
        if (clonable.TryGetValue(type, out bool cached))
            return cached;

        bool result = !type.IsArray && type != typeof(string) && !typeof(Delegate).IsAssignableFrom(type);
        for (Type level = type; result && level != null && level != typeof(object); level = SafeBase(level))
            if (level.FullName == "UnityEngine.Object" || level.GetMethod("Finalize", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) != null)
                result = false;

        return clonable[type] = result;
    }

    private static void Restore(object value, List<FieldInfo> cut, object key)
    {
        if (cut == null)
            return;

        foreach (FieldInfo field in cut)
        {
            try
            {
                field.SetValue(value, key);
            }
            catch
            {
            }
        }
    }

    private static void Discover(Func<Type, bool> scopedKey)
    {
        tableFields = [];
        hostFields = [];
        dictionaryFields = [];

        HashSet<Type> seen = [];

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic || Skipped(assembly.GetName().Name))
                continue;

            foreach (Type type in Types(assembly))
            {
                if (type == null)
                    continue;

                if (!type.ContainsGenericParameters)
                    ScanStatics(type, seen, scopedKey);

                for (Type baseType = SafeBase(type); baseType != null; baseType = SafeBase(baseType))
                    if (baseType.IsGenericType && !baseType.ContainsGenericParameters)
                        ScanStatics(baseType, seen, scopedKey);
            }
        }
    }

    private static bool Skipped(string name)
    {
        foreach (string prefix in SkippedAssemblies)
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static Type[] Types(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types;
        }
        catch
        {
            return [];
        }
    }

    private static Type SafeBase(Type type)
    {
        try
        {
            return type.BaseType;
        }
        catch
        {
            return null;
        }
    }

    private static void ScanStatics(Type type, HashSet<Type> seen, Func<Type, bool> scopedKey)
    {
        if (!seen.Add(type))
            return;

        FieldInfo[] fields;
        try
        {
            fields = type.GetFields(StaticFields);
        }
        catch
        {
            return;
        }

        foreach (FieldInfo field in fields)
        {
            try
            {
                if (field.IsLiteral)
                    continue;

                Type fieldType = field.FieldType;

                if (IsTable(fieldType))
                    tableFields.Add(field);
                else if (IsScopedDictionary(fieldType, scopedKey))
                    dictionaryFields.Add(field);
                else if (!fieldType.IsValueType && !fieldType.IsArray && fieldType != typeof(string) && !fieldType.ContainsGenericParameters)
                {
                    FieldInfo[] inner = TableFieldsOf(fieldType);
                    if (inner.Length > 0)
                        hostFields.Add(new(field, inner));
                }
            }
            catch
            {
            }
        }
    }

    private static bool IsTable(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ConditionalWeakTable<,>);

    private static bool IsScopedDictionary(Type type, Func<Type, bool> scopedKey) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>) && scopedKey(type.GetGenericArguments()[0]);

    private static FieldInfo[] TableFieldsOf(Type type)
    {
        if (tableFieldsOf.TryGetValue(type, out FieldInfo[] cached))
            return cached;

        List<FieldInfo> found = [];
        for (Type level = type; level != null && level != typeof(object); level = SafeBase(level))
        {
            try
            {
                foreach (FieldInfo field in level.GetFields(InstanceFields))
                    if (IsTable(field.FieldType))
                        found.Add(field);
            }
            catch
            {
            }
        }

        return tableFieldsOf[type] = found.ToArray();
    }

    private static List<object> Tables()
    {
        List<object> tables = [];
        HashSet<object> unique = new(ReferenceComparer.Instance);

        foreach (FieldInfo field in tableFields)
            Add(Read(field, null));

        foreach (KeyValuePair<FieldInfo, FieldInfo[]> host in hostFields)
        {
            object owner = Read(host.Key, null);
            if (owner == null)
                continue;

            foreach (FieldInfo field in host.Value)
                Add(Read(field, owner));
        }

        return tables;

        void Add(object table)
        {
            if (table != null && unique.Add(table))
                tables.Add(table);
        }
    }

    private static object Read(FieldInfo field, object owner)
    {
        try
        {
            return field.GetValue(owner);
        }
        catch
        {
            return null;
        }
    }

    private static TableAccess Access(Type type)
    {
        if (!tableAccess.TryGetValue(type, out TableAccess access))
        {
            access = new(type);
            if (!access.Usable)
                access = null;
            tableAccess[type] = access;
        }
        return access;
    }

    internal sealed class TableAccess
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly object Tombstone = typeof(GC).GetField("EPHEMERON_TOMBSTONE", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);

        public readonly MethodInfo Remove;
        public readonly MethodInfo Add;
        private readonly FieldInfo data;
        private readonly FieldInfo gate;
        private readonly FieldInfo key;
        private readonly FieldInfo value;
        private readonly PropertyInfo keys;
        private readonly MethodInfo tryGetValue;

        public TableAccess(Type type)
        {
            Remove = type.GetMethod("Remove");
            Add = type.GetMethod("Add");
            data = type.GetField("data", Members);
            gate = type.GetField("_lock", Members);
            Type slot = data?.FieldType.GetElementType();
            key = slot?.GetField("key", Members);
            value = slot?.GetField("value", Members);
            keys = type.GetProperty("Keys", Members);
            tryGetValue = type.GetMethod("TryGetValue");
        }

        public bool Usable => Remove != null && ((key != null && value != null) || (keys != null && tryGetValue != null));

        public List<KeyValuePair<object, object>> Entries(object table)
        {
            List<KeyValuePair<object, object>> entries = [];

            if (key != null && value != null)
            {
                lock (gate?.GetValue(table) ?? table)
                {
                    if (data.GetValue(table) is Array slots)
                        foreach (object slot in slots)
                        {
                            object k = key.GetValue(slot);
                            if (k != null && !ReferenceEquals(k, Tombstone))
                                entries.Add(new(k, value.GetValue(slot)));
                        }
                }
                return entries;
            }

            foreach (object k in (IEnumerable)keys.GetValue(table, null))
            {
                object[] args = [k, null];
                tryGetValue.Invoke(table, args);
                entries.Add(new(k, args[1]));
            }
            return entries;
        }
    }

    private sealed class Reach(object dead, Func<object, bool> barrier, Func<object, bool> prune)
    {
        private readonly HashSet<object> leading = new(ReferenceComparer.Instance);
        private readonly Dictionary<object, int> dryWithin = new(ReferenceComparer.Instance);

        public bool Doomed(object key, object value) =>
            ReferenceEquals(key, dead) || (!Closed(key) && (Leads(key) || Leads(value)));

        private bool Leads(object root)
        {
            if (root == null)
                return false;
            if (ReferenceEquals(root, dead) || leading.Contains(root))
                return true;
            if (Dry(root, MaxDepth) || Closed(root) || Pruned(root))
                return false;

            Dictionary<object, object> parent = new(ReferenceComparer.Instance) { [root] = null };
            List<List<object>> levels = [[root]];
            int visits = 0;

            for (int depth = 0; depth < MaxDepth && levels[depth].Count > 0; depth++)
            {
                List<object> next = [];
                int remaining = MaxDepth - depth - 1;

                foreach (object node in levels[depth])
                {
                    foreach (object child in Children(node))
                    {
                        if (ReferenceEquals(child, dead) || leading.Contains(child))
                        {
                            for (object step = node; step != null; step = parent[step])
                                leading.Add(step);
                            return true;
                        }

                        if (parent.ContainsKey(child))
                            continue;

                        parent[child] = node;

                        if (++visits > VisitBudget)
                            return false;

                        if (!Closed(child) && !Pruned(child) && !Dry(child, remaining))
                            next.Add(child);
                    }
                }

                levels.Add(next);
            }

            for (int depth = 0; depth < levels.Count; depth++)
                foreach (object node in levels[depth])
                    if (!Dry(node, MaxDepth - depth))
                        dryWithin[node] = MaxDepth - depth;

            return false;
        }

        private bool Pruned(object obj) => prune != null && prune(obj);

        private bool Dry(object node, int steps) =>
            dryWithin.TryGetValue(node, out int proven) && proven >= steps;

        private bool Closed(object obj)
        {
            Type type = obj.GetType();
            return type.IsPrimitive || type.IsEnum || obj is string or Type or MemberInfo or Delegate or Assembly or Module
                || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ConditionalWeakTable<,>))
                || barrier(obj);
        }
    }

    private static IEnumerable<object> Children(object obj)
    {
        if (obj is Array array)
        {
            Type element = array.GetType().GetElementType();
            if (!CarriesReferences(element))
                yield break;

            int probed = 0;
            foreach (object item in array)
            {
                if (probed++ == ArrayProbe)
                    yield break;
                if (item == null)
                    continue;

                if (element.IsValueType)
                {
                    foreach (object inner in FieldValues(item))
                        yield return inner;
                }
                else
                    yield return item;
            }
            yield break;
        }

        foreach (object inner in FieldValues(obj))
            yield return inner;
    }

    private static IEnumerable<object> FieldValues(object obj)
    {
        foreach (FieldInfo field in ReferenceFields(obj.GetType()))
        {
            object value;
            try
            {
                value = field.GetValue(obj);
            }
            catch
            {
                continue;
            }

            if (value == null)
                continue;

            if (field.FieldType.IsValueType)
            {
                foreach (object inner in FieldValues(value))
                    yield return inner;
            }
            else
                yield return value;
        }
    }

    private static FieldInfo[] ReferenceFields(Type type)
    {
        if (referenceFields.TryGetValue(type, out FieldInfo[] cached))
            return cached;

        List<FieldInfo> found = [];
        for (Type level = type; level != null && level != typeof(object); level = SafeBase(level))
        {
            try
            {
                foreach (FieldInfo field in level.GetFields(InstanceFields))
                    if (CarriesReferences(field.FieldType))
                        found.Add(field);
            }
            catch
            {
            }
        }

        return referenceFields[type] = found.ToArray();
    }

    private static bool CarriesReferences(Type type)
    {
        if (type.IsPrimitive || type.IsEnum || type.IsPointer || type == typeof(string))
            return false;
        if (!type.IsValueType)
            return true;
        if (carriesReferences.TryGetValue(type, out bool cached))
            return cached;

        carriesReferences[type] = false;
        bool result = false;
        try
        {
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (field.FieldType != type && CarriesReferences(field.FieldType))
                {
                    result = true;
                    break;
                }
        }
        catch
        {
        }

        return carriesReferences[type] = result;
    }

    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceComparer Instance = new();

        public new bool Equals(object x, object y) => ReferenceEquals(x, y);

        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
