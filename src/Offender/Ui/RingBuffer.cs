namespace Offender.Ui;

/// <summary>
/// Fixed-capacity history for one sparkline. Indexing is oldest-to-newest so the renderer
/// can walk it left to right without any modular arithmetic of its own.
/// </summary>
internal sealed class RingBuffer
{
    private readonly float[] _data;
    private int _head;     // next write position
    private int _count;

    public RingBuffer(int capacity) => _data = new float[capacity];

    public int Capacity => _data.Length;
    public int Count => _count;

    public void Add(float value)
    {
        _data[_head] = value;
        _head = (_head + 1) % _data.Length;
        if (_count < _data.Length) _count++;
    }

    /// <summary>0 is the oldest retained sample.</summary>
    public float this[int i]
    {
        get
        {
            int start = (_head - _count + _data.Length) % _data.Length;
            return _data[(start + i) % _data.Length];
        }
    }

    public float Max()
    {
        float m = 0;
        for (int i = 0; i < _count; i++)
        {
            float v = this[i];
            if (v > m) m = v;
        }
        return m;
    }

    public void Clear() { _head = 0; _count = 0; }
}
