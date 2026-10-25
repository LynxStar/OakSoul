namespace OakSoul.Wz;

/// <summary>Thrown when an archive does not match the expected WZ layout.</summary>
public sealed class WzFormatException(string message, Exception? inner = null) : Exception(message, inner);
