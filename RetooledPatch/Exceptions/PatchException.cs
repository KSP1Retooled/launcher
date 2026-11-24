namespace KSP.Retooled.Patch.Exceptions;

public sealed class PatchException(string message, Exception? inner = null) : Exception(message, inner);
