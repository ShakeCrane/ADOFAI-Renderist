using System;

namespace ADOFAI.Renderist.Export
{
    /// <summary>实际 band reader 使用的纯整数规划；末块满高重叠，所有相加前先保证不越界。</summary>
    internal struct Rgb24ReadbackBand
    {
        internal int StartRow { get; private set; }
        internal int RowCount { get; private set; }
        internal int NextRow { get; private set; }

        internal static bool TryPlan(int height, int requestedRows, int nextRow, out Rgb24ReadbackBand band)
        {
            band = default(Rgb24ReadbackBand);
            if (height <= 0 || requestedRows <= 0 || nextRow < 0 || nextRow > height)
                throw new ArgumentOutOfRangeException("band geometry");
            if (nextRow == height) return false;
            int rows = Math.Min(height, requestedRows);
            int start = Math.Min(nextRow, height - rows);
            band = new Rgb24ReadbackBand { StartRow = start, RowCount = rows, NextRow = start + rows };
            return true;
        }
    }
}
