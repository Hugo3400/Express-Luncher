using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ExpressLuncher.Controls;

/// <summary>
/// Grille virtualisée de tuiles de taille uniforme (jaquette 2:3 + légende). Le nombre de colonnes
/// s'adapte à la largeur et les tuiles s'étirent pour remplir la ligne. Seules les lignes visibles
/// (plus une ligne de marge) ont des conteneurs.
/// Deux modes : hôte d'un ScrollViewer (défilement vertical animé) ou « en ligne » dans une page qui
/// défile déjà, où le panneau prend la hauteur de son contenu, limitée à <see cref="MaxRows"/> lignes.
/// </summary>
public sealed class VirtualizingTilePanel : VirtualizingPanel, IScrollInfo
{
    private const int CacheRows = 1;
    private const double WheelStep = 110;
    private const double LineStep = 48;

    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(VirtualizingTilePanel),
        new FrameworkPropertyMetadata(150d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemSpacingProperty = DependencyProperty.Register(
        nameof(ItemSpacing), typeof(double), typeof(VirtualizingTilePanel),
        new FrameworkPropertyMetadata(14d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty CaptionHeightProperty = DependencyProperty.Register(
        nameof(CaptionHeight), typeof(double), typeof(VirtualizingTilePanel),
        new FrameworkPropertyMetadata(46d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(
        nameof(Padding), typeof(Thickness), typeof(VirtualizingTilePanel),
        new FrameworkPropertyMetadata(default(Thickness), FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxRowsProperty = DependencyProperty.Register(
        nameof(MaxRows), typeof(int), typeof(VirtualizingTilePanel),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private int _itemCount;
    private int _columns = 1;
    private double _itemWidth;
    private double _itemHeight;

    private Size _extent;
    private Size _viewport;
    private double _offset;
    private double _targetOffset;
    private bool _animating;
    private long _lastFrame;

    public VirtualizingTilePanel()
    {
        Unloaded += (_, _) => StopAnimation();
    }

    /// <summary>Largeur minimale d'une tuile ; la largeur réelle remplit la ligne.</summary>
    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double ItemSpacing
    {
        get => (double)GetValue(ItemSpacingProperty);
        set => SetValue(ItemSpacingProperty, value);
    }

    /// <summary>Hauteur réservée sous la jaquette (nom + ligne secondaire).</summary>
    public double CaptionHeight
    {
        get => (double)GetValue(CaptionHeightProperty);
        set => SetValue(CaptionHeightProperty, value);
    }

    public Thickness Padding
    {
        get => (Thickness)GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    /// <summary>Nombre maximal de lignes affichées (0 = toutes). Utile pour une rangée unique.</summary>
    public int MaxRows
    {
        get => (int)GetValue(MaxRowsProperty);
        set => SetValue(MaxRowsProperty, value);
    }

    public int Columns => _columns;

    /// <summary>Nombre d'éléments réellement présentés (tient compte de <see cref="MaxRows"/>).</summary>
    public int VisibleItemCount => MaxRows > 0 ? Math.Min(_itemCount, MaxRows * _columns) : _itemCount;

    public int RowsPerPage => Math.Max(1, (int)(_viewport.Height / Math.Max(1, _itemHeight + ItemSpacing)));

    private bool IsScrolling => ScrollOwner is not null;

    protected override Size MeasureOverride(Size availableSize)
    {
        var owner = ItemsControl.GetItemsOwner(this);
        _itemCount = owner?.Items.Count ?? 0;

        var padding = Padding;
        var gap = ItemSpacing;
        var width = double.IsInfinity(availableSize.Width) ? 1000 : availableSize.Width;
        var inner = Math.Max(0, width - padding.Left - padding.Right);

        _columns = Math.Max(1, (int)Math.Floor((inner + gap) / (MinItemWidth + gap)));
        _itemWidth = Math.Max(1, Math.Floor((inner - (_columns - 1) * gap) / _columns));
        _itemHeight = Math.Round(_itemWidth * 1.5) + CaptionHeight;

        var count = VisibleItemCount;
        var rows = (count + _columns - 1) / _columns;
        var rowPitch = _itemHeight + gap;
        var contentHeight = rows == 0 ? 0 : padding.Top + padding.Bottom + rows * _itemHeight + (rows - 1) * gap;

        double height;
        if (IsScrolling)
        {
            height = double.IsInfinity(availableSize.Height) ? contentHeight : availableSize.Height;
            UpdateScrollInfo(new Size(width, contentHeight), new Size(width, height));
        }
        else
        {
            height = contentHeight;
            _offset = _targetOffset = 0;
        }

        // L'accès à InternalChildren initialise le générateur de conteneurs.
        var children = InternalChildren;
        var generator = ItemContainerGenerator;
        if (generator is null) return new Size(width, height);

        var first = 0;
        var last = -1;
        if (count > 0)
        {
            var firstRow = 0;
            var lastRow = rows - 1;
            if (IsScrolling)
            {
                firstRow = Math.Max(0, (int)Math.Floor((_offset - padding.Top) / rowPitch) - CacheRows);
                lastRow = Math.Min(rows - 1, (int)Math.Floor((_offset + height - padding.Top) / rowPitch) + CacheRows);
            }
            first = firstRow * _columns;
            last = Math.Min(count - 1, (lastRow + 1) * _columns - 1);
        }

        if (last >= first)
        {
            var itemSize = new Size(_itemWidth, _itemHeight);
            var start = generator.GeneratorPositionFromIndex(first);
            var childIndex = start.Offset == 0 ? start.Index : start.Index + 1;

            using (generator.StartAt(start, GeneratorDirection.Forward, allowStartAtRealizedItem: true))
            {
                for (var index = first; index <= last; index++, childIndex++)
                {
                    if (generator.GenerateNext(out var isNew) is not UIElement child) break;
                    if (isNew)
                    {
                        if (childIndex >= children.Count) AddInternalChild(child);
                        else InsertInternalChild(childIndex, child);
                        generator.PrepareItemContainer(child);
                    }
                    child.Measure(itemSize);
                }
            }
        }

        for (var i = children.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            var index = generator.IndexFromGeneratorPosition(position);
            if (index >= first && index <= last) continue;

            // Retirer le conteneur qui a le focus clavier ferait perdre le focus à toute la liste.
            if (index >= 0 && index < count && children[i].IsKeyboardFocusWithin)
            {
                children[i].Measure(new Size(_itemWidth, _itemHeight));
                continue;
            }

            generator.Remove(position, 1);
            RemoveInternalChildRange(i, 1);
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var generator = ItemContainerGenerator;
        if (generator is null) return finalSize;

        var children = InternalChildren;
        for (var i = 0; i < children.Count; i++)
        {
            var index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index >= 0) children[i].Arrange(GetItemRect(index));
        }
        return finalSize;
    }

    private Rect GetItemRect(int index)
    {
        var padding = Padding;
        var gap = ItemSpacing;
        var row = index / _columns;
        var column = index % _columns;
        return new Rect(
            padding.Left + column * (_itemWidth + gap),
            padding.Top + row * (_itemHeight + gap) - Math.Round(_offset),
            _itemWidth,
            _itemHeight);
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        switch (args.Action)
        {
            case System.Collections.Specialized.NotifyCollectionChangedAction.Remove:
            case System.Collections.Specialized.NotifyCollectionChangedAction.Replace:
            case System.Collections.Specialized.NotifyCollectionChangedAction.Move:
                if (args.ItemUICount > 0) RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
        }
    }

    protected override void BringIndexIntoView(int index) => ScrollIndexIntoView(index);

    /// <summary>Fait défiler (sans animation) juste assez pour que l'élément soit entièrement visible.</summary>
    public void ScrollIndexIntoView(int index)
    {
        if (!IsScrolling || index < 0 || index >= _itemCount) return;
        StopAnimation();
        SetOffset(OffsetShowing(index));
        _targetOffset = _offset;
    }

    public void ScrollToTop() => SetVerticalOffset(0);

    private double OffsetShowing(int index)
    {
        var padding = Padding;
        var row = index / _columns;
        var top = padding.Top + row * (_itemHeight + ItemSpacing);
        var bottom = top + _itemHeight;
        var margin = Math.Min(ItemSpacing, 12);

        if (top - margin < _offset) return top - (row == 0 ? padding.Top : margin);
        if (bottom + margin > _offset + _viewport.Height) return bottom + margin - _viewport.Height;
        return _offset;
    }

    // ---- Défilement ----

    private void UpdateScrollInfo(Size extent, Size viewport)
    {
        var changed = false;
        if (extent != _extent) { _extent = extent; changed = true; }
        if (viewport != _viewport) { _viewport = viewport; changed = true; }

        var clamped = Clamp(_offset);
        if (clamped != _offset) { _offset = clamped; changed = true; }
        _targetOffset = Clamp(_targetOffset);

        if (changed) ScrollOwner?.InvalidateScrollInfo();
    }

    private double Clamp(double offset) => Math.Max(0, Math.Min(offset, Math.Max(0, _extent.Height - _viewport.Height)));

    private void SetOffset(double offset)
    {
        offset = Clamp(offset);
        if (Math.Abs(offset - _offset) < 0.01) return;
        _offset = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    private void AnimateTo(double offset)
    {
        _targetOffset = Clamp(offset);
        if (_animating || Math.Abs(_targetOffset - _offset) < 0.5) return;

        _animating = true;
        _lastFrame = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopAnimation()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = Math.Min(0.1, Stopwatch.GetElapsedTime(_lastFrame, now).TotalSeconds);
        _lastFrame = now;

        var remaining = _targetOffset - _offset;
        if (Math.Abs(remaining) < 0.5)
        {
            SetOffset(_targetOffset);
            StopAnimation();
            return;
        }
        SetOffset(_offset + remaining * (1 - Math.Exp(-elapsed / 0.07)));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        // Défilement proportionnel au delta : fluide aussi avec un pavé tactile de précision.
        if (IsScrolling && _extent.Height > _viewport.Height)
        {
            AnimateTo(_targetOffset - e.Delta / 120.0 * WheelStep);
            e.Handled = true;
        }
        base.OnMouseWheel(e);
    }

    public ScrollViewer? ScrollOwner { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _offset;

    public void SetVerticalOffset(double offset)
    {
        StopAnimation();
        SetOffset(offset);
        _targetOffset = _offset;
    }

    public void SetHorizontalOffset(double offset) { }
    public void LineUp() => AnimateTo(_targetOffset - LineStep);
    public void LineDown() => AnimateTo(_targetOffset + LineStep);
    public void PageUp() => AnimateTo(_targetOffset - _viewport.Height * 0.9);
    public void PageDown() => AnimateTo(_targetOffset + _viewport.Height * 0.9);
    public void MouseWheelUp() => AnimateTo(_targetOffset - WheelStep);
    public void MouseWheelDown() => AnimateTo(_targetOffset + WheelStep);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        var generator = ItemContainerGenerator;
        if (generator is null || !IsScrolling) return rectangle;

        var children = InternalChildren;
        for (var i = 0; i < children.Count; i++)
        {
            if (!ReferenceEquals(children[i], visual) && !children[i].IsAncestorOf(visual)) continue;

            var index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index >= 0) AnimateTo(OffsetShowing(index));
            break;
        }
        return rectangle;
    }
}
