namespace DesktopNMS.Core.Demo;

/// <summary>
/// One demo graph before it's drawn: its size, scale and lines. Desktop asks for
/// the SVG rrdtool would send; the Intelligence graph lookup asks for a PNG, as
/// a model reads images, not SVG. Both are drawn from the same numbers, so the
/// same request gives the same picture either way.
/// </summary>
internal sealed class DemoChart
{
    private readonly DemoGraphs _graphs;

    internal DemoChart(DemoGraphs graphs, int width, int height, string unit, double max, IReadOnlyList<DemoGraphs.Line> series)
    {
        _graphs = graphs;
        Width = width;
        Height = height;
        Unit = unit;
        Max = max;
        Series = series;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>"bps", "%", "ms", or empty for a sensor graph.</summary>
    public string Unit { get; }

    /// <summary>The value at the top of the graph.</summary>
    public double Max { get; }

    public IReadOnlyList<DemoGraphs.Line> Series { get; }

    /// <summary>The graph as rrdtool's SVG, legend and all.</summary>
    public string ToSvg() => _graphs.Chart(Width, Height, Unit, Max, Series);

    /// <summary>The graph as a PNG, with or without its legend.</summary>
    public byte[] ToPng(bool legend = true) => DemoPng.Draw(this, _graphs.Now(), legend);
}
