using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MoeLoaderR.Core;

namespace MoeLoaderR.Wpf.ControlParts;

// Selection belongs to the page data, not to the transient visual control.
public sealed class ThumbnailEntry
{
    private readonly VirtualThumbnailPanel _owner;
    private bool _isSelected;
    public MoeItem MoeItem { get; }
    public MoeItemControl Control { get; internal set; }
    public bool NeedsRefresh { get; internal set; }
    public ImageSource Thumbnail => Control?.PreviewImage.Source ?? _owner.GetCached(this);
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            if (Control != null) Control.ImageCheckBox.IsChecked = value;
            if (value) _owner.SelectedEntries.Add(this);
            else _owner.SelectedEntries.Remove(this);
        }
    }
    internal ThumbnailEntry(VirtualThumbnailPanel owner, MoeItem item) { _owner = owner; MoeItem = item; }
    public async Task RefreshAsync()
    {
        if (Control != null) await Control.TryLoad();
        else
        {
            // Invalidate an off-screen thumbnail; it reloads when brought into view.
            _owner.ForgetCached(this);
            try { await MoeItem.TryGetDetail(default); }
            catch (Exception e) { Ex.Log(e); }
        }
    }
}

// Pixel-scrolling panel: extent includes every item, visuals only cover the viewport
// plus one screen on each side. No off-screen WPF controls are kept in the cache.
public sealed class VirtualThumbnailPanel : Panel
{
    public static readonly DependencyProperty ItemSizeProperty = DependencyProperty.Register(
        nameof(ItemSize), typeof(double), typeof(VirtualThumbnailPanel),
        new FrameworkPropertyMetadata(200d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public double ItemSize { get => (double)GetValue(ItemSizeProperty); set => SetValue(ItemSizeProperty, value); }
    public IReadOnlyList<ThumbnailEntry> Entries => _entries;
    public ObservableCollection<ThumbnailEntry> SelectedEntries { get; } = new();
    public Func<ThumbnailEntry, MoeItemControl> CreateControl { get; set; }
    public Action<MoeItemControl> ReleaseControl { get; set; }
    public Action<MoeItemControl> LoadControl { get; set; }
    private readonly List<ThumbnailEntry> _entries = new();
    private readonly Dictionary<int, MoeItemControl> _realized = new();
    private readonly Dictionary<ThumbnailEntry, (ImageSource Image, long Bytes, LinkedListNode<ThumbnailEntry> Node)> _cache = new();
    private readonly LinkedList<ThumbnailEntry> _lru = new();
    private long _cacheBytes;
    private ScrollViewer _viewer;
    private int _columns = 1;
    private double _cellWidth = 208, _cellHeight = 208;
    public int RealizedCount => _realized.Count;
    public int CachedCount => _cache.Count;
    public long CachedBytes => _cacheBytes;

    public void Attach(ScrollViewer viewer)
    {
        if (_viewer != null) _viewer.ScrollChanged -= OnScrollChanged;
        _viewer = viewer;
        _viewer.ScrollChanged += OnScrollChanged;
    }
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e) => InvalidateMeasure();

    public void SetItems(IEnumerable<MoeItem> items)
    {
        ClearItems();
        _entries.AddRange(items.Select(item => new ThumbnailEntry(this, item)));
        InvalidateMeasure();
    }
    public void ClearItems()
    {
        foreach (var pair in _realized.ToArray()) RemoveControl(pair.Key, false);
        _entries.Clear();
        SelectedEntries.Clear();
        _cache.Clear();
        _lru.Clear();
        _cacheBytes = 0;
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? Math.Max(1, _viewer?.ViewportWidth ?? 800) : Math.Max(1, available.Width);
        var size = Math.Min(width, Math.Max(64, ItemSize));
        _columns = Math.Max(1, (int)(width / (size + 8)));
        _cellWidth = width / _columns;
        _cellHeight = size + 8;
        var height = Math.Max(_cellHeight, _viewer?.ViewportHeight ?? 600);
        var offset = Math.Max(0, _viewer?.VerticalOffset ?? 0);
        var first = Math.Max(0, (int)Math.Floor((offset - height) / _cellHeight) * _columns);
        var last = Math.Min(_entries.Count, ((int)Math.Ceiling((offset + 2 * height) / _cellHeight) + 1) * _columns);
        foreach (var index in _realized.Keys.Where(i => i < first || i >= last).ToArray()) RemoveControl(index, true);
        if (CreateControl != null)
        {
            var visibleFirst = (int)(offset / _cellHeight) * _columns;
            foreach (var index in Enumerable.Range(first, Math.Max(0, last - first)).OrderBy(i => i < visibleFirst ? visibleFirst - i + last : i))
            {
                if (_realized.ContainsKey(index)) continue;
                var entry = _entries[index];
                var control = CreateControl(entry);
                entry.Control = control;
                control.Margin = new Thickness(0);
                control.ImageCheckBox.IsChecked = entry.IsSelected;
                control.ImageCheckBox.Checked += (_, _) => entry.IsSelected = true;
                control.ImageCheckBox.Unchecked += (_, _) => entry.IsSelected = false;
                var cached = GetCached(entry);
                if (cached != null) control.SetCachedThumbnail(cached);
                _realized.Add(index, control);
                Children.Add(control);
                LoadControl?.Invoke(control);
            }
        }
        foreach (var control in _realized.Values)
        {
            control.Width = size;
            control.Height = size;
            control.Measure(new Size(size, size));
        }
        return new Size(width, Math.Ceiling((double)_entries.Count / _columns) * _cellHeight);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var pair in _realized) pair.Value.Arrange(GetItemBounds(pair.Key));
        return finalSize;
    }
    public Rect GetItemBounds(int index) => new(index % _columns * _cellWidth + Math.Max(0, (_cellWidth - (_cellHeight - 8)) / 2),
        index / _columns * _cellHeight + 4, _cellHeight - 8, _cellHeight - 8);
    public void SelectRectangle(Rect area, bool selected)
    {
        for (var i = 0; i < _entries.Count; i++)
            if (area.IntersectsWith(GetItemBounds(i))) _entries[i].IsSelected = selected;
    }
    private void RemoveControl(int index, bool cache)
    {
        var control = _realized[index];
        var entry = _entries[index];
        entry.NeedsRefresh = control.RefreshButton.Visibility == Visibility.Visible;
        if (cache && control.PreviewImage.Source != null) StoreCached(entry, control.PreviewImage.Source);
        ReleaseControl?.Invoke(control);
        control.Dispose();
        entry.Control = null;
        Children.Remove(control);
        _realized.Remove(index);
    }
    internal ImageSource GetCached(ThumbnailEntry entry)
    {
        if (!_cache.TryGetValue(entry, out var value)) return null;
        _lru.Remove(value.Node);
        _lru.AddLast(value.Node);
        return value.Image;
    }
    internal void ForgetCached(ThumbnailEntry entry)
    {
        if (!_cache.Remove(entry, out var value)) return;
        _cacheBytes -= value.Bytes;
        _lru.Remove(value.Node);
    }
    private void StoreCached(ThumbnailEntry entry, ImageSource image)
    {
        if (image is not BitmapSource bitmap) return;
        ForgetCached(entry);
        var bytes = (long)bitmap.PixelWidth * bitmap.PixelHeight * Math.Max(4, (bitmap.Format.BitsPerPixel + 7) / 8);
        const long limit = 64L * 1024 * 1024;
        if (bytes > limit) return;
        var node = _lru.AddLast(entry);
        _cache.Add(entry, (image, bytes, node));
        _cacheBytes += bytes;
        while (_cache.Count > 120 || _cacheBytes > limit) ForgetCached(_lru.First.Value);
    }
}
