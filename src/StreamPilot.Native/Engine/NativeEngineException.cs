namespace StreamPilot.Native.Engine;

public sealed class NativeEngineException(string operation, int code, string message)
    : Exception($"{operation} falló ({code}): {message}")
{
    public string Operation { get; } = operation;
    public int Code { get; } = code;
}
