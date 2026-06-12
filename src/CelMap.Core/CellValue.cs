namespace CelMap.Core;

public enum CellValueType { Empty, Text, Number, Boolean, DateTime, Error }

public sealed class CellValue
{
    public static readonly CellValue Empty = new(CellValueType.Empty, null);

    public CellValueType Type { get; }
    public object? Raw { get; }

    private CellValue(CellValueType type, object? raw) { Type = type; Raw = raw; }

    public static CellValue FromText(string value) => new(CellValueType.Text, value);
    public static CellValue FromNumber(double value) => new(CellValueType.Number, value);
    public static CellValue FromBoolean(bool value) => new(CellValueType.Boolean, value);
    public static CellValue FromDateTime(DateTime value) => new(CellValueType.DateTime, value);
    public static CellValue FromError(string error) => new(CellValueType.Error, error);

    public bool IsEmpty => Type == CellValueType.Empty;

    public override string ToString() => Raw?.ToString() ?? string.Empty;
}
