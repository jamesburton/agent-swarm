using System.Collections;

namespace Swarm.Render;

/// <summary>Insertion-ordered read-only file map with O(1) lookup (a plain <see cref="Dictionary{TKey,TValue}"/> does not guarantee enumeration order).</summary>
internal sealed class RenderedFiles : IReadOnlyDictionary<string, string>
{
    readonly List<KeyValuePair<string, string>> items = [];
    readonly Dictionary<string, int> index = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public int Count => items.Count;

    /// <inheritdoc />
    public IEnumerable<string> Keys => items.Select(p => p.Key);

    /// <inheritdoc />
    public IEnumerable<string> Values => items.Select(p => p.Value);

    /// <inheritdoc />
    /// <exception cref="KeyNotFoundException">Thrown when the key is absent.</exception>
    public string this[string key] =>
        index.TryGetValue(key, out var i) ? items[i].Value : throw new KeyNotFoundException($"file '{key}' was not rendered");

    /// <summary>Appends a file; enumeration order is insertion order.</summary>
    /// <param name="key">Relative path.</param>
    /// <param name="value">File content.</param>
    /// <exception cref="ArgumentException">Thrown when the key was already added.</exception>
    public void Add(string key, string value)
    {
        if (!index.TryAdd(key, items.Count)) throw new ArgumentException($"file '{key}' was already added", nameof(key));
        items.Add(new(key, value));
    }

    /// <inheritdoc />
    public bool ContainsKey(string key) => index.ContainsKey(key);

    /// <inheritdoc />
    public bool TryGetValue(string key, out string value)
    {
        if (index.TryGetValue(key, out var i)) { value = items[i].Value; return true; }
        value = "";
        return false;
    }

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
