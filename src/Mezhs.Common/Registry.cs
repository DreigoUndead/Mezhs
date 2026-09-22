namespace Mezhs.Common;

internal sealed class RegistryTree<TValue>
{
    private sealed class Node
    {
        public Dictionary<object, Node> Children { get; } = [];
        public bool HasValue { get; set; }
        public TValue? Value { get; set; }
    }

    private readonly int _depth;
    private readonly Node _root = new();
    private readonly object _sync = new();

    public RegistryTree(int depth)
    {
        if (depth < 1)
            throw new ArgumentOutOfRangeException(nameof(depth));
        _depth = depth;
    }

    public TValue? Get(params object[] keys) =>
        TryGet(out var value, keys) ? value : default;

    public bool TryGet(out TValue? value, params object[] keys)
    {
        ValidateExactKeys(keys);
        lock (_sync)
        {
            var node = FindNode(keys);
            if (node is not null && node.HasValue)
            {
                value = node.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    public IReadOnlyList<TValue> GetRange(params object[] prefix)
    {
        ValidatePrefix(prefix, allowLeaf: true);
        lock (_sync)
        {
            var node = FindNode(prefix);
            if (node is null)
                return [];

            var values = new List<TValue>();
            Collect(node, values);
            return values;
        }
    }

    public IReadOnlyList<object> GetKeys(params object[] prefix)
    {
        ValidatePrefix(prefix, allowLeaf: false);
        lock (_sync)
        {
            var node = FindNode(prefix);
            return node is null ? [] : node.Children.Keys.ToArray();
        }
    }

    public void Set(TValue value, params object[] keys)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateExactKeys(keys);

        lock (_sync)
        {
            var node = _root;
            foreach (var key in keys)
            {
                if (!node.Children.TryGetValue(key, out var child))
                {
                    child = new Node();
                    node.Children.Add(key, child);
                }
                node = child;
            }

            node.Value = value;
            node.HasValue = true;
        }
    }

    public bool Remove(params object[] keys)
    {
        ValidateExactKeys(keys);
        lock (_sync)
        {
            if (!TryFindPath(keys, out var path) || !path[^1].Child.HasValue)
                return false;

            var leaf = path[^1].Child;
            leaf.Value = default;
            leaf.HasValue = false;
            Prune(path);
            return true;
        }
    }

    public void Clear()
    {
        lock (_sync)
            _root.Children.Clear();
    }

    public bool Clear(params object[] prefix)
    {
        ValidatePrefix(prefix, allowLeaf: false);
        if (prefix.Length == 0)
        {
            Clear();
            return true;
        }

        lock (_sync)
        {
            if (!TryFindPath(prefix, out var path))
                return false;

            var last = path[^1];
            last.Parent.Children.Remove(last.Key);
            if (path.Count > 1)
                Prune(path.Take(path.Count - 1).ToArray());
            return true;
        }
    }

    private Node? FindNode(IReadOnlyList<object> keys)
    {
        var node = _root;
        foreach (var key in keys)
        {
            if (!node.Children.TryGetValue(key, out node))
                return null;
        }
        return node;
    }

    private bool TryFindPath(IReadOnlyList<object> keys, out List<(Node Parent, object Key, Node Child)> path)
    {
        path = [];
        var node = _root;
        foreach (var key in keys)
        {
            if (!node.Children.TryGetValue(key, out var child))
                return false;
            path.Add((node, key, child));
            node = child;
        }
        return true;
    }

    private static void Collect(Node node, List<TValue> values)
    {
        if (node.HasValue)
            values.Add(node.Value!);
        foreach (var child in node.Children.Values)
            Collect(child, values);
    }

    private static void Prune(IReadOnlyList<(Node Parent, object Key, Node Child)> path)
    {
        for (var index = path.Count - 1; index >= 0; index--)
        {
            var entry = path[index];
            if (entry.Child.HasValue || entry.Child.Children.Count > 0)
                break;
            entry.Parent.Children.Remove(entry.Key);
        }
    }

    private void ValidateExactKeys(object[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Length != _depth)
            throw new ArgumentException($"Registry requires exactly {_depth} keys.", nameof(keys));
        ValidateKeys(keys);
    }

    private void ValidatePrefix(object[] keys, bool allowLeaf)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var maximum = allowLeaf ? _depth : _depth - 1;
        if (keys.Length > maximum)
            throw new ArgumentException($"Registry prefix accepts at most {maximum} keys.", nameof(keys));
        ValidateKeys(keys);
    }

    private static void ValidateKeys(IEnumerable<object> keys)
    {
        if (keys.Any(key => key is null))
            throw new ArgumentNullException(nameof(keys), "Registry keys cannot be null.");
    }
}

public sealed class Registry<TKey1, TValue>
    where TKey1 : notnull
{
    private readonly RegistryTree<TValue> _tree = new(1);

    public TValue? Get(TKey1 key1) => _tree.Get(key1);
    public bool TryGet(TKey1 key1, out TValue? value) => _tree.TryGet(out value, key1);
    public IReadOnlyList<TValue> GetRange() => _tree.GetRange();
    public IReadOnlyList<TKey1> GetKeys() => _tree.GetKeys().Cast<TKey1>().ToArray();
    public void Set(TKey1 key1, TValue value) => _tree.Set(value, key1);
    public bool Remove(TKey1 key1) => _tree.Remove(key1);
    public void Clear() => _tree.Clear();
}

public sealed class Registry<TKey1, TKey2, TValue>
    where TKey1 : notnull
    where TKey2 : notnull
{
    private readonly RegistryTree<TValue> _tree = new(2);

    public TValue? Get(TKey1 key1, TKey2 key2) => _tree.Get(key1, key2);
    public bool TryGet(TKey1 key1, TKey2 key2, out TValue? value) => _tree.TryGet(out value, key1, key2);
    public IReadOnlyList<TValue> GetRange() => _tree.GetRange();
    public IReadOnlyList<TValue> GetRange(TKey1 key1) => _tree.GetRange(key1);
    public IReadOnlyList<TKey1> GetKeys() => _tree.GetKeys().Cast<TKey1>().ToArray();
    public IReadOnlyList<TKey2> GetKeys(TKey1 key1) => _tree.GetKeys(key1).Cast<TKey2>().ToArray();
    public void Set(TKey1 key1, TKey2 key2, TValue value) => _tree.Set(value, key1, key2);
    public bool Remove(TKey1 key1, TKey2 key2) => _tree.Remove(key1, key2);
    public void Clear() => _tree.Clear();
    public bool Clear(TKey1 key1) => _tree.Clear(key1);
}

public sealed class Registry<TKey1, TKey2, TKey3, TValue>
    where TKey1 : notnull
    where TKey2 : notnull
    where TKey3 : notnull
{
    private readonly RegistryTree<TValue> _tree = new(3);

    public TValue? Get(TKey1 key1, TKey2 key2, TKey3 key3) => _tree.Get(key1, key2, key3);
    public bool TryGet(TKey1 key1, TKey2 key2, TKey3 key3, out TValue? value) => _tree.TryGet(out value, key1, key2, key3);
    public IReadOnlyList<TValue> GetRange() => _tree.GetRange();
    public IReadOnlyList<TValue> GetRange(TKey1 key1) => _tree.GetRange(key1);
    public IReadOnlyList<TValue> GetRange(TKey1 key1, TKey2 key2) => _tree.GetRange(key1, key2);
    public IReadOnlyList<TKey1> GetKeys() => _tree.GetKeys().Cast<TKey1>().ToArray();
    public IReadOnlyList<TKey2> GetKeys(TKey1 key1) => _tree.GetKeys(key1).Cast<TKey2>().ToArray();
    public IReadOnlyList<TKey3> GetKeys(TKey1 key1, TKey2 key2) => _tree.GetKeys(key1, key2).Cast<TKey3>().ToArray();
    public void Set(TKey1 key1, TKey2 key2, TKey3 key3, TValue value) => _tree.Set(value, key1, key2, key3);
    public bool Remove(TKey1 key1, TKey2 key2, TKey3 key3) => _tree.Remove(key1, key2, key3);
    public void Clear() => _tree.Clear();
    public bool Clear(TKey1 key1) => _tree.Clear(key1);
    public bool Clear(TKey1 key1, TKey2 key2) => _tree.Clear(key1, key2);
}

public sealed class Registry<TKey1, TKey2, TKey3, TKey4, TValue>
    where TKey1 : notnull
    where TKey2 : notnull
    where TKey3 : notnull
    where TKey4 : notnull
{
    private readonly RegistryTree<TValue> _tree = new(4);

    public TValue? Get(TKey1 key1, TKey2 key2, TKey3 key3, TKey4 key4) => _tree.Get(key1, key2, key3, key4);
    public bool TryGet(TKey1 key1, TKey2 key2, TKey3 key3, TKey4 key4, out TValue? value) => _tree.TryGet(out value, key1, key2, key3, key4);
    public IReadOnlyList<TValue> GetRange() => _tree.GetRange();
    public IReadOnlyList<TValue> GetRange(TKey1 key1) => _tree.GetRange(key1);
    public IReadOnlyList<TValue> GetRange(TKey1 key1, TKey2 key2) => _tree.GetRange(key1, key2);
    public IReadOnlyList<TValue> GetRange(TKey1 key1, TKey2 key2, TKey3 key3) => _tree.GetRange(key1, key2, key3);
    public IReadOnlyList<TKey1> GetKeys() => _tree.GetKeys().Cast<TKey1>().ToArray();
    public IReadOnlyList<TKey2> GetKeys(TKey1 key1) => _tree.GetKeys(key1).Cast<TKey2>().ToArray();
    public IReadOnlyList<TKey3> GetKeys(TKey1 key1, TKey2 key2) => _tree.GetKeys(key1, key2).Cast<TKey3>().ToArray();
    public IReadOnlyList<TKey4> GetKeys(TKey1 key1, TKey2 key2, TKey3 key3) => _tree.GetKeys(key1, key2, key3).Cast<TKey4>().ToArray();
    public void Set(TKey1 key1, TKey2 key2, TKey3 key3, TKey4 key4, TValue value) => _tree.Set(value, key1, key2, key3, key4);
    public bool Remove(TKey1 key1, TKey2 key2, TKey3 key3, TKey4 key4) => _tree.Remove(key1, key2, key3, key4);
    public void Clear() => _tree.Clear();
    public bool Clear(TKey1 key1) => _tree.Clear(key1);
    public bool Clear(TKey1 key1, TKey2 key2) => _tree.Clear(key1, key2);
    public bool Clear(TKey1 key1, TKey2 key2, TKey3 key3) => _tree.Clear(key1, key2, key3);
}

public sealed class Registry<TKey1, TKey2, TKey3, TKey4, TKey5, TValue>
    where TKey1 : notnull
    where TKey2 : notnull
    where TKey3 : notnull
    where TKey4 : notnull
    where TKey5 : notnull
{
    private readonly RegistryTree<TValue> _tree = new(5);

    public TValue? Get(TKey1 key1, TKey2 key2, TKey3 key3, TKey4 key4, TKey5 key5) => _tree.Get(key1, key2, key3, key4, key5);
    public bool TryGet(TKey1 key1, TKey2 key2, TKey3 key3, TKey4 key4, TKey5 key5, out TValue? value) => _tree.TryGet(out value, key1, key2, key3, key4, key5);
    public IReadOnlyList<TValue> GetRange() => _tree.GetRange();
    public IReadOnlyList<TValue> GetRange(TKey1 key1) => _tree.GetRange(key1);
    public IReadOnlyList<TValue> GetRange(TKey1 key1, TKey2 key2) => _tree.GetRange(key1, key2);
    public IReadOnlyList<TValue> GetRange(TKey1 key1, TKey2 key2, TKey3 key3) => _tree.GetRange(key1, key2, key3);
    public IReadOnlyList<TValue> GetRange(TKey1 key1, TKey2 key2, TKey3 key3, TKey4 key4) => _tree.GetRange(key1, key2, key3, key4);
    public IReadOnlyList<TKey1> GetKeys() => _tree.GetKeys().Cast<TKey1>().ToArray();
    public IReadOnlyList<TKey2> GetKeys(TKey1 key1) => _tree.GetKeys(key1).Cast<TKey2>().ToArray();
    public IReadOnlyList<TKey3> GetKeys(TKey1 key1, TKey2 key2) => _tree.GetKeys(key1, key2).Cast<TKey3>().ToArray();
    public IReadOnlyList<TKey4> GetKeys(TKey1 key1, TKey2 key2, TKey3 key3) => _tree.GetKeys(key1, key2, key3).Cast<TKey4>().ToArray();
    public IReadOnlyList<TKey5> GetKeys(TKey1 key1, TKey2 key2, TKey3 key3, TKey4 key4) => _tree.GetKeys(key1, key2, key3, key4).Cast<TKey5>().ToArray();
    public void Set(TKey1 key1, TKey2 key2, TKey3 key3, TKey4 key4, TKey5 key5, TValue value) => _tree.Set(value, key1, key2, key3, key4, key5);
    public bool Remove(TKey1 key1, TKey2 key2, TKey3 key3, TKey4 key4, TKey5 key5) => _tree.Remove(key1, key2, key3, key4, key5);
    public void Clear() => _tree.Clear();
    public bool Clear(TKey1 key1) => _tree.Clear(key1);
    public bool Clear(TKey1 key1, TKey2 key2) => _tree.Clear(key1, key2);
    public bool Clear(TKey1 key1, TKey2 key2, TKey3 key3) => _tree.Clear(key1, key2, key3);
    public bool Clear(TKey1 key1, TKey2 key2, TKey3 key3, TKey4 key4) => _tree.Clear(key1, key2, key3, key4);
}
