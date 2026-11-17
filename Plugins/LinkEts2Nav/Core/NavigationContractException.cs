namespace LinkEts2Nav.Core;

/// <summary>A violated internal model precondition, checked at DEBUG entry boundaries.</summary>
public sealed class NavigationContractException(string message):InvalidOperationException(message);
