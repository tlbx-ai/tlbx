namespace Ai.Tlbx.MidTerm.Services.Connect;

internal sealed class ConnectLocalAccessException : Exception
{
    public ConnectLocalAccessException() : base("Connect configuration requires direct localhost access.") { }
}
