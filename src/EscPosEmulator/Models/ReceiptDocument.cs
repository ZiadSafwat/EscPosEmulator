namespace EscPosEmulator.Models;

/// <summary>
/// The receipt currently being emulated. One <see cref="ReceiptDocument"/> per
/// print job (one TCP connection from the spooler / a direct RAW send).
/// </summary>
public sealed class ReceiptDocument
{
    private readonly List<ReceiptElement> _elements = [];
    private readonly object _gate = new();

    public IReadOnlyList<ReceiptElement> Elements => Snapshot();

    public DateTime CreatedAt { get; } = DateTime.Now;
    public string JobId { get; } = Guid.NewGuid().ToString("N")[..8];

    public void Add(ReceiptElement? element)
    {
        if (element == null) return;
        lock (_gate)
        {
            _elements.Add(element);
        }
    }

    /// <summary>Last element, or null when the receipt is empty.</summary>
    public ReceiptElement? LastElement
    {
        get
        {
            lock (_gate)
                return _elements.Count > 0 ? _elements[^1] : null;
        }
    }

    /// <summary>Replace the last element (used to merge consecutive raster bands).</summary>
    public void ReplaceLast(ReceiptElement element)
    {
        if (element == null) return;
        lock (_gate)
        {
            if (_elements.Count == 0) _elements.Add(element);
            else _elements[^1] = element;
        }
    }

    public void Clear()
    {
        lock (_gate)
            _elements.Clear();
    }

    public int TotalHeightDots
    {
        get
        {
            int total = 0;
            lock (_gate)
            {
                foreach (var e in _elements)
                    total += Math.Max(0, e.HeightDots);
            }

            return total;
        }
    }

    private List<ReceiptElement> Snapshot()
    {
        lock (_gate)
            return _elements.ToList();
    }
}
