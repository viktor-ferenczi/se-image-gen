using System;

namespace Shared.ImageGen;

// Character grid and font size that fill an LCD surface with color-character art.
//
// The Monospace font's color glyphs (U+E100..U+E2FF) advance 36 units plus a spacing of 1,
// and its lines are 37 units high, so each cell is a 37 x 37 square. The game draws surface
// text at FontSize * min(texture width, height) / 512 and the font at 28.8 / 37 of its size,
// so a cell is 28.8 * FontSize * min(texture) / 512 texture pixels wide and high.
public readonly struct ScreenFit
{
    public const float CellPixelsPerFontSize = 28.8f;

    public readonly int Columns;
    public readonly int Rows;
    public readonly float FontSize;

    public ScreenFit(int columns, int rows, float fontSize)
    {
        Columns = columns;
        Rows = rows;
        FontSize = fontSize;
    }

    // Characters of the art, including the newlines between the rows
    public int Length => Columns * Rows + Rows - 1;

    public override string ToString() => $"{Columns}x{Rows} at font size {FontSize:0.000}";

    // textureWidth/Height: MyTextPanelComponent.TextureSize; surfaceWidth/Height: SurfaceSize (the visible part,
    // in texture pixels). maxCharacters: the text length limit. minCellPixels: smallest cell worth drawing.
    public static ScreenFit Calculate(
        float textureWidth,
        float textureHeight,
        float surfaceWidth,
        float surfaceHeight,
        int maxCharacters,
        float minCellPixels
    )
    {
        if (surfaceWidth < 1 || surfaceHeight < 1 || textureWidth < 1 || textureHeight < 1)
            throw new ArgumentException("Empty surface");

        var pixelsPerFontSize = CellPixelsPerFontSize * Math.Min(textureWidth, textureHeight) / 512f;
        var cell = Math.Max(minCellPixels, (float)Math.Sqrt(surfaceWidth * surfaceHeight / maxCharacters));

        // The game rounds FontSize to 3 decimals, so search on that grid
        var thousandths = Math.Max(1, (int)Math.Ceiling(cell / pixelsPerFontSize * 1000f - 1e-3f));
        while (true)
        {
            var fontSize = thousandths / 1000f;
            cell = fontSize * pixelsPerFontSize;
            var columns = Math.Max(1, (int)(surfaceWidth / cell));
            var rows = Math.Max(1, (int)(surfaceHeight / cell));
            var fit = new ScreenFit(columns, rows, fontSize);
            if (fit.Length <= maxCharacters || columns * rows == 1)
                return fit;
            thousandths++;
        }
    }
}
