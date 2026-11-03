namespace Tman.Monitoring;

/// <summary>Fixed-length ring buffer of samples; index 0 is the oldest.</summary>
public sealed class History
{
    readonly double[] _data;
    int _start;

    public History(int length) => _data = new double[length];

    public int Length => _data.Length;

    /// <summary>How many real samples have been added (up to Length); older slots are padding.</summary>
    public int Count { get; private set; }

    public double this[int i] => _data[(_start + i) % _data.Length];

    public double Latest => this[_data.Length - 1];

    public void Add(double value)
    {
        _data[_start] = double.IsFinite(value) ? value : 0;
        _start = (_start + 1) % _data.Length;
        if (Count < _data.Length) Count++;
    }

    public double Max()
    {
        double m = 0;
        foreach (var v in _data) if (v > m) m = v;
        return m;
    }
}
