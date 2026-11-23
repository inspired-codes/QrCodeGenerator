using System;

namespace Inspired.Codes.Graphics.QrCodeGenerator;

/// <summary>
/// The payload is larger than the allowed symbol versions can hold at the requested error correction level.
/// To recover, lower the error correction level, allow a larger maximum version, or split the payload
/// over several symbols.
/// </summary>
public class DataTooLongException : ArgumentException
{
    public DataTooLongException() { }

    public DataTooLongException(string message) : base(message) { }
}
