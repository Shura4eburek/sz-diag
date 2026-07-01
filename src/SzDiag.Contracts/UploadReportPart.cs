namespace SzDiag.Contracts;

/// <summary>Один файл отчёта, загружаемый агентом на hub.</summary>
public sealed record UploadReportPart(string Sz, string Timestamp, string FileName, byte[] Content);
